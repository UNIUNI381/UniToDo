using Microsoft.Extensions.Logging;

namespace TaskManager.Services;

/// <summary>GUI画面やトレイのないHeadless・クロスプラットフォーム環境向けの通知機能を提供する。</summary>
public sealed class HeadlessNotificationService(ILogger<HeadlessNotificationService> logger) : IUserNotificationService
{
    // アプリケーションログ出力先を保持する。
    private readonly ILogger<HeadlessNotificationService> applicationLogger = logger;

    /// <summary>ヘッドレス環境ではネイティブ確認画面を表示できないことを返す。</summary>
    public bool CanShowCodexReview => false;

    /// <summary>ヘッドレス環境ではトレイ等の初期化を行わない。</summary>
    public void Start()
    {
        // ヘッドレス環境ではトレイや画面スレッドを開始しない。
    }

    /// <summary>通知内容をログへ記録する。</summary>
    public void Notify(string title, string message)
    {
        // トレイ通知の代わりに情報ログへ出力する。
        applicationLogger.LogInformation("[通知] {Title}: {Message}", title, message);
    }

    /// <summary>Codex CLIの応答待ちをログへ記録する。</summary>
    public void ShowCodexProcessing()
    {
        // 処理中ダイアログの代わりに情報ログへ出力する。
        applicationLogger.LogInformation("[Codex] 処理中...");
    }

    /// <summary>Codex CLIの応答またはエラーをログへ記録する。</summary>
    public void ShowCodexResult(string message, bool isError)
    {
        // 結果ダイアログの代わりに適切なログレベルで出力する。
        if (isError)
        {
            applicationLogger.LogError("[Codex エラー] {Message}", message);
        }
        else
        {
            applicationLogger.LogInformation("[Codex 完了] {Message}", message);
        }
    }
}
