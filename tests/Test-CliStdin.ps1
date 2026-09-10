param([Parameter(Mandatory = $true)][string]$ExecutablePath)

# 起動済みUniToDoに対し、不正JSONの解析位置で日本語パイプ入力を検証する。更新APIには到達しない。
$ErrorActionPreference = 'Continue'
$previousOutputEncoding = $OutputEncoding
try {
    foreach ($includePreamble in @($false, $true)) {
        # BOMの有無にかかわらず日本語がUTF-8のバイト数で解析されることを確認する。
        $OutputEncoding = New-Object System.Text.UTF8Encoding($includePreamble)
        $jsonText = '{"title":"日本語"}'
        $expectedPosition = [Text.Encoding]::UTF8.GetByteCount($jsonText)
        $errorText = (($jsonText + 'x') | & $ExecutablePath update encoding-probe-unused --file - --json 2>&1 | Out-String -Width 4096)
        if ($LASTEXITCODE -ne 1 -or $errorText -notmatch "BytePositionInLine:\s*$expectedPosition") {
            throw "UTF-8 stdin probe failed: $errorText"
        }
    }
    Write-Output "PASS UTF-8 stdin with/without BOM: PowerShell $($PSVersionTable.PSVersion.Major)"
}
finally {
    # 呼出し元の外部プロセス向け文字コードを復元する。
    $OutputEncoding = $previousOutputEncoding
}
exit 0
