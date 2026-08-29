$ErrorActionPreference = "Stop"

# プロジェクトの絶対パスは呼出元スクリプトが設定する。 ASCII.
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    throw "ProjectRoot must be set before loading common.ps1."
}

function Get-DotNetExecutable {
    # ワークスペース内SDKを優先し、なければPATH上のdotnetを返す。 ASCII.
    $localExecutable = Join-Path $ProjectRoot ".dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localExecutable) {
        return $localExecutable
    }
    $systemCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $systemCommand) {
        throw ".NET 10 SDK was not found. Follow README.md to install it."
    }
    return $systemCommand.Source
}

function Assert-DotNetTen {
    param([Parameter(Mandatory = $true)][string]$DotNetExecutable)
    # global.jsonの範囲で選択されたSDKが10系であることを検証する。 ASCII.
    $sdkVersion = & $DotNetExecutable --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
        throw ".NET 10 SDK is required. Selected version: $sdkVersion"
    }
}

function Assert-SafeArtifactPath {
    param([Parameter(Mandatory = $true)][string]$TargetPath)
    # 再作成対象がプロジェクト内artifacts配下であることを絶対パスで検証する。 ASCII.
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot "artifacts"))
    $resolvedTarget = [System.IO.Path]::GetFullPath($TargetPath)
    if (-not $resolvedTarget.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Only paths under the artifacts directory can be recreated: $resolvedTarget"
    }
}
