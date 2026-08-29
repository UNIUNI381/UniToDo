param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [switch]$WhatIf)

. (Join-Path $ProjectRoot "scripts\common.ps1")

# TypeWhisper連携の固定パスと識別子を保持する。
$typeWhisperDataDirectory = Join-Path $env:LOCALAPPDATA "TypeWhisper-UserData"
$typeWhisperSdkPath = Join-Path $env:LOCALAPPDATA "TypeWhisper\current\TypeWhisper.PluginSDK.dll"
$taskManagerExecutable = Join-Path $env:LOCALAPPDATA "Programs\TaskManager\TaskManager.exe"
$codexExecutable = Join-Path $env:USERPROFILE ".codex\packages\standalone\current\bin\codex.exe"
$typeWhisperSettingsPath = Join-Path $typeWhisperDataDirectory "settings.json"
$workflowPath = Join-Path $typeWhisperDataDirectory "Data\workflows.json"
$pluginDirectory = Join-Path $typeWhisperDataDirectory "Plugins\com.local.codex-thread-output"
$scriptRunnerConfigurationPath = Join-Path $typeWhisperDataDirectory "PluginData\com.typewhisper.script\scripts.json"
$ollamaScriptSourcePath = Join-Path $ProjectRoot "integrations\TypeWhisper.Ollama\Invoke-OllamaTypeWhisper.ps1"
$ollamaScriptPath = Join-Path $typeWhisperDataDirectory "Scripts\Invoke-OllamaTypeWhisper.ps1"
$pluginProject = Join-Path $ProjectRoot "integrations\TypeWhisper.TaskManagerOutput\TypeWhisper.TaskManagerOutput.csproj"
$pluginBuildDirectory = Join-Path $ProjectRoot "integrations\TypeWhisper.TaskManagerOutput\bin\Release\net10.0-windows"
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$helperShortcutPath = Join-Path $startupDirectory "TypeWhisper Codex Hotkey.lnk"
$helperExecutablePath = Join-Path $typeWhisperDataDirectory "Helpers\TypeWhisperCodexHotkey\TypeWhisperCodexHotkey.exe"
$backupTimestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDirectory = Join-Path $typeWhisperDataDirectory "Backups\task-manager-review-$backupTimestamp"
$pluginIdentifier = "com.local.codex-thread-output"

function Assert-TypeWhisperIntegrationPrerequisites {
    param([bool]$RequireStopped)
    # 外部設定を書き換える前に必要ファイルと停止状態を検証する。
    if (Get-Process -Name "TypeWhisper" -ErrorAction SilentlyContinue) {
        if ($RequireStopped) {
            throw "TypeWhisperを終了してから再実行してください。"
        }
        Write-Warning "TypeWhisperは実際の移行前に終了する必要があります。"
    }
    if (-not (Test-Path -LiteralPath $typeWhisperSdkPath -PathType Leaf)) {
        throw "TypeWhisper Plugin SDKが見つかりません: $typeWhisperSdkPath"
    }
    if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
        throw "TypeWhisperワークフローが見つかりません: $workflowPath"
    }
    if (-not (Test-Path -LiteralPath $typeWhisperSettingsPath -PathType Leaf)) {
        throw "TypeWhisper設定が見つかりません: $typeWhisperSettingsPath"
    }
    if (-not (Test-Path -LiteralPath $scriptRunnerConfigurationPath -PathType Leaf)) {
        throw "TypeWhisper Script Runner設定が見つかりません: $scriptRunnerConfigurationPath"
    }
    if (-not (Test-Path -LiteralPath $ollamaScriptSourcePath -PathType Leaf)) {
        throw "Ollama校正スクリプトの配布元が見つかりません: $ollamaScriptSourcePath"
    }
    if (-not (Test-Path -LiteralPath $taskManagerExecutable -PathType Leaf)) {
        throw "インストール済みTask Managerが見つかりません: $taskManagerExecutable"
    }
    if (-not (Test-Path -LiteralPath $codexExecutable -PathType Leaf)) {
        throw "外部プロセスから実行可能なCodex CLIが見つかりません。公式Windowsインストーラーで導入してください: powershell -ExecutionPolicy ByPass -c `"irm https://chatgpt.com/codex/install.ps1 | iex`""
    }
    & $codexExecutable --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Codex CLIを実行できません: $codexExecutable"
    }
}

