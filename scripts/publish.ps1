$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

function Get-NuGetPackageRoot {
    # 選択中の.NET SDKが使用するNuGetグローバルパッケージ領域を取得する。
    param([Parameter(Mandatory = $true)][string]$DotNetExecutable)

    $packageRootOutput = @(& $DotNetExecutable nuget locals global-packages --list)
    if ($LASTEXITCODE -ne 0) {
        throw "The NuGet global package location could not be determined."
    }

    $packageRootLine = $packageRootOutput |
        Where-Object { $_ -match '^global-packages\s*:' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($packageRootLine)) {
        throw "The NuGet global package location was not present in dotnet output."
    }

    $separatorIndex = $packageRootLine.IndexOf(':')
    $packageRoot = $packageRootLine.Substring($separatorIndex + 1).Trim()
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "The NuGet global package directory was not found: $packageRoot"
    }

    return [System.IO.Path]::GetFullPath($packageRoot)
}

function Test-PackageLegalFileName {
    # NuGetパッケージが付属するライセンス・通知ファイル名か判定する。
    param([Parameter(Mandatory = $true)][string]$FileName)

    return $FileName -match '(?i)^(?:license|licence|notice|third[-_. ]party[-_. ]notices?|copying|copyright)(?:\..+)?$'
}

function Copy-PackageLegalFiles {
    # 実行時依存パッケージ付属のライセンスと通知全文をハッシュ索引付きで収録する。
    param(
        [Parameter(Mandatory = $true)][string]$DotNetExecutable,
        [Parameter(Mandatory = $true)][string]$LicenseDirectory
    )

    $packageRoot = Get-NuGetPackageRoot -DotNetExecutable $DotNetExecutable
    $dependencyManifestPath = Join-Path $ProjectRoot "licenses\dependencies.json"
    $dependencyManifest = Get-Content -Raw -LiteralPath $dependencyManifestPath | ConvertFrom-Json
    $packageLicenseRoot = Join-Path $LicenseDirectory "packages"
    New-Item -ItemType Directory -Path $packageLicenseRoot -Force | Out-Null

    $componentRecords = @()
    foreach ($component in @($dependencyManifest.components | Where-Object { $_.scope -eq "runtime" })) {
        $packageIdentifier = if ($component.name.StartsWith("runtimepack.", [System.StringComparison]::OrdinalIgnoreCase)) {
            $component.name.Substring("runtimepack.".Length)
        }
        else {
            $component.name
        }
        $packageVersion = [string]$component.version
        $packageDirectory = Join-Path (Join-Path $packageRoot $packageIdentifier.ToLowerInvariant()) $packageVersion.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
            throw "The licensed NuGet package directory was not found: $packageIdentifier/$packageVersion"
        }

        $legalFileRecords = @()
        $legalFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
            Where-Object { Test-PackageLegalFileName -FileName $_.Name } |
            Sort-Object FullName
        foreach ($legalFile in $legalFiles) {
            $relativePath = [System.IO.Path]::GetRelativePath($packageDirectory, $legalFile.FullName)
            $destinationPath = Join-Path (Join-Path (Join-Path $packageLicenseRoot $packageIdentifier) $packageVersion) $relativePath
            $destinationDirectory = Split-Path -Parent $destinationPath
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $legalFile.FullName -Destination $destinationPath -Force
            $legalFileRecords += [ordered]@{
                relativePath = $relativePath.Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
            }
        }

        $componentRecords += [ordered]@{
            name = [string]$component.name
            packageIdentifier = $packageIdentifier
            version = $packageVersion
            files = $legalFileRecords
        }
    }

    # 通知を付属する主要実行時パッケージでは通知全文の収録を必須とする。
    foreach ($requiredNoticePackage in @(
        "Microsoft.NETCore.App.Runtime.win-x64",
        "Microsoft.AspNetCore.App.Runtime.win-x64",
        "System.Management")) {
        $requiredComponent = $componentRecords |
            Where-Object { $_.packageIdentifier -eq $requiredNoticePackage } |
            Select-Object -First 1
        if ($null -eq $requiredComponent) {
            throw "A required notice-bearing package is absent from the runtime inventory: $requiredNoticePackage"
        }

        $noticeFiles = @($requiredComponent.files | Where-Object { $_.relativePath -match '(?i)(?:^|/)third[-_. ]party[-_. ]notices?(?:\..+)?$' })
        if ($noticeFiles.Count -eq 0) {
            throw "A package-provided third-party notice was not collected: $requiredNoticePackage"
        }
    }

    $legalFileIndex = [ordered]@{
        schemaVersion = 1
        components = $componentRecords
    }
    $legalFileIndex |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $LicenseDirectory "package-legal-files.json") -Encoding utf8
}

function Copy-PublishedLicenseFiles {
    # 独自ライセンス、パッケージ付属通知、発行SDK付属の.NETライセンスを発行物へ収録する。
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
    Copy-PackageLegalFiles -DotNetExecutable $DotNetExecutable -LicenseDirectory $licenseDirectory
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
