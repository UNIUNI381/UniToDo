param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$TaskArguments
)

$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

# インストール済み、発行済み、開発ビルドの順にtaskctlを実行する。 ASCII.
$installedExecutable = Join-Path $env:LOCALAPPDATA "Programs\TaskManager\taskctl.exe"
$publishedExecutable = Join-Path $ProjectRoot "artifacts\publish\TaskManager\taskctl.exe"
if (Test-Path -LiteralPath $installedExecutable) {
    & $installedExecutable @TaskArguments
    exit $LASTEXITCODE
}
if (Test-Path -LiteralPath $publishedExecutable) {
    & $publishedExecutable @TaskArguments
    exit $LASTEXITCODE
}
$developmentAssembly = Join-Path $ProjectRoot "src\TaskManager.Cli\bin\Debug\net10.0-windows\win-x64\taskctl.dll"
if (-not (Test-Path -LiteralPath $developmentAssembly)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}
$dotNetExecutable = Get-DotNetExecutable
& $dotNetExecutable exec $developmentAssembly @TaskArguments
exit $LASTEXITCODE
