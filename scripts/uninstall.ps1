$ErrorActionPreference = "Stop"

function Remove-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)
    # ユーザーPATHからインストール先だけを除外する。 ASCII.
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $pathEntries = @($currentPath -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not [string]::Equals($_.TrimEnd('\'), $DirectoryPath.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)
    })
    [Environment]::SetEnvironmentVariable("Path", ($pathEntries -join ';'), "User")
}

function Stop-InstalledTaskManager {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)
    # 削除対象と同じ絶対パスの常駐アプリだけを停止する。 ASCII.
    $runningProcesses = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq "TaskManager.exe" -and
        [string]::Equals($_.ExecutablePath, $ExecutablePath, [System.StringComparison]::OrdinalIgnoreCase)
    }
    foreach ($runningProcess in $runningProcesses) {
        Stop-Process -Id $runningProcess.ProcessId -Force
    }
}

# 実行ファイルとショートカットを削除し、ユーザーデータは保持する。 ASCII.
$installDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\TaskManager"))
$expectedDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs\TaskManager"))
if ($installDirectory -ne $expectedDirectory) {
    throw "Unexpected uninstall path: $installDirectory"
}

# 削除中の再起動とログオン後の起動失敗を防ぐため新旧の監視タスクを先に解除する。 ASCII.
$scheduledTaskNames = @("UniToDo Watchdog", "LocalTaskManager Watchdog")
foreach ($scheduledTaskName in $scheduledTaskNames) {
    Stop-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $scheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
}
Stop-InstalledTaskManager -ExecutablePath (Join-Path $installDirectory "TaskManager.exe")
$shortcutNames = @("UniToDo.lnk", "TaskManager.lnk")
foreach ($shortcutName in $shortcutNames) {
    $startupShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) $shortcutName
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) $shortcutName
    Remove-Item -LiteralPath $startupShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
}
Remove-UserPathEntry -DirectoryPath $installDirectory
if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}
Write-Output "UniToDo was removed. Data remains under $env:LOCALAPPDATA\TaskManager."
