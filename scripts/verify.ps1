$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

function Assert-WindowsPowerShellCompatibility {
    # 日本語を含む静的検査スクリプトがWindows PowerShell 5.1でも正しく解釈されることを確認する。 ASCII.
    $verificationScriptPath = Join-Path $PSScriptRoot "verify.ps1"
    $scriptBytes = [System.IO.File]::ReadAllBytes($verificationScriptPath)

    # UTF-8 BOMの3バイトが保持されていることを検査する。
    $hasUtf8Bom = $scriptBytes.Length -ge 3 -and
        $scriptBytes[0] -eq 0xEF -and
        $scriptBytes[1] -eq 0xBB -and
        $scriptBytes[2] -eq 0xBF
    if (-not $hasUtf8Bom) {
        throw "scripts/verify.ps1 must retain its UTF-8 BOM for Windows PowerShell 5.1 compatibility."
    }
}

function Assert-NoForbiddenIntegration {
    # 実行コードへOpenAI API、Apps Script、Google Sheets同期が混入していないことを確認する。 ASCII.
    $sourceFiles = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "src") -Recurse -File |
        Where-Object {
            $_.Extension -in ".cs", ".js", ".json", ".csproj" -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    $forbiddenPatterns = @(
        "api.openai.com",
        "OpenAIClient",
        "SpreadsheetApp",
        "google.script.run",
        "script.google.com",
        "ClosedXML",
        "/import/preview",
        "view-migration")
    foreach ($forbiddenPattern in $forbiddenPatterns) {
        $matches = $sourceFiles | Select-String -Pattern $forbiddenPattern -SimpleMatch
        if ($matches) {
            throw "A forbidden integration string was found: $forbiddenPattern"
        }
    }
}

function Assert-FrameworkAndAddress {
    # .NET 10への統一とloopback限定アドレスを確認する。 ASCII.
    $buildProperties = Get-Content -LiteralPath (Join-Path $ProjectRoot "Directory.Build.props") -Raw -Encoding UTF8
    $globalSettings = Get-Content -LiteralPath (Join-Path $ProjectRoot "global.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskConstants = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\Domain\TaskConstants.cs") -Raw -Encoding UTF8
    if ($buildProperties -notmatch '<TargetFramework>net10\.0-windows</TargetFramework>') {
        throw "The target framework must be net10.0-windows."
    }
    if ([version]$globalSettings.sdk.version -lt [version]"10.0.303") {
        throw "The selected .NET SDK baseline must include the 10.0.11 security-servicing runtime."
    }
    if ($taskConstants -notmatch 'http://127\.0\.0\.1:48120') {
        throw "The loopback-only listening address was changed."
    }
}

function Assert-ApplicationIcon {
    # 実行ファイルとスタートアップショートカットで共通利用するアイコン設定を検査する。 ASCII.
    $applicationProjectPath = Join-Path $ProjectRoot "src\TaskManager.App\TaskManager.App.csproj"
    $applicationIconPath = Join-Path $ProjectRoot "src\TaskManager.App\Assets\TaskManager.ico"
    $applicationProject = Get-Content -LiteralPath $applicationProjectPath -Raw -Encoding UTF8
    if ($applicationProject -notmatch '<ApplicationIcon>Assets\\TaskManager\.ico</ApplicationIcon>') {
        throw "The Task Manager application icon must be embedded into the executable."
    }
    if (-not (Test-Path -LiteralPath $applicationIconPath -PathType Leaf)) {
        throw "The Task Manager application icon file is missing."
    }
}

