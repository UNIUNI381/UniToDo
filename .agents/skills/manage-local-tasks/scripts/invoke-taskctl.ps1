param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TaskArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-TaskControlExecutable {
    # インストール先とPATHからtaskctl実行ファイルを安全に探索する。
    $installedExecutable = Join-Path $env:LOCALAPPDATA "Programs\TaskManager\taskctl.exe"
    if (Test-Path -LiteralPath $installedExecutable) {
        return $installedExecutable
    }

    $availableCommand = Get-Command "taskctl.exe" -ErrorAction SilentlyContinue
    if ($null -ne $availableCommand) {
        return $availableCommand.Source
    }

    throw "taskctl was not found. Install Task Manager first."
}

# 任意の作業フォルダーからインストール済みCLIを呼び出して終了コードを維持する。
$taskControlExecutable = Find-TaskControlExecutable
try {
    & $taskControlExecutable @TaskArguments
    exit $LASTEXITCODE
}
catch {
    # サンドボックスの実行拒否を限定的な権限昇格で再実行できる診断へ変換する。
    $errorMessage = $_.Exception.Message
    if ($errorMessage.IndexOf("Access is denied", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $errorMessage.IndexOf("アクセスが拒否", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        [Console]::Error.WriteLine(
            "taskctl execution was denied. Re-run this same wrapper command with narrowly scoped elevated sandbox permission.")
        exit 126
    }
    throw
}

