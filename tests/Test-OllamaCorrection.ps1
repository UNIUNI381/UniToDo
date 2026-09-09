param([switch]$LiveModel, [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
# 校正スクリプトの定義部分だけを読み、標準入力を待たずに検証する。
$scriptPath = Join-Path $projectRoot 'integrations/TypeWhisper.Ollama/Invoke-OllamaTypeWhisper.ps1'
$scriptText = Get-Content -Raw -Encoding utf8 $scriptPath
$definitionEnd = $scriptText.IndexOf('# TypeWhisperから渡された文字起こし本文を取得する。')
. ([ScriptBlock]::Create($scriptText.Substring(0, $definitionEnd)))

# 除去対象と、返答・引用・単語内の文字を保持する境界を確認する。
$noiseCases = @(
    @('えっと、保存してください。はい。', '保存してください。'),
    @('えーっと、あー、保存してください。 はい、はい。', '保存してください。'),
    @('保存してください。えーと、確認してください。はい。', '保存してください。確認してください。'),
    @('はい、参加します。', 'はい、参加します。'),
    @('はい。', 'はい。'),
    @('返事は、はい。', '返事は、はい。'),
    @('「保存してください。はい。」と読みます。', '「保存してください。はい。」と読みます。'),
    @('「えっと」という語を削除してください。', '「えっと」という語を削除してください。'),
    @('あの資料とはいからな服を確認してください。', 'あの資料とはいからな服を確認してください。')
)
foreach ($testCase in $noiseCases) {
    $actualText = Remove-ObviousSpeechNoise -Text $testCase[0]
    if ($actualText -cne $testCase[1]) {
        throw "音声ノイズ除去の不一致: $($testCase[0]) -> $actualText"
    }
}
Write-Output "Speech noise tests: $($noiseCases.Count) passed."
# 空応答と引用の欠落で原文へ戻り、引用が保たれた通常の校正は採用することを確認する。
if ((Complete-OllamaCorrection -SourceText '「はい」と答えてください。' -ResultText 'はい') -cne '「はい」と答えてください。') {
    throw '引用欠落時に原文が保持されません。'
}
if ((Complete-OllamaCorrection -SourceText '確認してください。' -ResultText '') -cne '確認してください。') {
    throw '空応答時に原文が保持されません。'
}
if ((Complete-OllamaCorrection -SourceText 'えっと、「設定」を開いてください。' -ResultText '「設定」を開いてください。') -cne '「設定」を開いてください。') {
    throw '引用を保った校正が採用されません。'
}
if (-not $LiveModel) { return }

# 明示した場合だけローカルモデルをロードし、実データを使わず合成例で品質を測る。
$loadBody = @{ model = 'qwen3-typewhisper:latest'; stream = $false; keep_alive = '5m' } | ConvertTo-Json
Invoke-RestMethod 'http://127.0.0.1:11434/api/generate' -Method Post -Body $loadBody -ContentType 'application/json' | Out-Null
$qualityCases = @(
    @('えっと、資料を確認してください。はい。', '資料を確認してください。'),
    @('あー、えーと、提出の機嫌を明日に変更してください。はい。', '提出の期限を明日に変更してください。'),
    @('風量を今日に設定してください。', '風量を強に設定してください。'),
    @('強度を今日から弱に変更してください。', '強度を強から弱に変更してください。'),
    @('今日は機嫌がいいです。', '今日は機嫌がいいです。'),
    @('はい、参加します。', 'はい、参加します。'),
    @('「はい」と答えてください。', '「はい」と答えてください。'),
    @('えっと、明日の15時、いや16時までに提出してください。はい。', '明日の16時までに提出してください。'),
    @('今日は変更しないでください。期限は9月12日です。', '今日は変更しないでください。期限は9月12日です。'),
    @('えっと、あの、設定画面を開いて、えーと、通知を無効にしてください。はい。', '設定画面を開いて、通知を無効にしてください。'),
    @('えっと、契約の機嫌は来週の金曜日です。はい。', '契約の期限は来週の金曜日です。'),
    @('このタスクの機嫌を一週間延ばしてください。', 'このタスクの期限を一週間延ばしてください。'),
    @('扇風機の設定を今日にしてください。', '扇風機の設定を強にしてください。'),
    @('火力は今日ではなく弱にしてください。', '火力は強ではなく弱にしてください。'),
    @('今日中に提出してください。', '今日中に提出してください。'),
    @('彼の機嫌を損ねないでください。', '彼の機嫌を損ねないでください。'),
    @('あの資料を確認してください。', 'あの資料を確認してください。'),
    @('「えっと」という表現を削除してください。', '「えっと」という表現を削除してください。'),
    @('返事は、はい。', '返事は、はい。'),
    @('はい。', 'はい。'),
    @('あの、えっと、保存してください。はい、はい。', '保存してください。'),
    @('納期は9月12日ではなく9月15日です。', '納期は9月12日ではなく9月15日です。'),
    @('設定値は0.25、上限は120です。変更しないでください。', '設定値は0.25、上限は120です。変更しないでください。'),
    @('ファイル C:\Work\input.txt を開いてください。', 'ファイル C:\Work\input.txt を開いてください。'),
    @('えっと、資料は資料は明日までに確認してください。はい。', '資料は明日までに確認してください。'),
    @('えーっと、通知だけ無効にしてください。はい。', '通知だけ無効にしてください。')
)
$matchedCount = 0
foreach ($testCase in $qualityCases) {
    # 本番と同じ生成・後処理を通し、期待本文との完全一致と差分を表示する。
    $actualText = Invoke-OllamaProcessing -Instruction $cleanupPrompt -SourceText $testCase[0] -TokenLimit 384 -Correction
    $actualText = Complete-OllamaCorrection -SourceText $testCase[0] -ResultText $actualText
    if ($actualText -ceq $testCase[1]) { $matchedCount++ }
    else { Write-Output "Mismatch: $($testCase[0]) -> $actualText (expected: $($testCase[1]))" }
}
Write-Output "Live exact matches: $matchedCount / $($qualityCases.Count)"
