$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

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
    $buildProperties = Get-Content -LiteralPath (Join-Path $ProjectRoot "Directory.Build.props") -Raw
    $taskConstants = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\Domain\TaskConstants.cs") -Raw
    if ($buildProperties -notmatch '<TargetFramework>net10\.0-windows</TargetFramework>') {
        throw "The target framework must be net10.0-windows."
    }
    if ($taskConstants -notmatch 'http://127\.0\.0\.1:48120') {
        throw "The loopback-only listening address was changed."
    }
}

function Assert-ApplicationIcon {
    # 実行ファイルとスタートアップショートカットで共通利用するアイコン設定を検査する。 ASCII.
    $applicationProjectPath = Join-Path $ProjectRoot "src\TaskManager.App\TaskManager.App.csproj"
    $applicationIconPath = Join-Path $ProjectRoot "src\TaskManager.App\Assets\TaskManager.ico"
    $applicationProject = Get-Content -LiteralPath $applicationProjectPath -Raw
    if ($applicationProject -notmatch '<ApplicationIcon>Assets\\TaskManager\.ico</ApplicationIcon>') {
        throw "The Task Manager application icon must be embedded into the executable."
    }
    if (-not (Test-Path -LiteralPath $applicationIconPath -PathType Leaf)) {
        throw "The Task Manager application icon file is missing."
    }
}

function Assert-WatchdogInstallation {
    # Codexの実行ジョブ外で監視親を起動し、誤ったユーザーパスをショートカットへ保存しないことを検査する。 ASCII.
    $installerPaths = @(
        (Join-Path $ProjectRoot "Install.ps1"),
        (Join-Path $ProjectRoot "scripts\install.ps1"))
    foreach ($installerPath in $installerPaths) {
        $installerSource = Get-Content -LiteralPath $installerPath -Raw
        if ($installerSource -notmatch 'Register-ScheduledTask' -or
            $installerSource -notmatch 'Start-ScheduledTask' -or
            $installerSource -notmatch '%LOCALAPPDATA%') {
            throw "The installer must register the watchdog with Task Scheduler and preserve the LOCALAPPDATA shortcut path: $installerPath"
        }
    }

    # taskctlの自動起動も登録済み監視タスクを優先することを確認する。 ASCII.
    $commandLineSource = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\TaskManager.App\Cli\CliRunner.cs") -Raw
    if ($commandLineSource -notmatch 'schtasks\.exe' -or
        $commandLineSource -notmatch 'WatchdogScheduledTaskName') {
        throw "taskctl must start the registered watchdog task before using its fallback."
    }
}

# 静的な受け入れ条件を検査する。 ASCII.
Assert-NoForbiddenIntegration
Assert-FrameworkAndAddress
Assert-ApplicationIcon
Assert-WatchdogInstallation
Write-Output "Static verification passed."
