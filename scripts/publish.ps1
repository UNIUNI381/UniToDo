$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

# 自己完結型アプリとCLIを同じ配布ディレクトリへ発行する。 ASCII.
$dotNetExecutable = Get-DotNetExecutable
Assert-DotNetTen -DotNetExecutable $dotNetExecutable
$publishDirectory = Join-Path $ProjectRoot "artifacts\publish\TaskManager"
Assert-SafeArtifactPath -TargetPath $publishDirectory
# OSが参照中のランタイムDLLを削除せず、同一配布先へ最新版を上書きする。 ASCII.
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
Push-Location $ProjectRoot
try {
    & $dotNetExecutable restore "src\TaskManager.App\TaskManager.App.csproj" --runtime win-x64 --property:SelfContained=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Application publish restore failed." }
    & $dotNetExecutable restore "src\TaskManager.Cli\TaskManager.Cli.csproj" --runtime win-x64 --property:SelfContained=true --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "CLI publish restore failed." }
    & $dotNetExecutable publish "src\TaskManager.App\TaskManager.App.csproj" --configuration Release --runtime win-x64 --self-contained true --no-restore --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Application publish failed." }
    & $dotNetExecutable publish "src\TaskManager.Cli\TaskManager.Cli.csproj" --configuration Release --runtime win-x64 --self-contained true --no-restore --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }
    Write-Output "Published to: $publishDirectory"
}
finally {
    Pop-Location
}