function Show-TypeWhisperIntegrationTargets {
    # WhatIfと実行前確認へ変更対象を一覧表示する。
    Write-Output "Plugin project: $pluginProject"
    Write-Output "Plugin directory: $pluginDirectory"
    Write-Output "TypeWhisper settings: $typeWhisperSettingsPath"
    Write-Output "Workflow file: $workflowPath"
    Write-Output "Ollama script: $ollamaScriptPath"
    Write-Output "Script Runner configuration: $scriptRunnerConfigurationPath"
    Write-Output "Codex CLI: $codexExecutable"
    Write-Output "Legacy shortcut: $helperShortcutPath"
    Write-Output "Backup directory: $backupDirectory"
}

function Backup-TypeWhisperIntegration {
    # 既存プラグイン、ワークフロー、旧ショートカットを復元可能な状態で退避する。
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $workflowPath -Destination (Join-Path $backupDirectory "workflows.json") -Force
    Copy-Item -LiteralPath $typeWhisperSettingsPath -Destination (Join-Path $backupDirectory "settings.json") -Force
    Copy-Item -LiteralPath $scriptRunnerConfigurationPath -Destination (Join-Path $backupDirectory "scripts.json") -Force
    if (Test-Path -LiteralPath $ollamaScriptPath -PathType Leaf) {
        Copy-Item -LiteralPath $ollamaScriptPath -Destination (Join-Path $backupDirectory "Invoke-OllamaTypeWhisper.ps1") -Force
    }
    if (Test-Path -LiteralPath $pluginDirectory -PathType Container) {
        Copy-Item -LiteralPath $pluginDirectory -Destination (Join-Path $backupDirectory "com.local.codex-thread-output") -Recurse -Force
    }
    if (Test-Path -LiteralPath $helperShortcutPath -PathType Leaf) {
        Move-Item -LiteralPath $helperShortcutPath -Destination (Join-Path $backupDirectory "TypeWhisper Codex Hotkey.lnk") -Force
    }
}

function Install-OllamaCorrectionScript {
    # 日本語をWindows PowerShell 5.1が確実に解釈できるUTF-8 BOM付きで校正スクリプトを配置する。
    $scriptDirectory = Split-Path -Parent $ollamaScriptPath
    New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
    $scriptText = Get-Content -LiteralPath $ollamaScriptSourcePath -Raw -Encoding utf8
    $utf8WithBom = [System.Text.UTF8Encoding]::new($true)
    [System.IO.File]::WriteAllText($ollamaScriptPath, $scriptText, $utf8WithBom)
}

function Update-ScriptRunnerConfiguration {
    # Script Runnerの有効な校正処理をBOM付き配布スクリプトへ固定する。
    $scripts = Get-Content -LiteralPath $scriptRunnerConfigurationPath -Raw -Encoding utf8 | ConvertFrom-Json
    $correctionScripts = @($scripts | Where-Object { $_.name -eq "Ollama校正・要約" })
    if ($correctionScripts.Count -ne 1) {
        throw "Ollama校正・要約スクリプトを一意に取得できません。"
    }
    $correctionScripts[0].command = "& powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$ollamaScriptPath'"
    $correctionScripts[0].shell = "powershell"
    $correctionScripts[0].isEnabled = $true
    $scripts | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $scriptRunnerConfigurationPath -Encoding utf8
}

function Stop-LegacyHotkeyHelper {
    # 検証済み絶対パスで動作中の旧F13補助ツールだけを停止する。
    if (-not (Test-Path -LiteralPath $helperExecutablePath -PathType Leaf)) {
        return
    }
    $helperProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq "TypeWhisperCodexHotkey.exe" -and
        [string]::Equals($_.ExecutablePath, $helperExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)
    }
    foreach ($helperProcess in $helperProcesses) {
        Stop-Process -Id $helperProcess.ProcessId
    }
}

