namespace TaskManager.Domain;

/// <summary>TypeWhisperから受け取る校正済み本文を表す。</summary>
public sealed class CodexReviewRequest
{
    // 確認画面へ表示する本文を保持する。
    public string Text { get; set; } = string.Empty;
}

/// <summary>確認待ちの音声入力本文を表す。</summary>
public sealed class CodexReviewItem
{
    // 確認要求の識別子、本文、受付日時を保持する。
    public string Identifier { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>確認要求を受け付けた結果を表す。</summary>
public sealed class CodexReviewReceipt
{
    // 受付識別子と確認順序を保持する。
    public string ReviewIdentifier { get; init; } = string.Empty;
    public int QueuePosition { get; init; }
}

/// <summary>Codex CLIへ渡す編集済み本文を表す。</summary>
public sealed class CodexSubmission
{
    // 元の確認要求、送信先、編集済み本文を保持する。
    public string ReviewIdentifier { get; init; } = string.Empty;
    public string ThreadIdentifier { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Codex CLIプロセスの実行結果を表す。</summary>
public sealed class CodexCommandResult
{
    // 未送信が確認でき、本文を再確認へ戻せるかを保持する。
    public bool CanRetry { get; init; } = true;
    // 終了状態、利用者向けの短いエラー、Codexの一時表示用応答を保持する。
    public bool IsSuccess { get; init; }
    public int ExitCode { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public string OutputMessage { get; init; } = string.Empty;
}
