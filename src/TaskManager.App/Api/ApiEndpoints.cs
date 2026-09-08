using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Services;

namespace TaskManager.Api;

/// <summary>ローカル画面とCLIで共有するHTTP APIを定義する。</summary>
public static class ApiEndpoints
{
    // SSEのJSONを通常APIと同じcamelCaseで出力する設定を保持する。
    private static readonly JsonSerializerOptions UiChangeJsonOptions = new(JsonSerializerDefaults.Web);
    // 接続維持コメントを送信する間隔を保持する。
    private static readonly TimeSpan UiChangeHeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>すべてのバージョン1 APIをアプリへ登録する。</summary>
    public static void MapTaskManagerApi(this WebApplication application)
    {
        // 読取、更新、カレンダー、保全の経路を機能別に登録する。
        RouteGroupBuilder api = application.MapGroup("/api/v1");
        api.MapGet("/health", GetHealth);
        api.MapGet("/changes", StreamUiChangesAsync);
        api.MapGet("/system-incidents/pending", GetPendingSystemIncidentAsync);
        api.MapPost("/system-incidents/{identifier}/acknowledge", AcknowledgeSystemIncidentAsync);
        api.MapPost("/codex/task-thread/open", OpenTaskManagementThread);
        api.MapPost("/codex/reviews", CreateCodexReview);
        api.MapGet("/dashboard", GetDashboardAsync);
        api.MapGet("/tasks", GetTasksAsync);
        api.MapGet("/tasks/{identifier}", GetTaskAsync);
        api.MapPost("/tasks", AddTaskAsync);
        api.MapPut("/tasks/{identifier}", UpdateTaskAsync);
        api.MapDelete("/tasks/{identifier}", DeleteTaskAsync);
        api.MapPost("/tasks/{identifier}/actions/{actionName}", ExecuteActionAsync);
        api.MapGet("/time-entries", GetTimeEntriesAsync);
        api.MapGet("/time-entries/active", GetActiveTimeEntryAsync);
        api.MapPost("/time-entries/start", StartTimeEntryAsync);
        api.MapPost("/time-entries/{identifier}/stop", StopTimeEntryAsync);
        api.MapPost("/time-entries/{identifier}/extend", ExtendTimeEntryAsync);
        api.MapPut("/time-entries/{identifier}/start", UpdateActiveTimeEntryStartAsync);
        api.MapPost("/time-entries", AddTimeEntryAsync);
        api.MapPut("/time-entries/{identifier}", UpdateTimeEntryAsync);
        api.MapPost("/time-entries/{identifier}/confirm", ConfirmTimeEntryAsync);
        api.MapPost("/time-entries/{identifier}/void", VoidTimeEntryAsync);
        api.MapGet("/time-reports", GetTimeReportAsync);
        api.MapGet("/projects", GetProjectsAsync);
        api.MapGet("/projects/resolve", ResolveProjectAsync);
        api.MapGet("/projects/prepare", PrepareProjectAsync);
        api.MapGet("/projects/{identifier}", GetProjectAsync);
        api.MapPost("/projects", AddProjectAsync);
        api.MapPut("/projects/{identifier}", UpdateProjectAsync);
        api.MapPost("/projects/{identifier}/archive", ArchiveProjectAsync);
        api.MapPost("/projects/{identifier}/aliases", AddProjectAliasAsync);
        api.MapDelete("/projects/{identifier}/aliases/{aliasIdentifier:long}", RemoveProjectAliasAsync);
        api.MapPost("/projects/{identifier}/contexts", AddProjectContextAsync);
        api.MapPut("/projects/{identifier}/contexts/{contextIdentifier}", UpdateProjectContextAsync);
        api.MapPut("/projects/{identifier}/deadline-rule", SaveProjectDeadlineRuleAsync);
        api.MapGet("/settings", GetSettingsAsync);
        api.MapPut("/settings", SaveSettingsAsync);
        api.MapGet("/history", GetHistoryAsync);
        api.MapGet("/draft-batches", GetDraftBatchesAsync);
        api.MapGet("/draft-batches/{identifier}", GetDraftBatchAsync);
        api.MapPost("/draft-batches", CreateDraftBatchAsync);
        api.MapPost("/draft-batches/{identifier}/approve", ApproveDraftBatchAsync);
        api.MapPost("/calendar/credentials", SaveCalendarCredentialsAsync).DisableAntiforgery();
        api.MapPost("/calendar/connect", ConnectCalendarAsync);
        api.MapPost("/calendar/synchronize", SynchronizeCalendarAsync);
        api.MapPost("/backup", CreateBackupAsync);
    }

