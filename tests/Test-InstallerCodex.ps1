param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = "Stop"

function Assert-InstallerCondition {
    # インストーラーの実行結果が期待と異なる場合は検証を失敗させる。
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

# 本体の導入・タスク登録を実行せず、同じインストーラーのCodex連携関数だけを読み込む。
$installerPath = Join-Path $ProjectRoot "Install.ps1"
$parseTokens = $null
$parseErrors = $null
$installerSyntax = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$parseTokens, [ref]$parseErrors)
Assert-InstallerCondition ($parseErrors.Count -eq 0) "Install.ps1の構文が不正です。"
$integrationFunctions = $installerSyntax.FindAll({
    param($syntaxNode)
    $syntaxNode -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $syntaxNode.Name -in @("Assert-SafeSkillDestination", "Install-CodexTaskIntegration")
}, $false)
Assert-InstallerCondition ($integrationFunctions.Count -eq 2) "Codex連携の関数を読み込めません。"
foreach ($integrationFunction in $integrationFunctions) {
    . ([ScriptBlock]::Create($integrationFunction.Extent.Text))
}

# 空白・日本語・引用符のある架空ユーザーと独自CODEX_HOMEをワークスペース内に用意する。
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot "artifacts"))
$testRoot = Join-Path $artifactRoot ("installer-codex-" + [Guid]::NewGuid().ToString("N"))
$profileDirectory = Join-Path $testRoot "受取人 O'Brien"
$codexDirectory = Join-Path $testRoot "custom codex"
$rulePath = Join-Path $codexDirectory "rules\unitodo-taskctl.rules"
$skillDirectory = Join-Path $profileDirectory ".agents\skills\manage-local-tasks"
$wrapperPath = Join-Path $skillDirectory "scripts\invoke-taskctl.ps1"
$integrationArguments = @{
    RepositorySkill = Join-Path $ProjectRoot ".agents\skills\manage-local-tasks"
    UserProfileDirectory = $profileDirectory
    CodexDirectory = $codexDirectory
}

