$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

# .NET 10 SDKを選択してロック済み依存関係から全プロジェクトをビルドする。 ASCII.
$dotNetExecutable = Get-DotNetExecutable
Assert-DotNetTen -DotNetExecutable $dotNetExecutable
Push-Location $ProjectRoot
try {
    & $dotNetExecutable restore "TaskManager.slnx" --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed." }
    & $dotNetExecutable build "TaskManager.slnx" --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
finally {
    Pop-Location
}
