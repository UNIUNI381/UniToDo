[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [switch]$OverwriteCodexSkill,
    [switch]$SkipCodexSkill,
    [switch]$DoNotStart
)

$ErrorActionPreference = "Stop"

function New-TaskManagerShortcut {
    # Task Managerを起動するWindowsショートカットを作成する。
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
    $scheduledTaskName = "LocalTaskManager Watchdog"
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
        -Description "Local Task Manager watchdog" `
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
$repositorySkill = Join-Path $distributionRoot ".agents\skills\manage-local-tasks"
foreach ($requiredPath in @($runtimeDirectory, $applicationSource, $commandLineSource, $projectLicensePath, $thirdPartyNoticePath, $dotNetLicensePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "頒布物に必要なファイルがありません: $requiredPath"
    }
}

# インストール前に独自部分と同梱.NETランタイムへ別の条件が適用されることを表示する。
Write-Output "Task Manager独自部分: MIT License - uniuni (https://x.com/lept_on)"
Write-Output "第三者コンポーネント: $thirdPartyNoticePath"
Write-Output "同梱.NETランタイム: $dotNetLicensePath"
Write-Output "続行して本ソフトウェアを使用する場合、これらのライセンス条件が適用されます。"

if (-not $SkipCodexSkill -and -not (Test-Path -LiteralPath (Join-Path $repositorySkill "SKILL.md"))) {
    throw "頒布物にmanage-local-tasks Skillがありません: $repositorySkill"
}

# 発行済みランタイムをユーザー単位のプログラム領域へ配置する。
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$installDirectory = Join-Path $localApplicationData "Programs\TaskManager"
$applicationExecutable = Join-Path $installDirectory "TaskManager.exe"
if ($PSCmdlet.ShouldProcess($installDirectory, "Task Managerランタイムをインストール")) {
    # 更新中にWindows側の再起動が割り込まないよう登録済みタスクを先に停止する。
    Stop-ScheduledTask -TaskName "LocalTaskManager Watchdog" -ErrorAction SilentlyContinue
    Stop-InstalledTaskManager -ExecutablePath $applicationExecutable
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Get-ChildItem -Force -LiteralPath $runtimeDirectory | Copy-Item -Destination $installDirectory -Recurse -Force
}

# デスクトップショートカットを作成し、旧スタートアップ登録を整理する。
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$startupShortcut = Join-Path $startupDirectory "TaskManager.lnk"
$desktopShortcut = Join-Path $desktopDirectory "TaskManager.lnk"
if ((Test-Path -LiteralPath $startupShortcut) -and $PSCmdlet.ShouldProcess($startupShortcut, "旧スタートアップショートカットを削除")) {
    Remove-Item -LiteralPath $startupShortcut -Force
}
if ($PSCmdlet.ShouldProcess($desktopShortcut, "デスクトップショートカットを作成")) {
    New-TaskManagerShortcut -ShortcutPath $desktopShortcut -ExecutablePath $applicationExecutable
}
if ($PSCmdlet.ShouldProcess("LocalTaskManager Watchdog", "監視親のログオンタスクを登録")) {
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

# 別の作業フォルダーでも使えるようrepo内Skillをユーザー領域へコピーする。
if (-not $SkipCodexSkill) {
    $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $userSkillRoot = Join-Path $userProfileDirectory ".agents\skills"
    $userSkillDestination = Join-Path $userSkillRoot "manage-local-tasks"
    Assert-SafeSkillDestination -SkillRoot $userSkillRoot -SkillDestination $userSkillDestination
    $normalizedRepositorySkill = [System.IO.Path]::GetFullPath($repositorySkill).TrimEnd('\')
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
        Copy-Item -LiteralPath $repositorySkill -Destination $userSkillDestination -Recurse -Force
    }
}

# インストール後にアプリを起動し、次に行う疎通確認を案内する。
if (-not $DoNotStart -and $PSCmdlet.ShouldProcess($applicationExecutable, "Task Managerを起動")) {
    Start-ScheduledTask -TaskName "LocalTaskManager Watchdog"
}

if ($WhatIfPreference) {
    Write-Output "事前確認が完了しました。上記の対象に問題がなければ -WhatIf を外して実行してください。"
}
else {
    Write-Output "インストール先: $installDirectory"
    Write-Output "新しく端末を開いた後、taskctl now --json で疎通を確認してください。"
}
