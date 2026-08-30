$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

function Copy-PublishedLicenseFiles {
    # 独自ライセンス、第三者一覧、発行SDK付属の.NETライセンスを発行物へ収録する。 ASCII.
    param(
        [Parameter(Mandatory = $true)][string]$DotNetExecutable,
        [Parameter(Mandatory = $true)][string]$PublishDirectory
    )

    $licenseDirectory = Join-Path $PublishDirectory "licenses"
    Assert-SafeArtifactPath -TargetPath $licenseDirectory
    if (Test-Path -LiteralPath $licenseDirectory) {
        Remove-Item -LiteralPath $licenseDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "licenses") -File |
        Copy-Item -Destination $licenseDirectory -Force
    Copy-Item -LiteralPath (Join-Path $ProjectRoot "LICENSE") -Destination (Join-Path $licenseDirectory "LICENSE") -Force
    Copy-Item -LiteralPath (Join-Path $ProjectRoot "THIRD-PARTY-NOTICES.md") -Destination (Join-Path $licenseDirectory "THIRD-PARTY-NOTICES.md") -Force

    $dotNetDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($DotNetExecutable))
    $dotNetLicenseSource = Join-Path $dotNetDirectory "LICENSE.txt"
    $dotNetNoticeSource = Join-Path $dotNetDirectory "ThirdPartyNotices.txt"
    foreach ($requiredDotNetFile in @($dotNetLicenseSource, $dotNetNoticeSource)) {
        if (-not (Test-Path -LiteralPath $requiredDotNetFile -PathType Leaf)) {
            throw "The selected .NET SDK license file was not found: $requiredDotNetFile"
        }
    }

    Copy-Item -LiteralPath $dotNetLicenseSource -Destination (Join-Path $licenseDirectory "Microsoft-DotNet-Library-License.txt") -Force
    Copy-Item -LiteralPath $dotNetNoticeSource -Destination (Join-Path $licenseDirectory "Microsoft-DotNet-ThirdPartyNotices.txt") -Force
}

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
    Copy-PublishedLicenseFiles -DotNetExecutable $dotNetExecutable -PublishDirectory $publishDirectory
    Write-Output "Published to: $publishDirectory"
}
finally {
    Pop-Location
}
