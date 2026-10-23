[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [switch]$OverwriteCodexSkill,
    [switch]$SkipCodexSkill,
    [switch]$DoNotStart
)

$ErrorActionPreference = "Stop"

function New-TaskManagerShortcut {
    # UniToDoを起動するWindowsショートカットを作成する。
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [string]$Arguments = ""
    )

    # サンドボックスの既知フォルダー変換を避けるためLOCALAPPDATAを展開可能な表記で保存する。
    $localApplicationData = [Environment]::GetEnvironmentVariable("LOCALAPPDATA")
    $normalizedExecutable = [System.IO.Path]::GetFullPath($ExecutablePath)
    $normalizedLocalApplicationData = [System.IO.Path]::GetFullPath($localApplicationData).TrimEnd('\')
    $shortcutTargetPath = $normalizedExecutable
    if ($normalizedExecutable.StartsWith(
        "$normalizedLocalApplicationData\",
        [System.StringComparison]::OrdinalIgnoreCase)) {
        $relativeExecutablePath = $normalizedExecutable.Substring($normalizedLocalApplicationData.Length).TrimStart('\')
        $shortcutTargetPath = "%LOCALAPPDATA%\$relativeExecutablePath"
    }

    # Windows標準のCOM機能でショートカットの内容を設定する。
    $windowsShell = New-Object -ComObject WScript.Shell
    $shortcut = $windowsShell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $shortcutTargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $shortcutTargetPath
    # 実行ファイルに埋め込んだアプリアイコンをスタートアップ表示にも明示する。
    $shortcut.IconLocation = "$shortcutTargetPath,0"
    $shortcut.Save()
}

function Register-TaskManagerWatchdog {
    # 監視親をCodexの実行ジョブから独立させ、異常終了時はWindows側から再起動する。
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    # 同じユーザーの対話セッションでログオン時に起動するタスクを上書き登録する。
    $scheduledTaskName = "UniToDo Watchdog"
    $scheduledTaskUser = "$env:USERDOMAIN\$env:USERNAME"
    $scheduledTaskAction = New-ScheduledTaskAction -Execute $ExecutablePath -Argument "--watchdog" -WorkingDirectory (Split-Path -Parent $ExecutablePath)
    $scheduledTaskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $scheduledTaskUser
    $scheduledTaskSettings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -StartWhenAvailable `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries
    $scheduledTaskPrincipal = New-ScheduledTaskPrincipal `
        -UserId $scheduledTaskUser `
        -LogonType Interactive `
        -RunLevel Limited
    Register-ScheduledTask `
        -TaskName $scheduledTaskName `
        -Action $scheduledTaskAction `
        -Trigger $scheduledTaskTrigger `
        -Settings $scheduledTaskSettings `
        -Principal $scheduledTaskPrincipal `
        -Description "UniToDo automatic startup and recovery" `
        -Force | Out-Null
}

function Test-PathEntryExists {
    # ユーザーPATHに同じディレクトリが登録済みか判定する。
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$PathEntries,
        [Parameter(Mandatory = $true)][string]$DirectoryPath
    )

    $normalizedDirectory = [System.IO.Path]::GetFullPath($DirectoryPath).TrimEnd('\')
    foreach ($pathEntry in $PathEntries) {
        $expandedEntry = [Environment]::ExpandEnvironmentVariables($pathEntry.Trim())
        if ([string]::IsNullOrWhiteSpace($expandedEntry)) {
            continue
        }

        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($expandedEntry).TrimEnd('\')
        }
        catch {
            continue
        }

        if ([string]::Equals($normalizedEntry, $normalizedDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Assert-SafeSkillDestination {
    # Skillの置換先がユーザーのSkillディレクトリ直下であることを検証する。
    param(
        [Parameter(Mandatory = $true)][string]$SkillRoot,
        [Parameter(Mandatory = $true)][string]$SkillDestination
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($SkillRoot).TrimEnd('\')
    $normalizedDestination = [System.IO.Path]::GetFullPath($SkillDestination).TrimEnd('\')
    $expectedDestination = Join-Path $normalizedRoot "manage-local-tasks"
    if (-not [string]::Equals($normalizedDestination, $expectedDestination, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Codex Skillの置換先が安全な場所ではありません: $normalizedDestination"
    }
}

function Install-CodexTaskIntegration {
    # ユーザー用Skillと、そのラッパーだけを許可するCodexルールを導入する。
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$RepositorySkill,
        [Parameter(Mandatory = $true)][string]$UserProfileDirectory,
        [Parameter(Mandatory = $true)][string]$CodexDirectory,
        [switch]$OverwriteCodexSkill,
        [switch]$SkipCodexSkill
    )

    if ($SkipCodexSkill) { return }

    # 既存Skillは指定された場合だけ置き換え、利用可能なラッパーの場所を確定する。
    $userSkillRoot = Join-Path $UserProfileDirectory ".agents\skills"
    $userSkillDestination = Join-Path $userSkillRoot "manage-local-tasks"
    Assert-SafeSkillDestination -SkillRoot $userSkillRoot -SkillDestination $userSkillDestination
    $normalizedRepositorySkill = [System.IO.Path]::GetFullPath($RepositorySkill).TrimEnd('\')
    $normalizedUserSkill = [System.IO.Path]::GetFullPath($userSkillDestination).TrimEnd('\')
    if ([string]::Equals($normalizedRepositorySkill, $normalizedUserSkill, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Output "repo内Skillが既にユーザーSkillの場所にあるため、コピーは不要です: $userSkillDestination"
    }
    elseif ((Test-Path -LiteralPath $userSkillDestination) -and -not $OverwriteCodexSkill) {
        Write-Warning "既存のCodex Skillは変更しませんでした。置き換える場合は -OverwriteCodexSkill を指定してください: $userSkillDestination"
    }
    elseif ($PSCmdlet.ShouldProcess($userSkillDestination, "manage-local-tasks Skillをインストール")) {
        New-Item -ItemType Directory -Path $userSkillRoot -Force | Out-Null
        if (Test-Path -LiteralPath $userSkillDestination) {
            Remove-Item -LiteralPath $userSkillDestination -Recurse -Force
        }
        Copy-Item -LiteralPath $RepositorySkill -Destination $userSkillDestination -Recurse -Force
    }

    # 存在するラッパーだけを対象にし、JSON文字列化で空白・日本語・引用符を安全に保持する。
    $wrapperPath = Join-Path $normalizedUserSkill "scripts\invoke-taskctl.ps1"
    if (-not $WhatIfPreference -and -not (Test-Path -LiteralPath $wrapperPath -PathType Leaf)) {
        Write-Warning "操作ラッパーがないためCodex許可ルールを登録しませんでした: $wrapperPath"
        return
    }
    $legacyCommandPattern = ConvertTo-Json -InputObject @(
        "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $wrapperPath
    ) -Compress
    $previousCommandPattern = ConvertTo-Json -InputObject @(
        "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @($wrapperPath, $wrapperPath.Replace('\', '/'))
    ) -Compress -Depth 3
    # 実行名と絶対パスの表記差だけを許容し、対象スクリプトと起動オプションは固定する。
    $windowsPowerShellPath = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
    $commandPattern = ConvertTo-Json -InputObject @(
        @("powershell", "powershell.exe", $windowsPowerShellPath, $windowsPowerShellPath.Replace('\', '/')),
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @($wrapperPath, $wrapperPath.Replace('\', '/'))
    ) -Compress -Depth 3
    $ruleText = "# UniToDo taskctl wrapper" + [Environment]::NewLine +
        'prefix_rule(pattern=' + $commandPattern + ', decision="allow")' + [Environment]::NewLine
    $legacyRuleText = "# UniToDo taskctl wrapper" + [Environment]::NewLine +
        'prefix_rule(pattern=' + $legacyCommandPattern + ', decision="allow")' + [Environment]::NewLine
    $previousRuleText = "# UniToDo taskctl wrapper" + [Environment]::NewLine +
        'prefix_rule(pattern=' + $previousCommandPattern + ', decision="allow")' + [Environment]::NewLine
    $ruleDirectory = Join-Path $CodexDirectory "rules"
    $rulePath = Join-Path $ruleDirectory "unitodo-taskctl.rules"

    # 旧版が生成した同一ラッパーのルールだけを更新し、共通ルールや独自編集を保持する。
    $ruleNeedsUpdate = $true
    if (Test-Path -LiteralPath $rulePath) {
        $existingRuleText = [System.IO.File]::ReadAllText($rulePath)
        if ($existingRuleText -ceq $ruleText) {
            $ruleNeedsUpdate = $false
        }
        elseif ($existingRuleText -cnotin @($legacyRuleText, $previousRuleText)) {
            Write-Warning "既存のCodex許可ルールは変更しませんでした。登録先と内容を確認してください: $rulePath"
            return
        }
    }
    if ($ruleNeedsUpdate -and $PSCmdlet.ShouldProcess($rulePath, "UniToDo操作ラッパーを全プロジェクトで許可するルールを登録・更新")) {
        New-Item -ItemType Directory -Path $ruleDirectory -Force | Out-Null
        [System.IO.File]::WriteAllText($rulePath, $ruleText, (New-Object System.Text.UTF8Encoding($false)))
    }
    if (-not $WhatIfPreference) {
        Write-Output "Codex許可ルール: $rulePath。反映するにはCodexを再起動してください。"
    }
}

function Stop-InstalledTaskManager {
    # 更新対象と同じ絶対パスで動作中のTask Managerだけを停止する。
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        return
    }

    $normalizedExecutable = [System.IO.Path]::GetFullPath($ExecutablePath)
    $runningProcesses = Get-CimInstance Win32_Process -Filter "Name = 'TaskManager.exe'" -ErrorAction SilentlyContinue
    foreach ($runningProcess in $runningProcesses) {
        if ([string]::Equals($runningProcess.ExecutablePath, $normalizedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $runningProcess.ProcessId -Force
        }
    }
}

# 頒布ZIPに同梱された自己完結ランタイムとCodex Skillを検証する。
if ($env:OS -ne "Windows_NT") {
    throw "この頒布物はWindows 10/11 x64専用です。"
}

$distributionRoot = $PSScriptRoot
$runtimeDirectory = Join-Path $distributionRoot "runtime"
$applicationSource = Join-Path $runtimeDirectory "TaskManager.exe"
$commandLineSource = Join-Path $runtimeDirectory "taskctl.exe"
$licenseDirectory = Join-Path $runtimeDirectory "licenses"
$projectLicensePath = Join-Path $licenseDirectory "LICENSE"
$thirdPartyNoticePath = Join-Path $licenseDirectory "THIRD-PARTY-NOTICES.md"
$dotNetLicensePath = Join-Path $licenseDirectory "Microsoft-DotNet-Library-License.txt"
$packageLegalIndexPath = Join-Path $licenseDirectory "package-legal-files.json"
$packageLegalDirectory = Join-Path $licenseDirectory "packages"
$repositorySkill = Join-Path $distributionRoot ".agents\skills\manage-local-tasks"
foreach ($requiredPath in @($runtimeDirectory, $applicationSource, $commandLineSource, $projectLicensePath, $thirdPartyNoticePath, $dotNetLicensePath, $packageLegalIndexPath, $packageLegalDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "頒布物に必要なファイルがありません: $requiredPath"
    }
}

# インストール前に独自部分と同梱.NETランタイムへ別の条件が適用されることを表示する。
Write-Output "UniToDo独自部分: MIT License - uniuni (https://x.com/lept_on)"
Write-Output "第三者コンポーネント: $thirdPartyNoticePath"
Write-Output "同梱.NETランタイム: $dotNetLicensePath"
Write-Output "パッケージ付属文書: $packageLegalIndexPath"
Write-Output "続行して本ソフトウェアを使用する場合、これらのライセンス条件が適用されます。"

if (-not $SkipCodexSkill -and -not (Test-Path -LiteralPath (Join-Path $repositorySkill "SKILL.md"))) {
    throw "頒布物にmanage-local-tasks Skillがありません: $repositorySkill"
}

# 発行済みランタイムをユーザー単位のプログラム領域へ配置する。
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$installDirectory = Join-Path $localApplicationData "Programs\TaskManager"
$applicationExecutable = Join-Path $installDirectory "TaskManager.exe"
if ($PSCmdlet.ShouldProcess($installDirectory, "UniToDoランタイムをインストール")) {
    # 更新中にWindows側の再起動が割り込まないよう新旧の登録済みタスクを停止し、旧名だけを解除する。
    $legacyScheduledTaskName = "LocalTaskManager Watchdog"
    foreach ($registeredTaskName in @("UniToDo Watchdog", $legacyScheduledTaskName)) {
        Stop-ScheduledTask -TaskName $registeredTaskName -ErrorAction SilentlyContinue
    }
    Unregister-ScheduledTask -TaskName $legacyScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Stop-InstalledTaskManager -ExecutablePath $applicationExecutable
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Get-ChildItem -Force -LiteralPath $runtimeDirectory | Copy-Item -Destination $installDirectory -Recurse -Force
}

# デスクトップショートカットを作成し、旧スタートアップ登録を整理する。
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$legacyShortcutName = "TaskManager.lnk"
$applicationShortcutName = "UniToDo.lnk"
foreach ($startupShortcutName in @($legacyShortcutName, $applicationShortcutName)) {
    $startupShortcut = Join-Path $startupDirectory $startupShortcutName
    if ((Test-Path -LiteralPath $startupShortcut) -and $PSCmdlet.ShouldProcess($startupShortcut, "旧スタートアップショートカットを削除")) {
        Remove-Item -LiteralPath $startupShortcut -Force
    }
}
$legacyDesktopShortcut = Join-Path $desktopDirectory $legacyShortcutName
if ((Test-Path -LiteralPath $legacyDesktopShortcut) -and $PSCmdlet.ShouldProcess($legacyDesktopShortcut, "旧デスクトップショートカットを削除")) {
    Remove-Item -LiteralPath $legacyDesktopShortcut -Force
}
$desktopShortcut = Join-Path $desktopDirectory $applicationShortcutName
if ($PSCmdlet.ShouldProcess($desktopShortcut, "デスクトップショートカットを作成")) {
    New-TaskManagerShortcut -ShortcutPath $desktopShortcut -ExecutablePath $applicationExecutable
}
if ($PSCmdlet.ShouldProcess("UniToDo Watchdog", "監視親のログオンタスクを登録")) {
    Register-TaskManagerWatchdog -ExecutablePath $applicationExecutable
}

# 新しく開く端末からtaskctlを呼べるようユーザーPATHへ登録する。
$currentUserPath = [Environment]::GetEnvironmentVariable("Path", "User")
$pathEntries = @($currentUserPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if (-not (Test-PathEntryExists -PathEntries $pathEntries -DirectoryPath $installDirectory)) {
    if ($PSCmdlet.ShouldProcess("ユーザーPATH", "$installDirectory を追加")) {
        $updatedUserPath = (($pathEntries + $installDirectory) -join ';')
        [Environment]::SetEnvironmentVariable("Path", $updatedUserPath, "User")
    }
}

# 別の作業フォルダーでも使えるようSkillと専用許可ルールをユーザー領域へ導入する。
if (-not $SkipCodexSkill) {
    $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $codexDirectory = $env:CODEX_HOME
    if ([string]::IsNullOrWhiteSpace($codexDirectory)) {
        $codexDirectory = Join-Path $userProfileDirectory ".codex"
    }
    Install-CodexTaskIntegration -RepositorySkill $repositorySkill -UserProfileDirectory $userProfileDirectory `
        -CodexDirectory $codexDirectory -OverwriteCodexSkill:$OverwriteCodexSkill -WhatIf:$WhatIfPreference
}

# インストール後にアプリを起動し、次に行う疎通確認を案内する。
if (-not $DoNotStart -and $PSCmdlet.ShouldProcess($applicationExecutable, "UniToDoを起動")) {
    Start-ScheduledTask -TaskName "UniToDo Watchdog"
}

if ($WhatIfPreference) {
    Write-Output "事前確認が完了しました。上記の対象に問題がなければ -WhatIf を外して実行してください。"
}
else {
    Write-Output "インストール先: $installDirectory"
    Write-Output "推奨: 設定画面の「追加バックアップ」で別の物理ディスク上の専用フォルダを指定してください。初期設定はOFFです。別ディスクがなければOFFのまま利用できます。"
    Write-Output "新しく端末を開いた後、taskctl now --json で疎通を確認してください。"
}