    /// <summary>プロセスが応答可能であることを返す。</summary>
    private static IResult GetHealth()
    {
        // CLIの起動確認に必要な最小情報だけを返す。
        return Results.Ok(new { status = "ok", version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() });
    }

    /// <summary>データ変更をServer-Sent Eventsで接続中の画面へ配信する。</summary>
    private static async Task StreamUiChangesAsync(
        HttpContext context,
        UiChangeNotifier changeNotifier,
        CancellationToken cancellationToken)
    {
        // 再接続可能な一方向ストリームとしてキャッシュと中間バッファを無効化する。
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        long observedVersion = changeNotifier.CurrentVersion;
        await WriteUiChangeEventAsync(
            context.Response,
            "ready",
            new { version = observedVersion },
            cancellationToken);

        // 変更時は直ちに配信し、無変更時も定期コメントで接続を維持する。
        while (!cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource heartbeatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeatCancellation.CancelAfter(UiChangeHeartbeatInterval);
            try
            {
                UiChangeNotification notification = await changeNotifier.WaitForChangeAsync(
                    observedVersion,
                    heartbeatCancellation.Token);
                observedVersion = notification.Version;
                await WriteUiChangeEventAsync(
                    context.Response,
                    "change",
                    notification,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>SSEイベントを1件JSON形式で書き込み直ちに送信する。</summary>
    private static async Task WriteUiChangeEventAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        // イベント名と1行JSONをSSEの空行区切りで出力する。
        string serializedPayload = JsonSerializer.Serialize(payload, UiChangeJsonOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {serializedPayload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>ユーザーがまだ確認していない最新の異常終了情報を返す。</summary>
    private static async Task<IResult> GetPendingSystemIncidentAsync(
        SystemIncidentService incidentService,
        CancellationToken cancellationToken)
    {
        // 異常終了がない場合または確認済みの場合はnullを返す。
        SystemIncidentRecord? incident = await incidentService.GetPendingAsync(cancellationToken);
        return Results.Ok(incident);
    }

    /// <summary>ダッシュボードに表示した異常終了情報を確認済みにする。</summary>
    private static async Task<IResult> AcknowledgeSystemIncidentAsync(
        string identifier,
        SystemIncidentService incidentService,
        CancellationToken cancellationToken)
    {
        // 指定IDの最新情報だけを確認済みに更新する。
        SystemIncidentRecord incident = await incidentService.AcknowledgeAsync(identifier, cancellationToken);
        return Results.Ok(incident);
    }

    /// <summary>Codexのタスク管理スレッドをデスクトップアプリで開く。</summary>
    private static async Task<IResult> OpenTaskManagementThread(
        TaskRepository repository,
        CodexDesktopLauncher desktopLauncher,
        CancellationToken cancellationToken)
    {
        // 保存済みUUIDから安全なディープリンクを作り、OSの関連付けだけでCodexへ渡す。
        TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
        string threadLink = CodexThreadIdentifier.CreateDeepLink(settings.CodexThreadIdentifier);
        CodexDesktopLaunchResult launchResult = desktopLauncher.OpenThread(threadLink);
        return launchResult.Opened
            ? Results.Ok(launchResult)
            : Results.Json(
                new { error = launchResult.ErrorMessage },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>TypeWhisperの校正済み本文を確認待ちへ追加する。</summary>
    private static IResult CreateCodexReview(
        CodexReviewRequest reviewRequest,
        CodexReviewService reviewService,
        IUserNotificationService userNotificationService,
        VoiceInputCoordinator voiceInputCoordinator)
    {
        // ネイティブ確認画面を表示できない状態では本文を受け取らず送信元へ返す。
        if (!userNotificationService.CanShowCodexReview)
        {
            return Results.Json(
                new { error = $"{TaskConstants.ApplicationDisplayName}の確認画面を表示できません。" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        voiceInputCoordinator.CompleteRecognition();
        CodexReviewReceipt receipt = reviewService.Enqueue(reviewRequest.Text);
        return Results.Accepted(value: receipt);
    }

    /// <summary>現在の推薦と確認待ちを返す。</summary>
    private static async Task<IResult> GetDashboardAsync(
        RecommendationService recommendationService,
        TimeEntryRepository timeEntryRepository,
        [FromQuery] string? category,
        [FromQuery] string? projectIdentifier,
        [FromQuery] bool? forceRecommendation,
        CancellationToken cancellationToken)
    {
        // 区分とプロジェクトは排他的な表示条件として受け付ける。
        if (!string.IsNullOrWhiteSpace(category)
            && category is not TaskConstants.WorkCategory and not TaskConstants.PrivateCategory)
        {
            return Results.BadRequest(new { error = "区分は仕事または私用を指定してください。" });
        }
        if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(projectIdentifier))
        {
            return Results.BadRequest(new { error = "区分とプロジェクトは同時に指定できません。" });
        }

        // 全件の推薦再計算後に表示範囲を適用し、保存済み優先度へ影響させない。
        RecommendationResult result = await recommendationService.RefreshAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(category)
            || !string.IsNullOrWhiteSpace(projectIdentifier)
            || forceRecommendation == true)
        {
            result = RecommendationService.FilterRecommendation(
                result,
                category,
                projectIdentifier,
                forceRecommendation == true);
        }
        result.ActiveTimeEntry = await timeEntryRepository.GetActiveAsync(cancellationToken);
        result.ReviewTimeEntries = await timeEntryRepository.GetNeedsReviewAsync(cancellationToken);
        result.CalendarWidget.TimeEntries = await timeEntryRepository.GetEntriesAsync(
            result.CalendarWidget.RangeStart,
            result.CalendarWidget.RangeEnd,
            cancellationToken: cancellationToken);
        return Results.Ok(result);
    }

    /// <summary>条件に一致するタスク一覧を返す。</summary>
    private static async Task<IResult> GetTasksAsync(
        TaskRepository repository,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] string? projectIdentifier,
        CancellationToken cancellationToken)
    {
        // 状態と検索文字列を任意に適用する。
        IEnumerable<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(status))
        {
            tasks = tasks.Where(task => string.Equals(task.Status, status, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            tasks = tasks.Where(task =>
                task.Identifier.Contains(search, StringComparison.OrdinalIgnoreCase)
                || task.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || task.Details.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(projectIdentifier))
        {
            tasks = tasks.Where(task =>
                string.Equals(task.ProjectIdentifier, projectIdentifier, StringComparison.OrdinalIgnoreCase));
        }
        return Results.Ok(tasks);
    }

    /// <summary>指定IDのタスクを返す。</summary>
    private static async Task<IResult> GetTaskAsync(
        string identifier,
        TaskRepository repository,
        CancellationToken cancellationToken)
    {
        // 見つからない場合は404を返す。
        ManagedTask? task = await repository.GetTaskAsync(identifier, cancellationToken);
        return task is null ? Results.NotFound(new { error = "タスクが見つかりません。" }) : Results.Ok(task);
    }

    /// <summary>画面またはCLIから新規タスクを登録する。</summary>
    private static async Task<IResult> AddTaskAsync(
        ManagedTask task,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // ヘッダーから操作元を判定して履歴へ残す。
        ManagedTask savedTask = await taskService.AddTaskAsync(task, GetSource(request), cancellationToken);
        return Results.Created($"/api/v1/tasks/{Uri.EscapeDataString(savedTask.Identifier)}", savedTask);
    }

    /// <summary>指定IDのタスクを更新する。</summary>
    private static async Task<IResult> UpdateTaskAsync(
        string identifier,
        ManagedTask task,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // ID変更を禁止した更新処理へ渡す。
        ManagedTask savedTask = await taskService.UpdateTaskAsync(identifier, task, GetSource(request), cancellationToken);
        return Results.Ok(savedTask);
    }

    /// <summary>タスクIDの明示確認を検証して完全削除する。</summary>
    private static async Task<IResult> DeleteTaskAsync(
        string identifier,
        [FromQuery] string confirmationIdentifier,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 通常の中止と区別し、IDが一致する場合だけ削除処理へ渡す。
        await taskService.DeleteTaskAsync(
            identifier,
            confirmationIdentifier,
            GetSource(request),
            cancellationToken);
        return Results.NoContent();
    }

    /// <summary>指定タスクへ状態操作を適用する。</summary>
    private static async Task<IResult> ExecuteActionAsync(
        string identifier,
        string actionName,
        TaskActionRequest actionRequest,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 延期分数を任意指定できる共通操作として実行する。
        RecommendationResult result = await taskService.ExecuteActionAsync(
            actionName,
            identifier,
            actionRequest.PostponeMinutes,
            GetSource(request),
            cancellationToken);
        return Results.Ok(result);
    }

    /// <summary>指定条件に一致する作業ログ一覧を返す。</summary>
    private static async Task<IResult> GetTimeEntriesAsync(
        TimeEntryRepository timeEntryRepository,
        [FromQuery] DateTimeOffset? rangeStart,
        [FromQuery] DateTimeOffset? rangeEnd,
        [FromQuery] string? projectIdentifier,
        [FromQuery] string? taskIdentifier,
        [FromQuery] bool? includeVoided,
        CancellationToken cancellationToken)
    {
        // 期間・プロジェクト・タスク・無効化済み条件を保存層へ渡す。
        return Results.Ok(await timeEntryRepository.GetEntriesAsync(
            rangeStart,
            rangeEnd,
            projectIdentifier,
            taskIdentifier,
            includeVoided.GetValueOrDefault(),
            cancellationToken));
    }

    /// <summary>現在実行中の作業ログを返す。</summary>
    private static async Task<IResult> GetActiveTimeEntryAsync(
        TimeEntryRepository timeEntryRepository,
        CancellationToken cancellationToken)
    {
        // タイマーがない場合も200とNULLで返して定期取得を簡単にする。
        return Results.Ok(await timeEntryRepository.GetActiveAsync(cancellationToken));
    }

    /// <summary>タスク未登録の自由活動タイマーを開始する。</summary>
    private static async Task<IResult> StartTimeEntryAsync(
        TimeEntryStartRequest startRequest,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 実行中タスクの自動中断を含むサービス処理へ渡す。
        TimeEntryRecord timeEntry = await taskService.StartFreeActivityAsync(
            startRequest,
            GetSource(request),
            cancellationToken);
        return Results.Created(
            $"/api/v1/time-entries/{Uri.EscapeDataString(timeEntry.Identifier)}",
            timeEntry);
    }

    /// <summary>指定した実行中作業ログを停止する。</summary>
    private static async Task<IResult> StopTimeEntryAsync(
        string identifier,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // タスク紐付きならタスク状態も実行可能へ戻す。
        return Results.Ok(await taskService.StopTimeEntryAsync(
            identifier,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>長時間警告中の作業ログを1時間延長する。</summary>
    private static async Task<IResult> ExtendTimeEntryAsync(
        string identifier,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 次の長時間警告を操作時刻の1時間後へ設定する。
        return Results.Ok(await taskService.ExtendTimeEntryAsync(
            identifier,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>実行中作業ログの開始時刻だけを修正する。</summary>
    private static async Task<IResult> UpdateActiveTimeEntryStartAsync(
        string identifier,
        ActiveTimeEntryStartUpdateRequest updateRequest,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 実行状態を維持したまま関連する確認・警告予定も同じ差分だけ移動する。
        return Results.Ok(await taskService.UpdateActiveTimeEntryStartAsync(
            identifier,
            updateRequest,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>完了済み作業ログを手入力で追加する。</summary>
    private static async Task<IResult> AddTimeEntryAsync(
        TimeEntryMutationRequest mutationRequest,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 重複未確認時はミドルウェアから409を返す。
        TimeEntryRecord timeEntry = await taskService.AddManualTimeEntryAsync(
            mutationRequest,
            GetSource(request),
            cancellationToken);
        return Results.Created(
            $"/api/v1/time-entries/{Uri.EscapeDataString(timeEntry.Identifier)}",
            timeEntry);
    }

    /// <summary>完了済み作業ログを更新する。</summary>
    private static async Task<IResult> UpdateTimeEntryAsync(
        string identifier,
        TimeEntryMutationRequest mutationRequest,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 対象IDを固定して重複確認付き更新を行う。
        return Results.Ok(await taskService.UpdateManualTimeEntryAsync(
            identifier,
            mutationRequest,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>要確認作業ログを現在内容のまま確定する。</summary>
    private static async Task<IResult> ConfirmTimeEntryAsync(
        string identifier,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 自動停止後の確認状態を解除する。
        return Results.Ok(await taskService.ConfirmTimeEntryAsync(
            identifier,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>作業ログを論理的に無効化する。</summary>
    private static async Task<IResult> VoidTimeEntryAsync(
        string identifier,
        HttpRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // SQLite行は保持したまま集計対象から除外する。
        return Results.Ok(await taskService.VoidTimeEntryAsync(
            identifier,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>日・週・月の作業時間集計を返す。</summary>
    private static async Task<IResult> GetTimeReportAsync(
        TimeReportService timeReportService,
        [FromQuery] string? period,
        [FromQuery] DateOnly? anchor,
        [FromQuery] string? projectIdentifier,
        CancellationToken cancellationToken)
    {
        // 未指定期間は日表示として集計する。
        return Results.Ok(await timeReportService.GetReportAsync(
            string.IsNullOrWhiteSpace(period) ? "day" : period,
            anchor,
            projectIdentifier,
            cancellationToken));
    }

    /// <summary>条件に一致するプロジェクト一覧を返す。</summary>
    private static async Task<IResult> GetProjectsAsync(
        ProjectService projectService,
        [FromQuery] bool? includeArchived,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        // アーカイブ指定と名称検索を任意に適用する。
        IEnumerable<ProjectRecord> projects = await projectService.GetProjectsAsync(
            includeArchived.GetValueOrDefault(),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            projects = projects.Where(project =>
                project.Identifier.Contains(search, StringComparison.OrdinalIgnoreCase)
                || project.CanonicalName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || project.Aliases.Any(alias =>
                    alias.AliasText.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }
        return Results.Ok(projects);
    }

    /// <summary>指定IDのプロジェクトを返す。</summary>
    private static async Task<IResult> GetProjectAsync(
        string identifier,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // アーカイブ済みを含めてIDで取得し、見つからなければ404を返す。
        ProjectRecord? project = await projectService.GetProjectAsync(identifier, cancellationToken);
        return project is null
            ? Results.NotFound(new { error = "プロジェクトが見つかりません。" })
            : Results.Ok(project);
    }

    /// <summary>表記ゆれを含む入力からプロジェクトを解決する。</summary>
    private static async Task<IResult> ResolveProjectAsync(
        [FromQuery] string reference,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // あいまいさも正常な解決結果として返す。
        return Results.Ok(await projectService.ResolveAsync(reference ?? string.Empty, cancellationToken));
    }

    /// <summary>Codex向けのプロジェクト背景情報を生成する。</summary>
    private static async Task<IResult> PrepareProjectAsync(
        [FromQuery] string reference,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 解決結果、次回期限、統合Markdownを1回で返す。
        return Results.Ok(await projectService.PrepareAsync(reference ?? string.Empty, cancellationToken));
    }

    /// <summary>画面またはCLIからプロジェクトを登録する。</summary>
    private static async Task<IResult> AddProjectAsync(
        ProjectRecord project,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 操作元を履歴へ記録して登録済み集約を返す。
        ProjectRecord savedProject = await projectService.AddProjectAsync(
            project,
            GetSource(request),
            cancellationToken);
        return Results.Created(
            $"/api/v1/projects/{Uri.EscapeDataString(savedProject.Identifier)}",
            savedProject);
    }

    /// <summary>指定IDのプロジェクト本体を更新する。</summary>
    private static async Task<IResult> UpdateProjectAsync(
        string identifier,
        ProjectRecord project,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 不変IDを検証するサービスへ更新を渡す。
        return Results.Ok(await projectService.UpdateProjectAsync(
            identifier,
            project,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>プロジェクトをアーカイブする。</summary>
    private static async Task<IResult> ArchiveProjectAsync(
        string identifier,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 関連タスクを残したまま新規候補から除外する。
        return Results.Ok(await projectService.ArchiveProjectAsync(
            identifier,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>ユーザー確認済みのプロジェクト別名を追加する。</summary>
    private static async Task<IResult> AddProjectAliasAsync(
        string identifier,
        ProjectAlias projectAlias,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // URL側のプロジェクトIDを正として別名本文だけを受け取る。
        ProjectAlias savedAlias = await projectService.AddAliasAsync(
            identifier,
            projectAlias.AliasText,
            GetSource(request),
            cancellationToken);
        return Results.Created(
            $"/api/v1/projects/{Uri.EscapeDataString(identifier)}/aliases/{savedAlias.Identifier}",
            savedAlias);
    }

    /// <summary>プロジェクト別名を解除する。</summary>
    private static async Task<IResult> RemoveProjectAliasAsync(
        string identifier,
        long aliasIdentifier,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 所属確認付きの解除処理を実行する。
        await projectService.RemoveAliasAsync(
            identifier,
            aliasIdentifier,
            GetSource(request),
            cancellationToken);
        return Results.NoContent();
    }

    /// <summary>プロジェクト背景情報を追加する。</summary>
    private static async Task<IResult> AddProjectContextAsync(
        string identifier,
        ProjectContextDocument contextDocument,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // 識別子をサービスで生成して新規背景情報として保存する。
        contextDocument.Identifier = string.Empty;
        ProjectContextDocument savedContext = await projectService.SaveContextDocumentAsync(
            identifier,
            contextDocument,
            GetSource(request),
            cancellationToken);
        return Results.Created(
            $"/api/v1/projects/{Uri.EscapeDataString(identifier)}/contexts/{savedContext.Identifier}",
            savedContext);
    }

    /// <summary>指定プロジェクト背景情報を更新する。</summary>
    private static async Task<IResult> UpdateProjectContextAsync(
        string identifier,
        string contextIdentifier,
        ProjectContextDocument contextDocument,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // URL側の背景情報IDを正として上書きする。
        contextDocument.Identifier = contextIdentifier;
        return Results.Ok(await projectService.SaveContextDocumentAsync(
            identifier,
            contextDocument,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>プロジェクトの毎週の既定期限を保存する。</summary>
    private static async Task<IResult> SaveProjectDeadlineRuleAsync(
        string identifier,
        ProjectDeadlineRule deadlineRule,
        HttpRequest request,
        ProjectService projectService,
        CancellationToken cancellationToken)
    {
        // URL側のプロジェクトIDを正として1件の期限規則を更新する。
        return Results.Ok(await projectService.SaveDeadlineRuleAsync(
            identifier,
            deadlineRule,
            GetSource(request),
            cancellationToken));
    }

    /// <summary>現在の設定を返す。</summary>
    private static async Task<IResult> GetSettingsAsync(TaskRepository repository, CancellationToken cancellationToken)
    {
        // 型付き設定をそのままJSONへ返す。
        return Results.Ok(await repository.GetSettingsAsync(cancellationToken));
    }

    /// <summary>設定を保存して推薦を再計算する。</summary>
    private static async Task<IResult> SaveSettingsAsync(
        TaskManagerSettings settings,
        HttpRequest request,
        TaskRepository repository,
        RecommendationService recommendationService,
        CancellationToken cancellationToken)
    {
        // 相対係数の範囲と最低1つの有効係数を検証してから全設定を保存する。
        double[] priorityWeights =
        [
            settings.DeadlineWeight,
            settings.ImportanceWeight,
            settings.UnblockWeight,
            settings.DeferralWeight,
            settings.ContinuityWeight,
            settings.AgingWeight
        ];
        if (priorityWeights.Any(weight => !double.IsFinite(weight) || weight is < 0 or > 1))
        {
            throw new InvalidOperationException("優先度係数は0から1で指定してください。");
        }
        if (priorityWeights.Sum() <= 0)
        {
            throw new InvalidOperationException("優先度係数を1つ以上0より大きくしてください。");
        }
        if (settings.LongTimerWarningMinutes is < 15 or > 1440)
        {
            throw new InvalidOperationException("長時間タイマー警告は15～1440分で指定してください。");
        }
        settings.CodexThreadIdentifier = CodexThreadIdentifier.Normalize(settings.CodexThreadIdentifier);
        settings.CalendarEmbedUrl = CalendarEmbedUrlValidator.Normalize(settings.CalendarEmbedUrl);
        await repository.SaveSettingsAsync(settings, GetSource(request), cancellationToken);
        return Results.Ok(await recommendationService.RefreshAsync(cancellationToken));
    }

    /// <summary>新しい順の操作履歴を返す。</summary>
    private static async Task<IResult> GetHistoryAsync(
        TaskRepository repository,
        [FromQuery] int maximumCount,
        CancellationToken cancellationToken)
    {
        // 未指定時は200件を返す。
        int appliedCount = maximumCount <= 0 ? 200 : maximumCount;
        return Results.Ok(await repository.GetHistoryAsync(appliedCount, cancellationToken));
    }

    /// <summary>保存済み下書きバッチを新しい順で返す。</summary>
    private static async Task<IResult> GetDraftBatchesAsync(
        TaskRepository repository,
        [FromQuery] string? projectIdentifier,
        CancellationToken cancellationToken)
    {
        // 任意のプロジェクトIDで下書きバッチを絞り込む。
        return Results.Ok(await repository.GetDraftBatchesAsync(projectIdentifier, cancellationToken));
    }

    /// <summary>指定IDの下書きバッチとタスク一覧を返す。</summary>
    private static async Task<IResult> GetDraftBatchAsync(
        string identifier,
        TaskRepository repository,
        CancellationToken cancellationToken)
    {
        // 存在しない下書きバッチは404として返す。
        DraftBatchRecord? draftBatch = await repository.GetDraftBatchAsync(identifier, cancellationToken);
        return draftBatch is null
            ? Results.NotFound(new { error = "下書きバッチが見つかりません。" })
            : Results.Ok(draftBatch);
    }

    /// <summary>Codexが生成した下書きバッチを登録する。</summary>
    private static async Task<IResult> CreateDraftBatchAsync(
        DraftBatchRequest request,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 保存成否、後処理状態、仮参照キー対応表を統一形式で返す。
        DraftBatchCreationResult result = await taskService.CreateDraftBatchAsync(request, cancellationToken);
        return Results.Created($"/api/v1/draft-batches/{result.BatchIdentifier}", result);
    }

    /// <summary>画面から下書きバッチを一括承認する。</summary>
    private static async Task<IResult> ApproveDraftBatchAsync(
        string identifier,
        TaskService taskService,
        CancellationToken cancellationToken)
    {
        // 最新検証を通過した下書きだけを実行可能へ変更する。
        int approvedCount = await taskService.ApproveDraftBatchAsync(identifier, cancellationToken);
        return Results.Ok(new { approvedCount });
    }

    /// <summary>GoogleデスクトップOAuth認証情報を保存する。</summary>
    private static async Task<IResult> SaveCalendarCredentialsAsync(
        IFormFile credentialFile,
        GoogleCalendarService calendarService,
        CancellationToken cancellationToken)
    {
        // 小さなJSONファイルだけを受け付ける。
        if (credentialFile.Length is <= 0 or > 1024 * 1024)
        {
            throw new InvalidOperationException("credentials.jsonのサイズが不正です。");
        }
        await using Stream credentialStream = credentialFile.OpenReadStream();
        await calendarService.SaveCredentialsAsync(credentialStream, cancellationToken);
        return Results.Ok(new { saved = true });
    }

    /// <summary>Google Calendarの対話認証を開始する。</summary>
    private static async Task<IResult> ConnectCalendarAsync(
        GoogleCalendarService calendarService,
        RecommendationService recommendationService,
        CancellationToken cancellationToken)
    {
        // 認証完了後に初回同期と推薦更新を実行する。
        await calendarService.ConnectAsync(cancellationToken);
        int eventCount = await calendarService.SynchronizeAsync(false, cancellationToken);
        await recommendationService.RefreshAsync(cancellationToken);
        return Results.Ok(new { connected = true, eventCount });
    }

    /// <summary>Google Calendarを手動同期する。</summary>
    private static async Task<IResult> SynchronizeCalendarAsync(
        GoogleCalendarService calendarService,
        RecommendationService recommendationService,
        CancellationToken cancellationToken)
    {
        // 認証済みトークンで予定を更新する。
        int eventCount = await calendarService.SynchronizeAsync(false, cancellationToken);
        await recommendationService.RefreshAsync(cancellationToken);
        return Results.Ok(new { eventCount });
    }

    /// <summary>利用者操作でSQLiteバックアップを作成する。</summary>
    private static async Task<IResult> CreateBackupAsync(
        DatabaseInitializer initializer,
        CancellationToken cancellationToken)
    {
        // 作成したバックアップの絶対パスを返す。
        string backupPath = await initializer.CreateBackupAsync("manual", cancellationToken);
        return Results.Ok(new { backupPath });
    }

    /// <summary>HTTPヘッダーから履歴の操作元を判定する。</summary>
    private static string GetSource(HttpRequest request)
    {
        // CLIだけCodexとして記録し、それ以外は画面とする。
        return string.Equals(request.Headers["X-TaskManager-Source"], "Codex", StringComparison.OrdinalIgnoreCase)
            ? TaskConstants.CodexSource
            : TaskConstants.ScreenSource;
    }
}

/// <summary>タスク状態操作の追加指定を表す。</summary>
public sealed class TaskActionRequest
{
    // 延期時に利用する分数を保持する。
    public int? PostponeMinutes { get; set; }
}
