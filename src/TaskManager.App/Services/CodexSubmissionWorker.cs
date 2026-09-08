using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>Codex CLI送信を1件ずつ実行して結果を通知する。</summary>
public sealed class CodexSubmissionWorker(
    CodexSubmissionQueue submissionQueue,
    ICodexCommandRunner commandRunner,
    CodexReviewService reviewService,
    IUserNotificationService notificationService,
    ILogger<CodexSubmissionWorker> logger) : BackgroundService
{
    // 送信元、CLI実行、再確認先、通知先、ログを保持する。
    private readonly CodexSubmissionQueue queue = submissionQueue;
    private readonly ICodexCommandRunner runner = commandRunner;
    private readonly CodexReviewService reviews = reviewService;
    private readonly IUserNotificationService notifications = notificationService;
    private readonly ILogger<CodexSubmissionWorker> applicationLogger = logger;

    /// <summary>送信キューを停止要求まで順番に処理する。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 同じCodexタスクへ複数ターンを並行送信しないよう直列で待機する。
        await foreach (CodexSubmission submission in queue.ReadAllAsync(stoppingToken))
        {
            await ProcessSubmissionAsync(submission, stoppingToken);
        }
    }

    /// <summary>1件のCLI送信を実行し、失敗時は編集済み本文を確認待ちへ戻す。</summary>
    public async Task ProcessSubmissionAsync(CodexSubmission submission, CancellationToken cancellationToken)
    {
        // アプリ停止による取消しは重複送信を避けるため再投入しない。
        try
        {
            // Windows通知を使わず、応答が返るまで中央下の状態画面を継続表示する。
            notifications.ShowCodexProcessing();
            CodexCommandResult result = await runner.SendAsync(submission, cancellationToken);
            if (result.IsSuccess)
            {
                // 終了コードだけでなくCodexの実際の回答を同じ状態画面で確認できるようにする。
                string responseMessage = string.IsNullOrWhiteSpace(result.OutputMessage)
                    ? "Codex CLIは正常終了しましたが、応答本文はありませんでした。"
                    : result.OutputMessage;
                notifications.ShowCodexResult(responseMessage, false);
                return;
            }
            applicationLogger.LogWarning("Codex submission failed with exit code {ExitCode}: {ErrorMessage}", result.ExitCode, result.ErrorMessage);
            reviews.RequeueFailedSubmission(submission);
            string failureDetail = string.Join(
                Environment.NewLine + Environment.NewLine,
                new[] { result.OutputMessage, result.ErrorMessage }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
            string displayedFailure = string.IsNullOrWhiteSpace(failureDetail)
                ? $"Codex CLIが終了コード{result.ExitCode}を返しました。"
                : failureDetail;
            notifications.ShowCodexResult(
                $"{displayedFailure}{Environment.NewLine}{Environment.NewLine}本文を確認画面へ戻しました。",
                true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception submissionError)
        {
            applicationLogger.LogError(submissionError, "Codex submission process failed.");
            reviews.RequeueFailedSubmission(submission);
            // 予期しない例外でも原因を短く表示し、再発時の切り分けを可能にする。
            string errorMessage = submissionError.Message.ReplaceLineEndings(" ").Trim();
            if (errorMessage.Length > 300)
            {
                errorMessage = errorMessage[..300];
            }
            notifications.ShowCodexResult(
                $"{(string.IsNullOrWhiteSpace(errorMessage) ? "予期しない送信エラーです。" : errorMessage)}{Environment.NewLine}{Environment.NewLine}本文を確認画面へ戻しました。",
                true);
        }
    }
}

/// <summary>利用者へ通知や対話画面を表示する機能を表す。</summary>
public interface IUserNotificationService
{
    /// <summary>音声確認画面を表示可能であるかを返す。</summary>
    bool CanShowCodexReview { get; }

    /// <summary>通知機能や常駐アイコンを開始する。</summary>
    void Start();

    /// <summary>指定したタイトルと本文を通知する。</summary>
    void Notify(string title, string message);

    /// <summary>Codex CLIの応答待ちを中央下の状態画面へ表示する。</summary>
    void ShowCodexProcessing();

    /// <summary>Codex CLIの応答またはエラーを中央下の詳細画面へ表示する。</summary>
    void ShowCodexResult(string message, bool isError);
}
