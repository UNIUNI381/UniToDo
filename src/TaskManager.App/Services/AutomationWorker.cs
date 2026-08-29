using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Windows;

namespace TaskManager.Services;

/// <summary>カレンダー同期、バックアップ、朝通知、完了確認を定期実行する。</summary>
public sealed class AutomationWorker(
    IServiceProvider serviceProvider,
    TaskManagerTray taskManagerTray,
    ILogger<AutomationWorker> logger) : BackgroundService
{
    // スコープ生成元、通知先、ログを保持する。
    private readonly IServiceProvider services = serviceProvider;
    private readonly TaskManagerTray tray = taskManagerTray;
    private readonly ILogger<AutomationWorker> applicationLogger = logger;
    private DateTimeOffset lastCalendarAttempt = DateTimeOffset.MinValue;

    /// <summary>1分周期の自動処理ループを開始する。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 起動直後にも処理し、その後は時刻境界を1分精度で検出する。
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception automationError)
            {
                applicationLogger.LogError(automationError, "Automation cycle failed.");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>定期処理の1サイクルを実行する。</summary>
    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        // 短命スコープで各サービスを取得して接続を残さない。
        using IServiceScope serviceScope = services.CreateScope();
        TaskRepository repository = serviceScope.ServiceProvider.GetRequiredService<TaskRepository>();
        RecommendationService recommendationService = serviceScope.ServiceProvider.GetRequiredService<RecommendationService>();
        DatabaseInitializer databaseInitializer = serviceScope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        GoogleCalendarService calendarService = serviceScope.ServiceProvider.GetRequiredService<GoogleCalendarService>();
        TaskService taskService = serviceScope.ServiceProvider.GetRequiredService<TaskService>();
        TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
        DateTimeOffset currentTime = DateTimeOffset.Now;

        if (calendarService.IsBackgroundSynchronizationReady()
            && currentTime - lastCalendarAttempt >= TimeSpan.FromMinutes(5))
        {
            lastCalendarAttempt = currentTime;
            try
            {
                await calendarService.SynchronizeAsync(false, cancellationToken);
            }
            catch (Exception synchronizationError)
            {
                applicationLogger.LogDebug(synchronizationError, "Background calendar synchronization skipped.");
            }
        }

        TimeTrackingAutomationResult timeTrackingResult =
            await taskService.ProcessTimeEntryWarningsAsync(cancellationToken);
        SendTimeTrackingNotifications(timeTrackingResult, settings);
        RecommendationResult recommendation = await recommendationService.RefreshAsync(cancellationToken);
        await SendMorningNotificationAsync(repository, recommendation, settings, currentTime, cancellationToken);
        await SendFollowUpNotificationsAsync(repository, settings, currentTime, cancellationToken);
        await CreateDailyBackupAsync(repository, databaseInitializer, currentTime, cancellationToken);
    }

    /// <summary>長時間タイマーの警告と自動停止をPC通知へ表示する。</summary>
    private void SendTimeTrackingNotifications(
        TimeTrackingAutomationResult automationResult,
        TaskManagerSettings settings)
    {
        // 通知無効時も自動停止処理は維持し、バルーン表示だけを省略する。
        if (!settings.NotificationsEnabled)
        {
            return;
        }
        foreach (TimeEntryRecord warnedEntry in automationResult.WarnedEntries)
        {
            tray.Notify(
                "作業タイマーが長時間継続しています",
                $"{warnedEntry.Title}：続けるか停止してください。");
        }
        foreach (TimeEntryRecord stoppedEntry in automationResult.AutoStoppedEntries)
        {
            tray.Notify(
                "作業タイマーを自動停止しました",
                $"{stoppedEntry.Title}：記録内容を確認してください。");
        }
    }

    /// <summary>当日未送信なら朝の推薦をPC通知する。</summary>
    private async Task SendMorningNotificationAsync(
        TaskRepository repository,
        RecommendationResult recommendation,
        TaskManagerSettings settings,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken)
    {
        // 設定時刻以降の最初の実行だけを通知台帳へ記録する。
        if (!settings.NotificationsEnabled || currentTime.Hour < settings.MorningHour || recommendation.Recommendation is null)
        {
            return;
        }
        string notificationKey = $"morning:{currentTime:yyyy-MM-dd}";
        if (await repository.TryRecordNotificationAsync(
            notificationKey,
            "朝の提案",
            recommendation.Recommendation.Task.Identifier,
            cancellationToken))
        {
            tray.Notify(
                "今日の最優先タスク",
                $"{recommendation.Recommendation.Task.Title}（{recommendation.Recommendation.SuggestedMinutes}分）");
        }
    }

    /// <summary>確認期限を過ぎた実行中タスクを一度だけ通知する。</summary>
    private async Task SendFollowUpNotificationsAsync(
        TaskRepository repository,
        TaskManagerSettings settings,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken)
    {
        // 続行時に新しい確認日時となるため日時を通知キーへ含める。
        if (!settings.NotificationsEnabled)
        {
            return;
        }
        List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
        foreach (ManagedTask task in tasks.Where(task =>
            task.Status == TaskConstants.InProgressStatus
            && task.FollowUpAt.HasValue
            && task.FollowUpAt <= currentTime))
        {
            string notificationKey = $"followup:{task.Identifier}:{task.FollowUpAt:O}";
            if (await repository.TryRecordNotificationAsync(notificationKey, "完了確認", task.Identifier, cancellationToken))
            {
                task.FollowUpNotifiedAt = currentTime;
                task.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(task, "完了確認通知", TaskConstants.SystemSource, cancellationToken);
                tray.Notify("完了しましたか？", task.Title);
            }
        }
    }

    /// <summary>当日未作成ならSQLiteバックアップを作成する。</summary>
    private static async Task CreateDailyBackupAsync(
        TaskRepository repository,
        DatabaseInitializer databaseInitializer,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken)
    {
        // 通知台帳を汎用の日次重複防止として利用する。
        string backupKey = $"backup:{currentTime:yyyy-MM-dd}";
        if (await repository.TryRecordNotificationAsync(backupKey, "日次バックアップ", string.Empty, cancellationToken))
        {
            await databaseInitializer.CreateBackupAsync("daily", cancellationToken);
        }
    }
}
