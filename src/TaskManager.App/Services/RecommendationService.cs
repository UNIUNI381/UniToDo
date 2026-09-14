using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>候補タスクを評価し現在実行すべき1件を選択する。</summary>
public sealed class RecommendationService(
    TaskRepository taskRepository,
    CalendarAvailabilityService calendarAvailabilityService,
    TimeProvider timeProvider)
{
    // 保存層、空き時間計算、現在時刻の供給元を保持する。
    private readonly TaskRepository repository = taskRepository;
    private readonly CalendarAvailabilityService availabilityService = calendarAvailabilityService;
    private readonly TimeProvider clock = timeProvider;

    /// <summary>現在条件から推薦を再計算して保存する。</summary>
    public async Task<RecommendationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // タスク、設定、予定を読み込んで1件だけを選択する。
        List<ManagedTask> tasks = await repository.GetRecommendationTasksAsync(cancellationToken);
        TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
        List<CalendarEventRecord> calendarEvents = await repository.GetCalendarEventsAsync(cancellationToken);
        DateTimeOffset currentTime = clock.GetLocalNow();
        RecommendationResult result = SelectRecommendation(currentTime, settings, tasks, calendarEvents);
        string recommendationKey = result.Recommendation is null
            ? $"NONE:{result.EmptyReason}"
            : result.Recommendation.Task.Identifier;
        string recommendationSummary = result.Recommendation?.Task.Title ?? result.EmptyReason;
        string recommendationDetails = result.Recommendation?.Reason ?? string.Empty;
        await repository.SaveEvaluationsAsync(
            result.Evaluations,
            recommendationKey,
            recommendationSummary,
            recommendationDetails,
            cancellationToken);
        string? calendarUpdatedText = await repository.GetStateAsync("calendar_updated_at", cancellationToken);
        string? calendarError = await repository.GetStateAsync("calendar_error", cancellationToken);
        return new RecommendationResult
        {
            Recommendation = result.Recommendation,
            Evaluations = result.Evaluations,
            CanForceRecommendation = result.CanForceRecommendation,
            CurrentSlot = result.CurrentSlot,
            EmptyReason = result.EmptyReason,
            FollowUpTask = result.FollowUpTask,
            CalendarUpdatedAt = DateTimeOffset.TryParse(calendarUpdatedText, out DateTimeOffset updatedAt) ? updatedAt : null,
            CalendarError = calendarError ?? string.Empty,
            CalendarWidget = BuildCalendarWidget(currentTime, settings, calendarEvents, tasks)
        };
    }

    /// <summary>計算済み候補を区分またはプロジェクトで絞り、範囲内の最上位を返す。</summary>
    public static RecommendationResult FilterRecommendation(
        RecommendationResult sourceResult,
        string? category,
        string? projectIdentifier,
        bool forceDisplay = false)
    {
        // 全タスクで算出済みの優先度を維持したまま、画面指定の範囲だけを比較する。
        IEnumerable<TaskEvaluation> filteredEvaluations = sourceResult.Evaluations;
        if (!string.IsNullOrWhiteSpace(category))
        {
            filteredEvaluations = filteredEvaluations.Where(evaluation =>
                string.Equals(evaluation.Task.Category, category, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(projectIdentifier))
        {
            filteredEvaluations = filteredEvaluations.Where(evaluation =>
                string.Equals(
                    evaluation.Task.ProjectIdentifier,
                    projectIdentifier,
                    StringComparison.OrdinalIgnoreCase));
        }
        List<TaskEvaluation> matchingEvaluations = filteredEvaluations.ToList();

        // 通常時は現在の空き時間へ収まる候補だけ、強制表示時は評価可能な全候補から確定する。
        IEnumerable<TaskEvaluation> selectionEvaluations = forceDisplay
            ? matchingEvaluations
            : matchingEvaluations.Where(evaluation => FitsCurrentSlot(
                evaluation.Task,
                sourceResult.CurrentSlot.AvailableMinutes,
                evaluation.SuggestedMinutes));
        TaskEvaluation? recommendation = selectionEvaluations
            .OrderByDescending(evaluation => evaluation.PriorityScore)
            .ThenBy(evaluation => evaluation.EffectiveDeadlineAt ?? DateTimeOffset.MaxValue)
            .ThenBy(evaluation => evaluation.Task.CreatedAt)
            .FirstOrDefault();
        string emptyReason = sourceResult.Evaluations.Count == 0
            ? sourceResult.EmptyReason
            : "選択した範囲に現在実行できるタスクがありません。";
        return new RecommendationResult
        {
            Recommendation = recommendation,
            Evaluations = matchingEvaluations,
            CanForceRecommendation = matchingEvaluations.Count > 0,
            CurrentSlot = sourceResult.CurrentSlot,
            EmptyReason = recommendation is null ? emptyReason : string.Empty,
            FollowUpTask = sourceResult.FollowUpTask,
            CalendarUpdatedAt = sourceResult.CalendarUpdatedAt,
            CalendarError = sourceResult.CalendarError,
            CalendarWidget = sourceResult.CalendarWidget,
            ActiveTimeEntry = sourceResult.ActiveTimeEntry,
            ReviewTimeEntries = sourceResult.ReviewTimeEntries
        };
    }

    /// <summary>今日から7日分のカレンダーウィジェット表示内容を構築する。</summary>
    public static CalendarWidgetResult BuildCalendarWidget(
        DateTimeOffset currentTime,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents,
        IReadOnlyList<ManagedTask>? tasks = null)
    {
        // 設定済みタイムゾーンで今日の開始と7日後の境界を求める。
        TimeZoneInfo displayTimeZone;
        try
        {
            displayTimeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneIdentifier);
        }
        catch (TimeZoneNotFoundException)
        {
            displayTimeZone = TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            displayTimeZone = TimeZoneInfo.Local;
        }
        DateTimeOffset displayTime = TimeZoneInfo.ConvertTime(currentTime, displayTimeZone);
        DateTime rangeStartDate = displayTime.Date;
        DateTimeOffset rangeStart = new(
            rangeStartDate,
            displayTimeZone.GetUtcOffset(rangeStartDate));
        DateTime rangeEndDate = rangeStartDate.AddDays(7);
        DateTimeOffset rangeEnd = new(
            rangeEndDate,
            displayTimeZone.GetUtcOffset(rangeEndDate));

        // 境界へ重なる予定だけを画面表示用の最小モデルへ変換する。
        List<CalendarWidgetEvent> visibleEvents = calendarEvents
            .Where(calendarEvent => calendarEvent.EndAt > rangeStart && calendarEvent.StartAt < rangeEnd)
            .OrderBy(calendarEvent => calendarEvent.StartAt)
            .ThenBy(calendarEvent => calendarEvent.EndAt)
            .Select(calendarEvent => new CalendarWidgetEvent
            {
                EventIdentifier = calendarEvent.EventIdentifier,
                CalendarIdentifier = calendarEvent.CalendarIdentifier,
                Title = calendarEvent.Title,
                StartAt = calendarEvent.StartAt,
                EndAt = calendarEvent.EndAt,
                Location = calendarEvent.Location,
                IsAllDay = calendarEvent.IsAllDay,
                IsBusy = calendarEvent.IsBusy
            })
            .ToList();

        // 下書き・完了・中止を除く期限付きタスクを同じ7日範囲へ投影する。
        List<CalendarWidgetDeadline> visibleDeadlines = (tasks ?? [])
            .Where(task => task.DeadlineAt.HasValue)
            .Where(task => task.DeadlineType != TaskConstants.NoDeadlineType)
            .Where(task => task.Status is not TaskConstants.DraftStatus
                and not TaskConstants.CompletedStatus
                and not TaskConstants.CancelledStatus)
            .Where(task => task.DeadlineAt >= rangeStart && task.DeadlineAt < rangeEnd)
            .OrderBy(task => task.DeadlineAt)
            .ThenBy(task => task.CreatedAt)
            .Select(task => new CalendarWidgetDeadline
            {
                TaskIdentifier = task.Identifier,
                ProjectIdentifier = task.ProjectIdentifier,
                Title = task.Title,
                DeadlineAt = task.DeadlineAt!.Value,
                DeadlineType = task.DeadlineType,
                Status = task.Status
            })
            .ToList();
        return new CalendarWidgetResult
        {
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            TimeZoneIdentifier = displayTimeZone.Id,
            ActivityStart = settings.ActivityStart,
            ActivityEnd = settings.ActivityEnd,
            EmbedUrl = settings.CalendarEmbedUrl,
            Events = visibleEvents,
            Deadlines = visibleDeadlines
        };
    }

    /// <summary>与えられたデータだけで推薦を決定する。</summary>
    public RecommendationResult SelectRecommendation(
        DateTimeOffset currentTime,
        TaskManagerSettings settings,
        IReadOnlyList<ManagedTask> tasks,
        IReadOnlyList<CalendarEventRecord> calendarEvents)
    {
        // 現在の連続空き時間と確認期限を取得する。
        AvailableSlot currentSlot = availabilityService.CalculateCurrentAvailableSlot(currentTime, settings, calendarEvents);
        ManagedTask? followUpTask = tasks.FirstOrDefault(task =>
            task.Status == TaskConstants.InProgressStatus && task.FollowUpAt.HasValue && task.FollowUpAt <= currentTime);

        // タスク一覧用の優先度は活動時間外でも算出できるよう、最低限の仮想作業枠を確保する。
        int priorityEvaluationMinutes = currentSlot.AvailableMinutes >= settings.MinimumWorkMinutes
            ? currentSlot.AvailableMinutes
            : Math.Max(settings.MaximumWorkMinutes, settings.MinimumWorkMinutes) + 5;
        HashSet<string> completedIdentifiers = tasks
            .Where(task => task.Status == TaskConstants.CompletedStatus)
            .Select(task => task.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> dependentCounts = BuildDependentCounts(tasks);
        List<ManagedTask> activeWorkTasks = GetActiveWorkTasks(tasks);
        DeadlinePressureAnalysis deadlinePressure = BuildDeadlinePressureAnalysis(
            currentTime,
            settings,
            activeWorkTasks,
            calendarEvents,
            task => task.DeadlineAt.HasValue && task.DeadlineType != TaskConstants.NoDeadlineType);
        DeadlinePressureAnalysis strictDeadlinePressure = BuildDeadlinePressureAnalysis(
            currentTime,
            settings,
            activeWorkTasks,
            calendarEvents,
            task => task.DeadlineAt.HasValue && task.DeadlineType == TaskConstants.StrictDeadlineType);
        DateTimeOffset? earliestInfeasibleStrictDeadline = strictDeadlinePressure.Checkpoints
            .Where(checkpoint => checkpoint.SlackMinutes < 0)
            .Select(checkpoint => (DateTimeOffset?)checkpoint.DeadlineAt)
            .FirstOrDefault();
        List<TaskEvaluation> evaluations = [];
        foreach (ManagedTask task in tasks)
        {
            if (!IsPriorityEligible(task, currentTime, completedIdentifiers))
            {
                continue;
            }
            TaskEvaluation evaluation = EvaluateTask(
                task,
                currentTime,
                settings,
                calendarEvents,
                priorityEvaluationMinutes,
                dependentCounts.GetValueOrDefault(task.Identifier),
                deadlinePressure,
                strictDeadlinePressure,
                earliestInfeasibleStrictDeadline);
            if (evaluation.SuggestedMinutes > 0)
            {
                evaluations.Add(evaluation);
            }
        }
        // ダッシュボードの通常推薦だけは現在の実作業枠へ収まる候補に限定する。
        List<TaskEvaluation> currentSlotEvaluations = evaluations
            .Where(evaluation => FitsCurrentSlot(
                evaluation.Task,
                currentSlot.AvailableMinutes,
                evaluation.SuggestedMinutes))
            .ToList();
        if (currentSlotEvaluations.Count == 0)
        {
            string emptyReason = currentSlot.AvailableMinutes < settings.MinimumWorkMinutes
                ? string.IsNullOrWhiteSpace(currentSlot.Reason)
                    ? "空き時間が15分未満のため、休憩または簡単な整理をしてください。"
                    : currentSlot.Reason
                : "現在の空き時間と実行条件に合うタスクがありません。";
            return new RecommendationResult
            {
                Evaluations = evaluations,
                CanForceRecommendation = evaluations.Count > 0,
                CurrentSlot = currentSlot,
                EmptyReason = emptyReason,
                FollowUpTask = followUpTask
            };
        }
        return new RecommendationResult
        {
            Recommendation = ChooseWithContinuity(currentSlotEvaluations),
            Evaluations = evaluations,
            CanForceRecommendation = evaluations.Count > 0,
            CurrentSlot = currentSlot,
            FollowUpTask = followUpTask
        };
    }

    /// <summary>累積締切評価へ含める未完了の作業タスクを抽出する。</summary>
    private static List<ManagedTask> GetActiveWorkTasks(IReadOnlyList<ManagedTask> tasks)
    {
        // 下書き、要確認、待機、完了、中止、検証エラーを作業量から除外する。
        return tasks
            .Where(task => task.Status is TaskConstants.ReadyStatus or TaskConstants.InProgressStatus)
            .Where(task => string.IsNullOrWhiteSpace(task.ValidationResult))
            .ToList();
    }

    /// <summary>実効期限ごとの累積作業量、作業可能時間、余裕時間を計算する。</summary>
    private DeadlinePressureAnalysis BuildDeadlinePressureAnalysis(
        DateTimeOffset currentTime,
        TaskManagerSettings settings,
        IReadOnlyList<ManagedTask> activeWorkTasks,
        IReadOnlyList<CalendarEventRecord> calendarEvents,
        Func<ManagedTask, bool> deadlineSeedPredicate)
    {
        // 後続タスクの期限を先行タスクへ伝播し、期限日時ごとに必要時間を集約する。
        Dictionary<string, EffectiveDeadline> effectiveDeadlines = BuildEffectiveDeadlines(
            activeWorkTasks,
            deadlineSeedPredicate);
        List<DeadlineCheckpoint> checkpoints = [];
        Dictionary<DateTimeOffset, DeadlineCheckpoint> checkpointsByDeadline = [];
        double cumulativeBufferedMinutes = 0;
        IEnumerable<IGrouping<DateTimeOffset, ManagedTask>> deadlineGroups = activeWorkTasks
            .Where(task => effectiveDeadlines.ContainsKey(task.Identifier))
            .GroupBy(task => effectiveDeadlines[task.Identifier].DeadlineAt)
            .OrderBy(deadlineGroup => deadlineGroup.Key);
        foreach (IGrouping<DateTimeOffset, ManagedTask> deadlineGroup in deadlineGroups)
        {
            // 各期限までに必要な全タスクへ予備時間率を個別適用して累積する。
            double groupBufferedMinutes = deadlineGroup.Sum(task => CalculateBufferedWorkMinutes(task, settings));
            cumulativeBufferedMinutes += groupBufferedMinutes;
            double availableMinutes = availabilityService.CalculateAvailableMinutesUntil(
                currentTime,
                deadlineGroup.Key,
                settings,
                calendarEvents);
            DeadlineCheckpoint checkpoint = new(
                deadlineGroup.Key,
                groupBufferedMinutes,
                cumulativeBufferedMinutes,
                availableMinutes,
                availableMinutes - cumulativeBufferedMinutes);
            checkpoints.Add(checkpoint);
            checkpointsByDeadline[deadlineGroup.Key] = checkpoint;
        }
        return new DeadlinePressureAnalysis(effectiveDeadlines, checkpoints, checkpointsByDeadline);
    }

    /// <summary>自身または未完了の後続タスクから最も早い実効期限を求める。</summary>
    private static Dictionary<string, EffectiveDeadline> BuildEffectiveDeadlines(
        IReadOnlyList<ManagedTask> activeWorkTasks,
        Func<ManagedTask, bool> deadlineSeedPredicate)
    {
        // 依存先から後続タスクを引ける逆向きグラフを構築する。
        Dictionary<string, ManagedTask> tasksByIdentifier = activeWorkTasks
            .ToDictionary(task => task.Identifier, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> dependentIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedTask task in activeWorkTasks)
        {
            foreach (string dependencyIdentifier in task.DependencyIdentifiers)
            {
                if (!tasksByIdentifier.ContainsKey(dependencyIdentifier))
                {
                    continue;
                }
                if (!dependentIdentifiers.TryGetValue(dependencyIdentifier, out List<string>? identifiers))
                {
                    identifiers = [];
                    dependentIdentifiers[dependencyIdentifier] = identifiers;
                }
                identifiers.Add(task.Identifier);
            }
        }

        // メモ化した深さ優先探索で期限を先行タスクへ一度だけ伝播する。
        Dictionary<string, EffectiveDeadline?> resolvedDeadlines = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> resolvingIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string taskIdentifier in tasksByIdentifier.Keys)
        {
            ResolveEffectiveDeadline(
                taskIdentifier,
                tasksByIdentifier,
                dependentIdentifiers,
                deadlineSeedPredicate,
                resolvedDeadlines,
                resolvingIdentifiers);
        }
        return resolvedDeadlines
            .Where(resolvedDeadline => resolvedDeadline.Value is not null)
            .ToDictionary(
                resolvedDeadline => resolvedDeadline.Key,
                resolvedDeadline => resolvedDeadline.Value!,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>1件のタスクへ後続タスクから実効期限を再帰的に伝播する。</summary>
    private static EffectiveDeadline? ResolveEffectiveDeadline(
        string taskIdentifier,
        IReadOnlyDictionary<string, ManagedTask> tasksByIdentifier,
        IReadOnlyDictionary<string, List<string>> dependentIdentifiers,
        Func<ManagedTask, bool> deadlineSeedPredicate,
        IDictionary<string, EffectiveDeadline?> resolvedDeadlines,
        ISet<string> resolvingIdentifiers)
    {
        // 解決済み値を再利用し、検証漏れの循環があっても再帰を終了する。
        if (resolvedDeadlines.TryGetValue(taskIdentifier, out EffectiveDeadline? resolvedDeadline))
        {
            return resolvedDeadline;
        }
        if (!resolvingIdentifiers.Add(taskIdentifier))
        {
            return null;
        }

        ManagedTask task = tasksByIdentifier[taskIdentifier];
        EffectiveDeadline? effectiveDeadline = deadlineSeedPredicate(task) && task.DeadlineAt.HasValue
            ? new EffectiveDeadline(task.DeadlineAt.Value, task.DeadlineType)
            : null;
        if (dependentIdentifiers.TryGetValue(taskIdentifier, out List<string>? taskDependentIdentifiers))
        {
            foreach (string dependentIdentifier in taskDependentIdentifiers)
            {
                EffectiveDeadline? dependentDeadline = ResolveEffectiveDeadline(
                    dependentIdentifier,
                    tasksByIdentifier,
                    dependentIdentifiers,
                    deadlineSeedPredicate,
                    resolvedDeadlines,
                    resolvingIdentifiers);
                effectiveDeadline = SelectEarlierDeadline(effectiveDeadline, dependentDeadline);
            }
        }
        resolvingIdentifiers.Remove(taskIdentifier);
        resolvedDeadlines[taskIdentifier] = effectiveDeadline;
        return effectiveDeadline;
    }

    /// <summary>2つの期限から早い日時を選び同時刻では厳守を優先する。</summary>
    private static EffectiveDeadline? SelectEarlierDeadline(
        EffectiveDeadline? currentDeadline,
        EffectiveDeadline? candidateDeadline)
    {
        // 期限なしを除き、時刻と期限種別の順に実効期限を確定する。
        if (currentDeadline is null)
        {
            return candidateDeadline;
        }
        if (candidateDeadline is null || currentDeadline.DeadlineAt < candidateDeadline.DeadlineAt)
        {
            return currentDeadline;
        }
        if (candidateDeadline.DeadlineAt < currentDeadline.DeadlineAt)
        {
            return candidateDeadline;
        }
        string deadlineType = currentDeadline.DeadlineType == TaskConstants.StrictDeadlineType
            || candidateDeadline.DeadlineType == TaskConstants.StrictDeadlineType
            ? TaskConstants.StrictDeadlineType
            : TaskConstants.TargetDeadlineType;
        return new EffectiveDeadline(currentDeadline.DeadlineAt, deadlineType);
    }

    /// <summary>タスクの残時間へ最低作業時間と予備時間率を適用する。</summary>
    private static double CalculateBufferedWorkMinutes(ManagedTask task, TaskManagerSettings settings)
    {
        // 未入力や0分の残時間を見積時間で補い、計算上の最低作業時間を保証する。
        int rawRemainingMinutes = task.RemainingMinutes > 0 ? task.RemainingMinutes : task.EstimatedMinutes;
        int remainingMinutes = Math.Max(rawRemainingMinutes, settings.MinimumWorkMinutes);
        return remainingMinutes * (1 + settings.BufferRatio);
    }

    /// <summary>今回の作業がそれ以前の締切群の余裕時間を使い切らないか判定する。</summary>
    private static bool ProtectsEarlierDeadlines(
        EffectiveDeadline? effectiveDeadline,
        int suggestedMinutes,
        IReadOnlyList<DeadlineCheckpoint> checkpoints)
    {
        // 候補自身が寄与しない早い期限だけを対象に今回の作業時間を差し引く。
        foreach (DeadlineCheckpoint checkpoint in checkpoints)
        {
            if (effectiveDeadline is not null && checkpoint.DeadlineAt >= effectiveDeadline.DeadlineAt)
            {
                break;
            }
            if (checkpoint.SlackMinutes < suggestedMinutes)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>1件のタスクについて優先度要素とスコアを計算する。</summary>
    private TaskEvaluation EvaluateTask(
        ManagedTask task,
        DateTimeOffset currentTime,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents,
        int availableMinutes,
        int dependentCount,
        DeadlinePressureAnalysis deadlinePressure,
        DeadlinePressureAnalysis strictDeadlinePressure,
        DateTimeOffset? earliestInfeasibleStrictDeadline)
    {
        // 実効期限までの累積作業量と作業可能時間の差から期限リスクを算出する。
        int rawRemainingMinutes = task.RemainingMinutes > 0 ? task.RemainingMinutes : task.EstimatedMinutes;
        int remainingMinutes = Math.Max(rawRemainingMinutes, settings.MinimumWorkMinutes);
        EffectiveDeadline? effectiveDeadline = deadlinePressure.EffectiveDeadlines.GetValueOrDefault(task.Identifier);
        DeadlineCheckpoint? deadlineCheckpoint = effectiveDeadline is null
            ? null
            : deadlinePressure.CheckpointsByDeadline.GetValueOrDefault(effectiveDeadline.DeadlineAt);
        double slackMinutes = deadlineCheckpoint?.SlackMinutes ?? double.PositiveInfinity;
        double deadlineRisk = effectiveDeadline is null || deadlineCheckpoint is null
            ? 0
            : CalculateDeadlineRisk(
                effectiveDeadline,
                deadlineCheckpoint.SlackMinutes,
                deadlineCheckpoint.GroupBufferedMinutes,
                currentTime);
        double importanceScore = Clamp((task.Importance - 1) / 4.0);
        double unblockScore = Clamp(dependentCount / 4.0);
        double deferralScore = Clamp(task.DeferralCount / 5.0);
        double continuityScore = task.Status == TaskConstants.InProgressStatus ? 1 : 0;
        double agingScore = CalculateAgingScore(task, currentTime);
        int suggestedMinutes = CalculateSuggestedMinutes(remainingMinutes, availableMinutes, settings);
        bool protectsEarlierDeadlines = ProtectsEarlierDeadlines(
            effectiveDeadline,
            suggestedMinutes,
            deadlinePressure.Checkpoints);
        EffectiveDeadline? strictEffectiveDeadline = strictDeadlinePressure.EffectiveDeadlines.GetValueOrDefault(task.Identifier);
        bool strictDeadlineInfeasible = earliestInfeasibleStrictDeadline.HasValue
            && strictEffectiveDeadline is not null
            && strictEffectiveDeadline.DeadlineAt <= earliestInfeasibleStrictDeadline.Value;
        // 6要素の相対加重平均を0から100のスコアへ換算する。
        double weightedScoreTotal = deadlineRisk * settings.DeadlineWeight
            + importanceScore * settings.ImportanceWeight
            + unblockScore * settings.UnblockWeight
            + deferralScore * settings.DeferralWeight
            + continuityScore * settings.ContinuityWeight
            + agingScore * settings.AgingWeight;
        double weightTotal = settings.DeadlineWeight
            + settings.ImportanceWeight
            + settings.UnblockWeight
            + settings.DeferralWeight
            + settings.ContinuityWeight
            + settings.AgingWeight;
        double weightedScore = weightTotal > 0 ? weightedScoreTotal / weightTotal : 0;
        double priorityScore = Math.Round(Clamp(weightedScore) * 1000) / 10;
        TaskEvaluation provisionalEvaluation = new()
        {
            Task = task,
            EffectiveDeadlineAt = effectiveDeadline?.DeadlineAt,
            DeadlineRisk = deadlineRisk,
            ImportanceScore = importanceScore,
            UnblockScore = unblockScore,
            DeferralScore = deferralScore,
            ContinuityScore = continuityScore,
            AgingScore = agingScore,
            SlackMinutes = effectiveDeadline is null ? null : slackMinutes,
            SuggestedMinutes = suggestedMinutes,
            ProtectsEarlierDeadlines = protectsEarlierDeadlines,
            StrictDeadlineInfeasible = strictDeadlineInfeasible,
            StrictPressureDeadlineAt = earliestInfeasibleStrictDeadline,
            PriorityScore = priorityScore
        };
        return new TaskEvaluation
        {
            Task = task,
            EffectiveDeadlineAt = effectiveDeadline?.DeadlineAt,
            DeadlineRisk = deadlineRisk,
            ImportanceScore = importanceScore,
            UnblockScore = unblockScore,
            DeferralScore = deferralScore,
            ContinuityScore = continuityScore,
            AgingScore = agingScore,
            SlackMinutes = effectiveDeadline is null ? null : slackMinutes,
            SuggestedMinutes = suggestedMinutes,
            ProtectsEarlierDeadlines = protectsEarlierDeadlines,
            StrictDeadlineInfeasible = strictDeadlineInfeasible,
            StrictPressureDeadlineAt = earliestInfeasibleStrictDeadline,
            PriorityScore = priorityScore,
            Reason = BuildReason(provisionalEvaluation, deadlineCheckpoint)
        };
    }

    /// <summary>期限種別と累積余裕時間から期限リスクを計算する。</summary>
    private static double CalculateDeadlineRisk(
        EffectiveDeadline effectiveDeadline,
        double slackMinutes,
        double groupBufferedMinutes,
        DateTimeOffset currentTime)
    {
        // 累積余裕は維持しつつ、後ろの期限が過去の作業量で過大評価されないよう当該期限群の作業量で正規化する。
        if (effectiveDeadline.DeadlineAt <= currentTime || slackMinutes <= 0)
        {
            return 1;
        }
        double normalizationMinutes = Math.Max(groupBufferedMinutes * 4, 240);
        double deadlineRisk = 1 - Clamp(slackMinutes / normalizationMinutes);
        if (effectiveDeadline.DeadlineType == TaskConstants.TargetDeadlineType)
        {
            deadlineRisk *= 0.75;
        }
        return Clamp(deadlineRisk);
    }

    /// <summary>開始可能日からの経過と重要度から経過スコアを計算する。</summary>
    private static double CalculateAgingScore(ManagedTask task, DateTimeOffset currentTime)
    {
        // 未設定の旧タスクは登録日時を開始可能日として扱う。
        DateTimeOffset effectiveStartAt = task.EarliestStartAt ?? task.CreatedAt;
        double elapsedDays = Math.Max(0, (currentTime - effectiveStartAt).TotalDays);
        int appliedImportance = Math.Clamp(task.Importance, 1, 5);

        // 重要度3は30日、重要度1は45日、重要度5は15日で最大値へ達する線形増加とする。
        double importanceRateFactor = 1.5 - ((appliedImportance - 1) * 0.25);
        double daysToMaximumScore = 30 * importanceRateFactor;
        return Clamp(elapsedDays / daysToMaximumScore);
    }

    /// <summary>空き時間に依存しない優先度計算の対象条件を満たすか判定する。</summary>
    private static bool IsPriorityEligible(
        ManagedTask task,
        DateTimeOffset currentTime,
        IReadOnlySet<string> completedIdentifiers)
    {
        // 状態、開始日時、検証、依存を確認する。必要環境は将来の判定機能まで保存だけ継続する。
        if (task.Status is not (TaskConstants.ReadyStatus or TaskConstants.InProgressStatus)
            || !string.IsNullOrWhiteSpace(task.ValidationResult)
            || task.EarliestStartAt > currentTime)
        {
            return false;
        }
        if (task.DependencyIdentifiers.Any(dependencyIdentifier => !completedIdentifiers.Contains(dependencyIdentifier)))
        {
            return false;
        }
        return true;
    }

    /// <summary>タスクが現在の連続空き時間で開始可能か判定する。</summary>
    private static bool FitsCurrentSlot(ManagedTask task, int availableMinutes, int suggestedMinutes)
    {
        // 最小作業量を確保し、分割不可タスクは残時間全体が5分の余白込みで収まる場合だけ許可する。
        if (suggestedMinutes <= 0 || availableMinutes < suggestedMinutes + 5)
        {
            return false;
        }
        int remainingMinutes = task.RemainingMinutes > 0 ? task.RemainingMinutes : task.EstimatedMinutes;
        return task.Splittable || remainingMinutes <= availableMinutes - 5;
    }

    /// <summary>直接後続タスク数を依存先ごとに集計する。</summary>
    private static Dictionary<string, int> BuildDependentCounts(IReadOnlyList<ManagedTask> tasks)
    {
        // 完了または中止済みの後続タスクを集計対象から除外する。
        Dictionary<string, int> dependentCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedTask task in tasks.Where(task => task.Status is not (TaskConstants.CompletedStatus or TaskConstants.CancelledStatus)))
        {
            foreach (string dependencyIdentifier in task.DependencyIdentifiers)
            {
                dependentCounts[dependencyIdentifier] = dependentCounts.GetValueOrDefault(dependencyIdentifier) + 1;
            }
        }
        return dependentCounts;
    }

    /// <summary>実行中タスクを維持しつつ厳守期限不足だけを割り込ませる。</summary>
    private static TaskEvaluation ChooseWithContinuity(IReadOnlyList<TaskEvaluation> evaluations)
    {
        // 累積厳守期限不足、実行中、先の締切を守れる候補、全候補の順で選択対象を決める。
        IReadOnlyList<TaskEvaluation> targetEvaluations = evaluations.Where(evaluation => evaluation.StrictDeadlineInfeasible).ToList();
        if (targetEvaluations.Count == 0)
        {
            targetEvaluations = evaluations.Where(evaluation => evaluation.Task.Status == TaskConstants.InProgressStatus).ToList();
        }
        if (targetEvaluations.Count == 0)
        {
            targetEvaluations = evaluations.Where(evaluation => evaluation.ProtectsEarlierDeadlines).ToList();
        }
        if (targetEvaluations.Count == 0)
        {
            targetEvaluations = evaluations;
        }
        return targetEvaluations
            .OrderByDescending(evaluation => evaluation.StrictDeadlineInfeasible)
            .ThenByDescending(evaluation => evaluation.PriorityScore)
            .ThenBy(evaluation => evaluation.EffectiveDeadlineAt ?? DateTimeOffset.MaxValue)
            .ThenBy(evaluation => evaluation.Task.CreatedAt)
            .First();
    }

    /// <summary>今回の推奨作業時間を空き時間内へ収める。</summary>
    private static int CalculateSuggestedMinutes(int remainingMinutes, int availableMinutes, TaskManagerSettings settings)
    {
        // 次の予定まで5分を残して最小・最大作業時間を適用する。
        int usableMinutes = Math.Max(0, availableMinutes - 5);
        int limitedMinutes = Math.Min(Math.Min(remainingMinutes, settings.MaximumWorkMinutes), usableMinutes);
        return limitedMinutes < settings.MinimumWorkMinutes ? 0 : limitedMinutes;
    }

    /// <summary>タスク評価の最大要因から短い推薦理由を作成する。</summary>
    private static string BuildReason(TaskEvaluation evaluation, DeadlineCheckpoint? deadlineCheckpoint)
    {
        // 累積期限負荷を最初に示し、次に従来の評価要因を優先順で返す。
        if (evaluation.StrictDeadlineInfeasible)
        {
            string deadlineText = evaluation.StrictPressureDeadlineAt?.ToString("M/d HH:mm") ?? "厳守期限";
            return $"{deadlineText}までの厳守タスクを合計すると作業時間が不足しています。期限または作業範囲の見直しが必要です。";
        }
        if (evaluation.Task.Status == TaskConstants.InProgressStatus)
        {
            return "実行中のタスクを継続し、切り替え負担を減らします。";
        }
        if (!evaluation.ProtectsEarlierDeadlines)
        {
            return "先の締切に必要な作業を現在実行できないため、実行可能な候補を提示します。";
        }
        if (deadlineCheckpoint is not null && deadlineCheckpoint.SlackMinutes < evaluation.SuggestedMinutes)
        {
            string deadlineText = deadlineCheckpoint.DeadlineAt.ToString("M/d HH:mm");
            int slackMinutes = (int)Math.Floor(deadlineCheckpoint.SlackMinutes);
            string slackText = slackMinutes >= 0 ? $"余裕が{slackMinutes}分" : $"{Math.Abs(slackMinutes)}分不足";
            return $"{deadlineText}までの累積作業量を考慮すると{slackText}のため、この期限群を優先します。";
        }
        if (evaluation.EffectiveDeadlineAt.HasValue
            && (!evaluation.Task.DeadlineAt.HasValue || evaluation.EffectiveDeadlineAt < evaluation.Task.DeadlineAt))
        {
            return "後続タスクの期限を守るため、先行するこのタスクを優先します。";
        }
        if (evaluation.DeadlineRisk >= 0.75)
        {
            return "期限までの累積余裕時間が少ないため優先します。";
        }
        if (evaluation.ImportanceScore >= 0.75)
        {
            return "重要度が高く、現在の空き時間で進められます。";
        }
        if (evaluation.AgingScore >= 0.5)
        {
            return "開始可能日から時間が経過しているため、優先度を繰り上げました。";
        }
        if (evaluation.UnblockScore >= 0.5)
        {
            return "完了すると後続タスクを進められるため優先します。";
        }
        if (evaluation.DeferralScore >= 0.4)
        {
            return "延期が続いているため、今回の候補に繰り上げました。";
        }
        return "現在の空き時間と実行条件に最も合うタスクです。";
    }

    /// <summary>数値を0から1の範囲へ収める。</summary>
    private static double Clamp(double value)
    {
        // 計算結果が加重範囲を超えないように制限する。
        return Math.Min(1, Math.Max(0, value));
    }

    /// <summary>自身または後続タスクから継承した実効期限を表す。</summary>
    private sealed record EffectiveDeadline(DateTimeOffset DeadlineAt, string DeadlineType);

    /// <summary>1つの期限日時における追加作業量、累積作業量、余裕時間を表す。</summary>
    private sealed record DeadlineCheckpoint(
        DateTimeOffset DeadlineAt,
        double GroupBufferedMinutes,
        double CumulativeBufferedMinutes,
        double AvailableMinutes,
        double SlackMinutes);

    /// <summary>実効期限と期限日時別の累積評価結果を保持する。</summary>
    private sealed record DeadlinePressureAnalysis(
        IReadOnlyDictionary<string, EffectiveDeadline> EffectiveDeadlines,
        IReadOnlyList<DeadlineCheckpoint> Checkpoints,
        IReadOnlyDictionary<DateTimeOffset, DeadlineCheckpoint> CheckpointsByDeadline);
}
