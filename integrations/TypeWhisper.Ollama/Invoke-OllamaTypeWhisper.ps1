# TypeWhisperの文字起こしをOllama固有APIで校正または要約する。

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# Script Runnerとの標準入出力をUTF-8で統一する。
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

# 音声校正用の指示文を保持する。
$cleanupPrompt = @'
次の音声認識結果を校正してください。
フィラー(「えっと、あー、あれ」など）、言い直し、不要な反復、句読点、文脈に不適な誤字と誤変換だけを修正してください。
固有名詞、数値、日時、単位、ID、URL、ファイルパス、コード、否定、条件、依頼内容は変更しないでください。
要約、補足、回答、前置き、Markdownは禁止です。必ず校正後の本文だけを返してください。

原文:
'@

# 音声要約用の指示文を保持する。
$summaryPrompt = @'
次の音声認識結果を、操作に必要な条件を残した短い指示文にしてください。
数値、期限、日時、否定、例外、ID、URL、ファイルパス、コード、依頼内容は必ず保持してください。
推測、補足、回答、前置き、Markdownは禁止です。変換後の本文だけを返してください。

原文:
'@

function Test-OllamaModelReady {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory = $true)]
        [string]$ModelName
    )
    # API応答と実行中モデル一覧を確認し、対象モデルがロード済みの場合だけ真を返す。
    $tagsResponse = $null
    $processResponse = $null
    try {
        $tagsResponse = $HttpClient.GetAsync(
            'http://127.0.0.1:11434/api/tags').GetAwaiter().GetResult()
        if (-not $tagsResponse.IsSuccessStatusCode) {
            return $false
        }
        $processResponse = $HttpClient.GetAsync(
            'http://127.0.0.1:11434/api/ps').GetAwaiter().GetResult()
        if (-not $processResponse.IsSuccessStatusCode) {
            return $false
        }
        $processBytes = $processResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $processJson = [System.Text.Encoding]::UTF8.GetString($processBytes) | ConvertFrom-Json
        $loadedModels = @($processJson.models)
        return @($loadedModels | Where-Object {
            [string]::Equals([string]$_.name, $ModelName, [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals([string]$_.model, $ModelName, [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
    }
    catch {
        return $false
    }
    finally {
        if ($tagsResponse) {
            $tagsResponse.Dispose()
        }
        if ($processResponse) {
            $processResponse.Dispose()
        }
    }
}

function Wait-OllamaModelReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ModelName,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )
    # 録音停止後の本文を渡す前に、Ollama APIと対象モデルのロード完了を一定時間待つ。
    $readinessClient = [System.Net.Http.HttpClient]::new()
    $readinessClient.Timeout = [TimeSpan]::FromSeconds(2)
    $readinessDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    try {
        while ([DateTimeOffset]::UtcNow -lt $readinessDeadline) {
            if (Test-OllamaModelReady -HttpClient $readinessClient -ModelName $ModelName) {
                return
            }
            Start-Sleep -Milliseconds 250
        }
    }
    finally {
        $readinessClient.Dispose()
    }
    throw "Ollama校正モデルのロードを${TimeoutSeconds}秒以内に確認できませんでした。"
}

function Invoke-OllamaProcessing {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Instruction,
        [Parameter(Mandatory = $true)]
        [string]$SourceText,
        [Parameter(Mandatory = $true)]
        [int]$TokenLimit
    )
    # 対象モデルのロード完了後にだけ、Ollama固有の生成APIへ原文を送る。
    $modelName = 'qwen3-typewhisper:latest'
    Wait-OllamaModelReady -ModelName $modelName -TimeoutSeconds 45
    $requestBody = @{
        model = $modelName
        prompt = $Instruction + [Environment]::NewLine + $SourceText
        stream = $false
        keep_alive = '5m'
        options = @{
            temperature = 0
            num_predict = $TokenLimit
        }
    } | ConvertTo-Json -Depth 5 -Compress

    $httpClient = [System.Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(45)

    try {
        # 日本語本文をUTF-8 JSONとしてループバックAPIへ送る。
        $requestContent = [System.Net.Http.StringContent]::new(
            $requestBody,
            [System.Text.Encoding]::UTF8,
            'application/json')
        $responseMessage = $httpClient.PostAsync(
            'http://127.0.0.1:11434/api/generate',
            $requestContent).GetAwaiter().GetResult()
        [void]$responseMessage.EnsureSuccessStatusCode()

        $responseBytes = $responseMessage.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $responseJson = [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
        return ([string]$responseJson.response).Trim()
    }
    finally {
        $httpClient.Dispose()
    }
}

# TypeWhisperから渡された文字起こし本文を取得する。
$sourceText = [Console]::In.ReadToEnd()
$profileName = $env:TYPEWHISPER_PROFILE

if ([string]::IsNullOrWhiteSpace($sourceText)) {
    exit 0
}

try {
    # ワークフロー名に応じて、Ollamaへ送る指示文を選択する。
    switch ($profileName) {
        '音声校正' {
            $resultText = Invoke-OllamaProcessing -Instruction $cleanupPrompt -SourceText $sourceText -TokenLimit 384
            break
        }
        '音声要約' {
            $resultText = Invoke-OllamaProcessing -Instruction $summaryPrompt -SourceText $sourceText -TokenLimit 256
            break
        }
        default {
            $resultText = $sourceText
            break
        }
    }

    # 空応答時は原文を維持し、入力先へ空文字を挿入しない。
    if ([string]::IsNullOrWhiteSpace($resultText)) {
        $resultText = $sourceText
    }
}
catch {
    # API障害やタイムアウト時は原文を返して音声入力を失わない。
    [Console]::Error.WriteLine($_.Exception.Message)
    $resultText = $sourceText
}

# Script Runnerへ変換済み本文だけを返す。
[Console]::Out.Write($resultText)
