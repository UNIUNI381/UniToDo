using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>Calendarプラグインの操作後に予定と推薦を更新し、全画面へ通知する。</summary>
public sealed class CalendarSynchronizationWorker(
    CalendarSynchronizationQueue synchronizationQueue,
    GoogleCalendarService calendarService,
    RecommendationService recommendationService,
    UiChangeNotifier changeNotifier,
    ILogger<CalendarSynchronizationWorker> logger) : BackgroundService
{
    // 同期要求と既存の読取同期・推薦・画面通知の依存先を保持する。
    private readonly CalendarSynchronizationQueue requests = synchronizationQueue;
    private readonly GoogleCalendarService calendar = calendarService;
    private readonly RecommendationService recommendations = recommendationService;
    private readonly UiChangeNotifier changes = changeNotifier;
    private readonly ILogger<CalendarSynchronizationWorker> applicationLogger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ブラウザを閉じても処理を継続し、アプリ終了時は待機と通信を取り消す。
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await requests.WaitAsync(stoppingToken);
                bool synchronizationAttempted = false;
                try
                {
                    if (!calendar.IsBackgroundSynchronizationReady()) continue;
                    synchronizationAttempted = true;
                    // 定期・手動同期と同じロックと読取権限で予定を取り直す。
                    await calendar.SynchronizeAsync(false, stoppingToken);
                    await recommendations.RefreshAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception synchronizationError)
                {
                    // 同期失敗は会話の成功結果を変えず、既存キャッシュと同期エラー表示を残す。
                    applicationLogger.LogWarning(synchronizationError, "Calendar synchronization after Codex operation failed.");
                }
                finally
                {
                    // 成功時の予定と、失敗時の同期状態をPC・Androidへ配信する。
                    if (synchronizationAttempted && !stoppingToken.IsCancellationRequested)
                        changes.Publish(TaskConstants.SystemSource, string.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