function Assert-ApplicationDisplayName {
    # 利用者向け表示名をUniToDoへ統一し、開発上の出力名を維持していることを検査する。 ASCII.
    $taskConstants = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\Domain\TaskConstants.cs") -Raw -Encoding UTF8
    $applicationProject = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\TaskManager.App.csproj") -Raw -Encoding UTF8
    $webPage = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\wwwroot\index.html") -Raw -Encoding UTF8
    $typeWhisperManifest = Get-Content -LiteralPath (Join-Path $ProjectRoot "integrations\TypeWhisper.TaskManagerOutput\manifest.json") -Raw -Encoding UTF8
    if ($taskConstants -notmatch 'ApplicationDisplayName = "UniToDo"' -or
        $taskConstants -notmatch 'WatchdogScheduledTaskName = "UniToDo Watchdog"') {
        throw "The UniToDo display-name constants are missing."
    }
    foreach ($requiredProjectProperty in @("<AssemblyName>TaskManager</AssemblyName>", "<AssemblyTitle>UniToDo</AssemblyTitle>", "<Product>UniToDo</Product>")) {
        if ($applicationProject -notmatch [regex]::Escape($requiredProjectProperty)) {
            throw "The executable metadata does not preserve the development name and expose UniToDo: $requiredProjectProperty"
        }
    }
    foreach ($requiredWebText in @("<title>UniToDo</title>", "<strong>UniToDo</strong>")) {
        if ($webPage -notmatch [regex]::Escape($requiredWebText)) {
            throw "The web UI is missing the UniToDo display name: $requiredWebText"
        }
    }
    if ($typeWhisperManifest -notmatch '"name": "UniToDo Review Output"') {
        throw "The TypeWhisper integration display name is not UniToDo."
    }
}

function Assert-AssistantKeyboardSubmission {
    # Codex会話のCtrl+Enter送信とIME変換中の誤送信防止を検査する。 ASCII.
    $assistantSource = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\wwwroot\assistant.js") -Raw -Encoding UTF8
    foreach ($requiredPattern in @('event\.key !== "Enter"', '!event\.ctrlKey', 'event\.isComposing', 'assistantForm\.requestSubmit\(sendButton\)')) {
        if ($assistantSource -notmatch $requiredPattern) {
            throw "The Codex assistant Ctrl+Enter submission guard is missing: $requiredPattern"
        }
    }
}

function Assert-AssistantPanelDismissal {
    # Codex会話の欄外判定がポインターの押下開始時に行われることを検査する。 ASCII.
    $assistantSource = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\wwwroot\assistant.js") -Raw -Encoding UTF8
    if ($assistantSource -notmatch 'document\.addEventListener\("pointerdown"' -or
        $assistantSource -match 'document\.addEventListener\("click", \(event\) =>') {
        throw "The Codex assistant must dismiss the panel from the pointer-down origin, not the completed click target."
    }
}

function Assert-WatchdogInstallation {
    # Codexの実行ジョブ外で監視親を起動し、誤ったユーザーパスをショートカットへ保存しないことを検査する。 ASCII.
    $installerPaths = @(
        (Join-Path $ProjectRoot "Install.ps1"),
        (Join-Path $ProjectRoot "scripts\install.ps1"))
    foreach ($installerPath in $installerPaths) {
        $installerSource = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8
        if ($installerSource -notmatch 'Register-ScheduledTask' -or
            $installerSource -notmatch 'Start-ScheduledTask' -or
            $installerSource -notmatch '%LOCALAPPDATA%' -or
            $installerSource -notmatch 'UniToDo Watchdog' -or
            $installerSource -notmatch 'UniToDo\.lnk' -or
            $installerSource -notmatch 'LocalTaskManager Watchdog' -or
            $installerSource -notmatch 'TaskManager\.lnk') {
            throw "The installer must register the watchdog with Task Scheduler and preserve the LOCALAPPDATA shortcut path: $installerPath"
        }
    }

    # taskctlの自動起動も登録済み監視タスクを優先することを確認する。 ASCII.
    $commandLineSource = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\Cli\CliRunner.cs") -Raw -Encoding UTF8
    if ($commandLineSource -notmatch 'schtasks\.exe' -or
        $commandLineSource -notmatch 'WatchdogScheduledTaskName') {
        throw "taskctl must start the registered watchdog task before using its fallback."
    }
}

function Assert-WatchdogUninstallation {
    # アンインストール時に監視タスクを停止・解除して起動失敗を残さないことを確認する。 ASCII.
    $uninstallerPath = Join-Path $ProjectRoot "scripts\uninstall.ps1"
    $uninstallerSource = Get-Content -LiteralPath $uninstallerPath -Raw -Encoding UTF8
    if ($uninstallerSource -notmatch 'Stop-ScheduledTask' -or
        $uninstallerSource -notmatch 'Unregister-ScheduledTask' -or
        $uninstallerSource -notmatch 'UniToDo Watchdog' -or
        $uninstallerSource -notmatch 'LocalTaskManager Watchdog' -or
        $uninstallerSource -notmatch 'UniToDo\.lnk' -or
        $uninstallerSource -notmatch 'TaskManager\.lnk') {
        throw "The uninstaller must remove both current and legacy watchdog tasks and shortcuts."
    }
}

