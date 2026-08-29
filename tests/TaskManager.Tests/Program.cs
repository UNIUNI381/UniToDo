using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManager.Cli;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Services;
using TaskManager.Windows;

namespace TaskManager.Tests;

/// <summary>外部テストフレームワークなしで受け入れ条件を検証する。</summary>
public static class Program
{
    // 成功件数と失敗件数を保持する。
    private static int passedCount;
    private static int failedCount;

    /// <summary>全テストを順に実行して終了コードを返す。</summary>
    public static async Task<int> Main()
    {
        // 中核ロジック、保存層、自動処理、性能をまとめて検証する。
        await RunTestAsync("空き時間15分未満では推薦しない", TestMinimumSlotAsync);
        await RunTestAsync("待機中も優先度を保存し強制表示できる", TestWaitingPriorityAndForcedRecommendationAsync);
        await RunTestAsync("依存タスク未完了を除外する", TestDependencyBlockingAsync);
        await RunTestAsync("最後の前提完了時に後続タスクの開始可能日を更新する", TestDependencyReleaseStartAsync);
        await RunTestAsync("必要環境の不一致では推薦候補から除外しない", TestRequiredContextDoesNotExcludeAsync);
        await RunTestAsync("開始可能日からの経過と重要度を優先度へ反映する", TestAgingPriorityAsync);
        await RunTestAsync("ダッシュボード推薦を区分とプロジェクトで絞り込む", TestDashboardRecommendationFilterAsync);
        await RunTestAsync("実行中タスクを原則継続する", TestContinuityAsync);
        await RunTestAsync("厳守期限不足を割り込ませる", TestStrictDeadlineInterruptionAsync);
        await RunTestAsync("締切集中時は先の期限群を優先する", TestCumulativeDeadlinePressureAsync);
        await RunTestAsync("後日期限を過去の累積作業量で過大評価しない", TestLaterDeadlineNormalizationAsync);
        await RunTestAsync("締切群に余裕があれば重要度を優先できる", TestSafeLaterDeadlineSelectionAsync);
        await RunTestAsync("同じ締切群では重要度を比較する", TestImportanceWithinDeadlineGroupAsync);
        await RunTestAsync("先行タスクへ後続の実効期限を継承する", TestEffectiveDeadlineInheritanceAsync);
        await RunTestAsync("予定による余裕減少を締切評価へ反映する", TestCalendarDeadlinePressureAsync);
        await RunTestAsync("累積厳守期限不足だけが実行中へ割り込む", TestCumulativeStrictDeadlineInterruptionAsync);
        await RunTestAsync("目安期限の不足では実行中を継続する", TestTargetDeadlineContinuityAsync);
        await RunTestAsync("循環依存を検出する", TestCycleDetectionAsync);
        await RunTestAsync("予定重複を統合して空き時間を計算する", TestCalendarOverlapAsync);
        await RunTestAsync("今日から7日分のウィジェット予定を抽出する", TestCalendarWidgetProjectionAsync);
        await RunTestAsync("Google Calendar埋め込みURLを安全に保存する", TestCalendarEmbedUrlAsync);
        await RunTestAsync("同期エラー時もカレンダーキャッシュを表示する", TestCalendarCacheOnErrorAsync);
        await RunTestAsync("CodexタスクIDとディープリンクを正規化する", TestCodexThreadIdentifierAsync);
        await RunTestAsync("CodexタスクをURLプロトコルだけで開く", TestCodexDeepLinkLaunchAsync);
        await RunTestAsync("Codex送信先設定をSQLiteで保持する", TestCodexSettingsRoundTripAsync);
        await RunTestAsync("音声確認待ちをFIFOで処理する", TestCodexReviewQueueAsync);
        await RunTestAsync("音声確認画面を指定ディスプレイ内へ配置する", TestCodexReviewWindowPlacementAsync);
        await RunTestAsync("Codex送信先未設定時にCLI起動を拒否する", TestCodexUnconfiguredDispatchAsync);
        await RunTestAsync("Codex CLIへ本文を標準入力で渡す", TestCodexCommandDispatchAsync);
        await RunTestAsync("Codex CLI失敗時に本文を確認待ちへ戻す", TestCodexSubmissionFailureAsync);
        await RunTestAsync("Codex CLI成功時の応答を詳細画面へ渡す", TestCodexSubmissionResponseDisplayAsync);
        await RunTestAsync("F13起動時にOllamaとTypeWhisperを必要時だけ起動する", TestVoiceInputDependencyStartupAsync);
        await RunTestAsync("起動済み音声入力環境を再利用する", TestVoiceInputDependencyReuseAsync);
        await RunTestAsync("録音中の2回目のF13でAPI停止を確認する", TestVoiceInputSecondPressStopsAsync);
        await RunTestAsync("TypeWhisper停止確認前に校正待ちを表示しない", TestVoiceInputStopAcknowledgementAsync);
        await RunTestAsync("TypeWhisper停止失敗時に録音状態を維持する", TestVoiceInputStopFailureAsync);
        await RunTestAsync("TypeWhisper APIで音声校正を開始停止する", TestTypeWhisperApiControlAsync);
        await RunTestAsync("TypeWhisper連携導入時にローカルAPIを有効化する", TestTypeWhisperInstallerEnablesApiAsync);
        await RunTestAsync("Ollama事前ロード要求を正しく構成する", TestOllamaPreloadRequestAsync);
        await RunTestAsync("校正本文はOllamaモデル準備後に送信する", TestOllamaScriptWaitsForModelAsync);
        await RunTestAsync("SQLiteへタスクと依存関係を保存する", TestRepositoryRoundTripAsync);
        await RunTestAsync("欠落依存と下書き操作を安全に拒否する", TestTaskServiceSafetyAsync);
        await RunTestAsync("空IDと仮参照キーで下書きを保存する", TestDraftReferenceMappingAsync);
        await RunTestAsync("不正な下書き参照をコミット前に拒否する", TestDraftPreCommitValidationAsync);
        await RunTestAsync("同じ冪等キーの下書きを重複作成しない", TestDraftIdempotencyAsync);
        await RunTestAsync("下書き保存後エラーを警告付き成功にする", TestDraftPostProcessingWarningAsync);
        await RunTestAsync("draft-createのCLI応答と警告を処理する", TestDraftCliResponseAsync);
        await RunTestAsync("無期限タスクを有効なJSONへ変換する", TestNoDeadlineSerializationAsync);
        await RunTestAsync("通知台帳が重複を防ぐ", TestNotificationDeduplicationAsync);
        await RunTestAsync("プロジェクト名の正規化と別名を解決する", TestProjectResolutionAsync);
        await RunTestAsync("プロジェクト色を自動生成してHSVで更新する", TestProjectColorAsync);
        await RunTestAsync("あいまいなプロジェクト名を自動確定しない", TestAmbiguousProjectResolutionAsync);
        await RunTestAsync("未解決プロジェクトへ低確信候補を返す", TestLowConfidenceProjectCandidatesAsync);
        await RunTestAsync("有効期間内の背景情報だけを統合する", TestProjectContextPreparationAsync);
        await RunTestAsync("毎週の既定期限を新規タスクだけへ適用する", TestProjectDeadlineDefaultAsync);
        await RunTestAsync("プロジェクト色と作業ログを含むスキーマ版5を保持する", TestProjectSchemaVersionAsync);
        await RunTestAsync("タスク操作から作業区間を自動記録する", TestTaskTimeEntryTransitionsAsync);
        await RunTestAsync("タスクと自由活動を単一タイマーで切り替える", TestFreeActivitySwitchAsync);
        await RunTestAsync("実行中ログの開始時刻を関連予定とともに修正する", TestActiveTimeEntryStartUpdateAsync);
        await RunTestAsync("長時間警告の延長と無応答停止を処理する", TestLongTimerAutomationAsync);
        await RunTestAsync("手入力ログの重複を明示確認まで拒否する", TestManualTimeEntryOverlapAsync);
        await RunTestAsync("異常終了を復旧通知から確認済みまで保持する", TestSystemIncidentLifecycleAsync);
        await RunTestAsync("異なるUTCオフセットの作業ログをローカル日付へ正しく振り分ける", TestTimeEntryTimeZoneBoundaryAsync);
        await RunTestAsync("日境界と10000件の作業時間を集計する", TestTimeReportPerformanceAsync);
        await RunTestAsync("5000件を300ミリ秒以内に評価する", TestPerformanceAsync);
        await RunTestAsync("5000件をSQLite込み300ミリ秒以内に再計算する", TestPersistentPerformanceAsync);
        Console.WriteLine($"結果: {passedCount}件成功 / {failedCount}件失敗");
        return failedCount == 0 ? 0 : 1;
    }

    /// <summary>1件のテストを実行して結果を記録する。</summary>
    private static async Task RunTestAsync(string testName, Func<Task> testAction)
    {
        // 例外を失敗として表示し次のテストを継続する。
        try
        {
            await testAction();
            passedCount += 1;
            Console.WriteLine($"PASS {testName}");
        }
        catch (Exception testError)
        {
            failedCount += 1;
            Console.WriteLine($"FAIL {testName}: {testError.Message}");
        }
    }

    /// <summary>最小作業時間に満たない空き時間を検証する。</summary>
    private static Task TestMinimumSlotAsync()
    {
        // 活動終了10分前ではタスクを開始しないことを確認する。
        DateTimeOffset currentTime = new(2026, 7, 21, 21, 50, 0, TimeSpan.FromHours(9));
        TaskManagerSettings settings = new();
        RecommendationService recommendationService = CreatePureRecommendationService(currentTime);
        RecommendationResult result = recommendationService.SelectRecommendation(
            currentTime,
            settings,
            [CreateTask("task-one", "短い作業")],
            []);
        Assert(result.Recommendation is null, "推薦が生成されました。");
        return Task.CompletedTask;
    }

    /// <summary>活動時間外でも優先度を計算し、明示操作時だけタスクを表示する。</summary>
    private static async Task TestWaitingPriorityAndForcedRecommendationAsync()
    {
        // 活動開始前は通常推薦を抑止しつつ、一覧用スコア保存と強制表示候補を維持する。
        DateTimeOffset currentTime = new(2026, 7, 21, 7, 0, 0, TimeSpan.FromHours(9));
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ManagedTask task = CreateTask("WAITING-PRIORITY", "活動開始前の候補", importance: 5);
        task.CreatedAt = currentTime.AddDays(-10);
        task.UpdatedAt = currentTime;
        await testDatabase.Repository.SaveTaskAsync(task, "追加", TaskConstants.SystemSource);
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            new FixedTimeProvider(currentTime));

        RecommendationResult result = await recommendationService.RefreshAsync();
        ManagedTask storedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("優先度確認タスクが見つかりません。");
        RecommendationResult forcedResult = RecommendationService.FilterRecommendation(
            result,
            null,
            null,
            forceDisplay: true);

