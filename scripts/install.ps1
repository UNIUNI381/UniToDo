param([switch]$SkipPublish)

$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

function New-TaskManagerShortcut {
    # UniToDoを起動するWindowsショートカットを作成する。 ASCII.
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [string]$Arguments = ""
    )
    # サンドボックスの既知フォルダー変換を避けるためLOCALAPPDATAを展開可能な表記で保存する。 ASCII.
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

    # Windows標準のショートカットを指定先へ作成する。 ASCII.
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $shortcutTargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $shortcutTargetPath
    # 実行ファイルに埋め込んだアプリアイコンをスタートアップ表示にも明示する。 ASCII.
    $shortcut.IconLocation = "$shortcutTargetPath,0"
    $shortcut.Save()
}

function Register-TaskManagerWatchdog {
    # 監視親をCodexの実行ジョブから独立させ、異常終了時はWindows側から再起動する。 ASCII.
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    # ログオン起動、外側の再起動、重複防止をWindowsタスクへ設定する。 ASCII.
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

function Add-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)
    # taskctlを新しい端末から呼べるようユーザーPATHへ重複なく追加する。 ASCII.
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $pathEntries = @($currentPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if (-not ($pathEntries | Where-Object { [string]::Equals($_.TrimEnd('\'), $DirectoryPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase) })) {
        $updatedPath = (($pathEntries + $DirectoryPath) -join ';')
        [Environment]::SetEnvironmentVariable("Path", $updatedPath, "User")
    }
}

function Stop-InstalledTaskManager {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)
    # 更新中のファイルロックを避けるため同じ絶対パスのアプリだけを停止する。 ASCII.
    $runningProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq "TaskManager.exe" -and
        [string]::Equals($_.ExecutablePath, $ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)
    }
    # 監視親を先に止め、更新中に子プロセスが再起動しないようにする。 ASCII.
    $watchdogProcesses = @($runningProcesses | Where-Object { $_.CommandLine -match '(?:^|\s)--watchdog(?:\s|$)' })
    $applicationProcesses = @($runningProcesses | Where-Object { $_.ProcessId -notin $watchdogProcesses.ProcessId })
    foreach ($runningProcess in @($watchdogProcesses + $applicationProcesses)) {
        Stop-Process -Id $runningProcess.ProcessId -Force
    }
}

# 最新配布物を作成してユーザー領域へ上書きインストールする。 ASCII.
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1")
}
$publishDirectory = Join-Path $ProjectRoot "artifacts\publish\TaskManager"
$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\TaskManager"
$applicationExecutable = Join-Path $installDirectory "TaskManager.exe"
# 更新中にWindows側の再起動が割り込まないよう新旧の登録済みタスクを停止し、旧名だけを解除する。 ASCII.
$scheduledTaskName = "UniToDo Watchdog"
$legacyScheduledTaskName = "LocalTaskManager Watchdog"
foreach ($registeredTaskName in @($scheduledTaskName, $legacyScheduledTaskName)) {
    Stop-ScheduledTask -TaskName $registeredTaskName -ErrorAction SilentlyContinue
}
Unregister-ScheduledTask -TaskName $legacyScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
Stop-InstalledTaskManager -ExecutablePath $applicationExecutable
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $installDirectory -Recurse -Force
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$legacyShortcutName = "TaskManager.lnk"
$applicationShortcutName = "UniToDo.lnk"
foreach ($startupShortcutName in @($legacyShortcutName, $applicationShortcutName)) {
    $startupShortcut = Join-Path $startupDirectory $startupShortcutName
    if (Test-Path -LiteralPath $startupShortcut) {
        # スタートアップショートカットとの二重起動を避け、タスクスケジューラへ一本化する。 ASCII.
        Remove-Item -LiteralPath $startupShortcut -Force
    }
}
$legacyDesktopShortcut = Join-Path $desktopDirectory $legacyShortcutName
Remove-Item -LiteralPath $legacyDesktopShortcut -Force -ErrorAction SilentlyContinue
New-TaskManagerShortcut -ShortcutPath (Join-Path $desktopDirectory $applicationShortcutName) -ExecutablePath $applicationExecutable
Register-TaskManagerWatchdog -ExecutablePath $applicationExecutable
Add-UserPathEntry -DirectoryPath $installDirectory
Start-ScheduledTask -TaskName $scheduledTaskName
Write-Output "UniToDo installed to: $installDirectory"
Write-Output "taskctl is available from newly opened terminals."