function Assert-DistributionSecretExclusions {
    # 頒布検査がGit除外と同じ代表的な秘密情報ファイルを扱うことを確認する。 ASCII.
    $distributionScriptPath = Join-Path $ProjectRoot "scripts\create-distribution.ps1"
    $distributionScriptSource = Get-Content -LiteralPath $distributionScriptPath -Raw -Encoding UTF8
    foreach ($requiredPattern in @("secrets.json", ".env.example", ".pfx", ".pem", "appsettings.Local.json")) {
        if ($distributionScriptSource -notmatch [regex]::Escape($requiredPattern)) {
            throw "The distribution safety checks are missing a secret-file rule: $requiredPattern"
        }
    }
}

function Assert-DistributionArchiveName {
    # 頒布ZIPの既定名が利用者向け名称と一致することを確認する。 ASCII.
    $distributionScriptPath = Join-Path $ProjectRoot "scripts\create-distribution.ps1"
    $distributionScriptSource = Get-Content -LiteralPath $distributionScriptPath -Raw -Encoding UTF8
    if ($distributionScriptSource -notmatch '\[string\]\$DistributionName = "UniToDo-win-x64"') {
        throw "The default distribution archive name must be UniToDo-win-x64.zip."
    }
}

function Assert-RecipientSetupGuide {
    # 受取人向け案内にCodexプロジェクト作成手順が揃っていることを確認する。 ASCII.
    $setupGuidePath = Join-Path $ProjectRoot "最初にお読みください.txt"
    $setupGuideSource = Get-Content -LiteralPath $setupGuidePath -Raw -Encoding UTF8
    $requiredInstructions = @(
        "UniToDo-win-x64.zip",
        "新しいチャット",
        "新しいプロジェクト",
        "ソースフォルダ",
        "UniToDo-win-x64 を選択")
    foreach ($requiredInstruction in $requiredInstructions) {
        if ($setupGuideSource -notmatch [regex]::Escape($requiredInstruction)) {
            throw "The recipient setup guide is missing a required instruction: $requiredInstruction"
        }
    }
}

