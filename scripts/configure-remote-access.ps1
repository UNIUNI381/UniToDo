[CmdletBinding(SupportsShouldProcess)]
param()

# 参加済みPCの情報から本人限定の接続設定を作り、Serveや管理画面の設定は変更しない。
$ErrorActionPreference = 'Stop'
$tailscaleExecutable = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (-not (Test-Path -LiteralPath $tailscaleExecutable)) { throw 'Tailscale is not installed.' }
$statusText = & $tailscaleExecutable status --json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Tailscale status. Run this script in an administrator terminal.' }
$connectionStatus = $statusText | ConvertFrom-Json
if ($connectionStatus.BackendState -ne 'Running') { throw 'Connect Tailscale before configuring remote access.' }
$machineName = ([string]$connectionStatus.Self.DNSName).TrimEnd('.')
$userIdentifier = [string]$connectionStatus.Self.UserID
$loginName = [string]$connectionStatus.User.$userIdentifier.LoginName
$machineAddresses = @($connectionStatus.Self.TailscaleIPs)
$machineAddress = @($machineAddresses | Where-Object { $_ -notlike '*:*' })[0]
if ($machineName -notmatch '^[a-zA-Z0-9-]+\.[a-zA-Z0-9-]+\.ts\.net$' -or
    [string]::IsNullOrWhiteSpace($loginName) -or $loginName -match '[\s,]' -or
    [string]::IsNullOrWhiteSpace($machineAddress)) { throw 'The current device must have a DNS name and a user identity.' }

# 個人識別子はリポジトリ外へ保存し、アプリの通常設定APIから変更できないようにする。
$dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TaskManager'
$configurationPath = Join-Path $dataDirectory 'remote-access.json'
$policyPath = Join-Path $dataDirectory 'tailscale-policy-proposal.json'
$configuration = [ordered]@{ serveOrigin = "https://$machineName"; allowedLogin = $loginName }
if (Test-Path -LiteralPath $configurationPath) {
    $existingConfiguration = Get-Content -Raw -LiteralPath $configurationPath | ConvertFrom-Json
    if ($existingConfiguration.serveOrigin -ne $configuration.serveOrigin -or $existingConfiguration.allowedLogin -ne $loginName) {
        throw 'Existing remote-access.json differs. Review it before replacing it.'
    }
}

# 新規の専用Tailnet向けに、本人からこのPCのHTTPSだけを許可する全体案を生成する。
$policy = [ordered]@{
    grants = @(@{ src = @($loginName); dst = $machineAddresses; ip = @('tcp:443') })
    tests = @(@{ src = $loginName; accept = @("${machineAddress}:443"); deny = @("${machineAddress}:22", "${machineAddress}:48120") })
}
if ($PSCmdlet.ShouldProcess($dataDirectory, 'Write UniToDo remote access settings and a Tailscale policy proposal')) {
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configurationPath -Encoding utf8
    $policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policyPath -Encoding utf8
    Write-Output "Remote access settings: $configurationPath"
    Write-Output "Policy proposal (review before applying): $policyPath"
    Write-Output 'Restart UniToDo after changing its settings. This script does not enable Serve, HTTPS, Funnel, or change grants.'
}
