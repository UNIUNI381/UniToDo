using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>タスクの登録、状態遷移、下書き承認を調整する。</summary>
public sealed class TaskService(
    TaskRepository taskRepository,
    RecommendationService recommendationService,
    TaskValidationService taskValidationService,
    ProjectService projectService,
    IDraftPostProcessingService draftPostProcessingService,
    TimeEntryRepository timeEntryRepository,
    TimeProvider timeProvider)
{
    // 保存層、推薦、検証、プロジェクト、下書き後処理、作業ログ、時刻供給元を保持する。
    private readonly TaskRepository repository = taskRepository;
    private readonly RecommendationService recommendations = recommendationService;
    private readonly TaskValidationService validation = taskValidationService;
    private readonly ProjectService projectManagement = projectService;
    private readonly IDraftPostProcessingService draftPostProcessing = draftPostProcessingService;
    private readonly TimeEntryRepository timeEntries = timeEntryRepository;
    private readonly TimeProvider clock = timeProvider;
    private readonly SemaphoreSlim operationLock = new(1, 1);

    /// <summary>新規タスクを正規化して登録する。</summary>
    public async Task<ManagedTask> AddTaskAsync(
        ManagedTask task,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 同一IDの重複と不整合を防いで保存する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
            Dictionary<string, ValidationSnapshot> validationSnapshots = CaptureValidationSnapshots(tasks);
            NormalizeTask(task, source, isNewTask: true);
            await projectManagement.ApplyProjectDefaultsAsync(task, isNewTask: true, cancellationToken);
            if (tasks.Any(existingTask => string.Equals(existingTask.Identifier, task.Identifier, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"同じタスクIDが既にあります: {task.Identifier}");
            }
            tasks.Add(task);
            validation.ValidateAll(tasks);
            ApplyValidationStatus(task, null);
            await repository.SaveTaskAsync(task, "新規登録", source, cancellationToken);
            await PersistValidationChangesAsync(tasks, validationSnapshots, task.Identifier, source, cancellationToken);
            await recommendations.RefreshAsync(cancellationToken);
            return task;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>既存タスクを更新する。</summary>
    public async Task<ManagedTask> UpdateTaskAsync(
        string taskIdentifier,
        ManagedTask replacementTask,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 作成日時を維持し、変更後の全依存関係を再検証する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
            Dictionary<string, ValidationSnapshot> validationSnapshots = CaptureValidationSnapshots(tasks);
            ManagedTask existingTask = tasks.FirstOrDefault(task => string.Equals(task.Identifier, taskIdentifier, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"タスクが見つかりません: {taskIdentifier}");
            if (!string.Equals(taskIdentifier, replacementTask.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新時にタスクIDは変更できません。");
            }
            if (!string.Equals(existingTask.Status, replacementTask.Status, StringComparison.Ordinal)
                && (existingTask.Status == TaskConstants.InProgressStatus
                    || replacementTask.Status == TaskConstants.InProgressStatus))
            {
                throw new InvalidOperationException("実行中への変更または実行中からの終了は開始・完了・中断ボタンを使用してください。");
            }
            replacementTask.CreatedAt = existingTask.CreatedAt;
            if (replacementTask.DeadlineOrigin == ProjectConstants.AutomaticDeadlineOrigin)
            {
                replacementTask.DeadlineOrigin = existingTask.DeadlineOrigin;
            }
            NormalizeTask(replacementTask, source, isNewTask: false);
            await projectManagement.ApplyProjectDefaultsAsync(replacementTask, isNewTask: false, cancellationToken);
            int existingIndex = tasks.IndexOf(existingTask);
            tasks[existingIndex] = replacementTask;
            validation.ValidateAll(tasks);
            ApplyValidationStatus(replacementTask, validationSnapshots.GetValueOrDefault(replacementTask.Identifier));
            await repository.SaveTaskAsync(replacementTask, "更新", source, cancellationToken);
            await PersistValidationChangesAsync(tasks, validationSnapshots, replacementTask.Identifier, source, cancellationToken);
            await recommendations.RefreshAsync(cancellationToken);
            return replacementTask;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>指定タスクへ開始・完了などの操作を適用する。</summary>
    public async Task<RecommendationResult> ExecuteActionAsync(
        string actionName,
        string? taskIdentifier,
        int? postponeMinutes,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 1操作ずつ直列化し、変更後に推薦を即時更新する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
            TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
            ManagedTask targetTask = await ResolveTargetTaskAsync(tasks, taskIdentifier, cancellationToken);
            DateTimeOffset currentTime = clock.GetLocalNow();
            EnsureActionAllowed(actionName, targetTask, tasks, currentTime);
            if (actionName == "start")
            {
                await StartTaskAsync(targetTask, tasks, settings, currentTime, source, cancellationToken);
            }
            else if (actionName == "complete")
            {
                await CompleteTaskAsync(targetTask, tasks, currentTime, source, cancellationToken);
            }
            else if (actionName == "continue")
            {
                await ContinueTaskAsync(targetTask, settings, currentTime, source, cancellationToken);
            }
            else if (actionName == "interrupt")
            {
                targetTask.Status = TaskConstants.ReadyStatus;
                targetTask.StartedAt = null;
                targetTask.FollowUpAt = null;
                targetTask.FollowUpNotifiedAt = null;
                targetTask.DeferralCount += 1;
                targetTask.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(targetTask, "中断", source, cancellationToken);
            }
            else if (actionName == "postpone")
            {
                int appliedMinutes = postponeMinutes.GetValueOrDefault(settings.DefaultPostponeMinutes);
                targetTask.Status = TaskConstants.ReadyStatus;
                targetTask.StartedAt = null;
                targetTask.EarliestStartAt = currentTime.AddMinutes(appliedMinutes);
                targetTask.FollowUpAt = null;
                targetTask.FollowUpNotifiedAt = null;
                targetTask.DeferralCount += 1;
                targetTask.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(targetTask, "延期", source, cancellationToken);
            }
            else if (actionName == "cancel")
            {
                targetTask.Status = TaskConstants.CancelledStatus;
                targetTask.StartedAt = null;
                targetTask.FollowUpAt = null;
                targetTask.FollowUpNotifiedAt = null;
                targetTask.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(targetTask, "中止", source, cancellationToken);
                await MarkCancelledDependencyTasksAsync(targetTask.Identifier, source, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"未対応の操作です: {actionName}");
            }
            return await recommendations.RefreshAsync(cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>起動時に実行中タスクと作業タイマーの不整合を修復する。</summary>
    public async Task<TimeEntryRecord?> ReconcileTimeTrackingAsync(
        CancellationToken cancellationToken = default)
    {
        // 既にタイマーがある場合は正本としてそのまま利用する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord? activeEntry = await timeEntries.GetActiveAsync(cancellationToken);
            if (activeEntry is not null)
            {
                return activeEntry;
            }
            List<ManagedTask> runningTasks = (await repository.GetTasksAsync(cancellationToken))
                .Where(task => task.Status == TaskConstants.InProgressStatus)
                .OrderByDescending(task => task.UpdatedAt)
                .ToList();
            if (runningTasks.Count == 0)
            {
                return null;
            }

            DateTimeOffset currentTime = clock.GetLocalNow();
            ManagedTask recoveredTask = runningTasks[0];
            foreach (ManagedTask duplicateTask in runningTasks.Skip(1))
            {
                duplicateTask.Status = TaskConstants.ReadyStatus;
                duplicateTask.StartedAt = null;
                duplicateTask.FollowUpAt = null;
                duplicateTask.FollowUpNotifiedAt = null;
                duplicateTask.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(
                    duplicateTask,
                    "移行時自動中断",
                    TaskConstants.SystemSource,
                    cancellationToken);
            }
            TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
            DateTimeOffset recoveredStart = recoveredTask.StartedAt ?? currentTime;
            return await timeEntries.StartAsync(
                recoveredTask.Identifier,
                recoveredTask.ProjectIdentifier,
                recoveredTask.Title,
                recoveredStart,
                recoveredStart.AddMinutes(settings.LongTimerWarningMinutes),
                needsReview: true,
                reviewReason: "スキーマ移行時に実行中だったタスクから開始ログを復旧しました。",
                TaskConstants.SystemSource,
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>タスクに紐付かない自由活動の計測を開始する。</summary>
    public async Task<TimeEntryRecord> StartFreeActivityAsync(
        TimeEntryStartRequest request,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 現在のタスクまたは自由活動を閉じてから新しいタイマーを開始する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            string title = NormalizeTimeEntryTitle(request.Title);
            string? projectIdentifier = NormalizeOptionalIdentifier(request.ProjectIdentifier);
            if (projectIdentifier is not null)
            {
                await timeEntries.GetProjectNameAsync(projectIdentifier, cancellationToken);
            }
            DateTimeOffset currentTime = clock.GetLocalNow();
            TimeEntryRecord? activeEntry = await timeEntries.GetActiveAsync(cancellationToken);
            if (activeEntry is not null && !string.IsNullOrWhiteSpace(activeEntry.TaskIdentifier))
            {
                ManagedTask? activeTask = await repository.GetTaskAsync(activeEntry.TaskIdentifier, cancellationToken);
                if (activeTask is not null)
                {
                    activeTask.Status = TaskConstants.ReadyStatus;
                    activeTask.StartedAt = null;
                    activeTask.FollowUpAt = null;
                    activeTask.FollowUpNotifiedAt = null;
                    activeTask.DeferralCount += 1;
                    activeTask.UpdatedAt = currentTime;
                    await repository.SaveTaskAsync(activeTask, "自動中断", source, cancellationToken);
                }
                else
                {
                    await timeEntries.StopAsync(
                        activeEntry,
                        currentTime,
                        TimeTrackingConstants.ReplacedStopReason,
                        needsReview: false,
                        reviewReason: string.Empty,
                        source,
                        cancellationToken);
                }
            }
            else if (activeEntry is not null)
            {
                await timeEntries.StopAsync(
                    activeEntry,
                    currentTime,
                    TimeTrackingConstants.ReplacedStopReason,
                    needsReview: false,
                    reviewReason: string.Empty,
                    source,
                    cancellationToken);
            }

            TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
            return await timeEntries.StartAsync(
                null,
                projectIdentifier,
                title,
                currentTime,
                currentTime.AddMinutes(settings.LongTimerWarningMinutes),
                needsReview: false,
                reviewReason: string.Empty,
                source,
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>指定した実行中の作業ログを停止する。</summary>
    public async Task<TimeEntryRecord> StopTimeEntryAsync(
        string timeEntryIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // タスク紐付きではタスク状態とログを同一保存操作で中断する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord timeEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            if (timeEntry.EndAt.HasValue || timeEntry.VoidedAt.HasValue)
            {
                throw new InvalidOperationException("実行中ではない作業ログは停止できません。");
            }
            DateTimeOffset currentTime = clock.GetLocalNow();
            if (!string.IsNullOrWhiteSpace(timeEntry.TaskIdentifier))
            {
                ManagedTask? task = await repository.GetTaskAsync(timeEntry.TaskIdentifier, cancellationToken);
                if (task is not null)
                {
                    task.Status = TaskConstants.ReadyStatus;
                    task.StartedAt = null;
                    task.FollowUpAt = null;
                    task.FollowUpNotifiedAt = null;
                    task.DeferralCount += 1;
                    task.UpdatedAt = currentTime;
                    await repository.SaveTaskAsync(task, "中断", source, cancellationToken);
                    await recommendations.RefreshAsync(cancellationToken);
                    return await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
                }
            }
            return await timeEntries.StopAsync(
                timeEntry,
                currentTime,
                TimeTrackingConstants.ManualStopReason,
                needsReview: false,
                reviewReason: string.Empty,
                source,
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>長時間警告中の実行ログを1時間延長する。</summary>
    public async Task<TimeEntryRecord> ExtendTimeEntryAsync(
        string timeEntryIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 警告表示中だけ延長を許可し、次回警告を操作時刻の1時間後へ設定する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord timeEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            DateTimeOffset currentTime = clock.GetLocalNow();
            bool warningDue = timeEntry.WarningAt.HasValue
                || timeEntry.NextWarningAt.HasValue && timeEntry.NextWarningAt.Value <= currentTime;
            if (timeEntry.EndAt.HasValue || !warningDue)
            {
                throw new InvalidOperationException("長時間警告中の実行ログだけを延長できます。");
            }
            if (timeEntry.WarningAt is null && timeEntry.NextWarningAt.HasValue)
            {
                timeEntry = await timeEntries.MarkWarningAsync(
                    timeEntry,
                    timeEntry.NextWarningAt.Value,
                    cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(timeEntry.TaskIdentifier))
            {
                ManagedTask task = await repository.GetTaskAsync(timeEntry.TaskIdentifier, cancellationToken)
                    ?? throw new KeyNotFoundException($"タスクが見つかりません: {timeEntry.TaskIdentifier}");
                TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
                await ContinueTaskAsync(task, settings, currentTime, source, cancellationToken);
                return await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            }
            return await timeEntries.ExtendAsync(timeEntry, currentTime, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>完了済み作業ログを手入力で追加する。</summary>
    public async Task<TimeEntryRecord> AddManualTimeEntryAsync(
        TimeEntryMutationRequest request,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 関連先と時間範囲を検証し、未確認の重複があれば保存前に止める。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset currentTime = clock.GetLocalNow();
            await NormalizeTimeEntryMutationAsync(request, currentTime, cancellationToken);
            await EnsureOverlapConfirmedAsync(request, null, cancellationToken);
            string projectName = await timeEntries.GetProjectNameAsync(request.ProjectIdentifier, cancellationToken);
            return await timeEntries.AddManualAsync(request, projectName, source, currentTime, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>完了済み作業ログを手入力内容で更新する。</summary>
    public async Task<TimeEntryRecord> UpdateManualTimeEntryAsync(
        string timeEntryIdentifier,
        TimeEntryMutationRequest request,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 実行中ログは専用停止操作を要求し、完了済みだけを編集する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord existingEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            if (!existingEntry.EndAt.HasValue || existingEntry.VoidedAt.HasValue)
            {
                throw new InvalidOperationException("実行中または無効化済みの作業ログは編集できません。");
            }
            DateTimeOffset currentTime = clock.GetLocalNow();
            await NormalizeTimeEntryMutationAsync(request, currentTime, cancellationToken);
            await EnsureOverlapConfirmedAsync(request, timeEntryIdentifier, cancellationToken);
            string projectName = await timeEntries.GetProjectNameAsync(request.ProjectIdentifier, cancellationToken);
            return await timeEntries.UpdateManualAsync(
                existingEntry,
                request,
                projectName,
                source,
                currentTime,
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>実行中作業ログの開始時刻と関連タイマー時刻を更新する。</summary>
    public async Task<TimeEntryRecord> UpdateActiveTimeEntryStartAsync(
        string timeEntryIdentifier,
        ActiveTimeEntryStartUpdateRequest request,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 実行中状態と関連先を維持し、時刻だけを単一トランザクションで修正する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord existingEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            if (existingEntry.EndAt.HasValue || existingEntry.VoidedAt.HasValue)
            {
                throw new InvalidOperationException("実行中の作業ログだけ開始時刻を変更できます。");
            }
            DateTimeOffset currentTime = clock.GetLocalNow();
            if (request.StartAt == default || request.StartAt > currentTime.AddMinutes(1))
            {
                throw new InvalidOperationException("開始日時は現在以前を指定してください。");
            }
            await EnsureOverlapConfirmedAsync(
                request.StartAt,
                currentTime,
                timeEntryIdentifier,
                request.AllowOverlap,
                cancellationToken);

            // タスク紐付きログでは画面の経過時間と完了確認予定も同じ差分だけ移動する。
            ManagedTask? linkedTask = null;
            if (!string.IsNullOrWhiteSpace(existingEntry.TaskIdentifier))
            {
                linkedTask = await repository.GetTaskAsync(existingEntry.TaskIdentifier, cancellationToken)
                    ?? throw new KeyNotFoundException($"タスクが見つかりません: {existingEntry.TaskIdentifier}");
                if (linkedTask.Status != TaskConstants.InProgressStatus)
                {
                    throw new InvalidOperationException("関連タスクが実行中ではないため開始時刻を変更できません。");
                }
            }
            TimeSpan startTimeDifference = request.StartAt - existingEntry.StartAt;
            return await timeEntries.UpdateActiveStartAsync(
                existingEntry,
                linkedTask,
                request.StartAt,
                startTimeDifference,
                source,
                currentTime,
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>要確認作業ログを現在の記録内容で確定する。</summary>
    public async Task<TimeEntryRecord> ConfirmTimeEntryAsync(
        string timeEntryIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 要確認状態を解除して確認履歴を残す。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord timeEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            if (!timeEntry.NeedsReview)
            {
                return timeEntry;
            }
            return await timeEntries.ConfirmAsync(
                timeEntry,
                source,
                clock.GetLocalNow(),
                cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>作業ログを集計対象外として無効化する。</summary>
    public async Task<TimeEntryRecord> VoidTimeEntryAsync(
        string timeEntryIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 実行中のタスク紐付きログではタスクを実行可能へ戻してから無効化する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord timeEntry = await RequireTimeEntryAsync(timeEntryIdentifier, cancellationToken);
            DateTimeOffset currentTime = clock.GetLocalNow();
            if (!timeEntry.EndAt.HasValue && !string.IsNullOrWhiteSpace(timeEntry.TaskIdentifier))
            {
                ManagedTask? task = await repository.GetTaskAsync(timeEntry.TaskIdentifier, cancellationToken);
                if (task is not null)
                {
                    task.Status = TaskConstants.ReadyStatus;
                    task.StartedAt = null;
                    task.FollowUpAt = null;
                    task.FollowUpNotifiedAt = null;
                    task.UpdatedAt = currentTime;
                    await repository.SaveTaskAsync(task, "中断", source, cancellationToken);
                }
            }
            return await timeEntries.VoidAsync(timeEntry, source, currentTime, cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>長時間タイマーの警告または自動停止を現在時刻へ適用する。</summary>
    public async Task<TimeTrackingAutomationResult> ProcessTimeEntryWarningsAsync(
        CancellationToken cancellationToken = default)
    {
        // 単一の実行中ログに対し警告1回と60分後の自動停止を判定する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            TimeEntryRecord? activeEntry = await timeEntries.GetActiveAsync(cancellationToken);
            if (activeEntry is null || !activeEntry.NextWarningAt.HasValue)
            {
                return new TimeTrackingAutomationResult();
            }
            DateTimeOffset currentTime = clock.GetLocalNow();
            DateTimeOffset warningAt = activeEntry.WarningAt ?? activeEntry.NextWarningAt.Value;
            DateTimeOffset automaticStopAt = warningAt.AddMinutes(TimeTrackingConstants.WarningGraceMinutes);
            if (currentTime >= automaticStopAt)
            {
                TimeEntryRecord stoppedEntry = await StopTimedOutEntryAsync(
                    activeEntry,
                    automaticStopAt,
                    cancellationToken);
                return new TimeTrackingAutomationResult { AutoStoppedEntries = [stoppedEntry] };
            }
            if (activeEntry.WarningAt is null && currentTime >= activeEntry.NextWarningAt.Value)
            {
                TimeEntryRecord warnedEntry = await timeEntries.MarkWarningAsync(
                    activeEntry,
                    activeEntry.NextWarningAt.Value,
                    cancellationToken);
                return new TimeTrackingAutomationResult { WarnedEntries = [warnedEntry] };
            }
            return new TimeTrackingAutomationResult();
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>明示確認済みで関連付けのないタスクを完全削除する。</summary>
    public async Task DeleteTaskAsync(
        string taskIdentifier,
        string confirmationIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // IDの再入力、子タスク、後続依存を確認してから削除する。
        if (!string.Equals(taskIdentifier, confirmationIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("完全削除には同じタスクIDを--confirmで指定してください。");
        }
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
            ManagedTask targetTask = tasks.FirstOrDefault(task =>
                string.Equals(task.Identifier, taskIdentifier, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"タスクが見つかりません: {taskIdentifier}");
            bool hasChildren = tasks.Any(task =>
                string.Equals(task.ParentIdentifier, taskIdentifier, StringComparison.OrdinalIgnoreCase));
            bool hasDependents = tasks.Any(task =>
                task.DependencyIdentifiers.Contains(taskIdentifier, StringComparer.OrdinalIgnoreCase));
            if (hasChildren || hasDependents)
            {
                throw new InvalidOperationException("子タスクまたは後続タスクがあるため完全削除できません。通常は中止を使用してください。");
            }
            await repository.DeleteTaskAsync(targetTask, source, cancellationToken);
            await recommendations.RefreshAsync(cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>AIが生成した下書きバッチを検証して冪等に登録する。</summary>
    public async Task<DraftBatchCreationResult> CreateDraftBatchAsync(
        DraftBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        // 保存前検証、トランザクション保存、保存後処理を明確に分離する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            request.IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? null
                : request.IdempotencyKey.Trim();
            if (request.IdempotencyKey?.Length > 200)
            {
                throw new InvalidOperationException("idempotency keyは200文字以内で指定してください。");
            }
            if (request.IdempotencyKey is not null)
            {
                DraftBatchRecord? existingBatch = await repository.GetDraftBatchByRequestKeyAsync(
                    request.IdempotencyKey,
                    cancellationToken);
                if (existingBatch is not null)
                {
                    return CreateDraftCreationResult(existingBatch, replayed: true);
                }
            }
            if (request.Tasks.Count == 0)
            {
                throw new InvalidOperationException("下書きタスクがありません。");
            }
            await PrepareDraftTasksAsync(request, cancellationToken);
            DraftBatchCreationResult creationResult = await repository.CreateDraftBatchAsync(request, cancellationToken);
            if (creationResult.Replayed)
            {
                return creationResult;
            }

            try
            {
                // 保存済み下書きを全体グラフで検証し推薦を更新する。
                await draftPostProcessing.ProcessAsync(
                    creationResult.BatchIdentifier,
                    cancellationToken);
                await repository.UpdateDraftBatchPostProcessingAsync(
                    creationResult.BatchIdentifier,
                    succeeded: true,
                    warning: string.Empty,
                    cancellationToken);
                return creationResult;
            }
            catch (Exception postProcessingError)
            {
                // コミット済み下書きを失敗扱いにせず警告付き成功応答へ変換する。
                string warning = $"下書きは保存されましたが、検証または推薦更新に失敗しました: {postProcessingError.Message}";
                creationResult.PostProcessingSucceeded = false;
                creationResult.Warning = warning;
                try
                {
                    await repository.UpdateDraftBatchPostProcessingAsync(
                        creationResult.BatchIdentifier,
                        succeeded: false,
                        warning,
                        CancellationToken.None);
                }
                catch
                {
                    // 状態記録に失敗しても保存済み下書きの成功応答を優先する。
                }
                return creationResult;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>下書きタスクの仮参照キー、ID、依存関係をコミット前に確定する。</summary>
    private async Task PrepareDraftTasksAsync(
        DraftBatchRequest request,
        CancellationToken cancellationToken)
    {
        // aiReferenceKeyを必須化し、大文字小文字を区別せず一意性を検証する。
        foreach (ManagedTask task in request.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.AiReferenceKey))
            {
                throw new InvalidOperationException("すべての下書きタスクにaiReferenceKeyを指定してください。");
            }
            task.AiReferenceKey = task.AiReferenceKey.Trim();
        }
        List<string> duplicateReferenceKeys = request.Tasks
            .GroupBy(task => task.AiReferenceKey, StringComparer.OrdinalIgnoreCase)
            .Where(referenceGroup => referenceGroup.Count() > 1)
            .Select(referenceGroup => referenceGroup.Key)
            .ToList();
        if (duplicateReferenceKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"aiReferenceKeyが重複しています: {string.Join(", ", duplicateReferenceKeys)}");
        }

        List<ManagedTask> existingTasks = await repository.GetTasksAsync(cancellationToken);
        foreach (ManagedTask task in request.Tasks)
        {
            // 空IDへ実IDと既定値を補い、プロジェクト既定期限を保存前に適用する。
            NormalizeTask(task, TaskConstants.CodexSource, isNewTask: true);
            await projectManagement.ApplyProjectDefaultsAsync(task, isNewTask: true, cancellationToken);
            task.Status = TaskConstants.DraftStatus;
        }
        List<string> duplicateTaskIdentifiers = request.Tasks
            .GroupBy(task => task.Identifier, StringComparer.OrdinalIgnoreCase)
            .Where(taskGroup => taskGroup.Count() > 1)
            .Select(taskGroup => taskGroup.Key)
            .ToList();
        if (duplicateTaskIdentifiers.Count > 0)
        {
            throw new InvalidOperationException(
                $"下書きタスクIDが重複しています: {string.Join(", ", duplicateTaskIdentifiers)}");
        }
        HashSet<string> existingIdentifiers = existingTasks
            .Select(task => task.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? conflictingIdentifier = request.Tasks
            .Select(task => task.Identifier)
            .FirstOrDefault(existingIdentifiers.Contains);
        if (conflictingIdentifier is not null)
        {
            throw new InvalidOperationException($"同じタスクIDが既にあります: {conflictingIdentifier}");
        }

        // 仮参照キーを生成済みIDへ変換し、既存またはバッチ内の実IDだけを許可する。
        Dictionary<string, string> referenceMap = request.Tasks.ToDictionary(
            task => task.AiReferenceKey,
            task => task.Identifier,
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownIdentifiers = existingIdentifiers;
        knownIdentifiers.UnionWith(request.Tasks.Select(task => task.Identifier));
        foreach (ManagedTask task in request.Tasks)
        {
            List<string> resolvedDependencies = [];
            foreach (string dependencyReference in task.DependencyIdentifiers)
            {
                string resolvedIdentifier = referenceMap.GetValueOrDefault(
                    dependencyReference,
                    dependencyReference);
                if (!knownIdentifiers.Contains(resolvedIdentifier))
                {
                    throw new InvalidOperationException(
                        $"未解決の依存参照があります: {task.AiReferenceKey} → {dependencyReference}");
                }
                resolvedDependencies.Add(resolvedIdentifier);
            }
            task.DependencyIdentifiers = resolvedDependencies
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>保存済みバッチを再送用の作成応答へ変換する。</summary>
    private static DraftBatchCreationResult CreateDraftCreationResult(
        DraftBatchRecord draftBatch,
        bool replayed)
    {
        // 保存時の後処理状態と対応表を失わず統一応答を返す。
        return new DraftBatchCreationResult
        {
            BatchIdentifier = draftBatch.BatchIdentifier,
            Identifier = draftBatch.Identifier,
            Saved = true,
            TaskCount = draftBatch.TaskCount,
            PostProcessingSucceeded = draftBatch.PostProcessingSucceeded,
            Warning = draftBatch.Warning,
            Replayed = replayed,
            TaskMappings = draftBatch.TaskMappings
        };
    }

    /// <summary>画面から指定された下書きバッチを一括承認する。</summary>
    public async Task<int> ApproveDraftBatchAsync(string batchIdentifier, CancellationToken cancellationToken = default)
    {
        // 最新状態で再検証し、エラーがなければ実行可能へ変更する。
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
            validation.ValidateAll(tasks);
            foreach (ManagedTask draftTask in tasks.Where(task => task.DraftBatchIdentifier == batchIdentifier))
            {
                await repository.SaveTaskAsync(draftTask, "下書き検証", TaskConstants.SystemSource, cancellationToken);
            }
            int approvedCount = await repository.ApproveDraftBatchAsync(batchIdentifier, cancellationToken);
            await recommendations.RefreshAsync(cancellationToken);
            return approvedCount;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>入力値へ安全な既定値と日時を補完する。</summary>
    private void NormalizeTask(ManagedTask task, string source, bool isNewTask)
    {
        // ID、期限種別、数値、作成更新日時を一貫させる。
        DateTimeOffset currentTime = clock.GetLocalNow();
        if (string.IsNullOrWhiteSpace(task.Identifier))
        {
            task.Identifier = $"TASK-{currentTime:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..29];
        }
        task.Identifier = task.Identifier.Trim();
        task.Title = task.Title.Trim();
        task.Category = string.IsNullOrWhiteSpace(task.Category) ? "仕事" : task.Category.Trim();
        task.Status = string.IsNullOrWhiteSpace(task.Status) ? TaskConstants.ReadyStatus : task.Status.Trim();
        task.EstimatedMinutes = task.EstimatedMinutes <= 0 ? 30 : task.EstimatedMinutes;
        task.RemainingMinutes = task.RemainingMinutes <= 0 && task.Status != TaskConstants.CompletedStatus
            ? task.EstimatedMinutes
            : task.RemainingMinutes;
        task.Importance = Math.Clamp(task.Importance, 1, 5);
        task.DeadlineType = task.DeadlineAt.HasValue
            ? task.DeadlineType is TaskConstants.StrictDeadlineType or TaskConstants.TargetDeadlineType
                ? task.DeadlineType
                : TaskConstants.TargetDeadlineType
            : TaskConstants.NoDeadlineType;
        task.DependencyIdentifiers = task.DependencyIdentifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        task.Source = source;
        task.CreatedAt = isNewTask || task.CreatedAt == default ? currentTime : task.CreatedAt;
        task.EarliestStartAt ??= task.CreatedAt;
        task.UpdatedAt = currentTime;
        task.CompletionCondition = string.IsNullOrWhiteSpace(task.CompletionCondition)
            ? $"{task.Title}を完了する"
            : task.CompletionCondition.Trim();
    }

    /// <summary>操作対象を明示IDまたは現在の推薦から取得する。</summary>
    private async Task<ManagedTask> ResolveTargetTaskAsync(
        IReadOnlyList<ManagedTask> tasks,
        string? taskIdentifier,
        CancellationToken cancellationToken)
    {
        // IDがない場合は確認期限超過中、次に推薦タスクを利用する。
        if (!string.IsNullOrWhiteSpace(taskIdentifier))
        {
            return tasks.FirstOrDefault(task => string.Equals(task.Identifier, taskIdentifier, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"タスクが見つかりません: {taskIdentifier}");
        }
        DateTimeOffset currentTime = clock.GetLocalNow();
        ManagedTask? followUpTask = tasks.FirstOrDefault(task =>
            task.Status == TaskConstants.InProgressStatus && task.FollowUpAt.HasValue && task.FollowUpAt <= currentTime);
        if (followUpTask is not null)
        {
            return followUpTask;
        }
        RecommendationResult recommendation = await recommendations.RefreshAsync(cancellationToken);
        return recommendation.Recommendation?.Task
            ?? throw new InvalidOperationException("操作できる推薦タスクがありません。");
    }

    /// <summary>状態操作が下書き承認や依存条件を迂回しないことを検証する。</summary>
    private static void EnsureActionAllowed(
        string actionName,
        ManagedTask targetTask,
        IReadOnlyList<ManagedTask> tasks,
        DateTimeOffset currentTime)
    {
        // 下書きと要確認を開始・完了・延期操作で実行可能へ変えないよう制限する。
        if (actionName == "cancel")
        {
            return;
        }
        if (targetTask.Status is TaskConstants.DraftStatus or TaskConstants.NeedsReviewStatus)
        {
            throw new InvalidOperationException("下書きまたは要確認のタスクは、承認・修正後に操作してください。");
        }
        if (actionName == "start")
        {
            HashSet<string> completedIdentifiers = tasks
                .Where(task => task.Status == TaskConstants.CompletedStatus)
                .Select(task => task.Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (targetTask.Status is not (TaskConstants.ReadyStatus or TaskConstants.InProgressStatus)
                || !string.IsNullOrWhiteSpace(targetTask.ValidationResult)
                || targetTask.EarliestStartAt > currentTime
                || targetTask.DependencyIdentifiers.Any(identifier => !completedIdentifiers.Contains(identifier)))
            {
                throw new InvalidOperationException("開始可能日、依存関係、または検証条件を満たしていません。");
            }
        }
        if ((actionName is "continue" or "interrupt") && targetTask.Status != TaskConstants.InProgressStatus)
        {
            throw new InvalidOperationException("実行中ではないタスクには続行または中断を適用できません。");
        }
        if (actionName == "postpone"
            && targetTask.Status is not (TaskConstants.ReadyStatus or TaskConstants.InProgressStatus))
        {
            throw new InvalidOperationException("実行可能または実行中のタスクだけを延期できます。");
        }
    }

    /// <summary>指定タスクを開始して確認予定を設定する。</summary>
    private async Task StartTaskAsync(
        ManagedTask targetTask,
        IReadOnlyList<ManagedTask> tasks,
        TaskManagerSettings settings,
        DateTimeOffset currentTime,
        string source,
        CancellationToken cancellationToken)
    {
        // 他の実行中タスクを中断して多重実行を防ぐ。
        foreach (ManagedTask otherTask in tasks.Where(task =>
            task.Status == TaskConstants.InProgressStatus
            && !string.Equals(task.Identifier, targetTask.Identifier, StringComparison.OrdinalIgnoreCase)))
        {
            otherTask.Status = TaskConstants.ReadyStatus;
            otherTask.StartedAt = null;
            otherTask.FollowUpAt = null;
            otherTask.FollowUpNotifiedAt = null;
            otherTask.DeferralCount += 1;
            otherTask.UpdatedAt = currentTime;
            await repository.SaveTaskAsync(otherTask, "自動中断", source, cancellationToken);
        }
        int remainingMinutes = targetTask.RemainingMinutes > 0 ? targetTask.RemainingMinutes : targetTask.EstimatedMinutes;
        int suggestedMinutes = targetTask.SuggestedMinutes > 0
            ? targetTask.SuggestedMinutes
            : Math.Min(remainingMinutes, settings.MaximumWorkMinutes);
        // 推奨作業時間へ完了確認猶予を加算する。
        targetTask.Status = TaskConstants.InProgressStatus;
        targetTask.StartedAt = currentTime;
        targetTask.FollowUpAt = currentTime.AddMinutes(suggestedMinutes + settings.FollowUpGraceMinutes);
        targetTask.FollowUpNotifiedAt = null;
        targetTask.SuggestedMinutes = suggestedMinutes;
        targetTask.UpdatedAt = currentTime;
        await repository.SaveTaskAsync(targetTask, "開始", source, cancellationToken);
    }

    /// <summary>指定タスクを完了して親タスクを整合させる。</summary>
    private async Task CompleteTaskAsync(
        ManagedTask targetTask,
        IReadOnlyList<ManagedTask> tasks,
        DateTimeOffset currentTime,
        string source,
        CancellationToken cancellationToken)
    {
        // 残時間と確認予定を消去し、完了によって解放される後続タスクを記録する。
        List<string> completedIdentifiers = [targetTask.Identifier];
        targetTask.Status = TaskConstants.CompletedStatus;
        targetTask.RemainingMinutes = 0;
        targetTask.CompletedAt = currentTime;
        targetTask.StartedAt = null;
        targetTask.FollowUpAt = null;
        targetTask.FollowUpNotifiedAt = null;
        targetTask.UpdatedAt = currentTime;
        await repository.SaveTaskAsync(targetTask, "完了", source, cancellationToken);
        if (!string.IsNullOrWhiteSpace(targetTask.ParentIdentifier))
        {
            List<ManagedTask> siblings = tasks.Where(task => task.ParentIdentifier == targetTask.ParentIdentifier).ToList();
            bool allChildrenFinished = siblings.All(task =>
                string.Equals(task.Identifier, targetTask.Identifier, StringComparison.OrdinalIgnoreCase)
                || task.Status is TaskConstants.CompletedStatus or TaskConstants.CancelledStatus);
            ManagedTask? parentTask = tasks.FirstOrDefault(task => task.Identifier == targetTask.ParentIdentifier);
            if (allChildrenFinished && parentTask is not null)
            {
                parentTask.Status = TaskConstants.CompletedStatus;
                parentTask.RemainingMinutes = 0;
                parentTask.CompletedAt = currentTime;
                parentTask.UpdatedAt = currentTime;
                await repository.SaveTaskAsync(parentTask, "親タスク完了", TaskConstants.SystemSource, cancellationToken);
                completedIdentifiers.Add(parentTask.Identifier);
            }
        }
        await UpdateReleasedDependentTasksAsync(
            completedIdentifiers,
            tasks,
            currentTime,
            source,
            cancellationToken);
    }

    /// <summary>最後の前提タスク完了時に後続タスクの開始可能日を更新する。</summary>
    private async Task UpdateReleasedDependentTasksAsync(
        IReadOnlyCollection<string> completedIdentifiers,
        IReadOnlyList<ManagedTask> tasks,
        DateTimeOffset currentTime,
        string source,
        CancellationToken cancellationToken)
    {
        // 今回完了したタスクを参照し、すべての前提が完了した後続タスクだけを抽出する。
        HashSet<string> newlyCompletedIdentifiers = completedIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allCompletedIdentifiers = tasks
            .Where(task => task.Status == TaskConstants.CompletedStatus)
            .Select(task => task.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedTask dependentTask in tasks.Where(task =>
            task.Status is not (TaskConstants.CompletedStatus or TaskConstants.CancelledStatus or TaskConstants.DraftStatus)
            && task.DependencyIdentifiers.Any(newlyCompletedIdentifiers.Contains)
            && task.DependencyIdentifiers.All(allCompletedIdentifiers.Contains)))
        {
            // 明示された未来の開始可能日は早めず、前提待ち期間を経過評価から除外する。
            if (dependentTask.EarliestStartAt >= currentTime)
            {
                continue;
            }
            dependentTask.EarliestStartAt = currentTime;
            dependentTask.UpdatedAt = currentTime;
            await repository.SaveTaskAsync(dependentTask, "依存解放", source, cancellationToken);
        }
    }

    /// <summary>実行中タスクの確認予定を延長する。</summary>
    private async Task ContinueTaskAsync(
        ManagedTask targetTask,
        TaskManagerSettings settings,
        DateTimeOffset currentTime,
        string source,
        CancellationToken cancellationToken)
    {
        // 実行中以外は続行できないようにする。
        if (targetTask.Status != TaskConstants.InProgressStatus)
        {
            throw new InvalidOperationException("実行中ではないタスクは続行できません。");
        }
        int continuationMinutes = Math.Min(
            targetTask.RemainingMinutes > 0 ? targetTask.RemainingMinutes : targetTask.EstimatedMinutes,
            settings.MaximumWorkMinutes);
        targetTask.FollowUpAt = currentTime.AddMinutes(continuationMinutes + settings.FollowUpGraceMinutes);
        targetTask.FollowUpNotifiedAt = null;
        targetTask.SuggestedMinutes = continuationMinutes;
        targetTask.UpdatedAt = currentTime;
        await repository.SaveTaskAsync(targetTask, "続行", source, cancellationToken);
    }

    /// <summary>中止された依存先を待つ後続タスクを要確認へ変更する。</summary>
    private async Task MarkCancelledDependencyTasksAsync(
        string cancelledIdentifier,
        string source,
        CancellationToken cancellationToken)
    {
        // 後続タスクを黙って実行可能にせずユーザー判断を要求する。
        List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
        validation.ValidateAll(tasks);
        foreach (ManagedTask dependentTask in tasks.Where(task =>
            task.DependencyIdentifiers.Contains(cancelledIdentifier, StringComparer.OrdinalIgnoreCase)
            && task.Status is not (TaskConstants.CompletedStatus or TaskConstants.CancelledStatus)))
        {
            dependentTask.Status = TaskConstants.NeedsReviewStatus;
            dependentTask.UpdatedAt = clock.GetLocalNow();
            await repository.SaveTaskAsync(dependentTask, "依存先中止", source, cancellationToken);
        }
    }

    /// <summary>全タスクの現在の検証状態を比較用に保存する。</summary>
    private static Dictionary<string, ValidationSnapshot> CaptureValidationSnapshots(IReadOnlyList<ManagedTask> tasks)
    {
        // 更新によって検証エラーが発生または解消したタスクだけを書き戻す。
        return tasks.ToDictionary(
            task => task.Identifier,
            task => new ValidationSnapshot(task.ValidationResult, task.Status),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>検証結果に応じて要確認への移動または自動復帰を適用する。</summary>
    private static void ApplyValidationStatus(ManagedTask task, ValidationSnapshot? previousSnapshot)
    {
        // システム検証が原因の要確認だけをエラー解消時に実行可能へ戻す。
        if (!string.IsNullOrWhiteSpace(task.ValidationResult)
            && task.Status is not (TaskConstants.DraftStatus or TaskConstants.CompletedStatus or TaskConstants.CancelledStatus))
        {
            task.Status = TaskConstants.NeedsReviewStatus;
        }
        else if (string.IsNullOrWhiteSpace(task.ValidationResult)
            && previousSnapshot is not null
            && !string.IsNullOrWhiteSpace(previousSnapshot.ValidationResult)
            && previousSnapshot.Status == TaskConstants.NeedsReviewStatus
            && task.Status == TaskConstants.NeedsReviewStatus)
        {
            task.Status = TaskConstants.ReadyStatus;
        }
    }

    /// <summary>検証結果または検証由来の状態が変わった関連タスクだけを保存する。</summary>
    private async Task PersistValidationChangesAsync(
        IReadOnlyList<ManagedTask> tasks,
        IReadOnlyDictionary<string, ValidationSnapshot> validationSnapshots,
        string excludedIdentifier,
        string source,
        CancellationToken cancellationToken)
    {
        // 5,000件時にも不要なSQLite書き込みを避けるため差分だけを処理する。
        foreach (ManagedTask task in tasks.Where(task =>
            !string.Equals(task.Identifier, excludedIdentifier, StringComparison.OrdinalIgnoreCase)
            && validationSnapshots.ContainsKey(task.Identifier)))
        {
            ValidationSnapshot previousSnapshot = validationSnapshots[task.Identifier];
            ApplyValidationStatus(task, previousSnapshot);
            if (string.Equals(task.ValidationResult, previousSnapshot.ValidationResult, StringComparison.Ordinal)
                && string.Equals(task.Status, previousSnapshot.Status, StringComparison.Ordinal))
            {
                continue;
            }
            task.UpdatedAt = clock.GetLocalNow();
            await repository.SaveTaskAsync(task, "再検証", source, cancellationToken);
        }
    }

    /// <summary>自動停止対象のログと関連タスクを予定停止時刻で終了する。</summary>
    private async Task<TimeEntryRecord> StopTimedOutEntryAsync(
        TimeEntryRecord timeEntry,
        DateTimeOffset automaticStopAt,
        CancellationToken cancellationToken)
    {
        // タスク紐付きでは延期回数を増やさず実行可能状態へ戻す。
        if (!string.IsNullOrWhiteSpace(timeEntry.TaskIdentifier))
        {
            ManagedTask? task = await repository.GetTaskAsync(timeEntry.TaskIdentifier, cancellationToken);
            if (task is not null)
            {
                task.Status = TaskConstants.ReadyStatus;
                task.StartedAt = null;
                task.FollowUpAt = null;
                task.FollowUpNotifiedAt = null;
                task.UpdatedAt = automaticStopAt;
                await repository.SaveTaskAsync(
                    task,
                    "タイマー自動中断",
                    TaskConstants.SystemSource,
                    cancellationToken);
                await recommendations.RefreshAsync(cancellationToken);
                return await RequireTimeEntryAsync(timeEntry.Identifier, cancellationToken);
            }
        }
        return await timeEntries.StopAsync(
            timeEntry,
            automaticStopAt,
            TimeTrackingConstants.AutomaticTimeoutStopReason,
            needsReview: true,
            reviewReason: "長時間警告後に操作がなかったため自動停止しました。",
            TaskConstants.SystemSource,
            cancellationToken);
    }

    /// <summary>作業ログの追加・更新入力を関連先とともに正規化する。</summary>
    private async Task NormalizeTimeEntryMutationAsync(
        TimeEntryMutationRequest request,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken)
    {
        // 期間逆転、未来終了、存在しないタスクやプロジェクトを保存前に拒否する。
        if (request.StartAt == default || request.EndAt == default || request.EndAt <= request.StartAt)
        {
            throw new InvalidOperationException("開始日時より後の終了日時を指定してください。");
        }
        if (request.EndAt > currentTime.AddMinutes(1))
        {
            throw new InvalidOperationException("未来の終了日時は指定できません。");
        }
        request.TaskIdentifier = NormalizeOptionalIdentifier(request.TaskIdentifier);
        request.ProjectIdentifier = NormalizeOptionalIdentifier(request.ProjectIdentifier);
        if (request.TaskIdentifier is not null)
        {
            ManagedTask task = await repository.GetTaskAsync(request.TaskIdentifier, cancellationToken)
                ?? throw new KeyNotFoundException($"タスクが見つかりません: {request.TaskIdentifier}");
            request.Title = string.IsNullOrWhiteSpace(request.Title) ? task.Title : request.Title;
            request.ProjectIdentifier ??= task.ProjectIdentifier;
        }
        request.Title = NormalizeTimeEntryTitle(request.Title);
        if (request.ProjectIdentifier is not null)
        {
            await timeEntries.GetProjectNameAsync(request.ProjectIdentifier, cancellationToken);
        }
    }

    /// <summary>作業ログの重複区間が明示確認されていることを検証する。</summary>
    private async Task EnsureOverlapConfirmedAsync(
        TimeEntryMutationRequest request,
        string? excludedIdentifier,
        CancellationToken cancellationToken)
    {
        // 共通の区間検証へ手入力要求の値を渡す。
        await EnsureOverlapConfirmedAsync(
            request.StartAt,
            request.EndAt,
            excludedIdentifier,
            request.AllowOverlap,
            cancellationToken);
    }

    /// <summary>指定作業区間の重複が明示確認されていることを検証する。</summary>
    private async Task EnsureOverlapConfirmedAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? excludedIdentifier,
        bool allowOverlap,
        CancellationToken cancellationToken)
    {
        // 重複許可がない場合だけ競合ログを取得してHTTP 409向け例外へ変換する。
        if (allowOverlap)
        {
            return;
        }
        List<TimeEntryRecord> conflicts = await timeEntries.FindOverlapsAsync(
            startAt,
            endAt,
            excludedIdentifier,
            cancellationToken);
        if (conflicts.Count > 0)
        {
            throw new TimeEntryOverlapException(conflicts);
        }
    }

    /// <summary>作業ログIDから対象を取得し、存在しなければ例外にする。</summary>
    private async Task<TimeEntryRecord> RequireTimeEntryAsync(
        string timeEntryIdentifier,
        CancellationToken cancellationToken)
    {
        // 空IDも見つからない対象として統一したエラーを返す。
        TimeEntryRecord? timeEntry = string.IsNullOrWhiteSpace(timeEntryIdentifier)
            ? null
            : await timeEntries.GetByIdentifierAsync(timeEntryIdentifier, cancellationToken);
        return timeEntry
            ?? throw new KeyNotFoundException($"作業ログが見つかりません: {timeEntryIdentifier}");
    }

    /// <summary>作業ログ表示名を検証して整形する。</summary>
    private static string NormalizeTimeEntryTitle(string title)
    {
        // 空欄と過度に長い名称を拒否する。
        string normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length == 0)
        {
            throw new InvalidOperationException("活動名を入力してください。");
        }
        if (normalizedTitle.Length > 200)
        {
            throw new InvalidOperationException("活動名は200文字以内で入力してください。");
        }
        return normalizedTitle;
    }

    /// <summary>空の任意識別子をNULLへ正規化する。</summary>
    private static string? NormalizeOptionalIdentifier(string? identifier)
    {
        // 前後空白を除去し、空文字を未指定として扱う。
        return string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
    }

    /// <summary>検証前のエラー文と状態を保持する。</summary>
    private sealed record ValidationSnapshot(string ValidationResult, string Status);
}