function Assert-LicenseInventory {
    # 独自ライセンス表示と全lockファイルの第三者依存一覧が一致することを検査する。 ASCII.
    $requiredLicenseFiles = @(
        "LICENSE"
        "THIRD-PARTY-NOTICES.md"
        "licenses\dependencies.json"
        "licenses\Apache-2.0.txt"
        "licenses\DotNet-MIT-LICENSE.txt"
        "licenses\Newtonsoft.Json-LICENSE.md"
    )
    foreach ($requiredLicenseFile in $requiredLicenseFiles) {
        $requiredLicensePath = Join-Path $ProjectRoot $requiredLicenseFile
        if (-not (Test-Path -LiteralPath $requiredLicensePath -PathType Leaf)) {
            throw "A required license file is missing: $requiredLicenseFile"
        }
    }

    $projectLicense = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "LICENSE") -Encoding UTF8
    if ($projectLicense -notmatch 'Copyright \(c\) 2026 uniuni \(https://x\.com/lept_on\)') {
        throw "The project MIT license must contain the approved copyright holder."
    }

    $thirdPartyNotice = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "THIRD-PARTY-NOTICES.md") -Encoding UTF8
    foreach ($projectAsset in @("TaskManager.ico", "favicon.svg")) {
        if ($thirdPartyNotice -notmatch [regex]::Escape($projectAsset)) {
            throw "The original project asset is missing from the license notice: $projectAsset"
        }
    }
    foreach ($requiredNoticeDescription in @("package-legal-files.json", "パッケージ付属")) {
        if ($thirdPartyNotice -notmatch [regex]::Escape($requiredNoticeDescription)) {
            throw "The package-provided notice collection is not documented: $requiredNoticeDescription"
        }
    }

    $dependencyManifest = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "licenses\dependencies.json") -Encoding UTF8 | ConvertFrom-Json
    $manifestPackageDependencies = @(
        $dependencyManifest.components |
            Where-Object { $_.name -notlike "runtimepack.*" } |
            ForEach-Object { "$($_.name)/$($_.version)" } |
            Sort-Object -Unique)
    $duplicateManifestDependencies = @(
        $dependencyManifest.components |
            Group-Object { "$($_.name)/$($_.version)" } |
            Where-Object { $_.Count -ne 1 })
    if ($duplicateManifestDependencies.Count -gt 0) {
        throw "The license dependency manifest contains duplicate entries."
    }

    $lockFilePaths = @(
        "src\TaskManager.App\packages.lock.json"
        "src\TaskManager.Cli\packages.lock.json"
        "tests\TaskManager.Tests\packages.lock.json"
    )
    $lockedPackageDependencies = @(
        foreach ($lockFilePath in $lockFilePaths) {
            $lockData = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot $lockFilePath) -Encoding UTF8 | ConvertFrom-Json
            foreach ($frameworkProperty in $lockData.dependencies.PSObject.Properties) {
                $frameworkProperty.Value.PSObject.Properties |
                    Where-Object { $_.Value.type -ne "Project" } |
                    ForEach-Object { "$($_.Name)/$($_.Value.resolved)" }
            }
        }
    )
    $lockedPackageDependencies = @($lockedPackageDependencies | Sort-Object -Unique)

    $unlistedLockedDependencies = @($lockedPackageDependencies | Where-Object { $_ -notin $manifestPackageDependencies })
    $staleManifestDependencies = @($manifestPackageDependencies | Where-Object { $_ -notin $lockedPackageDependencies })
    if ($unlistedLockedDependencies.Count -gt 0 -or $staleManifestDependencies.Count -gt 0) {
        $differenceText = @(
            "Unlisted locked dependencies: $($unlistedLockedDependencies -join ', ')"
            "Stale manifest dependencies: $($staleManifestDependencies -join ', ')") -join [Environment]::NewLine
        throw "The license dependency manifest does not match the lock files.`n$differenceText"
    }

    $runtimePackVersions = @(
        $dependencyManifest.components |
            Where-Object { $_.name -like "runtimepack.*" } |
            ForEach-Object { [version]$_.version })
    if ($runtimePackVersions.Count -ne 3 -or @($runtimePackVersions | Where-Object { $_ -lt [version]"10.0.11" }).Count -gt 0) {
        throw "The runtime pack license inventory must use .NET 10.0.11 or later."
    }

    $distributionScript = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "scripts\create-distribution.ps1") -Encoding UTF8
    foreach ($requiredPattern in @(
        "Assert-PublishedDependenciesCovered",
        "Assert-PackageLegalFilesIncluded",
        "AllowLicensePackages",
        "Microsoft-DotNet-Library-License.txt",
        "package-legal-files.json",
        "runtime\licenses\LICENSE")) {
        if ($distributionScript -notmatch [regex]::Escape($requiredPattern)) {
            throw "The distribution script is missing a license packaging rule: $requiredPattern"
        }
    }

    $publishScript = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "scripts\publish.ps1") -Encoding UTF8
    foreach ($requiredPattern in @("Copy-PackageLegalFiles", "Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.AspNetCore.App.Runtime.win-x64", "System.Management")) {
        if ($publishScript -notmatch [regex]::Escape($requiredPattern)) {
            throw "The publish script is missing a package-provided notice rule: $requiredPattern"
        }
    }

    $distributionInstaller = Get-Content -Raw -LiteralPath (Join-Path $ProjectRoot "Install.ps1") -Encoding UTF8
    foreach ($requiredPattern in @("MIT License - uniuni (https://x.com/lept_on)", "THIRD-PARTY-NOTICES.md", "Microsoft-DotNet-Library-License.txt", "package-legal-files.json")) {
        if ($distributionInstaller -notmatch [regex]::Escape($requiredPattern)) {
            throw "The distribution installer is missing a license display rule: $requiredPattern"
        }
    }
}

# 静的な受け入れ条件を検査する。 ASCII.
Assert-WindowsPowerShellCompatibility
Assert-NoForbiddenIntegration
Assert-FrameworkAndAddress
Assert-ApplicationIcon
Assert-ApplicationDisplayName
Assert-AssistantKeyboardSubmission
Assert-AssistantPanelDismissal
Assert-WatchdogInstallation
Assert-WatchdogUninstallation
Assert-DistributionSecretExclusions
Assert-DistributionArchiveName
Assert-RecipientSetupGuide
Assert-LicenseInventory
Write-Output "Static verification passed."