        Assert(result.Recommendation is null, "活動開始前に通常推薦が表示されました。");
        Assert(result.CanForceRecommendation, "強制表示可能な候補が検出されませんでした。");
        Assert(storedTask.SuggestedMinutes > 0 && storedTask.PriorityScore > 0, "待機中の優先度がDBへ保存されませんでした。");
        Assert(forcedResult.Recommendation?.Task.Identifier == task.Identifier, "強制表示で最上位タスクを取得できませんでした。");
    }

    /// <summary>未完了の依存先を持つタスクを検証する。</summary>
    private static Task TestDependencyBlockingAsync()
    {
        // 後続タスクの重要度が高くても先行タスクを選ぶことを確認する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask firstTask = CreateTask("task-first", "先行作業");
        ManagedTask secondTask = CreateTask("task-second", "後続作業", importance: 5);
        secondTask.DependencyIdentifiers = [firstTask.Identifier];
        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [firstTask, secondTask],
            []);
        Assert(result.Recommendation?.Task.Identifier == firstTask.Identifier, "後続タスクが先に選ばれました。");
        return Task.CompletedTask;
    }

    /// <summary>複数の前提タスク完了による開始可能日の更新を検証する。</summary>
    private static async Task TestDependencyReleaseStartAsync()
    {
        // 最後の前提が終わるまでは登録日時を維持し、解放時刻より未来の明示値は早めない。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(StandardTime());
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        ManagedTask firstDependency = await taskService.AddTaskAsync(
            CreateTask("RELEASE-FIRST", "最初の前提"),
            TaskConstants.SystemSource);
        ManagedTask secondDependency = await taskService.AddTaskAsync(
            CreateTask("RELEASE-SECOND", "最後の前提"),
            TaskConstants.SystemSource);
        ManagedTask dependentTask = CreateTask("RELEASE-TARGET", "解放される後続");
        dependentTask.DependencyIdentifiers = [firstDependency.Identifier, secondDependency.Identifier];
        dependentTask = await taskService.AddTaskAsync(dependentTask, TaskConstants.SystemSource);
        ManagedTask futureDependentTask = CreateTask("RELEASE-FUTURE", "未来開始を維持する後続");
        futureDependentTask.DependencyIdentifiers = [firstDependency.Identifier, secondDependency.Identifier];
        futureDependentTask.EarliestStartAt = StandardTime().AddDays(1);
        futureDependentTask = await taskService.AddTaskAsync(futureDependentTask, TaskConstants.SystemSource);

        timeProvider.SetCurrentTime(StandardTime().AddMinutes(30));
        await taskService.ExecuteActionAsync(
            "complete",
            firstDependency.Identifier,
            null,
            TaskConstants.SystemSource);
        ManagedTask afterFirstCompletion = await testDatabase.Repository.GetTaskAsync(dependentTask.Identifier)
            ?? throw new InvalidOperationException("最初の前提完了後の後続タスクが見つかりません。");
        Assert(
            afterFirstCompletion.EarliestStartAt == StandardTime(),
            "未完了の前提が残る段階で開始可能日が更新されました。");

        DateTimeOffset releasedAt = StandardTime().AddMinutes(90);
        timeProvider.SetCurrentTime(releasedAt);
        await taskService.ExecuteActionAsync(
            "complete",
            secondDependency.Identifier,
            null,
            TaskConstants.SystemSource);
        ManagedTask releasedTask = await testDatabase.Repository.GetTaskAsync(dependentTask.Identifier)
            ?? throw new InvalidOperationException("解放後の後続タスクが見つかりません。");
        ManagedTask preservedFutureTask = await testDatabase.Repository.GetTaskAsync(futureDependentTask.Identifier)
            ?? throw new InvalidOperationException("未来開始の後続タスクが見つかりません。");
        Assert(releasedTask.EarliestStartAt == releasedAt, "最後の前提完了日時が開始可能日へ設定されませんでした。");
        Assert(
            preservedFutureTask.EarliestStartAt == StandardTime().AddDays(1),
            "明示された未来の開始可能日が前提完了日時で早められました。");
    }

    /// <summary>必要環境が現在設定と異なっても推薦対象になることを検証する。</summary>
    private static Task TestRequiredContextDoesNotExcludeAsync()
    {
        // 環境判定機能を導入するまでは必要環境を保存情報としてのみ扱う。
        DateTimeOffset currentTime = StandardTime();
        TaskManagerSettings settings = new() { CurrentContext = "PC" };
        ManagedTask matchingTask = CreateTask("matching-context", "現在環境と一致", importance: 1);
        ManagedTask differentTask = CreateTask("different-context", "現在環境と不一致", importance: 5);
        differentTask.RequiredContext = "外出";

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            settings,
            [matchingTask, differentTask],
            []);

        Assert(
            result.Evaluations.Any(evaluation => evaluation.Task.Identifier == differentTask.Identifier),
            "必要環境が異なるタスクが評価対象から除外されました。");
        Assert(
            result.Recommendation?.Task.Identifier == differentTask.Identifier,
            "必要環境が異なる高重要度タスクを推薦できませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>実行中タスクの継続性を検証する。</summary>
    private static Task TestContinuityAsync()
    {
        // 通常の高得点タスクより実行中タスクを維持することを確認する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask runningTask = CreateTask("running", "実行中", importance: 1);
        runningTask.Status = TaskConstants.InProgressStatus;
        ManagedTask importantTask = CreateTask("important", "重要", importance: 5);
        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [runningTask, importantTask],
            []);
        Assert(result.Recommendation?.Task.Identifier == runningTask.Identifier, "実行中タスクが維持されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>実行不能な厳守期限の割込みを検証する。</summary>
    private static Task TestStrictDeadlineInterruptionAsync()
    {
        // 実行中タスクより期限不足の厳守タスクを選ぶことを確認する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask runningTask = CreateTask("running", "実行中");
        runningTask.Status = TaskConstants.InProgressStatus;
        ManagedTask urgentTask = CreateTask("urgent", "期限不足", importance: 2);
        urgentTask.DeadlineAt = currentTime.AddMinutes(30);
        urgentTask.DeadlineType = TaskConstants.StrictDeadlineType;
        urgentTask.EstimatedMinutes = 120;
        urgentTask.RemainingMinutes = 120;
        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [runningTask, urgentTask],
            []);
        Assert(result.Recommendation?.Task.Identifier == urgentTask.Identifier, "厳守期限が割り込みませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>同じ期限へ作業が集中した場合の累積余裕時間を検証する。</summary>
    private static Task TestCumulativeDeadlinePressureAsync()
    {
        // 個別には余裕があっても合計すると余裕がなくなる期限群を後日期限より優先する。
        DateTimeOffset currentTime = StandardTime();
        DateTimeOffset todayDeadline = new(2026, 7, 21, 22, 0, 0, currentTime.Offset);
        List<ManagedTask> tasks = Enumerable.Range(1, 5)
            .Select(taskNumber =>
            {
                ManagedTask deadlineTask = CreateTask($"today-{taskNumber}", $"今日の作業{taskNumber}");
                deadlineTask.DeadlineAt = todayDeadline;
                deadlineTask.DeadlineType = TaskConstants.TargetDeadlineType;
                deadlineTask.EstimatedMinutes = 120;
                deadlineTask.RemainingMinutes = 120;
                return deadlineTask;
            })
            .ToList();
        ManagedTask laterImportantTask = CreateTask("later-important", "後日の重要作業", importance: 4);
        laterImportantTask.DeadlineAt = todayDeadline.AddDays(1);
        laterImportantTask.DeadlineType = TaskConstants.TargetDeadlineType;
        tasks.Add(laterImportantTask);

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            tasks,
            []);

        Assert(result.Recommendation?.Task.DeadlineAt == todayDeadline, "締切が集中した今日のタスクが優先されませんでした。");
        TaskEvaluation laterEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == laterImportantTask.Identifier);
        Assert(!laterEvaluation.ProtectsEarlierDeadlines, "後日期限タスクが先の締切余裕を消費しないと判定されました。");
        return Task.CompletedTask;
    }

    /// <summary>後ろの小規模な期限群が以前の作業量によって過大評価されないことを検証する。</summary>
    private static Task TestLaterDeadlineNormalizationAsync()
    {
        // 早い期限と遅い小規模期限の間へ、現在は開始できない大きな作業群を配置して累積分母の混入を再現する。
        DateTimeOffset currentTime = StandardTime();
        TaskManagerSettings settings = new()
        {
            DeadlineWeight = 0.5,
            ImportanceWeight = 0.2,
            UnblockWeight = 0.1,
            DeferralWeight = 0.1,
            ContinuityWeight = 0.1,
            AgingWeight = 0.1
        };
        ManagedTask earlyTask = CreateTask("normalization-early", "早い期限の作業");
        earlyTask.DeadlineAt = currentTime.AddDays(2);
        earlyTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask laterTask = CreateTask("normalization-later", "遅い小規模作業");
        laterTask.DeadlineAt = currentTime.AddDays(7);
        laterTask.DeadlineType = TaskConstants.TargetDeadlineType;
        List<ManagedTask> tasks = [earlyTask, laterTask];
        tasks.AddRange(Enumerable.Range(1, 20).Select(taskNumber =>
        {
            // 中間作業は期限計算へ含めるが、開始可能日前なので現在の推薦候補から外す。
            ManagedTask intermediateTask = CreateTask($"normalization-workload-{taskNumber}", $"中間作業{taskNumber}");
            intermediateTask.DeadlineAt = currentTime.AddDays(4);
            intermediateTask.DeadlineType = TaskConstants.TargetDeadlineType;
            intermediateTask.EstimatedMinutes = 120;
            intermediateTask.RemainingMinutes = 120;
            intermediateTask.EarliestStartAt = currentTime.AddDays(3);
            return intermediateTask;
        }));

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            settings,
            tasks,
            []);
        TaskEvaluation earlyEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == earlyTask.Identifier);
        TaskEvaluation laterEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == laterTask.Identifier);

        Assert(laterEvaluation.DeadlineRisk <= earlyEvaluation.DeadlineRisk,
            "後日期限が過去の累積作業量によって高い期限リスクになりました。");
        Assert(laterEvaluation.PriorityScore <= earlyEvaluation.PriorityScore,
            "同条件の後日期限タスクが早い期限タスクより高い優先度になりました。");
        Assert(result.Recommendation?.Task.Identifier == earlyTask.Identifier,
            "同条件の早い期限タスクが推薦されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>先の締切群に十分な余裕がある場合の重要度選択を検証する。</summary>
    private static Task TestSafeLaterDeadlineSelectionAsync()
    {
        // 次の作業ブロックを使っても余裕が残る場合は後日の高重要度タスクを許可する。
        DateTimeOffset currentTime = StandardTime();
        DateTimeOffset todayDeadline = new(2026, 7, 21, 22, 0, 0, currentTime.Offset);
        ManagedTask firstTodayTask = CreateTask("safe-today-one", "余裕のある今日作業1");
        firstTodayTask.DeadlineAt = todayDeadline;
        firstTodayTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask secondTodayTask = CreateTask("safe-today-two", "余裕のある今日作業2");
        secondTodayTask.DeadlineAt = todayDeadline;
        secondTodayTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask laterImportantTask = CreateTask("safe-later", "後日の高重要度作業", importance: 4);
        laterImportantTask.DeadlineAt = todayDeadline.AddDays(1);
        laterImportantTask.DeadlineType = TaskConstants.TargetDeadlineType;

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [firstTodayTask, secondTodayTask, laterImportantTask],
            []);

        Assert(result.Recommendation?.Task.Identifier == laterImportantTask.Identifier, "十分な余裕があるのに重要度を比較できませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>開始可能日からの経過と重要度による増加率を検証する。</summary>
    private static Task TestAgingPriorityAsync()
    {
        // 経過係数だけを有効にし、同じ経過日数では重要度が高いほど速く上昇することを確認する。
        DateTimeOffset currentTime = StandardTime();
        TaskManagerSettings settings = new()
        {
            DeadlineWeight = 0,
            ImportanceWeight = 0,
            UnblockWeight = 0,
            DeferralWeight = 0,
            ContinuityWeight = 0,
            AgingWeight = 1
        };
        ManagedTask recentTask = CreateTask("aging-recent", "登録直後", importance: 3);
        recentTask.EarliestStartAt = currentTime;
        ManagedTask lowImportanceTask = CreateTask("aging-low", "経過した重要度1", importance: 1);
        lowImportanceTask.EarliestStartAt = currentTime.AddDays(-15);
        ManagedTask highImportanceTask = CreateTask("aging-high", "経過した重要度5", importance: 5);
        highImportanceTask.EarliestStartAt = currentTime.AddDays(-15);

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            settings,
            [recentTask, lowImportanceTask, highImportanceTask],
            []);
        TaskEvaluation recentEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == recentTask.Identifier);
        TaskEvaluation lowEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == lowImportanceTask.Identifier);
        TaskEvaluation highEvaluation = result.Evaluations.Single(evaluation => evaluation.Task.Identifier == highImportanceTask.Identifier);

        Assert(recentEvaluation.AgingScore == 0, "登録直後の経過スコアが0ではありません。");
        Assert(highEvaluation.AgingScore > lowEvaluation.AgingScore, "重要度が経過スコアの増加率へ反映されていません。");
        Assert(result.Recommendation?.Task.Identifier == highImportanceTask.Identifier, "経過と重要度が高いタスクが推薦されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>計算済み推薦を区分とプロジェクトで絞り込めることを検証する。</summary>
    private static Task TestDashboardRecommendationFilterAsync()
    {
        // 全体順位と異なる範囲でも、範囲内の最大スコアが選ばれることを確認する。
        ManagedTask workTask = CreateTask("dashboard-work", "仕事候補");
        workTask.Category = TaskConstants.WorkCategory;
        workTask.ProjectIdentifier = "PROJECT-WORK";
        ManagedTask privateTask = CreateTask("dashboard-private", "私用候補");
        privateTask.Category = TaskConstants.PrivateCategory;
        privateTask.ProjectIdentifier = "PROJECT-PRIVATE";
        ManagedTask projectTask = CreateTask("dashboard-project", "同一プロジェクト候補");
        projectTask.Category = TaskConstants.WorkCategory;
        projectTask.ProjectIdentifier = "PROJECT-PRIVATE";
        TaskEvaluation workEvaluation = new() { Task = workTask, PriorityScore = 80, SuggestedMinutes = 30 };
        TaskEvaluation privateEvaluation = new() { Task = privateTask, PriorityScore = 90, SuggestedMinutes = 30 };
        TaskEvaluation projectEvaluation = new() { Task = projectTask, PriorityScore = 70, SuggestedMinutes = 30 };
        RecommendationResult sourceResult = new()
        {
            Recommendation = privateEvaluation,
            Evaluations = [workEvaluation, privateEvaluation, projectEvaluation],
            CurrentSlot = new AvailableSlot(60, string.Empty, string.Empty, string.Empty, StandardTime().AddMinutes(60))
        };

        RecommendationResult workResult = RecommendationService.FilterRecommendation(
            sourceResult,
            TaskConstants.WorkCategory,
            null);
        RecommendationResult projectResult = RecommendationService.FilterRecommendation(
            sourceResult,
            null,
            "PROJECT-PRIVATE");
        RecommendationResult emptyResult = RecommendationService.FilterRecommendation(
            sourceResult,
            null,
            "PROJECT-NONE");

        Assert(workResult.Recommendation?.Task.Identifier == workTask.Identifier, "仕事内の最上位が選ばれませんでした。");
        Assert(projectResult.Recommendation?.Task.Identifier == privateTask.Identifier, "プロジェクト内の最上位が選ばれませんでした。");
        Assert(emptyResult.Recommendation is null, "候補なしの範囲で推薦が生成されました。");
        Assert(sourceResult.Recommendation?.Task.Identifier == privateTask.Identifier, "元の全体推薦が変更されました。");
        return Task.CompletedTask;
    }

    /// <summary>同じ締切日時の候補間で重要度が反映されることを検証する。</summary>
    private static Task TestImportanceWithinDeadlineGroupAsync()
    {
        // 累積期限リスクが同じ候補では重要度の高いタスクを選択する。
        DateTimeOffset currentTime = StandardTime();
        DateTimeOffset deadline = currentTime.AddMinutes(30);
        ManagedTask normalTask = CreateTask("same-normal", "通常重要度");
        normalTask.DeadlineAt = deadline;
        normalTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask importantTask = CreateTask("same-important", "高重要度", importance: 5);
        importantTask.DeadlineAt = deadline;
        importantTask.DeadlineType = TaskConstants.TargetDeadlineType;

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [normalTask, importantTask],
            []);

        Assert(result.Recommendation?.Task.Identifier == importantTask.Identifier, "同じ締切群で重要度が反映されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>期限なしの先行タスクが後続タスクの期限を継承することを検証する。</summary>
    private static Task TestEffectiveDeadlineInheritanceAsync()
    {
        // 未完了の後続タスクを解放する先行タスクへ同じ実効期限を設定して推薦する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask prerequisiteTask = CreateTask("effective-first", "期限なし先行作業");
        ManagedTask dependentTask = CreateTask("effective-second", "期限付き後続作業", importance: 5);
        dependentTask.DeadlineAt = currentTime.AddMinutes(30);
        dependentTask.DeadlineType = TaskConstants.StrictDeadlineType;
        dependentTask.DependencyIdentifiers = [prerequisiteTask.Identifier];

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [prerequisiteTask, dependentTask],
            []);

        Assert(result.Recommendation?.Task.Identifier == prerequisiteTask.Identifier, "期限付き後続を解放する先行タスクが選ばれませんでした。");
        Assert(result.Recommendation?.EffectiveDeadlineAt == dependentTask.DeadlineAt, "後続タスクの期限が先行タスクへ継承されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>カレンダー予定による作業可能時間の減少を累積期限へ反映する。</summary>
    private static Task TestCalendarDeadlinePressureAsync()
    {
        // 長い予定が入る前後で後日期限から今日期限へ推薦が切り替わることを確認する。
        DateTimeOffset currentTime = StandardTime();
        DateTimeOffset todayDeadline = new(2026, 7, 21, 22, 0, 0, currentTime.Offset);
        ManagedTask firstTodayTask = CreateTask("calendar-today-one", "予定影響作業1");
        firstTodayTask.DeadlineAt = todayDeadline;
        firstTodayTask.DeadlineType = TaskConstants.TargetDeadlineType;
        firstTodayTask.EstimatedMinutes = 60;
        firstTodayTask.RemainingMinutes = 60;
        ManagedTask secondTodayTask = CreateTask("calendar-today-two", "予定影響作業2");
        secondTodayTask.DeadlineAt = todayDeadline;
        secondTodayTask.DeadlineType = TaskConstants.TargetDeadlineType;
        secondTodayTask.EstimatedMinutes = 60;
        secondTodayTask.RemainingMinutes = 60;
        ManagedTask laterImportantTask = CreateTask("calendar-later", "予定後の重要作業", importance: 5);
        laterImportantTask.DeadlineAt = todayDeadline.AddDays(1);
        laterImportantTask.DeadlineType = TaskConstants.TargetDeadlineType;
        TaskManagerSettings settings = new();
        RecommendationService recommendationService = CreatePureRecommendationService(currentTime);

        RecommendationResult withoutEvent = recommendationService.SelectRecommendation(
            currentTime,
            settings,
            [firstTodayTask, secondTodayTask, laterImportantTask],
            []);
        List<CalendarEventRecord> calendarEvents =
        [
            new()
            {
                EventIdentifier = "deadline-pressure-event",
                StartAt = currentTime.AddHours(2),
                EndAt = currentTime.AddHours(9),
                IsBusy = true
            }
        ];
        RecommendationResult withEvent = recommendationService.SelectRecommendation(
            currentTime,
            settings,
            [firstTodayTask, secondTodayTask, laterImportantTask],
            calendarEvents);

        Assert(withoutEvent.Recommendation?.Task.Identifier == laterImportantTask.Identifier, "予定追加前に後日の重要タスクが選ばれませんでした。");
        Assert(withEvent.Recommendation?.Task.DeadlineAt == todayDeadline, "予定による締切余裕の減少が推薦へ反映されませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>複数の厳守タスクが合計で実行不能な場合の割込みを検証する。</summary>
    private static Task TestCumulativeStrictDeadlineInterruptionAsync()
    {
        // 各タスク単独では間に合っても合計では不足する厳守期限群を実行中より優先する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask runningTask = CreateTask("strict-running", "実行中作業");
        runningTask.Status = TaskConstants.InProgressStatus;
        ManagedTask firstStrictTask = CreateTask("strict-group-one", "厳守作業1");
        firstStrictTask.DeadlineAt = currentTime.AddMinutes(60);
        firstStrictTask.DeadlineType = TaskConstants.StrictDeadlineType;
        ManagedTask secondStrictTask = CreateTask("strict-group-two", "厳守作業2");
        secondStrictTask.DeadlineAt = currentTime.AddMinutes(60);
        secondStrictTask.DeadlineType = TaskConstants.StrictDeadlineType;

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [runningTask, firstStrictTask, secondStrictTask],
            []);

        Assert(result.Recommendation?.Task.Identifier is "strict-group-one" or "strict-group-two", "累積厳守期限不足が実行中タスクへ割り込みませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>目安期限の累積不足では実行中タスクを維持することを検証する。</summary>
    private static Task TestTargetDeadlineContinuityAsync()
    {
        // 目安期限は通常候補を制限しても実行中タスクを強制中断しないことを確認する。
        DateTimeOffset currentTime = StandardTime();
        ManagedTask runningTask = CreateTask("target-running", "継続中作業");
        runningTask.Status = TaskConstants.InProgressStatus;
        ManagedTask firstTargetTask = CreateTask("target-group-one", "目安作業1");
        firstTargetTask.DeadlineAt = currentTime.AddMinutes(30);
        firstTargetTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask secondTargetTask = CreateTask("target-group-two", "目安作業2");
        secondTargetTask.DeadlineAt = currentTime.AddMinutes(30);
        secondTargetTask.DeadlineType = TaskConstants.TargetDeadlineType;

        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [runningTask, firstTargetTask, secondTargetTask],
            []);

        Assert(result.Recommendation?.Task.Identifier == runningTask.Identifier, "目安期限の不足で実行中タスクが中断されました。");
        return Task.CompletedTask;
    }

    /// <summary>循環依存の経路検出を検証する。</summary>
    private static Task TestCycleDetectionAsync()
    {
        // 3件の循環を閉路として返すことを確認する。
        ManagedTask firstTask = CreateTask("one", "1");
        ManagedTask secondTask = CreateTask("two", "2");
        ManagedTask thirdTask = CreateTask("three", "3");
        firstTask.DependencyIdentifiers = [thirdTask.Identifier];
        secondTask.DependencyIdentifiers = [firstTask.Identifier];
        thirdTask.DependencyIdentifiers = [secondTask.Identifier];
        List<string> cycle = TaskValidationService.FindCycle([firstTask, secondTask, thirdTask]);
        Assert(cycle.Count == 4 && cycle[0] == cycle[^1], "循環経路を検出できませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>重複する予定時間の統合を検証する。</summary>
    private static Task TestCalendarOverlapAsync()
    {
        // 重複予定中は空き時間が0になることを確認する。
        DateTimeOffset currentTime = StandardTime();
        List<CalendarEventRecord> events =
        [
            new() { EventIdentifier = "one", StartAt = currentTime.AddMinutes(-10), EndAt = currentTime.AddMinutes(20), IsBusy = true },
            new() { EventIdentifier = "two", StartAt = currentTime.AddMinutes(10), EndAt = currentTime.AddMinutes(40), IsBusy = true }
        ];
        AvailableSlot slot = new CalendarAvailabilityService().CalculateCurrentAvailableSlot(currentTime, new TaskManagerSettings(), events);
        Assert(slot.AvailableMinutes == 0, "予定中に空き時間が生成されました。");
        Assert(slot.SlotEnd >= currentTime.AddMinutes(40), "重複予定が統合されていません。");
        return Task.CompletedTask;
    }

    /// <summary>ウィジェット用予定の7日境界と表示専用項目を検証する。</summary>
    private static Task TestCalendarWidgetProjectionAsync()
    {
        // 今日の開始へ重なる予定と7日目までを含め、両端の境界外を除外する。
        DateTimeOffset currentTime = StandardTime();
        DateTimeOffset todayStart = new(2026, 7, 21, 0, 0, 0, TimeSpan.FromHours(9));
        List<CalendarEventRecord> calendarEvents =
        [
            new()
            {
                EventIdentifier = "before",
                Title = "境界前",
                StartAt = todayStart.AddHours(-2),
                EndAt = todayStart
            },
            new()
            {
                EventIdentifier = "overlap",
                CalendarIdentifier = "primary",
                Title = "日またぎ",
                Description = "ウィジェットへ返してはならない本文",
                StartAt = todayStart.AddHours(-1),
                EndAt = todayStart.AddMinutes(30),
                Location = "会議室",
                IsBusy = true
            },
            new()
            {
                EventIdentifier = "all-day",
                CalendarIdentifier = "primary",
                Title = "終日予定",
                StartAt = todayStart,
                EndAt = todayStart.AddDays(1),
                IsAllDay = true
            },
            new()
            {
                EventIdentifier = "last-day",
                CalendarIdentifier = "primary",
                Title = "7日目",
                StartAt = todayStart.AddDays(6).AddHours(23),
                EndAt = todayStart.AddDays(7).AddHours(1)
            },
            new()
            {
                EventIdentifier = "after",
                Title = "境界後",
                StartAt = todayStart.AddDays(7),
                EndAt = todayStart.AddDays(7).AddHours(1)
            }
        ];
        TaskManagerSettings settings = new()
        {
            TimeZoneIdentifier = "Tokyo Standard Time",
            ActivityStart = "07:30",
            ActivityEnd = "21:15",
            CalendarEmbedUrl = "https://calendar.google.com/calendar/embed?src=work%40example.com"
        };
        ManagedTask visibleDeadlineTask = CreateTask("calendar-deadline", "表示対象期限");
        visibleDeadlineTask.ProjectIdentifier = "PROJECT-CALENDAR";
        visibleDeadlineTask.DeadlineAt = todayStart.AddHours(18);
        visibleDeadlineTask.DeadlineType = TaskConstants.StrictDeadlineType;
        ManagedTask completedDeadlineTask = CreateTask("calendar-completed", "完了済み期限");
        completedDeadlineTask.Status = TaskConstants.CompletedStatus;
        completedDeadlineTask.DeadlineAt = todayStart.AddHours(19);
        completedDeadlineTask.DeadlineType = TaskConstants.TargetDeadlineType;
        ManagedTask outsideDeadlineTask = CreateTask("calendar-outside", "範囲外期限");
        outsideDeadlineTask.DeadlineAt = todayStart.AddDays(7);
        outsideDeadlineTask.DeadlineType = TaskConstants.TargetDeadlineType;

        CalendarWidgetResult widget = RecommendationService.BuildCalendarWidget(
            currentTime,
            settings,
            calendarEvents,
            [visibleDeadlineTask, completedDeadlineTask, outsideDeadlineTask]);

        Assert(widget.RangeStart == todayStart, "ウィジェットの開始日が今日の0時ではありません。");
        Assert(widget.RangeEnd == todayStart.AddDays(7), "ウィジェットの終了境界が7日後ではありません。");
        Assert(widget.ActivityStart == "07:30", "活動開始時刻がウィジェットへ反映されていません。");
        Assert(widget.ActivityEnd == "21:15", "活動終了時刻がウィジェットへ反映されていません。");
        Assert(
            widget.Events.Select(calendarEvent => calendarEvent.EventIdentifier)
                .SequenceEqual(["overlap", "all-day", "last-day"]),
            "7日範囲へ重なる予定を正しく抽出できませんでした。");
        Assert(
            widget.Deadlines.Count == 1
            && widget.Deadlines[0].TaskIdentifier == visibleDeadlineTask.Identifier
            && widget.Deadlines[0].DeadlineType == TaskConstants.StrictDeadlineType,
            "未完了タスクの期限を7日範囲へ正しく抽出できませんでした。");
        Assert(
            typeof(CalendarWidgetEvent).GetProperty("Description") is null,
            "予定の詳細本文がウィジェット公開モデルへ含まれています。");
        return Task.CompletedTask;
    }

    /// <summary>埋め込みURLの検証と設定保存の往復を確認する。</summary>
    private static async Task TestCalendarEmbedUrlAsync()
    {
        // Google公式URLだけを正規化し、キー値設定から同じURLを復元する。
        string normalizedUrl = CalendarEmbedUrlValidator.Normalize(
            " https://calendar.google.com/calendar/embed?src=work%40example.com&amp;ctz=Asia%2FTokyo ");
        Assert(
            normalizedUrl.StartsWith(
                "https://calendar.google.com/calendar/embed?src=work%40example.com",
                StringComparison.Ordinal),
            "有効な埋め込みURLを正規化できませんでした。");
        Assert(
            CalendarEmbedUrlValidator.Normalize(" ") == string.Empty,
            "空の埋め込みURLが未設定として扱われませんでした。");

        string[] invalidUrls =
        [
            "https://example.com/calendar/embed?src=work%40example.com",
            "https://calendar.google.com/calendar/embed",
            "<iframe src=\"https://calendar.google.com/calendar/embed?src=work%40example.com\"></iframe>"
        ];
        foreach (string invalidUrl in invalidUrls)
        {
            bool rejected = false;
            try
            {
                CalendarEmbedUrlValidator.Normalize(invalidUrl);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Assert(rejected, $"不正な埋め込みURLが許可されました: {invalidUrl}");
        }

        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskManagerSettings settings = await testDatabase.Repository.GetSettingsAsync();
        settings.CalendarEmbedUrl = normalizedUrl;
        settings.AgingWeight = 0.23;
        await testDatabase.Repository.SaveSettingsAsync(settings, TaskConstants.SystemSource);
        TaskManagerSettings loadedSettings = await testDatabase.Repository.GetSettingsAsync();
        Assert(
            loadedSettings.CalendarEmbedUrl == normalizedUrl,
            "埋め込みURLを設定から復元できませんでした。");
        Assert(Math.Abs(loadedSettings.AgingWeight - 0.23) < 0.0001, "開始後経過の係数を設定から復元できませんでした。");
    }

    /// <summary>同期失敗後も最終成功予定と状態をダッシュボードへ返すことを検証する。</summary>
    private static async Task TestCalendarCacheOnErrorAsync()
    {
        // 初期状態と、成功キャッシュを保持したままエラーを記録した状態を順に確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        DateTimeOffset currentTime = StandardTime();
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            new FixedTimeProvider(currentTime));
        RecommendationResult initialResult = await recommendationService.RefreshAsync();
        Assert(initialResult.CalendarUpdatedAt is null, "未同期状態に同期日時が設定されています。");
        Assert(string.IsNullOrEmpty(initialResult.CalendarError), "未同期状態にエラーが設定されています。");

        CalendarEventRecord cachedEvent = new()
        {
            EventIdentifier = "cached",
            CalendarIdentifier = "primary",
            Title = "保存済み予定",
            StartAt = currentTime.AddHours(1),
            EndAt = currentTime.AddHours(2),
            IsBusy = true
        };
        DateTimeOffset synchronizedAt = currentTime.AddMinutes(-5);
        await testDatabase.Repository.ReplaceCalendarEventsAsync([cachedEvent], synchronizedAt);
        await testDatabase.Repository.SaveCalendarErrorAsync("認証期限切れ");

        RecommendationResult errorResult = await recommendationService.RefreshAsync();
        Assert(errorResult.CalendarUpdatedAt == synchronizedAt, "最終成功日時が同期エラーで失われました。");
        Assert(errorResult.CalendarError == "認証期限切れ", "最新の同期エラーを取得できませんでした。");
        Assert(
            errorResult.CalendarWidget.Events.Single().EventIdentifier == cachedEvent.EventIdentifier,
            "同期エラー後にカレンダーキャッシュがウィジェットから失われました。");
    }

    /// <summary>CodexタスクIDとディープリンクの正規化を検証する。</summary>
    private static Task TestCodexThreadIdentifierAsync()
    {
        // UUID単体とディープリンクが同じ小文字UUIDになり、不正値が拒否されることを確認する。
        string expectedIdentifier = "123e4567-e89b-42d3-a456-426614174000";
        string normalizedIdentifier = CodexThreadIdentifier.Normalize(expectedIdentifier.ToUpperInvariant());
        string normalizedLink = CodexThreadIdentifier.Normalize($"codex://threads/{expectedIdentifier}");
        Assert(normalizedIdentifier == expectedIdentifier, "UUIDを正規化できませんでした。");
        Assert(normalizedLink == expectedIdentifier, "Codexディープリンクを正規化できませんでした。");
        Assert(
            CodexThreadIdentifier.CreateDeepLink(expectedIdentifier) == $"codex://threads/{expectedIdentifier}",
            "正規化済みディープリンクを生成できませんでした。");

        bool invalidIdentifierRejected = false;
        try
        {
            _ = CodexThreadIdentifier.Normalize("https://example.com/thread");
        }
        catch (InvalidOperationException)
        {
            invalidIdentifierRejected = true;
        }
        Assert(invalidIdentifierRejected, "不正なCodexタスクIDが許可されました。");
        return Task.CompletedTask;
    }

    /// <summary>Codexタスクの起動にURLプロトコルだけを使用することを検証する。</summary>
    private static Task TestCodexDeepLinkLaunchAsync()
    {
        // サーバー側で外部プロセスを起動せず、ブラウザ用ディープリンクだけを返すことを固定する。
        const string threadLink = "codex://threads/123e4567-e89b-42d3-a456-426614174000";
        CodexDesktopLaunchResult launchResult = new CodexDesktopLauncher().OpenThread(threadLink);
        Assert(
            launchResult.Opened
            && launchResult.LaunchMethod == "browser-protocol"
            && launchResult.ThreadLink == threadLink,
            "Codexタスクのブラウザ用リンクを安全に返せていません。");
        return Task.CompletedTask;
    }

    /// <summary>Codex送信先設定の既定値とSQLite往復を検証する。</summary>
    private static async Task TestCodexSettingsRoundTripAsync()
    {
        // 新規DBの送信先が未設定であり、UUIDへ更新した設定を再読込できることを確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskManagerSettings settings = await testDatabase.Repository.GetSettingsAsync();
        Assert(
            settings.CodexThreadIdentifier.Length == 0
            && TaskConstants.DefaultCodexThreadIdentifier.Length == 0,
            "Codex送信先の初期値が未設定ではありません。");
        settings.CodexThreadIdentifier = "123e4567-e89b-42d3-a456-426614174005";
        await testDatabase.Repository.SaveSettingsAsync(settings, TaskConstants.SystemSource);
        TaskManagerSettings loadedSettings = await testDatabase.Repository.GetSettingsAsync();
        Assert(
            loadedSettings.CodexThreadIdentifier == settings.CodexThreadIdentifier,
            "Codex送信先設定を復元できませんでした。");
    }

    /// <summary>音声入力の確認待ちが受付順で処理されることを検証する。</summary>
    private static Task TestCodexReviewQueueAsync()
    {
        // 2件を順番に表示し、CLI失敗時の編集済み本文が先頭へ戻ることを確認する。
        CodexReviewService reviewService = new();
        CodexReviewReceipt firstReceipt = reviewService.Enqueue("最初の本文");
        CodexReviewReceipt secondReceipt = reviewService.Enqueue("次の本文");
        Assert(firstReceipt.QueuePosition == 1, "最初の確認順序が1ではありません。");
        Assert(secondReceipt.QueuePosition == 2, "2件目の確認順序が2ではありません。");
        Assert(
            reviewService.TryActivateNext(out CodexReviewItem? firstReview, out int firstWaitingCount)
            && firstReview?.Text == "最初の本文"
            && firstWaitingCount == 1,
            "最初の確認項目を取得できませんでした。");
        Assert(reviewService.CompleteActive(firstReview!.Identifier), "最初の確認項目を完了できませんでした。");
        Assert(
            reviewService.TryActivateNext(out CodexReviewItem? secondReview, out int secondWaitingCount)
            && secondReview?.Text == "次の本文"
            && secondWaitingCount == 0,
            "2件目の確認項目を取得できませんでした。");
        Assert(reviewService.CompleteActive(secondReview!.Identifier), "2件目の確認項目を完了できませんでした。");

        CodexSubmission failedSubmission = new()
        {
            ReviewIdentifier = "failed-review",
            ThreadIdentifier = "123e4567-e89b-42d3-a456-426614174001",
            Text = "編集後の本文",
            CreatedAt = StandardTime()
        };
        reviewService.RequeueFailedSubmission(failedSubmission);
        Assert(
            reviewService.TryActivateNext(out CodexReviewItem? failedReview, out _)
            && failedReview?.Text == failedSubmission.Text,
            "CLI失敗時の編集済み本文を確認待ちへ戻せませんでした。");
        return Task.CompletedTask;
    }

    /// <summary>音声確認画面が指定したディスプレイ作業領域内へ完全に収まることを検証する。</summary>
    private static Task TestCodexReviewWindowPlacementAsync()
    {
        // 原点がずれた複数画面相当の領域でもCenterScreenへ依存しない配置を確認する。
        Rectangle workingArea = new(1920, 640, 1920, 1080);
        using CodexReviewForm reviewForm = new(
            new CodexReviewItem
            {
                Identifier = "placement-review",
                Text = "配置確認",
                CreatedAt = StandardTime()
            },
            "123e4567-e89b-42d3-a456-426614174004",
            0,
            workingArea,
            (reviewItem, text) => Task.FromResult<string?>(null),
            reviewItem => { });

        Assert(workingArea.Contains(reviewForm.Bounds), "音声確認画面が指定ディスプレイ領域からはみ出しました。");
        Assert(reviewForm.TopMost, "音声確認画面が最前面表示に設定されていません。");
        Assert(reviewForm.ShowInTaskbar, "音声確認画面がタスクバーから選択できません。");
        return Task.CompletedTask;
    }

    /// <summary>Codex送信先が未設定の場合にCLIを起動せず設定方法を案内することを検証する。</summary>
    private static async Task TestCodexUnconfiguredDispatchAsync()
    {
        // 空の既定値を指定した送信を拒否し、プロセスが起動されないことを確認する。
        CapturingCodexProcessExecutor processExecutor = new();
        CodexCommandRunner commandRunner = new(processExecutor);
        CodexSubmission submission = new()
        {
            ReviewIdentifier = "unconfigured-review",
            ThreadIdentifier = TaskConstants.DefaultCodexThreadIdentifier,
            Text = "送信しない本文",
            CreatedAt = StandardTime()
        };

        bool unconfiguredIdentifierRejected = false;
        try
        {
            await commandRunner.SendAsync(submission, CancellationToken.None);
        }
        catch (InvalidOperationException sendError)
        {
            unconfiguredIdentifierRejected = sendError.Message.Contains("設定画面", StringComparison.Ordinal);
        }

        Assert(unconfiguredIdentifierRejected, "未設定時に送信先の設定案内が表示されませんでした。");
        Assert(processExecutor.StartInformation is null, "未設定時にCodex CLIが起動されました。");
    }

    /// <summary>Codex CLI引数と標準入力の分離を検証する。</summary>
    private static async Task TestCodexCommandDispatchAsync()
    {
        // 偽プロセスへ渡された引数に本文が含まれず、標準入力だけに本文があることを確認する。
        CapturingCodexProcessExecutor processExecutor = new();
        CodexCommandRunner commandRunner = new(processExecutor);
        string threadIdentifier = "123e4567-e89b-42d3-a456-426614174002";
        CodexSubmission submission = new()
        {
            ReviewIdentifier = "dispatch-review",
            ThreadIdentifier = threadIdentifier,
            Text = "送信本文",
            CreatedAt = StandardTime()
        };
        CodexCommandResult result = await commandRunner.SendAsync(submission, CancellationToken.None);
        Assert(result.IsSuccess, "偽Codex CLI送信が成功しませんでした。");
        Assert(
            string.Equals(Path.GetFileName(processExecutor.StartInformation?.FileName), "codex.exe", StringComparison.OrdinalIgnoreCase),
            "Codex CLI実行ファイル名が異なります。");
        Assert(
            processExecutor.StartInformation?.ArgumentList.SequenceEqual(
                [
                    "exec",
                    "--model",
                    "gpt-5.6-luna",
                    "--config",
                    "model_reasoning_effort=\"low\"",
                    "--config",
                    "service_tier=\"fast\"",
                    "--config",
                    "apps.connector_947e0d954944416db111db556030eea6.default_tools_approval_mode=\"approve\"",
                    "--enable",
                    "fast_mode",
                    "--skip-git-repo-check",
                    "--add-dir",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskManager"),
                    "resume",
                    threadIdentifier,
                    "-"
                ]) == true,
            "Codex CLI引数が想定形式ではありません。");
        Assert(processExecutor.StandardInput == submission.Text, "本文が標準入力へ渡されませんでした。");
        Assert(
            !processExecutor.StartInformation!.ArgumentList.Contains(submission.Text),
            "本文がCLI引数へ混入しました。");
    }

    /// <summary>Codex CLI失敗後の再確認と通知を検証する。</summary>
    private static async Task TestCodexSubmissionFailureAsync()
    {
        // 固定失敗を返すランナーで編集済み本文が復元されることを確認する。
        CodexReviewService reviewService = new();
        RecordingNotificationService notificationService = new();
        CodexSubmissionWorker worker = new(
            new CodexSubmissionQueue(),
            new FixedCodexCommandRunner(new CodexCommandResult
            {
                IsSuccess = false,
                ExitCode = 1,
                ErrorMessage = "テスト失敗"
            }),
            reviewService,
            notificationService,
            NullLogger<CodexSubmissionWorker>.Instance);
        CodexSubmission submission = new()
        {
            ReviewIdentifier = "retry-review",
            ThreadIdentifier = "123e4567-e89b-42d3-a456-426614174003",
            Text = "再確認する本文",
            CreatedAt = StandardTime()
        };
        await worker.ProcessSubmissionAsync(submission, CancellationToken.None);
        Assert(
            reviewService.TryActivateNext(out CodexReviewItem? reviewItem, out _)
            && reviewItem?.Text == submission.Text,
            "CLI失敗後の本文が確認待ちへ戻りませんでした。");
        Assert(notificationService.Messages.Count == 0, "Codex送信でWindows通知が使用されました。");
        Assert(notificationService.ProcessingCount == 1, "CLI応答待ち画面が表示されませんでした。");
        Assert(
            notificationService.Details.Count == 1
            && notificationService.Details[0].IsError
            && notificationService.Details[0].Message.Contains("テスト失敗", StringComparison.Ordinal),
            "CLI失敗内容が詳細画面へ渡されませんでした。");
    }

    /// <summary>Codex CLIの成功応答が中央下の詳細画面へ渡されることを検証する。</summary>
    private static async Task TestCodexSubmissionResponseDisplayAsync()
    {
        // 終了コード0だけでなくCodexの回答本文を利用者へ提示することを確認する。
        RecordingNotificationService notificationService = new();
        CodexSubmissionWorker worker = new(
            new CodexSubmissionQueue(),
            new FixedCodexCommandRunner(new CodexCommandResult
            {
                IsSuccess = true,
                ExitCode = 0,
                OutputMessage = "タスクを登録しました。"
            }),
            new CodexReviewService(),
            notificationService,
            NullLogger<CodexSubmissionWorker>.Instance);
        await worker.ProcessSubmissionAsync(new CodexSubmission
        {
            ReviewIdentifier = "response-review",
            ThreadIdentifier = "123e4567-e89b-42d3-a456-426614174004",
            Text = "登録する本文",
            CreatedAt = StandardTime()
        }, CancellationToken.None);

        Assert(
            notificationService.Details.Count == 1
            && !notificationService.Details[0].IsError
            && notificationService.Details[0].Message == "タスクを登録しました。",
            "Codexの成功応答が詳細画面へ渡されませんでした。");
        Assert(notificationService.Messages.Count == 0, "Codex成功時にWindows通知が使用されました。");
        Assert(notificationService.ProcessingCount == 1, "Codex成功前の応答待ち画面が表示されませんでした。");
    }

    /// <summary>未起動のOllamaとTypeWhisperを準備してAPI録音を開始することを検証する。</summary>
    private static async Task TestVoiceInputDependencyStartupAsync()
    {
        // 偽実行環境で両アプリの起動と利用者向け状態通知を確認する。
        RecordingVoiceInputRuntime voiceInputRuntime = new()
        {
            ModelLoadCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        VoiceInputCoordinator coordinator = CreateVoiceInputCoordinator(voiceInputRuntime);
        List<VoiceInputStatus> statuses = [];
        coordinator.StatusChanged += statuses.Add;

        await coordinator.StartAsync(CancellationToken.None);

        Assert(voiceInputRuntime.OllamaStartCount == 1, "未起動のOllamaが開始されませんでした。");
        Assert(voiceInputRuntime.TypeWhisperStartCount == 1, "未起動のTypeWhisperが開始されませんでした。");
        Assert(voiceInputRuntime.ModelLoadCount == 1, "Ollama校正モデルのバックグラウンドロードが開始されませんでした。");
        Assert(voiceInputRuntime.RecognitionStartCount == 1, "TypeWhisper準備完了後にAPI録音が開始されませんでした。");
        Assert(
            voiceInputRuntime.Operations.Contains("start-recognition")
            && !voiceInputRuntime.Operations.Contains("load-model-complete"),
            "Ollama校正モデルのロード完了を待ってから録音が開始されました。");
        voiceInputRuntime.ModelLoadCompletion.SetResult();
        Assert(
            statuses.FirstOrDefault()?.Message == "音声入力を準備しています…",
            "F13への即時状態表示が発行されませんでした。");
        Assert(
            statuses.Any(status => status.Kind == VoiceInputStatusKind.Success),
            "音声認識開始の成功状態が発行されませんでした。");
    }

    /// <summary>起動済みの音声入力環境を重複起動しないことを検証する。</summary>
    private static async Task TestVoiceInputDependencyReuseAsync()
    {
        // Ollama応答とTypeWhisper起動が済んだ状態ではAPI録音開始だけを行う。
        RecordingVoiceInputRuntime voiceInputRuntime = new()
        {
            OllamaReady = true,
            OllamaRunning = true,
            TypeWhisperRunning = true
        };
        VoiceInputCoordinator coordinator = CreateVoiceInputCoordinator(voiceInputRuntime);

        await coordinator.StartAsync(CancellationToken.None);

        Assert(voiceInputRuntime.OllamaStartCount == 0, "起動済みOllamaが重複起動されました。");
        Assert(voiceInputRuntime.TypeWhisperStartCount == 0, "起動済みTypeWhisperが重複起動されました。");
        Assert(voiceInputRuntime.ModelLoadCount == 1, "起動済み環境で校正モデルが事前ロードされませんでした。");
        Assert(voiceInputRuntime.RecognitionStartCount == 1, "起動済みTypeWhisperでAPI録音が開始されませんでした。");
    }

    /// <summary>録音中の2回目のF13が新規開始せずAPIで録音を停止することを検証する。</summary>
    private static async Task TestVoiceInputSecondPressStopsAsync()
    {
        // 起動済み環境で開始後にもう一度操作し、モデル再読込と二重開始を防ぐ。
        RecordingVoiceInputRuntime voiceInputRuntime = new()
        {
            OllamaReady = true,
            OllamaRunning = true,
            TypeWhisperRunning = true
        };
        VoiceInputCoordinator coordinator = CreateVoiceInputCoordinator(voiceInputRuntime);
        List<VoiceInputStatus> statuses = [];
        coordinator.StatusChanged += statuses.Add;

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        Assert(voiceInputRuntime.RecognitionStartCount == 1, "録音停止時に音声認識が再開始されました。");
        Assert(voiceInputRuntime.RecognitionStopCount == 1, "録音停止APIが呼び出されませんでした。");
        Assert(voiceInputRuntime.ModelLoadCount == 1, "録音停止時に校正モデルが再読込されました。");
        Assert(
            statuses.LastOrDefault()?.Message == "音声認識を停止しました。文字起こしと校正を待っています…",
            "録音停止後の校正待ち状態が表示されませんでした。");
    }

    /// <summary>TypeWhisperの停止完了前に停止済み表示へ進まないことを検証する。</summary>
    private static async Task TestVoiceInputStopAcknowledgementAsync()
    {
        // 停止APIをテスト側で保留し、受付中表示と確認後表示の順序を固定する。
        TaskCompletionSource stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingVoiceInputRuntime voiceInputRuntime = new()
        {
            OllamaReady = true,
            OllamaRunning = true,
            TypeWhisperRunning = true,
            RecognitionStopCompletion = stopCompletion
        };
        VoiceInputCoordinator coordinator = CreateVoiceInputCoordinator(voiceInputRuntime);
        List<VoiceInputStatus> statuses = [];
        coordinator.StatusChanged += statuses.Add;
        await coordinator.StartAsync(CancellationToken.None);

        Task stoppingTask = coordinator.StartAsync(CancellationToken.None);
        await Task.Delay(30);

        Assert(!stoppingTask.IsCompleted, "TypeWhisperの停止確認前に停止処理が完了しました。");
        Assert(
            statuses.LastOrDefault()?.Message == "音声認識を停止しています…",
            "TypeWhisperの停止確認前に校正待ち表示へ進みました。");
        stopCompletion.SetResult();
        await stoppingTask;
        Assert(
            statuses.LastOrDefault()?.Message == "音声認識を停止しました。文字起こしと校正を待っています…",
            "TypeWhisperの停止確認後に校正待ち表示へ進みませんでした。");
    }

    /// <summary>TypeWhisper停止APIの失敗を停止済みと誤認しないことを検証する。</summary>
    private static async Task TestVoiceInputStopFailureAsync()
    {
        // 停止失敗後も録音中フラグを維持し、次のF13が停止再試行になることを確認する。
        RecordingVoiceInputRuntime voiceInputRuntime = new()
        {
            OllamaReady = true,
            OllamaRunning = true,
            TypeWhisperRunning = true,
            RecognitionStopError = new HttpRequestException("停止API失敗")
        };
        VoiceInputCoordinator coordinator = CreateVoiceInputCoordinator(voiceInputRuntime);
        List<VoiceInputStatus> statuses = [];
        coordinator.StatusChanged += statuses.Add;

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None);

        Assert(voiceInputRuntime.RecognitionStartCount == 1, "停止失敗後に新しい録音が開始されました。");
        Assert(voiceInputRuntime.RecognitionStopCount == 2, "停止失敗後のF13で停止が再試行されませんでした。");
        Assert(statuses.LastOrDefault()?.Kind == VoiceInputStatusKind.Error, "停止失敗がエラー表示されませんでした。");
        Assert(
            statuses.All(status => status.Message != "音声認識を停止しました。文字起こしと校正を待っています…"),
            "停止失敗を停止済みとして校正待ち表示へ進みました。");
    }

    /// <summary>TypeWhisper APIの準備確認、ワークフロー開始、状態確認、停止要求を検証する。</summary>
    private static async Task TestTypeWhisperApiControlAsync()
    {
        // 偽HTTPハンドラーで実APIと同じJSONを返し、ホットキーなしの制御経路を固定する。
        RecordingTypeWhisperMessageHandler messageHandler = new();
        using HttpClient httpClient = new(messageHandler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8978/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        TypeWhisperApiClient apiClient = new(httpClient);

        Assert(await apiClient.IsReadyAsync(CancellationToken.None), "TypeWhisperのダウンロード済み選択モデルを録音開始可能と判定できませんでした。");
        await apiClient.StartCorrectionRecordingAsync(CancellationToken.None);
        Assert(await apiClient.IsRecordingAsync(CancellationToken.None), "TypeWhisperの録音開始状態を取得できませんでした。");
        await apiClient.StopRecordingAsync(CancellationToken.None);
        Assert(!await apiClient.IsRecordingAsync(CancellationToken.None), "TypeWhisperの録音停止状態を取得できませんでした。");
        Assert(
            messageHandler.StartRequestBody.Contains("f3e169bf-39f6-4563-b85c-70e51f3d9a5a", StringComparison.Ordinal),
            "音声校正ワークフローIDが録音開始APIへ渡されませんでした。");
    }

    /// <summary>TypeWhisper連携導入スクリプトがローカルAPIを既定ポートで有効化することを検証する。</summary>
    private static Task TestTypeWhisperInstallerEnablesApiAsync()
    {
        // 実ユーザー設定を書き換えず、配布スクリプトに必要な設定更新が含まれることを確認する。
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "scripts", "install-typewhisper-integration.implementation.ps1");
        string scriptSource = File.ReadAllText(Path.GetFullPath(scriptPath));
        Assert(
            scriptSource.Contains("$settings.apiServerEnabled = $true", StringComparison.Ordinal),
            "TypeWhisper APIを有効化する設定更新がありません。");
        Assert(
            scriptSource.Contains("$settings.apiServerPort = 8978", StringComparison.Ordinal),
            "TypeWhisper APIを既定ポートへ設定する更新がありません。");
        return Task.CompletedTask;
    }

    /// <summary>Ollamaモデル準備完了の確認後にだけ校正本文を生成APIへ渡すことを検証する。</summary>
    private static Task TestOllamaScriptWaitsForModelAsync()
    {
        // 校正スクリプト内の待機呼出しが本文入り要求の構築と送信より前にあることを固定する。
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "integrations", "TypeWhisper.Ollama", "Invoke-OllamaTypeWhisper.ps1");
        string scriptSource = File.ReadAllText(Path.GetFullPath(scriptPath));
        int waitCallPosition = scriptSource.IndexOf(
            "Wait-OllamaModelReady -ModelName $modelName",
            StringComparison.Ordinal);
        int requestBodyPosition = scriptSource.IndexOf("$requestBody = @{", StringComparison.Ordinal);
        int generateRequestPosition = scriptSource.IndexOf(
            "'http://127.0.0.1:11434/api/generate'",
            StringComparison.Ordinal);
        Assert(waitCallPosition >= 0, "Ollamaモデル準備完了の待機処理がありません。");
        Assert(
            waitCallPosition < requestBodyPosition && requestBodyPosition < generateRequestPosition,
            "モデル準備完了前に校正本文をOllama要求へ渡す構造になっています。");
        return Task.CompletedTask;
    }

    /// <summary>Ollama事前ロード要求のモデル名と常駐時間を検証する。</summary>
    private static async Task TestOllamaPreloadRequestAsync()
    {
        // 偽HTTPハンドラーへgenerate要求を送り、生成プロンプトを含めず5分常駐を指定する。
        RecordingOllamaMessageHandler messageHandler = new();
        using HttpClient httpClient = new(messageHandler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        using HttpClient typeWhisperHttpClient = new(new RecordingTypeWhisperMessageHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:8978/")
        };
        VoiceInputRuntime runtime = new(httpClient, new TypeWhisperApiClient(typeWhisperHttpClient));
        await runtime.LoadOllamaModelAsync(CancellationToken.None);

        Assert(messageHandler.RequestPath == "/api/generate", "Ollama事前ロードAPIのパスが異なります。");
        using JsonDocument requestDocument = JsonDocument.Parse(messageHandler.RequestBody);
        JsonElement requestRoot = requestDocument.RootElement;
        Assert(
            requestRoot.GetProperty("model").GetString() == "qwen3-typewhisper:latest",
            "事前ロードするOllamaモデルが異なります。");
        Assert(requestRoot.GetProperty("keep_alive").GetString() == "5m", "Ollamaの常駐時間が5分ではありません。");
        Assert(!requestRoot.TryGetProperty("prompt", out _), "事前ロード要求に生成プロンプトが含まれています。");
    }

    /// <summary>音声入力コーディネーターを短いテスト設定で生成する。</summary>
    private static VoiceInputCoordinator CreateVoiceInputCoordinator(IVoiceInputRuntime voiceInputRuntime)
    {
        // 即時準備される偽環境を使い、外部アプリを起動せず制御順序だけを検証する。
        return new VoiceInputCoordinator(
            voiceInputRuntime,
            new VoiceInputCoordinatorOptions
            {
                ReadinessTimeout = TimeSpan.FromSeconds(1),
                PollingInterval = TimeSpan.FromMilliseconds(10)
            },
            TimeProvider.System,
            NullLogger<VoiceInputCoordinator>.Instance);
    }

    /// <summary>SQLite保存と依存関係の往復を検証する。</summary>
    private static async Task TestRepositoryRoundTripAsync()
    {
        // 一時データベースへ2件と依存関係を保存して再読込する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ManagedTask firstTask = CreateTask("first", "先行");
        firstTask.EarliestStartAt = null;
        ManagedTask secondTask = CreateTask("second", "後続");
        await testDatabase.Repository.SaveTaskAsync(firstTask, "テスト", TaskConstants.SystemSource);
        await testDatabase.Repository.SaveTaskAsync(secondTask, "テスト", TaskConstants.SystemSource);
        secondTask.DependencyIdentifiers = [firstTask.Identifier];
        await testDatabase.Repository.SaveTaskAsync(secondTask, "テスト", TaskConstants.SystemSource);
        List<ManagedTask> tasks = await testDatabase.Repository.GetTasksAsync();
        ManagedTask loadedTask = tasks.Single(task => task.Identifier == secondTask.Identifier);
        ManagedTask loadedFirstTask = tasks.Single(task => task.Identifier == firstTask.Identifier);
        Assert(loadedTask.DependencyIdentifiers.SequenceEqual([firstTask.Identifier]), "依存関係を復元できませんでした。");
        Assert(loadedFirstTask.EarliestStartAt == loadedFirstTask.CreatedAt, "旧タスクの開始可能日が登録日時で補完されませんでした。");
    }

    /// <summary>欠落依存の保存、下書き保護、完全削除確認を検証する。</summary>
    private static async Task TestTaskServiceSafetyAsync()
    {
        // 不整合は要確認として保存し、承認迂回と曖昧な削除を拒否する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        DateTimeOffset currentTime = StandardTime();
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            new FixedTimeProvider(currentTime));
        ProjectService projectService = new(
            testDatabase.ProjectRepository,
            testDatabase.Repository,
            new FixedTimeProvider(currentTime));
        TaskValidationService validationService = new();
        DraftPostProcessingService draftPostProcessingService = new(
            testDatabase.Repository,
            validationService,
            recommendationService);
        TaskService taskService = new(
            testDatabase.Repository,
            recommendationService,
            validationService,
            projectService,
            draftPostProcessingService,
            testDatabase.TimeEntryRepository,
            new FixedTimeProvider(currentTime));
        ManagedTask missingDependencyTask = CreateTask("missing-dependency", "欠落依存");
        missingDependencyTask.DependencyIdentifiers = ["not-found"];
        ManagedTask savedTask = await taskService.AddTaskAsync(missingDependencyTask, TaskConstants.SystemSource);
        Assert(savedTask.Status == TaskConstants.NeedsReviewStatus, "欠落依存が要確認になりませんでした。");
        Assert(savedTask.ValidationResult.Contains("not-found", StringComparison.Ordinal), "欠落依存の理由が保存されませんでした。");
        Assert(savedTask.EarliestStartAt == savedTask.CreatedAt, "新規タスクの開始可能日が登録日時で補完されませんでした。");

        ManagedTask draftTask = CreateTask("draft-task", "未承認下書き");
        draftTask.Status = TaskConstants.DraftStatus;
        await taskService.AddTaskAsync(draftTask, TaskConstants.SystemSource);
        bool draftStartRejected = false;
        try
        {
            await taskService.ExecuteActionAsync("start", draftTask.Identifier, null, TaskConstants.SystemSource);
        }
        catch (InvalidOperationException)
        {
            draftStartRejected = true;
        }
        Assert(draftStartRejected, "下書きタスクを開始できてしまいました。");

        ManagedTask deletableTask = CreateTask("delete-task", "削除確認");
        await taskService.AddTaskAsync(deletableTask, TaskConstants.SystemSource);
        bool wrongConfirmationRejected = false;
        try
        {
            await taskService.DeleteTaskAsync(deletableTask.Identifier, "wrong-id", TaskConstants.SystemSource);
        }
        catch (InvalidOperationException)
        {
            wrongConfirmationRejected = true;
        }
        Assert(wrongConfirmationRejected, "異なる確認IDで削除できてしまいました。");
        await taskService.DeleteTaskAsync(deletableTask.Identifier, deletableTask.Identifier, TaskConstants.SystemSource);
        Assert(await testDatabase.Repository.GetTaskAsync(deletableTask.Identifier) is null, "確認済みタスクを削除できませんでした。");
    }

    /// <summary>空IDとaiReferenceKeyによる下書き依存関係の保存を検証する。</summary>
    private static async Task TestDraftReferenceMappingAsync()
    {
        // サーバー生成IDと仮参照キーの対応表を返し依存関係を実IDへ変換する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskService taskService = CreateTaskServiceForTest(testDatabase, StandardTime());
        ManagedTask firstTask = CreateTask(string.Empty, "下書き先行");
        firstTask.AiReferenceKey = "DRAFT-FIRST";
        ManagedTask secondTask = CreateTask(string.Empty, "下書き後続");
        secondTask.AiReferenceKey = "DRAFT-SECOND";
        secondTask.DependencyIdentifiers = [firstTask.AiReferenceKey];
        DraftBatchRequest request = new()
        {
            Title = "仮参照テスト",
            Tasks = [firstTask, secondTask]
        };

        DraftBatchCreationResult result = await taskService.CreateDraftBatchAsync(request);

        Assert(result.Saved && result.PostProcessingSucceeded, "下書き作成が成功しませんでした。");
        Assert(result.BatchIdentifier == result.Identifier, "互換IDが正式バッチIDと一致しません。");
        Assert(result.TaskCount == 2 && result.TaskMappings.Count == 2, "タスク対応表の件数が不正です。");
        DraftTaskMapping firstMapping = result.TaskMappings.Single(mapping =>
            mapping.AiReferenceKey == firstTask.AiReferenceKey);
        DraftTaskMapping secondMapping = result.TaskMappings.Single(mapping =>
            mapping.AiReferenceKey == secondTask.AiReferenceKey);
        DraftBatchRecord storedBatch = await testDatabase.Repository.GetDraftBatchAsync(result.BatchIdentifier)
            ?? throw new InvalidOperationException("保存済み下書きバッチが見つかりません。");
        ManagedTask storedSecondTask = storedBatch.Tasks.Single(task =>
            task.Identifier == secondMapping.TaskIdentifier);
        Assert(
            storedSecondTask.DependencyIdentifiers.SequenceEqual([firstMapping.TaskIdentifier]),
            "仮参照キーが生成済みタスクIDへ変換されませんでした。");
        string responseJson = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument responseDocument = JsonDocument.Parse(responseJson);
        Assert(
            responseDocument.RootElement.GetProperty("batchIdentifier").GetString() == result.BatchIdentifier,
            "JSON応答にbatchIdentifierがありません。");
    }

    /// <summary>重複キーと未解決参照が保存前に拒否されることを検証する。</summary>
    private static async Task TestDraftPreCommitValidationAsync()
    {
        // どちらの入力不正でもバッチとタスクを1件も保存しないことを確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskService taskService = CreateTaskServiceForTest(testDatabase, StandardTime());
        ManagedTask duplicateFirstTask = CreateTask(string.Empty, "重複1");
        duplicateFirstTask.AiReferenceKey = "DUPLICATE";
        ManagedTask duplicateSecondTask = CreateTask(string.Empty, "重複2");
        duplicateSecondTask.AiReferenceKey = "duplicate";
        bool duplicateRejected = false;
        try
        {
            await taskService.CreateDraftBatchAsync(new DraftBatchRequest
            {
                Title = "重複キー",
                Tasks = [duplicateFirstTask, duplicateSecondTask]
            });
        }
        catch (InvalidOperationException)
        {
            duplicateRejected = true;
        }
        Assert(duplicateRejected, "重複aiReferenceKeyが拒否されませんでした。");

        ManagedTask unresolvedTask = CreateTask(string.Empty, "未解決参照");
        unresolvedTask.AiReferenceKey = "UNRESOLVED";
        unresolvedTask.DependencyIdentifiers = ["UNKNOWN-REFERENCE"];
        bool unresolvedRejected = false;
        try
        {
            await taskService.CreateDraftBatchAsync(new DraftBatchRequest
            {
                Title = "未解決参照",
                Tasks = [unresolvedTask]
            });
        }
        catch (InvalidOperationException)
        {
            unresolvedRejected = true;
        }
        Assert(unresolvedRejected, "未解決依存参照が拒否されませんでした。");
        Assert((await testDatabase.Repository.GetDraftBatchesAsync()).Count == 0, "不正入力の下書きバッチが保存されました。");
        Assert((await testDatabase.Repository.GetTasksAsync()).Count == 0, "不正入力の下書きタスクが保存されました。");
    }

    /// <summary>同じ冪等キーの再送で既存下書きを返すことを検証する。</summary>
    private static async Task TestDraftIdempotencyAsync()
    {
        // 同じ要求を2回送ってもバッチとタスクが1件ずつだけ存在することを確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskService taskService = CreateTaskServiceForTest(testDatabase, StandardTime());
        ManagedTask task = CreateTask(string.Empty, "冪等下書き");
        task.AiReferenceKey = "IDEMPOTENT-TASK";
        DraftBatchRequest request = new()
        {
            Title = "冪等テスト",
            IdempotencyKey = "request-20260729-001",
            Tasks = [task]
        };

        DraftBatchCreationResult firstResult = await taskService.CreateDraftBatchAsync(request);
        DraftBatchCreationResult secondResult = await taskService.CreateDraftBatchAsync(request);

        Assert(firstResult.BatchIdentifier == secondResult.BatchIdentifier, "再送で異なるバッチが返されました。");
        Assert(secondResult.Replayed, "再送応答が既存結果として識別されませんでした。");
        Assert((await testDatabase.Repository.GetDraftBatchesAsync()).Count == 1, "冪等再送でバッチが重複しました。");
        Assert((await testDatabase.Repository.GetTasksAsync()).Count == 1, "冪等再送でタスクが重複しました。");
    }

    /// <summary>コミット後処理の失敗が警告付き成功へ変換されることを検証する。</summary>
    private static async Task TestDraftPostProcessingWarningAsync()
    {
        // 意図的な後処理例外でも保存済みバッチと終了成功用応答を維持する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskService taskService = CreateTaskServiceForTest(
            testDatabase,
            StandardTime(),
            new FailingDraftPostProcessingService());
        ManagedTask task = CreateTask(string.Empty, "後処理失敗下書き");
        task.AiReferenceKey = "POST-FAILURE";

        DraftBatchCreationResult result = await taskService.CreateDraftBatchAsync(new DraftBatchRequest
        {
            Title = "後処理失敗",
            Tasks = [task]
        });

        Assert(result.Saved, "後処理失敗で保存結果が失敗扱いになりました。");
        Assert(!result.PostProcessingSucceeded, "後処理失敗が成功扱いになりました。");
        Assert(result.Warning.Contains("下書きは保存されました", StringComparison.Ordinal), "保存後警告が返されませんでした。");
        DraftBatchRecord storedBatch = await testDatabase.Repository.GetDraftBatchAsync(result.BatchIdentifier)
            ?? throw new InvalidOperationException("警告付き下書きが保存されていません。");
        Assert(!storedBatch.PostProcessingSucceeded, "DBへ後処理失敗状態が保存されませんでした。");
    }

    /// <summary>draft-createの互換応答、JSON、警告、終了コードを検証する。</summary>
    private static async Task TestDraftCliResponseAsync()
    {
        // 実データを変更せず模擬HTTP応答からCLIの保存後表示処理を確認する。
        string draftPath = Path.Combine(Path.GetTempPath(), $"TaskManager-Draft-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(draftPath, """{"title":"CLIテスト","tasks":[]}""");
        try
        {
            (int compatibilityExitCode, string compatibilityOutput, string compatibilityError) =
                await RunDraftCliAsync(
                    """{"identifier":"DRAFT-COMPATIBILITY"}""",
                    draftPath,
                    jsonOutput: false);
            Assert(compatibilityExitCode == 0, "identifier互換応答でCLIが失敗しました。");
            Assert(
                compatibilityOutput.Contains("DRAFT-COMPATIBILITY", StringComparison.Ordinal),
                "identifierへのフォールバック表示ができませんでした。");
            Assert(string.IsNullOrWhiteSpace(compatibilityError), "正常応答で標準エラーが出力されました。");

            const string warningResponse = """
                {
                  "batchIdentifier": "DRAFT-WARNING",
                  "identifier": "DRAFT-WARNING",
                  "saved": true,
                  "taskCount": 2,
                  "postProcessingSucceeded": false,
                  "warning": "下書きは保存されましたが、後処理に失敗しました",
                  "taskMappings": []
                }
                """;
            (int warningExitCode, string warningOutput, string warningError) =
                await RunDraftCliAsync(warningResponse, draftPath, jsonOutput: true);
            Assert(warningExitCode == 0, "保存後警告でCLIが非ゼロ終了しました。");
            using JsonDocument outputDocument = JsonDocument.Parse(warningOutput);
            Assert(outputDocument.RootElement.GetProperty("saved").GetBoolean(), "CLIのJSON出力が保存成功を保持していません。");
            Assert(
                warningError.Contains("下書きは保存されました", StringComparison.Ordinal),
                "保存後警告が標準エラーへ出力されませんでした。");
        }
        finally
        {
            File.Delete(draftPath);
        }
    }

    /// <summary>模擬API応答を使ってdraft-createを実行し標準出力を取得する。</summary>
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDraftCliAsync(
        string draftResponseJson,
        string draftPath,
        bool jsonOutput)
    {
        // Consoleの出力先を一時変更し、終了後は必ず元へ戻す。
        using HttpClient httpClient = new(new DraftResponseMessageHandler(draftResponseJson))
        {
            BaseAddress = new Uri(TaskConstants.LocalAddress)
        };
        CliRunner cliRunner = new(httpClient);
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter standardOutput = new();
        using StringWriter standardError = new();
        Console.SetOut(standardOutput);
        Console.SetError(standardError);
        try
        {
            List<string> arguments = ["draft-create", "--file", draftPath];
            if (jsonOutput)
            {
                arguments.Add("--json");
            }
            int exitCode = await cliRunner.RunAsync(arguments.ToArray());
            return (exitCode, standardOutput.ToString(), standardError.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    /// <summary>無期限タスクの推薦結果がJSON標準値だけを含むことを検証する。</summary>
    private static Task TestNoDeadlineSerializationAsync()
    {
        // 無限大の余裕時間をnullへ変換してWeb APIのJSONエラーを防ぐ。
        DateTimeOffset currentTime = StandardTime();
        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            [CreateTask("no-deadline", "無期限")],
            []);
        string json = JsonSerializer.Serialize(result);
        Assert(!json.Contains("Infinity", StringComparison.Ordinal), "JSONへInfinityが出力されました。");
        Assert(result.Recommendation?.SlackMinutes is null, "無期限タスクの余裕時間がnullではありません。");
        return Task.CompletedTask;
    }

    /// <summary>同じ通知キーが1回だけ登録されることを検証する。</summary>
    private static async Task TestNotificationDeduplicationAsync()
    {
        // SQLite主キーによる重複防止を確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        bool firstResult = await testDatabase.Repository.TryRecordNotificationAsync("same-key", "テスト", string.Empty);
        bool secondResult = await testDatabase.Repository.TryRecordNotificationAsync("same-key", "テスト", string.Empty);
        Assert(firstResult && !secondResult, "通知の重複が防止されませんでした。");
    }

    /// <summary>全半角正規化、別名、高確信の表記ゆれ解決を検証する。</summary>
    private static async Task TestProjectResolutionAsync()
    {
        // 確認済み別名の完全一致と長い名称の軽微な誤記を解決する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ProjectService projectService = CreateProjectService(testDatabase, StandardTime());
        ProjectRecord project = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-ALPHA", CanonicalName = "アルファ販売管理システム刷新計画" },
            TaskConstants.SystemSource);
        await projectService.AddAliasAsync(project.Identifier, "ＡＬＰＨＡ　案件", TaskConstants.SystemSource);
        ProjectResolutionResult aliasResolution = await projectService.ResolveAsync("alpha案件");
        Assert(aliasResolution.Project?.Identifier == project.Identifier, "全半角別名を解決できませんでした。");
        ProjectResolutionResult fuzzyResolution = await projectService.ResolveAsync("アルファ販売管理システム刷新計画案");
        Assert(fuzzyResolution.Project?.Identifier == project.Identifier, "高確信の表記ゆれを解決できませんでした。");
    }

    /// <summary>プロジェクト色の自動生成、手動更新、不正値拒否を検証する。</summary>
    private static async Task TestProjectColorAsync()
    {
        // 新規色の固定彩度・明度と、HSV全項目を指定した更新結果を確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ProjectService projectService = CreateProjectService(testDatabase, StandardTime());
        ProjectRecord project = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-COLOR", CanonicalName = "色設定テスト" },
            TaskConstants.SystemSource);
        Assert(project.ColorHue is >= 0 and <= 359, "自動色相が有効範囲外です。");
        Assert(project.ColorSaturation == 50, "自動色の彩度が既定値ではありません。");
        Assert(project.ColorValue == 78, "自動色の明度が既定値ではありません。");

        ProjectRecord updatedProject = await projectService.UpdateProjectAsync(
            project.Identifier,
            new ProjectRecord
            {
                CanonicalName = project.CanonicalName,
                Status = ProjectConstants.ActiveStatus,
                ColorHue = 215,
                ColorSaturation = 35,
                ColorValue = 84
            },
            TaskConstants.SystemSource);
        Assert(
            updatedProject.ColorHue == 215
            && updatedProject.ColorSaturation == 35
            && updatedProject.ColorValue == 84,
            "手動指定したHSV色を保存・復元できませんでした。");

        bool partialColorRejected = false;
        try
        {
            await projectService.AddProjectAsync(
                new ProjectRecord
                {
                    Identifier = "PROJECT-PARTIAL-COLOR",
                    CanonicalName = "不完全色テスト",
                    ColorHue = 120
                },
                TaskConstants.SystemSource);
        }
        catch (InvalidOperationException)
        {
            partialColorRejected = true;
        }
        Assert(partialColorRejected, "Hだけの不完全なプロジェクト色が登録されました。");
    }

    /// <summary>複数候補が近いプロジェクト名をあいまいとして返すことを検証する。</summary>
    private static async Task TestAmbiguousProjectResolutionAsync()
    {
        // 1位と2位の差が小さい名称を勝手に自動確定しない。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ProjectService projectService = CreateProjectService(testDatabase, StandardTime());
        await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-IMPROVE", CanonicalName = "顧客管理改善" },
            TaskConstants.SystemSource);
        await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-RENEW", CanonicalName = "顧客管理改修" },
            TaskConstants.SystemSource);
        ProjectResolutionResult resolution = await projectService.ResolveAsync("顧客管理改");
        Assert(resolution.Status == ProjectConstants.AmbiguousResolution, "近い複数候補が自動確定されました。");
        Assert(resolution.Project is null && resolution.Candidates.Count == 2, "確認候補が正しく返されませんでした。");
    }

    /// <summary>未解決結果でも低確信候補を最大3件返すことを検証する。</summary>
    private static async Task TestLowConfidenceProjectCandidatesAsync()
    {
        // 類似度0.65未満の候補を参考表示し自動確定や別名登録を行わない。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ProjectService projectService = CreateProjectService(testDatabase, StandardTime());
        ProjectRecord project = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-LOW", CanonicalName = "アルファ販売管理" },
            TaskConstants.SystemSource);

        ProjectResolutionResult resolution = await projectService.ResolveAsync("アルファ資料");

        Assert(resolution.Status == ProjectConstants.NotFoundResolution, "低確信候補が自動確定されました。");
        Assert(resolution.Project is null, "未解決結果へプロジェクト本体が設定されました。");
        Assert(resolution.Candidates.Count is > 0 and <= 3, "低確信候補が最大3件で返されませんでした。");
        Assert(resolution.Candidates[0].ProjectIdentifier == project.Identifier, "期待する低確信候補が返されませんでした。");
        Assert(resolution.Candidates[0].Confidence < 0.65, "低確信候補の類似度がしきい値以上です。");
        Assert(resolution.Candidates[0].MatchSource == "canonical", "正式名称との一致元が返されませんでした。");
        ProjectRecord reloadedProject = await testDatabase.ProjectRepository.GetProjectAsync(project.Identifier)
            ?? throw new InvalidOperationException("低確信候補のプロジェクトが見つかりません。");
        Assert(reloadedProject.Aliases.Count == 0, "低確信入力が別名として自動登録されました。");
    }

    /// <summary>有効状態と有効期間に従って背景情報を統合することを検証する。</summary>
    private static async Task TestProjectContextPreparationAsync()
    {
        // 現在有効な文書だけを優先度順でCodex向けMarkdownへ含める。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        DateTimeOffset currentTime = StandardTime();
        ProjectService projectService = CreateProjectService(testDatabase, currentTime);
        ProjectRecord project = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-CONTEXT", CanonicalName = "背景情報テスト" },
            TaskConstants.SystemSource);
        await projectService.SaveContextDocumentAsync(
            project.Identifier,
            new ProjectContextDocument
            {
                Title = "現在有効",
                ContentMarkdown = "毎週レビューを行う。",
                Priority = 5,
                Enabled = true,
                ValidFrom = currentTime.AddDays(-1),
                ValidUntil = currentTime.AddDays(1)
            },
            TaskConstants.SystemSource);
        await projectService.SaveContextDocumentAsync(
            project.Identifier,
            new ProjectContextDocument
            {
                Title = "期限切れ",
                ContentMarkdown = "古い手順。",
                Priority = 4,
                Enabled = true,
                ValidUntil = currentTime.AddMinutes(-1)
            },
            TaskConstants.SystemSource);
        ProjectPreparationResult preparation = await projectService.PrepareAsync(project.Identifier);
        Assert(preparation.ContextMarkdown.Contains("現在有効", StringComparison.Ordinal), "有効な背景情報が含まれません。");
        Assert(!preparation.ContextMarkdown.Contains("期限切れ", StringComparison.Ordinal), "期限切れ背景情報が含まれました。");
        Assert(preparation.ContextMarkdown.Contains("上書きしません", StringComparison.Ordinal), "指示境界文がありません。");
    }

    /// <summary>毎週の既定期限を期限未指定の新規タスクだけへ適用することを検証する。</summary>
    private static async Task TestProjectDeadlineDefaultAsync()
    {
        // 火曜日現在から次の金曜日17時を計算し、明示的な期限なしを維持する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        DateTimeOffset currentTime = StandardTime();
        ProjectService projectService = CreateProjectService(testDatabase, currentTime);
        ProjectRecord project = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "PROJECT-DEADLINE", CanonicalName = "期限テスト" },
            TaskConstants.SystemSource);
        await projectService.SaveDeadlineRuleAsync(
            project.Identifier,
            new ProjectDeadlineRule
            {
                Weekday = 5,
                LocalTime = "17:00",
                DeadlineType = TaskConstants.TargetDeadlineType,
                Enabled = true
            },
            TaskConstants.SystemSource);
        ManagedTask automaticTask = CreateTask("project-auto", "既定期限");
        automaticTask.ProjectIdentifier = project.Identifier;
        automaticTask.DeadlineOrigin = ProjectConstants.AutomaticDeadlineOrigin;
        await projectService.ApplyProjectDefaultsAsync(automaticTask, isNewTask: true);
        Assert(
            automaticTask.DeadlineAt == new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.FromHours(9)),
            "次の毎週期限が正しく計算されませんでした。");
        Assert(
            automaticTask.DeadlineOrigin == ProjectConstants.ProjectDefaultDeadlineOrigin,
            "期限由来がプロジェクト既定になりませんでした。");

        ManagedTask noDeadlineTask = CreateTask("project-none", "期限なし");
        noDeadlineTask.ProjectIdentifier = project.Identifier;
        noDeadlineTask.DeadlineOrigin = ProjectConstants.NoDeadlineOrigin;
        await projectService.ApplyProjectDefaultsAsync(noDeadlineTask, isNewTask: true);
        Assert(noDeadlineTask.DeadlineAt is null, "明示的な期限なしが上書きされました。");
    }

    /// <summary>スキーマ版4とプロジェクト・下書き・作業ログが再初期化後も維持されることを検証する。</summary>
    private static async Task TestProjectSchemaVersionAsync()
    {
        // v2初期化後に既存タスクを保存し、再初期化してもデータを失わないことを確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        ManagedTask existingTask = CreateTask("schema-preserve", "保持確認");
        await testDatabase.Repository.SaveTaskAsync(existingTask, "テスト", TaskConstants.SystemSource);
        await testDatabase.Initializer.InitializeAsync();
        ManagedTask? loadedTask = await testDatabase.Repository.GetTaskAsync(existingTask.Identifier);
        Assert(loadedTask is not null, "スキーマ再初期化で既存タスクが失われました。");
        await using Microsoft.Data.Sqlite.SqliteConnection connection = testDatabase.Initializer.OpenConnection();
        await using Microsoft.Data.Sqlite.SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version_number) FROM schema_versions;";
        long versionNumber = Convert.ToInt64(await versionCommand.ExecuteScalarAsync());
        Assert(versionNumber == 5, "スキーマ版5が記録されていません。");
        await using Microsoft.Data.Sqlite.SqliteCommand columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name IN ('project_identifier', 'deadline_origin');";
        long columnCount = Convert.ToInt64(await columnCommand.ExecuteScalarAsync());
        Assert(columnCount == 2, "タスクのプロジェクト列が不足しています。");
        await using Microsoft.Data.Sqlite.SqliteCommand timeEntryTableCommand = connection.CreateCommand();
        timeEntryTableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'time_entries';";
        long timeEntryTableCount = Convert.ToInt64(await timeEntryTableCommand.ExecuteScalarAsync());
        Assert(timeEntryTableCount == 1, "作業時間ログテーブルがありません。");
        await using Microsoft.Data.Sqlite.SqliteCommand colorColumnCommand = connection.CreateCommand();
        colorColumnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name IN ('color_hue', 'color_saturation', 'color_value');";
        long colorColumnCount = Convert.ToInt64(await colorColumnCommand.ExecuteScalarAsync());
        Assert(colorColumnCount == 3, "プロジェクトのHSV色列が不足しています。");
        await using Microsoft.Data.Sqlite.SqliteCommand draftColumnCommand = connection.CreateCommand();
        draftColumnCommand.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('draft_batches')
            WHERE name IN ('request_key', 'post_processing_succeeded', 'warning');
            """;
        long draftColumnCount = Convert.ToInt64(await draftColumnCommand.ExecuteScalarAsync());
        Assert(draftColumnCount == 3, "下書きバッチの冪等性列が不足しています。");
    }

    /// <summary>開始・中断・再開・完了から作業区間が自動生成されることを検証する。</summary>
    private static async Task TestTaskTimeEntryTransitionsAsync()
    {
        // 可変時刻で2区間を作り、タスク状態と終了理由を確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(StandardTime());
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        ManagedTask task = await taskService.AddTaskAsync(
            CreateTask("TIME-TASK", "時間記録テスト"),
            TaskConstants.SystemSource);

        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord firstActive = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("開始ログが作成されませんでした。");
        Assert(firstActive.TaskIdentifier == task.Identifier, "開始ログへタスクIDが保存されませんでした。");
        timeProvider.SetCurrentTime(StandardTime().AddMinutes(25));
        await taskService.ExecuteActionAsync("interrupt", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord firstEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(firstActive.Identifier)
            ?? throw new InvalidOperationException("中断ログが見つかりません。");
        Assert(firstEntry.StopReason == TimeTrackingConstants.InterruptedStopReason, "中断理由が保存されませんでした。");
        Assert(firstEntry.EndAt == StandardTime().AddMinutes(25), "中断時刻が正しくありません。");

        timeProvider.SetCurrentTime(StandardTime().AddMinutes(40));
        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord secondActive = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("再開ログが作成されませんでした。");
        Assert(secondActive.Identifier != firstEntry.Identifier, "再開時に新しい区間が作成されませんでした。");
        timeProvider.SetCurrentTime(StandardTime().AddMinutes(55));
        await taskService.ExecuteActionAsync("complete", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord secondEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(secondActive.Identifier)
            ?? throw new InvalidOperationException("完了ログが見つかりません。");
        ManagedTask completedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("完了タスクが見つかりません。");
        Assert(secondEntry.StopReason == TimeTrackingConstants.CompletedStopReason, "完了理由が保存されませんでした。");
        Assert(completedTask.StartedAt is null, "終了後も現在区間の開始日時が残っています。");
        await taskService.DeleteTaskAsync(
            task.Identifier,
            task.Identifier,
            TaskConstants.SystemSource);
        TimeEntryRecord retainedEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(secondActive.Identifier)
            ?? throw new InvalidOperationException("タスク削除後に作業ログが失われました。");
        Assert(retainedEntry.TaskIdentifier is null, "タスク削除後の参照がSET NULLになりませんでした。");
        Assert(retainedEntry.Title == task.Title, "タスク削除後に名称スナップショットが失われました。");
    }

    /// <summary>タスクと自由活動の切替が常に単一タイマーになることを検証する。</summary>
    private static async Task TestFreeActivitySwitchAsync()
    {
        // タスク開始後に自由活動へ切り替え、さらにタスクへ戻す。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(StandardTime());
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        ManagedTask task = await taskService.AddTaskAsync(
            CreateTask("SWITCH-TASK", "切替対象"),
            TaskConstants.SystemSource);
        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord taskEntry = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("タスクタイマーがありません。");

        timeProvider.SetCurrentTime(StandardTime().AddMinutes(10));
        TimeEntryRecord freeEntry = await taskService.StartFreeActivityAsync(
            new TimeEntryStartRequest { Title = "資料整理" },
            TaskConstants.SystemSource);
        TimeEntryRecord closedTaskEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(taskEntry.Identifier)
            ?? throw new InvalidOperationException("切替前ログが見つかりません。");
        ManagedTask interruptedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("切替対象タスクが見つかりません。");
        Assert(closedTaskEntry.StopReason == TimeTrackingConstants.ReplacedStopReason, "自由活動への切替理由が正しくありません。");
        Assert(interruptedTask.Status == TaskConstants.ReadyStatus, "自由活動開始時にタスクが中断されませんでした。");
        Assert((await testDatabase.TimeEntryRepository.GetActiveAsync())?.Identifier == freeEntry.Identifier, "自由活動が単一タイマーになりませんでした。");

        timeProvider.SetCurrentTime(StandardTime().AddMinutes(20));
        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord closedFreeEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(freeEntry.Identifier)
            ?? throw new InvalidOperationException("自由活動ログが見つかりません。");
        TimeEntryRecord resumedTaskEntry = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("タスク再開ログがありません。");
        Assert(closedFreeEntry.StopReason == TimeTrackingConstants.ReplacedStopReason, "タスク再開時に自由活動が終了しませんでした。");
        Assert(resumedTaskEntry.TaskIdentifier == task.Identifier, "再開後の単一タイマーがタスクへ紐付きませんでした。");
    }

    /// <summary>実行中ログの開始時刻修正が関連するタスク時刻へ反映されることを検証する。</summary>
    private static async Task TestActiveTimeEntryStartUpdateAsync()
    {
        // 開始を10分後へ修正し、実行状態を維持したまま確認・警告予定も10分移動する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(StandardTime());
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        ManagedTask task = await taskService.AddTaskAsync(
            CreateTask("ACTIVE-START-UPDATE", "開始時刻修正"),
            TaskConstants.SystemSource);
        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord activeEntry = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("開始時刻修正対象のログがありません。");
        ManagedTask startedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("開始済みタスクが見つかりません。");
        DateTimeOffset? originalFollowUpAt = startedTask.FollowUpAt;
        DateTimeOffset? originalWarningAt = activeEntry.NextWarningAt;

        timeProvider.SetCurrentTime(StandardTime().AddMinutes(30));
        DateTimeOffset revisedStartAt = StandardTime().AddMinutes(10);
        TimeEntryRecord updatedEntry = await taskService.UpdateActiveTimeEntryStartAsync(
            activeEntry.Identifier,
            new ActiveTimeEntryStartUpdateRequest { StartAt = revisedStartAt },
            TaskConstants.SystemSource);
        ManagedTask updatedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("修正後タスクが見つかりません。");

        Assert(!updatedEntry.EndAt.HasValue, "開始時刻修正で実行中ログが終了しました。");
        Assert(updatedEntry.StartAt == revisedStartAt, "実行中ログの開始時刻が更新されませんでした。");
        Assert(updatedTask.Status == TaskConstants.InProgressStatus && updatedTask.StartedAt == revisedStartAt,
            "関連タスクの実行状態と開始時刻が一致しません。");
        Assert(updatedTask.FollowUpAt == originalFollowUpAt?.AddMinutes(10), "完了確認予定が修正差分だけ移動しませんでした。");
        Assert(updatedEntry.NextWarningAt == originalWarningAt?.AddMinutes(10), "長時間警告予定が修正差分だけ移動しませんでした。");
    }

    /// <summary>長時間タイマーの警告、延長、無応答停止を検証する。</summary>
    private static async Task TestLongTimerAutomationAsync()
    {
        // 3時間で警告し、延長後の次回警告から60分後の予定時刻で停止する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        TaskManagerSettings settings = await testDatabase.Repository.GetSettingsAsync();
        settings.LongTimerWarningMinutes = 180;
        await testDatabase.Repository.SaveSettingsAsync(settings, TaskConstants.SystemSource);
        AdjustableTimeProvider timeProvider = new(StandardTime());
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        ManagedTask task = await taskService.AddTaskAsync(
            CreateTask("WARNING-TASK", "長時間作業"),
            TaskConstants.SystemSource);
        await taskService.ExecuteActionAsync("start", task.Identifier, null, TaskConstants.SystemSource);
        TimeEntryRecord activeEntry = await testDatabase.TimeEntryRepository.GetActiveAsync()
            ?? throw new InvalidOperationException("警告対象ログがありません。");

        timeProvider.SetCurrentTime(StandardTime().AddHours(3));
        TimeTrackingAutomationResult firstWarning = await taskService.ProcessTimeEntryWarningsAsync();
        Assert(firstWarning.WarnedEntries.Count == 1, "3時間経過時に警告されませんでした。");
        await taskService.ExtendTimeEntryAsync(activeEntry.Identifier, TaskConstants.SystemSource);
        TimeEntryRecord extendedEntry = await testDatabase.TimeEntryRepository.GetByIdentifierAsync(activeEntry.Identifier)
            ?? throw new InvalidOperationException("延長ログが見つかりません。");
        Assert(extendedEntry.NextWarningAt == StandardTime().AddHours(4), "次回警告が1時間後になりませんでした。");

        timeProvider.SetCurrentTime(StandardTime().AddHours(4));
        TimeTrackingAutomationResult secondWarning = await taskService.ProcessTimeEntryWarningsAsync();
        Assert(secondWarning.WarnedEntries.Count == 1, "延長後に再警告されませんでした。");
        timeProvider.SetCurrentTime(StandardTime().AddHours(5).AddMinutes(10));
        TimeTrackingAutomationResult automaticStop = await taskService.ProcessTimeEntryWarningsAsync();
        TimeEntryRecord stoppedEntry = automaticStop.AutoStoppedEntries.Single();
        ManagedTask stoppedTask = await testDatabase.Repository.GetTaskAsync(task.Identifier)
            ?? throw new InvalidOperationException("自動停止タスクが見つかりません。");
        Assert(stoppedEntry.EndAt == StandardTime().AddHours(5), "復帰時刻ではなく予定停止時刻が使われていません。");
        Assert(stoppedEntry.NeedsReview, "自動停止ログが要確認になりませんでした。");
        Assert(stoppedTask.Status == TaskConstants.ReadyStatus, "自動停止後にタスクが実行可能へ戻りませんでした。");
        Assert(stoppedTask.DeferralCount == 0, "自動停止で延期回数が増えました。");
    }

    /// <summary>手入力ログの重複が明示確認まで保存されないことを検証する。</summary>
    private static async Task TestManualTimeEntryOverlapAsync()
    {
        // 1件目を保存し、重複する2件目を拒否後に承認付きで保存する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(StandardTime().AddHours(2));
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        TimeEntryRecord firstEntry = await taskService.AddManualTimeEntryAsync(
            new TimeEntryMutationRequest
            {
                Title = "手入力1",
                StartAt = StandardTime(),
                EndAt = StandardTime().AddHours(1)
            },
            TaskConstants.SystemSource);
        TimeEntryMutationRequest overlappingRequest = new()
        {
            Title = "手入力2",
            StartAt = StandardTime().AddMinutes(30),
            EndAt = StandardTime().AddMinutes(90)
        };
        bool overlapRejected = false;
        try
        {
            await taskService.AddManualTimeEntryAsync(overlappingRequest, TaskConstants.SystemSource);
        }
        catch (TimeEntryOverlapException overlapError)
        {
            overlapRejected = overlapError.Conflicts.Count == 1;
        }
        Assert(overlapRejected, "重複ログが候補付きで拒否されませんでした。");
        Assert((await testDatabase.TimeEntryRepository.GetEntriesAsync(null, null)).Count == 1, "重複確認前にログが保存されました。");
        overlappingRequest.AllowOverlap = true;
        await taskService.AddManualTimeEntryAsync(overlappingRequest, TaskConstants.SystemSource);
        TimeReportService reportService = new(testDatabase.TimeEntryRepository, testDatabase.Repository, timeProvider);
        TimeReportResult report = await reportService.GetReportAsync("day", new DateOnly(2026, 7, 21), null);
        Assert(report.TotalSeconds == 7200, "承認済み重複ログが双方合算されませんでした。");
        Assert(report.OverlapCount == 1, "統計へ重複警告が反映されませんでした。");
        await taskService.VoidTimeEntryAsync(firstEntry.Identifier, TaskConstants.SystemSource);
        TimeReportResult voidedReport = await reportService.GetReportAsync("day", new DateOnly(2026, 7, 21), null);
        Assert(voidedReport.TotalSeconds == 3600 && voidedReport.OverlapCount == 0, "無効化ログが集計から除外されませんでした。");

        ProjectService projectService = CreateProjectService(testDatabase, timeProvider.GetLocalNow());
        ProjectRecord archivedProject = await projectService.AddProjectAsync(
            new ProjectRecord { Identifier = "ARCHIVED-TIME", CanonicalName = "過去案件" },
            TaskConstants.SystemSource);
        await projectService.ArchiveProjectAsync(archivedProject.Identifier, TaskConstants.SystemSource);
        await taskService.AddManualTimeEntryAsync(
            new TimeEntryMutationRequest
            {
                ProjectIdentifier = archivedProject.Identifier,
                Title = "アーカイブ案件作業",
                StartAt = StandardTime().AddMinutes(90),
                EndAt = StandardTime().AddHours(2)
            },
            TaskConstants.SystemSource);
        TimeReportResult archivedReport = await reportService.GetReportAsync("day", new DateOnly(2026, 7, 21), null);
        Assert(
            archivedReport.Projects.Any(project =>
                project.ProjectIdentifier == archivedProject.Identifier
                && project.ProjectName == archivedProject.CanonicalName),
            "アーカイブ済みプロジェクトの作業時間が集計されませんでした。");

        // 未割当用の固定値ではプロジェクトIDがNULLのログだけを取得・集計する。
        List<TimeEntryRecord> unassignedEntries = await testDatabase.TimeEntryRepository.GetEntriesAsync(
            null,
            null,
            TimeTrackingConstants.UnassignedProjectIdentifier);
        Assert(
            unassignedEntries.Count == 1 && unassignedEntries.All(entry => string.IsNullOrWhiteSpace(entry.ProjectIdentifier)),
            "未割当の作業ログだけを絞り込めませんでした。");
        TimeReportResult unassignedReport = await reportService.GetReportAsync(
            "day",
            new DateOnly(2026, 7, 21),
            TimeTrackingConstants.UnassignedProjectIdentifier);
        Assert(
            unassignedReport.Projects.Count == 1
            && unassignedReport.Projects[0].ProjectIdentifier == TimeTrackingConstants.UnassignedProjectIdentifier
            && unassignedReport.Projects[0].ProjectName == "未割当",
            "未割当の作業時間だけを集計できませんでした。");
    }

    /// <summary>異常終了情報が復旧通知後もユーザー確認まで保持されることを検証する。</summary>
    private static async Task TestSystemIncidentLifecycleAsync()
    {
        // 一時ログ領域で記録、初回通知、再通知抑止、確認済み化を順に確認する。
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"TaskManager-Incident-{Guid.NewGuid():N}");
        string? originalDataDirectory = Environment.GetEnvironmentVariable("TASKMANAGER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", temporaryDirectory);
            DateTimeOffset currentTime = new(2026, 8, 28, 12, 0, 0, TimeSpan.FromHours(9));
            TaskManagerPaths paths = new();
            SystemIncidentService incidentService = new(paths, new FixedTimeProvider(currentTime));
            SystemIncidentRecord recordedIncident = await incidentService.RecordFailureAsync(-1, 1);
            SystemIncidentRecord? pendingIncident = await incidentService.GetPendingAsync();
            Assert(pendingIncident?.Identifier == recordedIncident.Identifier, "異常終了情報が未確認として保存されませんでした。");

            SystemIncidentRecord? notificationIncident = await incidentService.MarkRecoveredAsync();
            Assert(notificationIncident?.RecoveredAt == currentTime, "復旧日時または初回通知対象が保存されませんでした。");
            SystemIncidentRecord? repeatedNotification = await incidentService.MarkRecoveredAsync();
            Assert(repeatedNotification is null, "同じ異常終了がWindows通知対象として重複しました。");

            await incidentService.AcknowledgeAsync(recordedIncident.Identifier);
            Assert(await incidentService.GetPendingAsync() is null, "確認済みの異常終了情報が残りました。");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", originalDataDirectory);
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    /// <summary>UTC保存ログが東京時間の日付境界で正しく抽出されることを検証する。</summary>
    private static async Task TestTimeEntryTimeZoneBoundaryAsync()
    {
        // UTCでは27日でも東京時間では28日になる区間を27日一覧から除外する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.FromHours(9)));
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        TimeEntryRecord timeEntry = await taskService.AddManualTimeEntryAsync(
            new TimeEntryMutationRequest
            {
                Title = "UTC日付境界",
                StartAt = new DateTimeOffset(2026, 8, 27, 15, 20, 0, TimeSpan.Zero),
                EndAt = new DateTimeOffset(2026, 8, 27, 17, 52, 0, TimeSpan.Zero)
            },
            TaskConstants.SystemSource);
        DateTimeOffset localDayStart = new(2026, 8, 27, 0, 0, 0, TimeSpan.FromHours(9));
        List<TimeEntryRecord> previousDayEntries = await testDatabase.TimeEntryRepository.GetEntriesAsync(
            localDayStart,
            localDayStart.AddDays(1));
        List<TimeEntryRecord> correctDayEntries = await testDatabase.TimeEntryRepository.GetEntriesAsync(
            localDayStart.AddDays(1),
            localDayStart.AddDays(2));
        TimeReportService reportService = new(testDatabase.TimeEntryRepository, testDatabase.Repository, timeProvider);
        TimeReportResult previousDayReport = await reportService.GetReportAsync(
            "day",
            new DateOnly(2026, 8, 27),
            null);
        TimeReportResult correctDayReport = await reportService.GetReportAsync(
            "day",
            new DateOnly(2026, 8, 28),
            null);

        Assert(previousDayEntries.All(entry => entry.Identifier != timeEntry.Identifier), "東京時間28日のログが27日の一覧へ混入しました。");
        Assert(correctDayEntries.Count(entry => entry.Identifier == timeEntry.Identifier) == 1, "東京時間28日の一覧へログが表示されませんでした。");
        Assert(previousDayReport.TotalSeconds == 0, "東京時間27日の集計へ28日のログが加算されました。");
        Assert(correctDayReport.TotalSeconds == 9120, "東京時間28日の集計時間が正しくありません。");
    }

    /// <summary>日またぎ区間と10000件の月次集計性能を検証する。</summary>
    private static async Task TestTimeReportPerformanceAsync()
    {
        // 日境界を半分ずつ集計し、大量ログを単一トランザクションで準備して性能を測る。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        AdjustableTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(9)));
        TaskService taskService = CreateTaskServiceForTimeTest(testDatabase, timeProvider);
        await taskService.AddManualTimeEntryAsync(
            new TimeEntryMutationRequest
            {
                Title = "日またぎ",
                StartAt = new DateTimeOffset(2026, 7, 20, 23, 30, 0, TimeSpan.FromHours(9)),
                EndAt = new DateTimeOffset(2026, 7, 21, 0, 30, 0, TimeSpan.FromHours(9))
            },
            TaskConstants.SystemSource);
        TimeReportService reportService = new(testDatabase.TimeEntryRepository, testDatabase.Repository, timeProvider);
        TimeReportResult firstDay = await reportService.GetReportAsync("day", new DateOnly(2026, 7, 20), null);
        TimeReportResult secondDay = await reportService.GetReportAsync("day", new DateOnly(2026, 7, 21), null);
        Assert(firstDay.TotalSeconds == 1800 && secondDay.TotalSeconds == 1800, "日またぎログが日境界で分割されませんでした。");

        await InsertPerformanceTimeEntriesAsync(testDatabase, 10000);
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeReportResult monthlyReport = await reportService.GetReportAsync("month", new DateOnly(2026, 7, 15), null);
        stopwatch.Stop();
        Assert(monthlyReport.TotalSeconds == 10000L * 900L + 3600L, "10000件の月次合計が正しくありません。");
        Assert(stopwatch.ElapsedMilliseconds < 1000, $"10000件の集計に{stopwatch.ElapsedMilliseconds}ミリ秒かかりました。");
    }

    /// <summary>性能テスト用の完了済み作業ログを一括登録する。</summary>
    private static async Task InsertPerformanceTimeEntriesAsync(TestDatabase testDatabase, int entryCount)
    {
        // 事前準備時間を計測から除外するため1接続・1トランザクションで保存する。
        await using Microsoft.Data.Sqlite.SqliteConnection connection = testDatabase.Initializer.OpenConnection();
        await using Microsoft.Data.Sqlite.SqliteTransaction transaction = connection.BeginTransaction();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO time_entries(
                identifier, task_identifier, project_identifier, title, project_name_snapshot,
                start_at, end_at, stop_reason, source, warning_at, next_warning_at,
                needs_review, review_reason, created_at, updated_at, voided_at, active_key)
            VALUES(
                $identifier, NULL, NULL, '性能ログ', '', $startAt, $endAt, 'manual', 'テスト',
                NULL, NULL, 0, '', $createdAt, $updatedAt, NULL, 1);
            """;
        Microsoft.Data.Sqlite.SqliteParameter identifierParameter = command.Parameters.Add("$identifier", Microsoft.Data.Sqlite.SqliteType.Text);
        Microsoft.Data.Sqlite.SqliteParameter startParameter = command.Parameters.Add("$startAt", Microsoft.Data.Sqlite.SqliteType.Text);
        Microsoft.Data.Sqlite.SqliteParameter endParameter = command.Parameters.Add("$endAt", Microsoft.Data.Sqlite.SqliteType.Text);
        Microsoft.Data.Sqlite.SqliteParameter createdParameter = command.Parameters.Add("$createdAt", Microsoft.Data.Sqlite.SqliteType.Text);
        Microsoft.Data.Sqlite.SqliteParameter updatedParameter = command.Parameters.Add("$updatedAt", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Prepare();
        DateTimeOffset monthStart = new(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(9));
        for (int entryIndex = 0; entryIndex < entryCount; entryIndex += 1)
        {
            // 15分区間を月内へ循環配置し、全件が月次集計へ含まれるようにする。
            DateTimeOffset startAt = monthStart.AddMinutes((entryIndex % (31 * 24 * 4)) * 15);
            string formattedStart = startAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            string formattedEnd = startAt.AddMinutes(15).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            identifierParameter.Value = $"PERFORMANCE-TIME-{entryIndex}";
            startParameter.Value = formattedStart;
            endParameter.Value = formattedEnd;
            createdParameter.Value = formattedStart;
            updatedParameter.Value = formattedEnd;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    /// <summary>5000件の推薦計算時間を検証する。</summary>
    private static Task TestPerformanceAsync()
    {
        // データベースI/Oを除く優先度計算が300ミリ秒以内で完了することを確認する。
        DateTimeOffset currentTime = StandardTime();
        List<ManagedTask> tasks = Enumerable.Range(1, 5000)
            .Select(taskNumber => CreateTask($"PERF-{taskNumber}", $"性能テスト{taskNumber}", taskNumber % 5 + 1))
            .ToList();
        Stopwatch stopwatch = Stopwatch.StartNew();
        RecommendationResult result = CreatePureRecommendationService(currentTime).SelectRecommendation(
            currentTime,
            new TaskManagerSettings(),
            tasks,
            []);
        stopwatch.Stop();
        Assert(result.Recommendation is not null, "推薦が生成されませんでした。");
        Assert(stopwatch.ElapsedMilliseconds < 300, $"計算に{stopwatch.ElapsedMilliseconds}ミリ秒かかりました。");
        return Task.CompletedTask;
    }

    /// <summary>5000件の読込、計算、計算結果保存を含む時間を検証する。</summary>
    private static async Task TestPersistentPerformanceAsync()
    {
        // 事前データ作成を計測から除外し通常の再計算経路だけを300ミリ秒以内で確認する。
        await using TestDatabase testDatabase = await TestDatabase.CreateAsync();
        List<ManagedTask> tasks = Enumerable.Range(1, 5000)
            .Select(taskNumber => CreateTask($"DB-PERF-{taskNumber}", $"保存性能テスト{taskNumber}", taskNumber % 5 + 1))
            .ToList();
        foreach (ManagedTask task in tasks)
        {
            // 計測前の準備として通常の保存経路で性能確認用タスクを登録する。
            await testDatabase.Repository.SaveTaskAsync(task, "性能確認データ作成", TaskConstants.SystemSource);
        }
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            new FixedTimeProvider(StandardTime()));
        Stopwatch stopwatch = Stopwatch.StartNew();
        RecommendationResult result = await recommendationService.RefreshAsync();
        stopwatch.Stop();
        Assert(result.Recommendation is not null, "SQLite経由の推薦が生成されませんでした。");
        Assert(stopwatch.ElapsedMilliseconds < 300, $"SQLite込み再計算に{stopwatch.ElapsedMilliseconds}ミリ秒かかりました。");
    }

    /// <summary>テスト用のタスクサービスと依存サービスを作成する。</summary>
    private static TaskService CreateTaskServiceForTest(
        TestDatabase testDatabase,
        DateTimeOffset currentTime,
        IDraftPostProcessingService? draftPostProcessingOverride = null)
    {
        // 同じ一時DBと固定時刻を使い、必要なテストだけ下書き後処理を差し替える。
        FixedTimeProvider timeProvider = new(currentTime);
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            timeProvider);
        ProjectService projectService = new(
            testDatabase.ProjectRepository,
            testDatabase.Repository,
            timeProvider);
        TaskValidationService validationService = new();
        IDraftPostProcessingService draftPostProcessingService = draftPostProcessingOverride
            ?? new DraftPostProcessingService(
                testDatabase.Repository,
                validationService,
                recommendationService);
        return new TaskService(
            testDatabase.Repository,
            recommendationService,
            validationService,
            projectService,
            draftPostProcessingService,
            testDatabase.TimeEntryRepository,
            timeProvider);
    }

    /// <summary>可変時刻を共有する作業時間テスト用タスクサービスを作成する。</summary>
    private static TaskService CreateTaskServiceForTimeTest(
        TestDatabase testDatabase,
        TimeProvider timeProvider)
    {
        // 推薦、プロジェクト、検証、作業ログへ同じDBと時刻供給元を渡す。
        RecommendationService recommendationService = new(
            testDatabase.Repository,
            new CalendarAvailabilityService(),
            timeProvider);
        ProjectService projectService = new(
            testDatabase.ProjectRepository,
            testDatabase.Repository,
            timeProvider);
        TaskValidationService validationService = new();
        DraftPostProcessingService draftPostProcessingService = new(
            testDatabase.Repository,
            validationService,
            recommendationService);
        return new TaskService(
            testDatabase.Repository,
            recommendationService,
            validationService,
            projectService,
            draftPostProcessingService,
            testDatabase.TimeEntryRepository,
            timeProvider);
    }

    /// <summary>純粋計算テスト用の推薦サービスを作成する。</summary>
    private static RecommendationService CreatePureRecommendationService(DateTimeOffset currentTime)
    {
        // 未使用の保存層へ一時パスを渡し固定時刻を設定する。
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"TaskManager-Pure-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", temporaryDirectory);
        TaskManagerPaths paths = new();
        DatabaseInitializer initializer = new(paths);
        TaskRepository repository = new(initializer);
        return new RecommendationService(repository, new CalendarAvailabilityService(), new FixedTimeProvider(currentTime));
    }

    /// <summary>テスト用のプロジェクトサービスを作成する。</summary>
    private static ProjectService CreateProjectService(TestDatabase testDatabase, DateTimeOffset currentTime)
    {
        // 同じ一時DBと固定時刻をプロジェクト処理へ渡す。
        return new ProjectService(
            testDatabase.ProjectRepository,
            testDatabase.Repository,
            new FixedTimeProvider(currentTime));
    }

    /// <summary>テスト用の標準日時を返す。</summary>
    private static DateTimeOffset StandardTime()
    {
        // 活動時間内の平日午前を固定値として利用する。
        return new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.FromHours(9));
    }

    /// <summary>テスト用タスクを既定値付きで作成する。</summary>
    private static ManagedTask CreateTask(string identifier, string title, int importance = 3)
    {
        // 推薦対象になる最小限の実行可能タスクを返す。
        DateTimeOffset currentTime = StandardTime();
        return new ManagedTask
        {
            Identifier = identifier,
            Title = title,
            Status = TaskConstants.ReadyStatus,
            EstimatedMinutes = 30,
            RemainingMinutes = 30,
            Importance = importance,
            RequiredContext = "PC",
            CreatedAt = currentTime,
            UpdatedAt = currentTime
        };
    }

    /// <summary>条件が偽の場合にテストを失敗させる。</summary>
    private static void Assert(bool condition, string errorMessage)
    {
        // 独立した例外型で失敗理由を呼出元へ返す。
        if (!condition)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    /// <summary>CLIテストへ固定JSONを返すHTTPハンドラー。</summary>
    private sealed class DraftResponseMessageHandler(string draftResponseJson) : HttpMessageHandler
    {
        // draft-createへ返す固定応答本文を保持する。
        private readonly string responseJson = draftResponseJson;

        /// <summary>ヘルス確認と下書き作成へ成功応答を返す。</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // ヘルスAPIと下書きAPIで必要なJSON本文を切り替える。
            string responseContent = request.RequestUri?.AbsolutePath.EndsWith(
                "/health",
                StringComparison.Ordinal) == true
                ? """{"status":"ok"}"""
                : responseJson;
            HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseContent,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>下書き保存後処理で意図的な例外を返すテスト用実装。</summary>
    private sealed class FailingDraftPostProcessingService : IDraftPostProcessingService
    {
        /// <summary>保存後処理の失敗を再現する。</summary>
        public Task ProcessAsync(
            string batchIdentifier,
            CancellationToken cancellationToken = default)
        {
            // コミット後エラーの警告変換を検証するため固定例外を返す。
            throw new InvalidOperationException($"後処理テスト失敗: {batchIdentifier}");
        }
    }

    /// <summary>Codexプロセスへ渡された引数と標準入力を記録する。</summary>
    private sealed class CapturingCodexProcessExecutor : ICodexProcessExecutor
    {
        // 最後に受け取ったプロセス設定と標準入力を保持する。
        public ProcessStartInfo? StartInformation { get; private set; }
        public string StandardInput { get; private set; } = string.Empty;

        /// <summary>プロセスを起動せず引数と標準入力を記録して成功を返す。</summary>
        public Task<CodexCommandResult> ExecuteAsync(
            ProcessStartInfo startInformation,
            string standardInput,
            CancellationToken cancellationToken)
        {
            // テスト対象の値をそのまま保存する。
            StartInformation = startInformation;
            StandardInput = standardInput;
            return Task.FromResult(new CodexCommandResult { IsSuccess = true, ExitCode = 0 });
        }
    }

    /// <summary>固定したCodex CLI結果を返すテスト用ランナー。</summary>
    private sealed class FixedCodexCommandRunner(CodexCommandResult commandResult) : ICodexCommandRunner
    {
        // すべての送信で返す固定結果を保持する。
        private readonly CodexCommandResult result = commandResult;

        /// <summary>外部プロセスを起動せず固定結果を返す。</summary>
        public Task<CodexCommandResult> SendAsync(
            CodexSubmission submission,
            CancellationToken cancellationToken)
        {
            // 失敗時のワーカー動作だけを分離して検証する。
            return Task.FromResult(result);
        }
    }

    /// <summary>音声入力アプリの状態と操作回数を記録するテスト用実行環境。</summary>
    private sealed class RecordingVoiceInputRuntime : IVoiceInputRuntime
    {
        // 偽の準備状態と各起動操作の回数を保持する。
        public bool OllamaReady { get; set; }
        public bool OllamaRunning { get; set; }
        public bool TypeWhisperRunning { get; set; }
        public bool TypeWhisperReady { get; set; } = true;
        public bool TypeWhisperRecording { get; set; }
        public int OllamaStartCount { get; private set; }
        public int TypeWhisperStartCount { get; private set; }
        public int ModelLoadCount { get; private set; }
        public int RecognitionStartCount { get; private set; }
        public int RecognitionStopCount { get; private set; }
        public List<string> Operations { get; } = [];
        // モデルロードと録音停止をテスト側の任意時点まで保留する合図を保持する。
        public TaskCompletionSource? ModelLoadCompletion { get; init; }
        public TaskCompletionSource? RecognitionStopCompletion { get; init; }
        // 録音停止時に返す任意の障害を保持する。
        public Exception? RecognitionStopError { get; init; }

        /// <summary>設定されたOllama API準備状態を返す。</summary>
        public Task<bool> IsOllamaReadyAsync(CancellationToken cancellationToken)
        {
            // ネットワーク通信をせず保持中の状態を即時返す。
            return Task.FromResult(OllamaReady);
        }

        /// <summary>設定されたOllamaプロセス状態を返す。</summary>
        public bool IsOllamaRunning()
        {
            // テストケースが指定した起動状態を返す。
            return OllamaRunning;
        }

        /// <summary>Ollama起動を記録して準備完了状態へ進める。</summary>
        public void StartOllama()
        {
            // 実プロセスの代わりに回数と状態だけを更新する。
            OllamaStartCount += 1;
            OllamaRunning = true;
            OllamaReady = true;
            Operations.Add("start-ollama");
        }

        /// <summary>Ollama校正モデルの事前ロード回数を記録する。</summary>
        public Task LoadOllamaModelAsync(CancellationToken cancellationToken)
        {
            // 実モデルを読み込まず呼出し回数と操作順序だけを保存する。
            ModelLoadCount += 1;
            Operations.Add("load-model");
            if (ModelLoadCompletion is null)
            {
                Operations.Add("load-model-complete");
                return Task.CompletedTask;
            }
            return CompleteModelLoadAsync(ModelLoadCompletion.Task);
        }

        /// <summary>テスト側の合図後にOllamaモデルロード完了を記録する。</summary>
        private async Task CompleteModelLoadAsync(Task completionTask)
        {
            // 録音開始よりモデル完了を遅らせ、非同期順序を明示的に制御する。
            await completionTask;
            Operations.Add("load-model-complete");
        }

        /// <summary>設定されたTypeWhisperプロセス状態を返す。</summary>
        public bool IsTypeWhisperRunning()
        {
            // テストケースが指定した起動状態を返す。
            return TypeWhisperRunning;
        }

        /// <summary>TypeWhisper起動を記録してプロセス稼働状態へ進める。</summary>
        public void StartTypeWhisper()
        {
            // 実プロセスの代わりに回数と準備状態だけを更新する。
            TypeWhisperStartCount += 1;
            TypeWhisperRunning = true;
            TypeWhisperReady = true;
            Operations.Add("start-typewhisper");
        }

        /// <summary>設定されたTypeWhisper APIとモデルの準備状態を返す。</summary>
        public Task<bool> IsTypeWhisperReadyAsync(CancellationToken cancellationToken)
        {
            // 外部APIへ接続せずテストケースの準備状態を返す。
            return Task.FromResult(TypeWhisperReady);
        }

        /// <summary>TypeWhisperの音声校正録音開始を記録する。</summary>
        public Task StartTypeWhisperRecognitionAsync(CancellationToken cancellationToken)
        {
            // 実APIを呼ばず開始回数と録音状態だけを更新する。
            RecognitionStartCount += 1;
            TypeWhisperRecording = true;
            Operations.Add("start-recognition");
            return Task.CompletedTask;
        }

        /// <summary>設定されたTypeWhisper録音状態を返す。</summary>
        public Task<bool> IsTypeWhisperRecordingAsync(CancellationToken cancellationToken)
        {
            // 外部APIへ接続せずテストケースの録音状態を返す。
            return Task.FromResult(TypeWhisperRecording);
        }

        /// <summary>TypeWhisperの録音停止を記録して任意の合図後に完了する。</summary>
        public async Task StopTypeWhisperRecognitionAsync(CancellationToken cancellationToken)
        {
            // 停止APIの成功、待機、失敗をテストケースの指定どおりに再現する。
            RecognitionStopCount += 1;
            Operations.Add("stop-recognition");
            if (RecognitionStopError is not null)
            {
                throw RecognitionStopError;
            }
            if (RecognitionStopCompletion is not null)
            {
                await RecognitionStopCompletion.Task.WaitAsync(cancellationToken);
            }
            TypeWhisperRecording = false;
        }
    }

    /// <summary>TypeWhisper API要求を記録し、録音状態に応じた固定応答を返すHTTPハンドラー。</summary>
    private sealed class RecordingTypeWhisperMessageHandler : HttpMessageHandler
    {
        // API内で保持する録音状態と最後の開始要求本文を保持する。
        private bool recording;
        public string StartRequestBody { get; private set; } = string.Empty;

        /// <summary>TypeWhisperの主要な制御APIへテスト用JSON応答を返す。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // パスとメソッドごとに実TypeWhisper 1.0.8と同じ応答形式を返す。
            string requestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (requestPath == "/v1/status" && request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"status\":\"no_model\",\"active_model\":null}");
            }
            if (requestPath == "/v1/models" && request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"models\":[{\"id\":\"large-v3-turbo\",\"full_id\":\"plugin:com.typewhisper.whisper-cpp:large-v3-turbo\",\"status\":\"ready\",\"selected\":true,\"downloaded\":true,\"loaded\":false}]}");
            }
            if (requestPath == "/v1/rules" && request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"rules\":[{\"id\":\"f3e169bf-39f6-4563-b85c-70e51f3d9a5a\",\"name\":\"音声校正\",\"is_enabled\":true}],\"count\":1}");
            }
            if (requestPath == "/v1/dictation/start" && request.Method == HttpMethod.Post)
            {
                StartRequestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                recording = true;
                return JsonResponse("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"status\":\"recording\",\"workflow_id\":\"f3e169bf-39f6-4563-b85c-70e51f3d9a5a\",\"workflow_name\":\"音声校正\"}");
            }
            if (requestPath == "/v1/dictation/status" && request.Method == HttpMethod.Get)
            {
                return JsonResponse($"{{\"state\":\"{(recording ? "recording" : "idle")}\",\"is_recording\":{recording.ToString().ToLowerInvariant()}}}");
            }
            if (requestPath == "/v1/dictation/stop" && request.Method == HttpMethod.Post)
            {
                recording = false;
                return JsonResponse("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"status\":\"stopped\"}");
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Not found\"}}")
            };
        }

        /// <summary>UTF-8 JSONの成功応答を生成する。</summary>
        private static HttpResponseMessage JsonResponse(string responseBody)
        {
            // 各テスト応答へTypeWhisperと同じJSONコンテンツ型を付ける。
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Ollama事前ロード要求を記録して成功を返すテスト用HTTPハンドラー。</summary>
    private sealed class RecordingOllamaMessageHandler : HttpMessageHandler
    {
        // 最後に受け取ったAPIパスとJSON本文を保持する。
        public string RequestPath { get; private set; } = string.Empty;
        public string RequestBody { get; private set; } = string.Empty;

        /// <summary>HTTP要求の内容を保存して成功応答を返す。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // 外部通信をせず、モデル事前ロードの要求だけを検証用に記録する。
            RequestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    /// <summary>Windows通知の内容をメモリへ記録するテスト用通知先。</summary>
    private sealed class RecordingNotificationService : IUserNotificationService
    {
        // 通知タイトル・本文と詳細画面の内容を受付順に保持する。
        public List<(string Title, string Message)> Messages { get; } = [];
        public List<(string Message, bool IsError)> Details { get; } = [];
        public int ProcessingCount { get; private set; }

        /// <summary>指定された通知をテスト用一覧へ追加する。</summary>
        public void Notify(string title, string message)
        {
            // 実際のトレイ通知を出さず内容だけを記録する。
            Messages.Add((title, message));
        }

        /// <summary>Codex CLIの応答待ち画面表示回数を記録する。</summary>
        public void ShowCodexProcessing()
        {
            // 実画面を出さず処理中表示の呼出し回数だけを増やす。
            ProcessingCount += 1;
        }

        /// <summary>Codex CLIの詳細表示内容をテスト用一覧へ追加する。</summary>
        public void ShowCodexResult(string message, bool isError)
        {
            // 実画面を出さず応答本文とエラー区分だけを記録する。
            Details.Add((message, isError));
        }
    }

    /// <summary>テスト用の固定時刻を提供する。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        // すべての呼出しで返す固定日時を保持する。
        private readonly DateTimeOffset fixedTime = currentTime;

        /// <summary>固定UTC時刻を返す。</summary>
        public override DateTimeOffset GetUtcNow()
        {
            // ローカル時刻へ変換できるUTC表現を返す。
            return fixedTime.ToUniversalTime();
        }

        /// <summary>固定日時のローカルタイムゾーンを返す。</summary>
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    }

    /// <summary>テスト途中で現在時刻を進められる時刻供給元を提供する。</summary>
    private sealed class AdjustableTimeProvider(DateTimeOffset currentTime) : TimeProvider
    {
        // 現在として返す変更可能な日時を保持する。
        private DateTimeOffset adjustableTime = currentTime;

        /// <summary>以後の呼出しで返す現在時刻を変更する。</summary>
        public void SetCurrentTime(DateTimeOffset currentTime)
        {
            // 入力オフセットを保った日時を内部状態へ保存する。
            adjustableTime = currentTime;
        }

        /// <summary>設定済み時刻のUTC表現を返す。</summary>
        public override DateTimeOffset GetUtcNow()
        {
            // TimeProviderの契約に従いUTCへ変換して返す。
            return adjustableTime.ToUniversalTime();
        }

        /// <summary>テストで使用する東京タイムゾーンを返す。</summary>
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    }

    /// <summary>テストごとに独立したSQLite環境を管理する。</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        // 一時保存先とデータアクセスを保持する。
        public string DirectoryPath { get; }
        public TaskManagerPaths Paths { get; }
        public DatabaseInitializer Initializer { get; }
        public TaskRepository Repository { get; }
        public ProjectRepository ProjectRepository { get; }
        public TimeEntryRepository TimeEntryRepository { get; }

        /// <summary>テスト用保存先とサービスを構築する。</summary>
        private TestDatabase(
            string directoryPath,
            TaskManagerPaths paths,
            DatabaseInitializer initializer,
            TaskRepository repository,
            ProjectRepository projectRepository,
            TimeEntryRepository timeEntryRepository)
        {
            // 生成済み依存先をプロパティへ保持する。
            DirectoryPath = directoryPath;
            Paths = paths;
            Initializer = initializer;
            Repository = repository;
            ProjectRepository = projectRepository;
            TimeEntryRepository = timeEntryRepository;
        }

        /// <summary>初期化済みのテスト用データベースを作成する。</summary>
        public static async Task<TestDatabase> CreateAsync()
        {
            // 一意な一時ディレクトリを環境変数経由で指定する。
            string directoryPath = Path.Combine(Path.GetTempPath(), $"TaskManager-Test-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", directoryPath);
            TaskManagerPaths paths = new();
            DatabaseInitializer initializer = new(paths);
            await initializer.InitializeAsync();
            TaskRepository repository = new(initializer);
            ProjectRepository projectRepository = new(initializer);
            TimeEntryRepository timeEntryRepository = new(initializer);
            return new TestDatabase(
                directoryPath,
                paths,
                initializer,
                repository,
                projectRepository,
                timeEntryRepository);
        }

        /// <summary>テスト終了時に一時データを削除する。</summary>
        public ValueTask DisposeAsync()
        {
            // 接続プールを消去して一時SQLiteファイルを削除可能にする。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