function Install-TypeWhisperOutputPlugin {
    # ローカルSDKで新しいAPI転送プラグインをビルドして既存IDへ上書きする。
    $dotNetExecutable = Get-DotNetExecutable
    Assert-DotNetTen -DotNetExecutable $dotNetExecutable
    & $dotNetExecutable build $pluginProject --configuration Release --property:TypeWhisperSdkPath=$typeWhisperSdkPath
    if ($LASTEXITCODE -ne 0) {
        throw "TypeWhisper output plugin build failed."
    }
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginBuildDirectory "TypeWhisper.TaskManagerOutput.dll") -Destination $pluginDirectory -Force
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $pluginProject) "manifest.json") -Destination $pluginDirectory -Force
}

function Update-TypeWhisperWorkflows {
    # Task ManagerのF13から呼ぶ校正ワークフローをF14へ割り当て、両出力を確認プラグインへ変更する。
    $workflows = Get-Content -Raw -Encoding utf8 $workflowPath | ConvertFrom-Json
    $cleanupWorkflow = @($workflows | Where-Object { $_.Name -eq "音声校正" })
    $summaryWorkflow = @($workflows | Where-Object { $_.Name -eq "音声要約" })
    if ($cleanupWorkflow.Count -ne 1) {
        throw "音声校正ワークフローを一意に取得できません。"
    }
    if ($summaryWorkflow.Count -ne 1) {
        throw "音声要約ワークフローを一意に取得できません。"
    }
    $cleanupWorkflow[0].Trigger.Hotkeys = @("F14")
    $cleanupWorkflow[0].Output.TargetActionPluginId = $pluginIdentifier
    $cleanupWorkflow[0].UpdatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    $summaryWorkflow[0].Output.TargetActionPluginId = $pluginIdentifier
    $summaryWorkflow[0].UpdatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    $workflows | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 $workflowPath
}

function Update-TypeWhisperHotkeys {
    # API制御を有効にし、手動操作用のF14開始とF15停止も予備経路として維持する。
    $settings = Get-Content -Raw -Encoding utf8 $typeWhisperSettingsPath | ConvertFrom-Json
    $toggleOnlyHotkeys = @($settings.toggleOnlyHotkeys)
    if ($toggleOnlyHotkeys -notcontains "F15") {
        $toggleOnlyHotkeys += "F15"
    }
    if ($settings.PSObject.Properties.Name -contains "toggleOnlyHotkeys") {
        $settings.toggleOnlyHotkeys = $toggleOnlyHotkeys
    }
    else {
        $settings | Add-Member -NotePropertyName toggleOnlyHotkeys -NotePropertyValue $toggleOnlyHotkeys
    }
    if ($settings.PSObject.Properties.Name -contains "apiServerEnabled") {
        $settings.apiServerEnabled = $true
    }
    else {
        $settings | Add-Member -NotePropertyName apiServerEnabled -NotePropertyValue $true
    }
    if ($settings.PSObject.Properties.Name -contains "apiServerPort") {
        $settings.apiServerPort = 8978
    }
    else {
        $settings | Add-Member -NotePropertyName apiServerPort -NotePropertyValue 8978
    }
    $settings | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 $typeWhisperSettingsPath
}

# 事前検査と変更対象表示を行い、WhatIfでは外部状態を変更せず終了する。
Assert-TypeWhisperIntegrationPrerequisites -RequireStopped (-not $WhatIf)
Show-TypeWhisperIntegrationTargets
if ($WhatIf) {
    Write-Output "WhatIf: TypeWhisper連携は変更されていません。"
    exit 0
}

# バックアップ後に旧補助ツールを停止し、新しい連携へ切り替える。
Backup-TypeWhisperIntegration
Stop-LegacyHotkeyHelper
Install-OllamaCorrectionScript
Update-ScriptRunnerConfiguration
Install-TypeWhisperOutputPlugin
Update-TypeWhisperHotkeys
Update-TypeWhisperWorkflows
Write-Output "TypeWhisper integration installed. Backup: $backupDirectory"
Write-Output "Task Managerを起動し、F13でTypeWhisper APIによる音声校正を開始してください。"
