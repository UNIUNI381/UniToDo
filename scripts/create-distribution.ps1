[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$')]
    [string]$DistributionName = "TaskManager-win-x64"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "common.ps1")

function Test-SkippedSourcePath {
    # allowlist内でもビルド生成物やローカル専用ディレクトリなら除外する。
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $excludedSegments = @(".git", ".codex", ".dotnet", ".vs", "artifacts", "bin", "obj", "TestResults", "node_modules", "packages")
    $pathSegments = @($RelativePath -split '[\\/]')
    foreach ($pathSegment in $pathSegments) {
        if ($excludedSegments -contains $pathSegment) {
            return $true
        }
    }

    $fileName = [System.IO.Path]::GetFileName($RelativePath)
    $excludedFileNames = @("credentials.json", "token.json", "secrets.json")
    $isEnvironmentFile = $fileName -eq ".env" -or
        ($fileName.StartsWith(".env.", [System.StringComparison]::OrdinalIgnoreCase) -and
            $fileName -notin @(".env.example", ".env.template"))
    $isLocalSettingsFile = $fileName -eq "appsettings.Local.json" -or
        $fileName.EndsWith(".Local.json", [System.StringComparison]::OrdinalIgnoreCase)
    if ($excludedFileNames -contains $fileName -or
        $fileName -match '(?i)^client_secret.*\.json$' -or
        $isEnvironmentFile -or
        $isLocalSettingsFile) {
        return $true
    }

    $excludedExtensions = @(
        ".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm", ".bak", ".backup", ".log", ".user", ".suo",
        ".pfx", ".p12", ".key", ".pem")
    foreach ($excludedExtension in $excludedExtensions) {
        if ($fileName.EndsWith($excludedExtension, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Copy-AllowedFile {
    # allowlistで指定された1ファイルを相対位置を保ってステージングへコピーする。
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "頒布に必要なファイルがありません: $SourcePath"
    }

    $destinationDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
}

function Copy-AllowedDirectory {
    # allowlistで指定されたディレクトリからローカル生成物を除いてコピーする。
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "頒布に必要なディレクトリがありません: $SourceDirectory"
    }

    $sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force
    foreach ($sourceFile in $sourceFiles) {
        if (($sourceFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "シンボリックリンク等は頒布へ含められません: $($sourceFile.FullName)"
        }

        $relativePath = [System.IO.Path]::GetRelativePath($SourceDirectory, $sourceFile.FullName)
        if (Test-SkippedSourcePath -RelativePath $relativePath) {
            continue
        }

        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath -Force
    }
}

function Assert-CodexDefaultIsEmpty {
    # 頒布ソースのCodexタスクID既定値が未設定であることを検証する。
    param([Parameter(Mandatory = $true)][string]$DistributionRoot)

    $constantFile = Join-Path $DistributionRoot "src\TaskManager.App\Domain\TaskConstants.cs"
    $constantText = Get-Content -Raw -LiteralPath $constantFile
    $emptyDefaultPattern = 'DefaultCodexThreadIdentifier\s*=\s*(?:string\.Empty|"")\s*;'
    if ($constantText -notmatch $emptyDefaultPattern) {
        throw "DefaultCodexThreadIdentifierが空ではありません。所有者固有のCodexタスクIDを除去してください。"
    }
}

function Assert-PublishedDependenciesCovered {
    # 発行済み依存関係と承認済みライセンス一覧が完全一致することを検証する。
    param(
        [Parameter(Mandatory = $true)][string]$DistributionRoot,
        [Parameter(Mandatory = $true)][string]$RuntimeDirectory
    )

    $dependencyManifestPath = Join-Path $DistributionRoot "licenses\dependencies.json"
    $dependencyManifest = Get-Content -Raw -LiteralPath $dependencyManifestPath | ConvertFrom-Json
    $approvedRuntimeDependencies = @(
        $dependencyManifest.components |
            Where-Object { $_.scope -eq "runtime" } |
            ForEach-Object { "$($_.name)/$($_.version)" } |
            Sort-Object -Unique)

    $publishedDependencies = @(
        Get-ChildItem -LiteralPath $RuntimeDirectory -Filter "*.deps.json" -File |
            ForEach-Object {
                $dependencyData = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
                $dependencyData.libraries.PSObject.Properties |
                    Where-Object { $_.Value.type -in @("package", "runtimepack") } |
                    ForEach-Object { $_.Name }
            } |
            Sort-Object -Unique)

    $unlistedDependencies = @($publishedDependencies | Where-Object { $_ -notin $approvedRuntimeDependencies })
    if ($unlistedDependencies.Count -gt 0) {
        throw "頒布ライセンス一覧にない発行依存関係があります:`n$($unlistedDependencies -join [Environment]::NewLine)"
    }

    $missingDependencies = @($approvedRuntimeDependencies | Where-Object { $_ -notin $publishedDependencies })
    if ($missingDependencies.Count -gt 0) {
        throw "頒布ライセンス一覧の実行時依存関係が発行物にありません:`n$($missingDependencies -join [Environment]::NewLine)"
    }
}

function Assert-DistributionSafe {
    # ステージング全体に個人データや認証情報が混入していないことを検査する。
    param([Parameter(Mandatory = $true)][string]$DistributionRoot)

    $forbiddenDirectoryNames = @(".git", ".codex", ".dotnet", ".vs", "artifacts", "bin", "obj", "TestResults", "node_modules", "packages")
    $forbiddenDirectories = Get-ChildItem -LiteralPath $DistributionRoot -Recurse -Directory -Force | Where-Object {
        $forbiddenDirectoryNames -contains $_.Name
    }
    if ($forbiddenDirectories.Count -gt 0) {
        $forbiddenPaths = $forbiddenDirectories.FullName -join [Environment]::NewLine
        throw "頒布禁止ディレクトリが見つかりました:`n$forbiddenPaths"
    }

    # ソースコード内のData名前空間は許可し、頒布ルート直下の実データ領域だけを禁止する。
    foreach ($forbiddenRootDirectory in @("data", "backups", "tokens")) {
        $forbiddenRootPath = Join-Path $DistributionRoot $forbiddenRootDirectory
        if (Test-Path -LiteralPath $forbiddenRootPath) {
            throw "頒布禁止ディレクトリが見つかりました: $forbiddenRootPath"
        }
    }

    $forbiddenFiles = Get-ChildItem -LiteralPath $DistributionRoot -Recurse -File -Force | Where-Object {
        $fileName = $_.Name
        $isEnvironmentFile = $fileName -eq ".env" -or
            ($fileName.StartsWith(".env.", [System.StringComparison]::OrdinalIgnoreCase) -and
                $fileName -notin @(".env.example", ".env.template"))
        $fileName -in @("credentials.json", "token.json", "secrets.json") -or
        $fileName -match '(?i)^client_secret.*\.json$' -or
        $fileName -eq "appsettings.Local.json" -or
        $fileName.EndsWith(".Local.json", [System.StringComparison]::OrdinalIgnoreCase) -or
        $isEnvironmentFile -or
        $fileName -match '(?i)\.(?:db|sqlite|sqlite3|db-wal|db-shm|bak|backup|log|user|suo|pfx|p12|key|pem)$'
    }
    if ($forbiddenFiles.Count -gt 0) {
        $forbiddenPaths = $forbiddenFiles.FullName -join [Environment]::NewLine
        throw "頒布禁止ファイルが見つかりました:`n$forbiddenPaths"
    }

    # 実値入りの秘密鍵やOAuth資格情報に特徴的な内容をテキストファイルから検出する。
    $textExtensions = @(".cs", ".html", ".js", ".json", ".md", ".props", ".ps1", ".targets", ".txt", ".xml", ".yaml", ".yml")
    $sensitiveContentFiles = Get-ChildItem -LiteralPath $DistributionRoot -Recurse -File -Force | Where-Object {
        $textExtensions -contains $_.Extension
    } | Select-String -Pattern '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|"refresh_token"\s*:\s*"[^"\s]+"|"client_secret"\s*:\s*"[^"\s]+"' -List
    if ($sensitiveContentFiles.Count -gt 0) {
        $sensitivePaths = $sensitiveContentFiles.Path -join [Environment]::NewLine
        throw "資格情報らしい内容が見つかりました:`n$sensitivePaths"
    }

    Assert-CodexDefaultIsEmpty -DistributionRoot $DistributionRoot
}

# テストと自己完結型win-x64発行を成功させてから頒布物を組み立てる。
& (Join-Path $PSScriptRoot "test.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "頒布前テストに失敗しました。"
}
& (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "頒布用ランタイムの発行に失敗しました。"
}

# artifacts配下の検証済み専用領域だけを安全に再作成する。
$distributionDirectory = Join-Path $ProjectRoot "artifacts\distribution"
$stagingDirectory = Join-Path $distributionDirectory $DistributionName
$archivePath = Join-Path $distributionDirectory "$DistributionName.zip"
Assert-SafeArtifactPath -TargetPath $distributionDirectory
Assert-SafeArtifactPath -TargetPath $stagingDirectory
Assert-SafeArtifactPath -TargetPath $archivePath
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

# ソース、設計資料、Skill、受取人用ファイルを明示allowlistからコピーする。
$allowedRootFiles = @(
    ".gitignore",
    "AGENTS.md",
    "Directory.Build.props",
    "global.json",
    "LICENSE",
    "README.md",
    "TaskManager.slnx",
    "THIRD-PARTY-NOTICES.md",
    "Install.ps1",
    "最初にお読みください.txt"
)
foreach ($allowedRootFile in $allowedRootFiles) {
    Copy-AllowedFile -SourcePath (Join-Path $ProjectRoot $allowedRootFile) -DestinationPath (Join-Path $stagingDirectory $allowedRootFile)
}

$allowedRootDirectories = @(".agents", "docs", "integrations", "licenses", "scripts", "src", "tests")
foreach ($allowedRootDirectory in $allowedRootDirectories) {
    Copy-AllowedDirectory -SourceDirectory (Join-Path $ProjectRoot $allowedRootDirectory) -DestinationDirectory (Join-Path $stagingDirectory $allowedRootDirectory)
}

# 発行済みAppとCLIを同一runtimeディレクトリへコピーする。
$publishDirectory = Join-Path $ProjectRoot "artifacts\publish\TaskManager"
$runtimeDirectory = Join-Path $stagingDirectory "runtime"
foreach ($requiredExecutable in @("TaskManager.exe", "taskctl.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredExecutable) -PathType Leaf)) {
        throw "発行済みランタイムに必要な実行ファイルがありません: $requiredExecutable"
    }
}
Copy-AllowedDirectory -SourceDirectory $publishDirectory -DestinationDirectory $runtimeDirectory

# 頒布ルートでも確認できるよう、発行物に収録済みの.NETライセンス原文を配置する。
$runtimeLicenseDirectory = Join-Path $runtimeDirectory "licenses"
Copy-AllowedFile -SourcePath (Join-Path $runtimeLicenseDirectory "Microsoft-DotNet-Library-License.txt") -DestinationPath (Join-Path $stagingDirectory "licenses\Microsoft-DotNet-Library-License.txt")
Copy-AllowedFile -SourcePath (Join-Path $runtimeLicenseDirectory "Microsoft-DotNet-ThirdPartyNotices.txt") -DestinationPath (Join-Path $stagingDirectory "licenses\Microsoft-DotNet-ThirdPartyNotices.txt")

Assert-PublishedDependenciesCovered -DistributionRoot $stagingDirectory -RuntimeDirectory $runtimeDirectory

# 必須Skillと初回案内を確認し、個人データ検査を通過した内容だけをZIP化する。
$requiredDistributionFiles = @(
    ".agents\skills\manage-local-tasks\SKILL.md",
    ".agents\skills\manage-local-tasks\scripts\invoke-taskctl.ps1",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "licenses\dependencies.json",
    "licenses\Apache-2.0.txt",
    "licenses\DotNet-MIT-LICENSE.txt",
    "licenses\Newtonsoft.Json-LICENSE.md",
    "licenses\Microsoft-DotNet-Library-License.txt",
    "licenses\Microsoft-DotNet-ThirdPartyNotices.txt",
    "最初にお読みください.txt",
    "runtime\TaskManager.exe",
    "runtime\taskctl.exe",
    "runtime\licenses\LICENSE",
    "runtime\licenses\THIRD-PARTY-NOTICES.md",
    "runtime\licenses\Microsoft-DotNet-Library-License.txt",
    "runtime\licenses\Microsoft-DotNet-ThirdPartyNotices.txt"
)
foreach ($requiredDistributionFile in $requiredDistributionFiles) {
    $requiredDistributionPath = Join-Path $stagingDirectory $requiredDistributionFile
    if (-not (Test-Path -LiteralPath $requiredDistributionPath -PathType Leaf)) {
        throw "頒布物に必要なファイルがありません: $requiredDistributionFile"
    }
}
Assert-DistributionSafe -DistributionRoot $stagingDirectory

# .agentsを含むステージング直下の全内容を1つのZIPへ圧縮する。
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stagingDirectory,
    $archivePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Output "頒布ZIPを作成しました: $archivePath"