try {
    # 事前確認とCodex連携の省略では、Skillもルールも作成しない。
    Install-CodexTaskIntegration @integrationArguments -WhatIf | Out-Null
    Assert-InstallerCondition (-not (Test-Path -LiteralPath $testRoot)) "WhatIfでファイルを作成しました。"
    Install-CodexTaskIntegration @integrationArguments -SkipCodexSkill | Out-Null
    Assert-InstallerCondition (-not (Test-Path -LiteralPath $testRoot)) "SkipCodexSkillでファイルを作成しました。"

    # 既存の共通ルールを保持して、導入先のラッパーだけを許可するルールを追加する。
    $defaultRulePath = Join-Path $codexDirectory "rules\default.rules"
    $defaultRuleText = 'prefix_rule(pattern=["unrelated"], decision="prompt")'
    New-Item -ItemType Directory -Path (Split-Path -Parent $defaultRulePath) -Force | Out-Null
    [IO.File]::WriteAllText($defaultRulePath, $defaultRuleText)
    Install-CodexTaskIntegration @integrationArguments | Out-Null
    Assert-InstallerCondition (Test-Path -LiteralPath $wrapperPath -PathType Leaf) "操作ラッパーが導入されません。"
    Assert-InstallerCondition ([IO.File]::ReadAllText($defaultRulePath) -ceq $defaultRuleText) "既存ルールが変更されました。"
    $ruleText = [IO.File]::ReadAllText($rulePath)
    $patternMatch = [regex]::Match($ruleText, 'prefix_rule\(pattern=(\[.*\]), decision="allow"\)')
    Assert-InstallerCondition $patternMatch.Success "許可ルールを読み取れません。"
    $commandPattern = ConvertFrom-Json $patternMatch.Groups[1].Value
    $windowsPowerShellPath = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    Assert-InstallerCondition ($commandPattern.Count -eq 6 -and
        $commandPattern[0].Count -eq 4 -and
        $commandPattern[0][0] -ceq "powershell" -and
        $commandPattern[0][1] -ceq "powershell.exe" -and
        $commandPattern[0][2] -ceq $windowsPowerShellPath -and
        $commandPattern[0][3] -ceq $windowsPowerShellPath.Replace('\', '/') -and
        ($commandPattern[1..4] -join " ") -ceq "-NoProfile -ExecutionPolicy Bypass -File" -and
        $commandPattern[5].Count -eq 2 -and
        $commandPattern[5][0] -ceq $wrapperPath -and
        $commandPattern[5][1] -ceq $wrapperPath.Replace('\', '/')) "許可範囲が同じラッパーの両パス表記に限定されていません。"
    $ruleBytes = [IO.File]::ReadAllBytes($rulePath)
    Assert-InstallerCondition (-not ($ruleBytes[0] -eq 0xEF -and $ruleBytes[1] -eq 0xBB -and $ruleBytes[2] -eq 0xBF)) "ルールへBOMが混入しました。"

    # 旧インストーラーが生成した単一表記のルールはWhatIfで保持し、通常実行で移行する。
    $legacyPattern = ConvertTo-Json -InputObject @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $wrapperPath) -Compress
    $legacyRuleText = "# UniToDo taskctl wrapper" + [Environment]::NewLine +
        'prefix_rule(pattern=' + $legacyPattern + ', decision="allow")' + [Environment]::NewLine
    [IO.File]::WriteAllText($rulePath, $legacyRuleText)
    Install-CodexTaskIntegration @integrationArguments -WhatIf -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition ([IO.File]::ReadAllText($rulePath) -ceq $legacyRuleText) "WhatIfで旧ルールを書き換えました。"
    Install-CodexTaskIntegration @integrationArguments -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition ([IO.File]::ReadAllText($rulePath) -ceq $ruleText) "旧ルールを両パス表記の許可へ移行できません。"

    # 実行名がpowershellだけの旧ルールも、同じラッパーに限定したまま更新する。
    $previousPattern = ConvertTo-Json -InputObject @(
        "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @($wrapperPath, $wrapperPath.Replace('\', '/'))
    ) -Compress -Depth 3
    $previousRuleText = "# UniToDo taskctl wrapper" + [Environment]::NewLine +
        'prefix_rule(pattern=' + $previousPattern + ', decision="allow")' + [Environment]::NewLine
    [IO.File]::WriteAllText($rulePath, $previousRuleText)
    Install-CodexTaskIntegration @integrationArguments -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition ([IO.File]::ReadAllText($rulePath) -ceq $ruleText) "実行名が単一の旧ルールを移行できません。"

    # 再導入は同じルールを書き直さず、既存Skillの独自内容も保持する。
    $customSkillPath = Join-Path $skillDirectory "custom.txt"
    [IO.File]::WriteAllText($customSkillPath, "preserve")
    $previousWriteTime = [DateTime]::UtcNow.AddDays(-1)
    [IO.File]::SetLastWriteTimeUtc($rulePath, $previousWriteTime)
    Install-CodexTaskIntegration @integrationArguments -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition ([IO.File]::GetLastWriteTimeUtc($rulePath) -eq $previousWriteTime) "再導入で同じルールを書き直しました。"
    Assert-InstallerCondition (Test-Path -LiteralPath $customSkillPath) "既存Skillの独自内容が消えました。"
    Install-CodexTaskIntegration @integrationArguments -OverwriteCodexSkill | Out-Null
    Assert-InstallerCondition (-not (Test-Path -LiteralPath $customSkillPath)) "指定したSkillの置換が行われません。"

    # 同名ルールの独自編集を保持し、ラッパーのない既存Skillには許可を追加しない。
    $customRuleText = 'prefix_rule(pattern=["custom"], decision="prompt")'
    [IO.File]::WriteAllText($rulePath, $customRuleText)
    Install-CodexTaskIntegration @integrationArguments -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition ([IO.File]::ReadAllText($rulePath) -ceq $customRuleText) "独自編集された専用ルールが上書きされました。"
    Remove-Item -LiteralPath $rulePath
    Remove-Item -LiteralPath $wrapperPath
    Install-CodexTaskIntegration @integrationArguments -WarningAction SilentlyContinue | Out-Null
    Assert-InstallerCondition (-not (Test-Path -LiteralPath $rulePath)) "ラッパーのないSkillに許可を追加しました。"
    Write-Output "PASS installer Codex integration: install, existing rules, reinstall, WhatIf, skip, missing wrapper."
}
finally {
    # 削除対象がワークスペース内artifacts直下の今回の一時領域であることを検証する。
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "テスト領域の削除先が不正です。"
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
