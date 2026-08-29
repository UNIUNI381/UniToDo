$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

# ビルド、実行テスト、静的検査を順に実行する。 ASCII.
& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { throw "Build verification failed." }
$dotNetExecutable = Get-DotNetExecutable
Push-Location $ProjectRoot
try {
    & $dotNetExecutable run --project "tests\TaskManager.Tests\TaskManager.Tests.csproj" --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    & (Join-Path $PSScriptRoot "verify.ps1")
}
finally {
    Pop-Location
}
