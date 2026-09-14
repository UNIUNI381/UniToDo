using System.Globalization;
using Microsoft.Data.Sqlite;
using TaskManager.Domain;

namespace TaskManager.Data;

/// <summary>SQLiteに対するタスク管理データの読み書きを提供する。</summary>
public sealed class TaskRepository(DatabaseInitializer databaseInitializer)
{
    // SQLite接続の生成元を保持する。
    private readonly DatabaseInitializer initializer = databaseInitializer;

    /// <summary>全タスクと依存関係を読み込む。</summary>
    public Task<List<ManagedTask>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        // 全体検証など全件が必要な呼出元向けに従来の順序で返す。
        return ReadTasksAsync("1 = 1", null, cancellationToken);
    }

    /// <summary>指定IDのタスクとその依存関係だけを取得する。</summary>
    public async Task<ManagedTask?> GetTaskAsync(string taskIdentifier, CancellationToken cancellationToken = default)
    {
        // 従来のOrdinalIgnoreCaseと同じ照合の索引で対象を検索する。
        List<ManagedTask> tasks = await ReadTasksAsync(
            "tasks.identifier COLLATE TASK_IDENTIFIER = $value", taskIdentifier, cancellationToken);
        return tasks.FirstOrDefault();
    }

    /// <summary>指定状態のタスクとその依存関係だけを取得する。</summary>
    public Task<List<ManagedTask>> GetTasksByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        // 状態索引で実行中タスクなどの定期確認を限定する。
        return ReadTasksAsync("tasks.status = $value", status, cancellationToken);
    }

    /// <summary>未完了タスクと必要な完了済み依存先の識別情報を推薦用に取得する。</summary>
    public async Task<List<ManagedTask>> GetRecommendationTasksAsync(CancellationToken cancellationToken = default)
    {
        // 期限表示と後続数の計算に必要な下書き・待機・要確認も保持する。
        List<ManagedTask> tasks = await ReadTasksAsync(
            "tasks.status NOT IN ('完了', '中止')", null, cancellationToken);
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT completed.identifier
            FROM tasks AS active
            CROSS JOIN task_dependencies AS dependency
              ON dependency.task_identifier COLLATE TASK_IDENTIFIER = active.identifier
            CROSS JOIN tasks AS completed INDEXED BY index_tasks_identifier_lookup
              ON completed.identifier COLLATE TASK_IDENTIFIER = dependency.dependency_identifier
            WHERE active.status NOT IN ('完了', '中止') AND completed.status = '完了';
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // 完了済みタスクは依存解除判定のIDと状態だけを利用する。
            tasks.Add(new ManagedTask { Identifier = reader.GetString(0), Status = TaskConstants.CompletedStatus });
        }
        return tasks;
    }

    /// <summary>内部で指定した条件に合うタスクと依存関係を読み込む。</summary>
    private async Task<List<ManagedTask>> ReadTasksAsync(
        string predicate, string? value, CancellationToken cancellationToken)
    {
        // SQL条件は内部の固定文字列だけを使用し、入力値はパラメーターで渡す。
        await using SqliteConnection connection = initializer.OpenConnection();
        List<ManagedTask> tasks = [];
        await using SqliteCommand taskCommand = connection.CreateCommand();
        taskCommand.CommandText = $"SELECT tasks.* FROM tasks WHERE {predicate} ORDER BY tasks.created_at, tasks.identifier;";
        taskCommand.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        await using (SqliteDataReader reader = await taskCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add(ReadTask(reader));
            }
        }
        if (tasks.Count == 0)
        {
            return tasks;
        }

        // 対象タスクへ結び付く依存関係だけを索引で取得する。
        Dictionary<string, ManagedTask> taskMap = tasks.ToDictionary(task => task.Identifier, StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand dependencyCommand = connection.CreateCommand();
        dependencyCommand.CommandText = $"""
            SELECT dependency.task_identifier, dependency.dependency_identifier
            FROM tasks CROSS JOIN task_dependencies AS dependency
              ON dependency.task_identifier COLLATE TASK_IDENTIFIER = tasks.identifier
            WHERE {predicate}
            ORDER BY dependency.task_identifier, dependency.dependency_identifier;
            """;
        dependencyCommand.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        await using SqliteDataReader dependencyReader = await dependencyCommand.ExecuteReaderAsync(cancellationToken);
        while (await dependencyReader.ReadAsync(cancellationToken))
        {
            taskMap[dependencyReader.GetString(0)].DependencyIdentifiers.Add(dependencyReader.GetString(1));
        }
        return tasks;
    }

    /// <summary>タスクを追加または更新して履歴を記録する。</summary>
    public async Task SaveTaskAsync(
        ManagedTask task,
        string eventType,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 本体、依存関係、履歴を同一トランザクションで確定する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpsertTaskAsync(connection, transaction, task, cancellationToken);
        await ReplaceDependenciesAsync(connection, transaction, task, cancellationToken);
        await ApplyTimeEntryTransitionAsync(
            connection,
            transaction,
            task,
            eventType,
            source,
            cancellationToken);
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = task.UpdatedAt == default ? DateTimeOffset.Now : task.UpdatedAt,
            EventType = eventType,
            TaskIdentifier = task.Identifier,
            ProjectIdentifier = task.ProjectIdentifier ?? string.Empty,
            Summary = task.Title,
            Details = string.Empty,
            Source = source
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>関連付けのない指定タスクを履歴を残して完全削除する。</summary>
    public async Task DeleteTaskAsync(
        ManagedTask task,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 関連表、本体、削除履歴を同一トランザクションで確定する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await CloseActiveTimeEntryAsync(
            connection,
            transaction,
            task.Identifier,
            task.UpdatedAt == default ? DateTimeOffset.Now : task.UpdatedAt,
            TimeTrackingConstants.CancelledStopReason,
            needsReview: false,
            reviewReason: string.Empty,
            source,
            cancellationToken);
        await using (SqliteCommand dependencyCommand = connection.CreateCommand())
        {
            dependencyCommand.Transaction = transaction;
            dependencyCommand.CommandText = "DELETE FROM task_dependencies WHERE task_identifier = $identifier OR dependency_identifier = $identifier;";
            dependencyCommand.Parameters.AddWithValue("$identifier", task.Identifier);
            await dependencyCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (SqliteCommand taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = transaction;
            taskCommand.CommandText = "DELETE FROM tasks WHERE identifier = $identifier;";
            taskCommand.Parameters.AddWithValue("$identifier", task.Identifier);
            int deletedCount = await taskCommand.ExecuteNonQueryAsync(cancellationToken);
            if (deletedCount != 1)
            {
                throw new KeyNotFoundException($"タスクが見つかりません: {task.Identifier}");
            }
        }
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = DateTimeOffset.Now,
            EventType = "完全削除",
            TaskIdentifier = task.Identifier,
            ProjectIdentifier = task.ProjectIdentifier ?? string.Empty,
            Summary = task.Title,
            Details = "タスクIDの明示確認後に削除しました。",
            Source = source
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>複数タスクの計算結果と現在の推薦を保存する。</summary>
    public async Task SaveEvaluationsAsync(
        IReadOnlyList<TaskEvaluation> evaluations,
        string recommendationKey,
        string recommendationSummary,
        string recommendationDetails,
        CancellationToken cancellationToken = default)
    {
        // 候補から外れた行だけを消去し、候補は保存済み値との差分だけ更新する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        HashSet<string> evaluatedIdentifiers = evaluations.Select(evaluation => evaluation.Task.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> staleIdentifiers = [];
        await using (SqliteCommand previousCommand = connection.CreateCommand())
        {
            previousCommand.Transaction = transaction;
            previousCommand.CommandText = """
                SELECT identifier FROM tasks
                WHERE suggested_minutes <> 0 OR priority_score <> 0
                   OR slack_minutes IS NOT NULL OR recommendation_reason <> '';
                """;
            await using SqliteDataReader reader = await previousCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string identifier = reader.GetString(0);
                if (!evaluatedIdentifiers.Contains(identifier))
                {
                    staleIdentifiers.Add(identifier);
                }
            }
        }
        await using (SqliteCommand clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = """
                UPDATE tasks SET suggested_minutes = 0, priority_score = 0,
                    slack_minutes = NULL, recommendation_reason = '' WHERE identifier = $identifier;
                """;
            SqliteParameter identifierParameter = clearCommand.Parameters.Add("$identifier", SqliteType.Text);
            clearCommand.Prepare();
            foreach (string identifier in staleIdentifiers)
            {
                identifierParameter.Value = identifier;
                await clearCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (SqliteCommand evaluationCommand = connection.CreateCommand())
        {
            evaluationCommand.Transaction = transaction;
            evaluationCommand.CommandText = """
                UPDATE tasks
                SET suggested_minutes = $suggestedMinutes,
                    priority_score = $priorityScore,
                    slack_minutes = $slackMinutes,
                    recommendation_reason = $reason
                WHERE identifier = $identifier
                  AND (suggested_minutes IS NOT $suggestedMinutes OR priority_score IS NOT $priorityScore
                    OR slack_minutes IS NOT $slackMinutes OR recommendation_reason IS NOT $reason);
                """;
            SqliteParameter suggestedMinutesParameter = evaluationCommand.Parameters.Add("$suggestedMinutes", SqliteType.Integer);
            SqliteParameter priorityScoreParameter = evaluationCommand.Parameters.Add("$priorityScore", SqliteType.Real);
            SqliteParameter slackMinutesParameter = evaluationCommand.Parameters.Add("$slackMinutes", SqliteType.Integer);
            SqliteParameter reasonParameter = evaluationCommand.Parameters.Add("$reason", SqliteType.Text);
            SqliteParameter identifierParameter = evaluationCommand.Parameters.Add("$identifier", SqliteType.Text);
            evaluationCommand.Prepare();
            foreach (TaskEvaluation evaluation in evaluations)
            {
                // 同じ準備済みSQLの値だけを変えて大量候補の更新負担を抑える。
                suggestedMinutesParameter.Value = evaluation.SuggestedMinutes;
                priorityScoreParameter.Value = evaluation.PriorityScore;
                slackMinutesParameter.Value = evaluation.SlackMinutes.HasValue && double.IsFinite(evaluation.SlackMinutes.Value)
                    ? (object)(int)Math.Floor(evaluation.SlackMinutes.Value)
                    : DBNull.Value;
                reasonParameter.Value = evaluation.Reason;
                identifierParameter.Value = evaluation.Task.Identifier;
                await evaluationCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        string previousRecommendationKey = await GetStateAsync(connection, transaction, "last_recommendation", cancellationToken) ?? string.Empty;
        if (!string.Equals(previousRecommendationKey, recommendationKey, StringComparison.Ordinal))
        {
            await SetStateAsync(connection, transaction, "last_recommendation", recommendationKey, cancellationToken);
            await InsertHistoryAsync(connection, transaction, new HistoryRecord
            {
                OccurredAt = DateTimeOffset.Now,
                EventType = recommendationKey.StartsWith("NONE:", StringComparison.Ordinal) ? "提案なし" : "提案",
                TaskIdentifier = recommendationKey.StartsWith("NONE:", StringComparison.Ordinal) ? string.Empty : recommendationKey,
                Summary = recommendationSummary,
                Details = recommendationDetails,
                Source = TaskConstants.SystemSource
            }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>設定を読み込んで不足値を既定値で補完する。</summary>
    public async Task<TaskManagerSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        // キー値テーブルを型付き設定へ変換する。
        await using SqliteConnection connection = initializer.OpenConnection();
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM settings;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }
        return DeserializeSettings(values);
    }

    /// <summary>型付き設定をキー値テーブルへ保存する。</summary>
    public async Task SaveSettingsAsync(TaskManagerSettings settings, string source, CancellationToken cancellationToken = default)
    {
        // 全設定を同一トランザクションで上書きする。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (KeyValuePair<string, string> settingValue in SerializeSettings(settings))
        {
            await using SqliteCommand settingCommand = connection.CreateCommand();
            settingCommand.Transaction = transaction;
            settingCommand.CommandText = """
                INSERT INTO settings(setting_key, setting_value) VALUES ($key, $value)
                ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
                """;
            settingCommand.Parameters.AddWithValue("$key", settingValue.Key);
            settingCommand.Parameters.AddWithValue("$value", settingValue.Value);
            await settingCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = DateTimeOffset.Now,
            EventType = "設定更新",
            Summary = "設定を更新しました",
            Source = source
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>カレンダーキャッシュを読み込む。</summary>
    public async Task<List<CalendarEventRecord>> GetCalendarEventsAsync(CancellationToken cancellationToken = default)
    {
        // 保存済み予定を開始日時順で返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        return await GetCalendarEventsAsync(connection, null, cancellationToken);
    }

    /// <summary>指定トランザクション内の予定キャッシュを読み込む。</summary>
    private static async Task<List<CalendarEventRecord>> GetCalendarEventsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        // 比較と保存で同じデータベース状態を使用する。
        List<CalendarEventRecord> events = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM calendar_events ORDER BY start_at, end_at;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new CalendarEventRecord
            {
                EventIdentifier = reader.GetString(reader.GetOrdinal("event_identifier")),
                CalendarIdentifier = reader.GetString(reader.GetOrdinal("calendar_identifier")),
                Title = reader.GetString(reader.GetOrdinal("title")),
                Description = reader.GetString(reader.GetOrdinal("description")),
                StartAt = ParseDate(reader.GetString(reader.GetOrdinal("start_at")))!.Value,
                EndAt = ParseDate(reader.GetString(reader.GetOrdinal("end_at")))!.Value,
                Location = reader.GetString(reader.GetOrdinal("location")),
                IsAllDay = reader.GetInt32(reader.GetOrdinal("is_all_day")) != 0,
                IsBusy = reader.GetInt32(reader.GetOrdinal("is_busy")) != 0,
                ExternalUpdatedAt = ReadNullableDate(reader, "external_updated_at")
            });
        }
        return events;
    }

    /// <summary>カレンダーキャッシュを正常な同期結果で置き換える。</summary>
    public async Task ReplaceCalendarEventsAsync(
        IReadOnlyList<CalendarEventRecord> events,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken = default)
    {
        // 古いキャッシュの削除と新しい予定の保存を一括確定する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        List<CalendarEventRecord> previousEvents = await GetCalendarEventsAsync(connection, transaction, cancellationToken);
        bool eventsChanged = !previousEvents.OrderBy(CalendarEventKey).Select(CalendarEventValue)
            .SequenceEqual(events.OrderBy(CalendarEventKey).Select(CalendarEventValue));
        string? previousError = await GetStateAsync(connection, transaction, "calendar_error", cancellationToken);
        if (eventsChanged)
        {
            await using (SqliteCommand deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM calendar_events;";
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (CalendarEventRecord calendarEvent in events)
            {
                await using SqliteCommand insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO calendar_events(
                        event_identifier, calendar_identifier, title, description, start_at, end_at,
                        location, is_all_day, is_busy, external_updated_at)
                    VALUES ($eventIdentifier, $calendarIdentifier, $title, $description, $startAt, $endAt,
                        $location, $isAllDay, $isBusy, $externalUpdatedAt);
                    """;
                insertCommand.Parameters.AddWithValue("$eventIdentifier", calendarEvent.EventIdentifier);
                insertCommand.Parameters.AddWithValue("$calendarIdentifier", calendarEvent.CalendarIdentifier);
                insertCommand.Parameters.AddWithValue("$title", calendarEvent.Title);
                insertCommand.Parameters.AddWithValue("$description", calendarEvent.Description);
                insertCommand.Parameters.AddWithValue("$startAt", FormatDate(calendarEvent.StartAt));
                insertCommand.Parameters.AddWithValue("$endAt", FormatDate(calendarEvent.EndAt));
                insertCommand.Parameters.AddWithValue("$location", calendarEvent.Location);
                insertCommand.Parameters.AddWithValue("$isAllDay", calendarEvent.IsAllDay ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$isBusy", calendarEvent.IsBusy ? 1 : 0);
                insertCommand.Parameters.AddWithValue("$externalUpdatedAt", ToDatabaseValue(calendarEvent.ExternalUpdatedAt));
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await SetStateAsync(connection, transaction, "calendar_updated_at", FormatDate(synchronizedAt), cancellationToken);
        await SetStateAsync(connection, transaction, "calendar_error", string.Empty, cancellationToken);
        if (eventsChanged || !string.IsNullOrEmpty(previousError))
        {
            await InsertHistoryAsync(connection, transaction, new HistoryRecord
            {
                OccurredAt = synchronizedAt,
                EventType = "カレンダー同期",
                Summary = eventsChanged ? $"{events.Count}件の予定キャッシュを更新しました" : "カレンダー同期が復旧しました",
                Source = TaskConstants.SystemSource
            }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>予定の並べ替えに使う複合識別子を返す。</summary>
    private static (string Calendar, string Event) CalendarEventKey(CalendarEventRecord calendarEvent)
    {
        // 同時刻の予定や取得順序の違いを識別子で正規化する。
        return (calendarEvent.CalendarIdentifier, calendarEvent.EventIdentifier);
    }

    /// <summary>予定の全保存項目を比較可能な値として返す。</summary>
    private static object CalendarEventValue(CalendarEventRecord calendarEvent)
    {
        // 日時は絶対時刻で比較し、外部更新日時だけの変化も検出する。
        return (calendarEvent.CalendarIdentifier, calendarEvent.EventIdentifier,
            calendarEvent.Title, calendarEvent.Description, calendarEvent.StartAt,
            calendarEvent.EndAt, calendarEvent.Location, calendarEvent.IsAllDay,
            calendarEvent.IsBusy, calendarEvent.ExternalUpdatedAt);
    }

    /// <summary>カレンダー同期エラーを状態として保存する。</summary>
    public async Task SaveCalendarErrorAsync(string errorMessage, CancellationToken cancellationToken = default)
    {
        // キャッシュを消さずに最新エラーだけを更新する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        string? previousError = await GetStateAsync(connection, transaction, "calendar_error", cancellationToken);
        if (string.Equals(previousError ?? string.Empty, errorMessage, StringComparison.Ordinal))
        {
            return;
        }
        await SetStateAsync(connection, transaction, "calendar_error", errorMessage, cancellationToken);
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = DateTimeOffset.Now,
            EventType = "カレンダーエラー",
            Summary = "Google Calendarの同期に失敗しました",
            Details = errorMessage,
            Source = TaskConstants.SystemSource
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>アプリケーション状態を取得する。</summary>
    public async Task<string?> GetStateAsync(string stateKey, CancellationToken cancellationToken = default)
    {
        // 指定したキーの保存値を返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        return await GetStateAsync(connection, null, stateKey, cancellationToken);
    }

    /// <summary>履歴を新しい順で取得する。</summary>
    public async Task<List<HistoryRecord>> GetHistoryAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        // 画面表示用に取得件数を安全な範囲へ制限する。
        int limitedCount = Math.Clamp(maximumCount, 1, 1000);
        await using SqliteConnection connection = initializer.OpenConnection();
        List<HistoryRecord> history = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM history ORDER BY occurred_at DESC, identifier DESC LIMIT $maximumCount;";
        command.Parameters.AddWithValue("$maximumCount", limitedCount);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(ReadHistory(reader));
        }
        return history;
    }

    /// <summary>通知キーが未送信なら送信済みとして記録する。</summary>
    public async Task<bool> TryRecordNotificationAsync(
        string notificationKey,
        string notificationType,
        string taskIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 主キー制約を利用して重複通知を防止する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO notification_ledger(notification_key, notification_type, task_identifier, notified_at)
            VALUES ($notificationKey, $notificationType, $taskIdentifier, $notifiedAt);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$notificationKey", notificationKey);
        command.Parameters.AddWithValue("$notificationType", notificationType);
        command.Parameters.AddWithValue("$taskIdentifier", taskIdentifier);
        command.Parameters.AddWithValue("$notifiedAt", FormatDate(DateTimeOffset.Now));
        object? changedRows = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(changedRows, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>保存済み下書きバッチを新しい順で取得する。</summary>
    public async Task<List<DraftBatchRecord>> GetDraftBatchesAsync(
        string? projectIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        // バッチ本体を読み込んだ後で下書きタスク、プロジェクト、対応表を関連付ける。
        List<DraftBatchRecord> draftBatches = [];
        await using (SqliteConnection connection = initializer.OpenConnection())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT identifier, title, parent_identifier, created_at, approved_at, source,
                       request_key, post_processing_succeeded, warning
                FROM draft_batches
                ORDER BY created_at DESC, identifier DESC;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                draftBatches.Add(ReadDraftBatch(reader));
            }
        }

        List<ManagedTask> tasks = await ReadTasksAsync("tasks.draft_batch_identifier IS NOT NULL", null, cancellationToken);
        ILookup<string, ManagedTask> tasksByBatch = tasks.ToLookup(
            task => task.DraftBatchIdentifier ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (DraftBatchRecord draftBatch in draftBatches)
        {
            // バッチIDが一致する下書きだけを読取結果へ設定する。
            List<ManagedTask> batchTasks = tasksByBatch[draftBatch.BatchIdentifier].ToList();
            PopulateDraftBatch(draftBatch, batchTasks);
        }
        if (!string.IsNullOrWhiteSpace(projectIdentifier))
        {
            draftBatches = draftBatches
                .Where(draftBatch => draftBatch.ProjectIdentifiers.Contains(
                    projectIdentifier,
                    StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        return draftBatches;
    }

    /// <summary>指定IDの下書きバッチとタスク一覧を取得する。</summary>
    public Task<DraftBatchRecord?> GetDraftBatchAsync(
        string batchIdentifier, CancellationToken cancellationToken = default)
    {
        // 単一バッチの取得で全バッチと全タスクを展開しない。
        return ReadDraftBatchAsync("identifier COLLATE TASK_IDENTIFIER = $value", batchIdentifier, cancellationToken);
    }

    /// <summary>冪等キーに対応する既存下書きバッチを取得する。</summary>
    public Task<DraftBatchRecord?> GetDraftBatchByRequestKeyAsync(
        string requestKey, CancellationToken cancellationToken = default)
    {
        // 冪等キーの既存一意索引で再送時の対象だけを取得する。
        return ReadDraftBatchAsync("request_key = $value", requestKey, cancellationToken);
    }

    /// <summary>内部条件に一致するバッチと所属タスクだけを読み込む。</summary>
    private async Task<DraftBatchRecord?> ReadDraftBatchAsync(
        string predicate, string value, CancellationToken cancellationToken)
    {
        // バッチ本体のリーダーを閉じてから所属タスクを読み込む。
        DraftBatchRecord selectedBatch;
        await using (SqliteConnection connection = initializer.OpenConnection())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM draft_batches WHERE {predicate} ORDER BY created_at DESC, identifier DESC LIMIT 1;";
            command.Parameters.AddWithValue("$value", value);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            selectedBatch = ReadDraftBatch(reader);
        }
        selectedBatch.Tasks = await ReadTasksAsync(
            "tasks.draft_batch_identifier COLLATE TASK_IDENTIFIER = $value", selectedBatch.BatchIdentifier, cancellationToken);
        PopulateDraftBatch(selectedBatch, selectedBatch.Tasks);
        return selectedBatch;
    }

    /// <summary>所属タスクから下書きバッチの件数と対応情報を構築する。</summary>
    private static void PopulateDraftBatch(DraftBatchRecord draftBatch, IReadOnlyList<ManagedTask> batchTasks)
    {
        // 一覧と単一取得で同じ件数・プロジェクト・仮参照対応を返す。
        draftBatch.TaskCount = batchTasks.Count;
        draftBatch.ProjectIdentifiers = batchTasks
            .Select(task => task.ProjectIdentifier)
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identifier => identifier, StringComparer.OrdinalIgnoreCase)
            .ToList();
        draftBatch.TaskMappings = batchTasks
            .Where(task => !string.IsNullOrWhiteSpace(task.AiReferenceKey))
            .Select(task => new DraftTaskMapping
            {
                AiReferenceKey = task.AiReferenceKey,
                TaskIdentifier = task.Identifier
            })
            .ToList();
    }

    /// <summary>下書き保存後の検証・推薦結果をバッチへ記録する。</summary>
    public async Task UpdateDraftBatchPostProcessingAsync(
        string batchIdentifier,
        bool succeeded,
        string warning,
        CancellationToken cancellationToken = default)
    {
        // 保存済みバッチの後処理状態だけを更新して下書き本体を維持する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE draft_batches
            SET post_processing_succeeded = $succeeded,
                warning = $warning
            WHERE identifier = $identifier;
            """;
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$warning", warning);
        command.Parameters.AddWithValue("$identifier", batchIdentifier);
        int updatedCount = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updatedCount != 1)
        {
            throw new KeyNotFoundException($"下書きバッチが見つかりません: {batchIdentifier}");
        }
    }

    /// <summary>AI下書き群を1つのバッチとして冪等に保存する。</summary>
    public async Task<DraftBatchCreationResult> CreateDraftBatchAsync(
        DraftBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        // 同じ冪等キーの既存結果を返し、仮参照キーを実IDへ変換して一括登録する。
        string? requestKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey.Trim();
        if (requestKey is not null)
        {
            DraftBatchRecord? existingBatch = await GetDraftBatchByRequestKeyAsync(requestKey, cancellationToken);
            if (existingBatch is not null)
            {
                return CreateDraftCreationResult(existingBatch, replayed: true);
            }
        }
        string batchIdentifier = $"DRAFT-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];
        Dictionary<string, string> referenceMap = request.Tasks
            .ToDictionary(task => task.AiReferenceKey, task => task.Identifier, StringComparer.OrdinalIgnoreCase);
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand batchCommand = connection.CreateCommand())
        {
            batchCommand.Transaction = transaction;
            batchCommand.CommandText = """
                INSERT INTO draft_batches(
                    identifier, title, parent_identifier, created_at, source,
                    request_key, post_processing_succeeded, warning)
                VALUES (
                    $identifier, $title, $parentIdentifier, $createdAt, $source,
                    $requestKey, 1, '');
                """;
            batchCommand.Parameters.AddWithValue("$identifier", batchIdentifier);
            batchCommand.Parameters.AddWithValue("$title", request.Title);
            batchCommand.Parameters.AddWithValue("$parentIdentifier", (object?)request.ParentIdentifier ?? DBNull.Value);
            batchCommand.Parameters.AddWithValue("$createdAt", FormatDate(DateTimeOffset.Now));
            batchCommand.Parameters.AddWithValue("$source", TaskConstants.CodexSource);
            batchCommand.Parameters.AddWithValue("$requestKey", (object?)requestKey ?? DBNull.Value);
            await batchCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (ManagedTask task in request.Tasks)
        {
            task.Status = TaskConstants.DraftStatus;
            task.ParentIdentifier ??= request.ParentIdentifier;
            task.DraftBatchIdentifier = batchIdentifier;
            task.Source = TaskConstants.CodexSource;
            task.CreatedAt = task.CreatedAt == default ? DateTimeOffset.Now : task.CreatedAt;
            task.UpdatedAt = DateTimeOffset.Now;
            task.DependencyIdentifiers = task.DependencyIdentifiers
                .Select(dependency => referenceMap.GetValueOrDefault(dependency, dependency))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await UpsertTaskAsync(connection, transaction, task, cancellationToken);
        }
        foreach (ManagedTask task in request.Tasks)
        {
            await ReplaceDependenciesAsync(connection, transaction, task, cancellationToken);
        }
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = DateTimeOffset.Now,
            EventType = "下書き登録",
            TaskIdentifier = request.ParentIdentifier ?? string.Empty,
            Summary = $"{request.Tasks.Count}件の下書きを登録しました",
            Source = TaskConstants.CodexSource
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DraftBatchCreationResult
        {
            BatchIdentifier = batchIdentifier,
            Identifier = batchIdentifier,
            Saved = true,
            TaskCount = request.Tasks.Count,
            PostProcessingSucceeded = true,
            TaskMappings = request.Tasks
                .Select(task => new DraftTaskMapping
                {
                    AiReferenceKey = task.AiReferenceKey,
                    TaskIdentifier = task.Identifier
                })
                .ToList()
        };
    }

    /// <summary>検証済みの下書きバッチを一括承認する。</summary>
    public async Task<int> ApproveDraftBatchAsync(string batchIdentifier, CancellationToken cancellationToken = default)
    {
        // 下書きを実行可能へ変更し親タスクを待機中へ移す。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand validationCommand = connection.CreateCommand();
        validationCommand.Transaction = transaction;
        validationCommand.CommandText = """
            SELECT COUNT(*) FROM tasks
            WHERE draft_batch_identifier = $batchIdentifier
              AND status = $draftStatus
              AND validation_result <> '';
            """;
        validationCommand.Parameters.AddWithValue("$batchIdentifier", batchIdentifier);
        validationCommand.Parameters.AddWithValue("$draftStatus", TaskConstants.DraftStatus);
        long invalidCount = Convert.ToInt64(await validationCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (invalidCount > 0)
        {
            throw new InvalidOperationException("検証エラーがある下書きは承認できません。");
        }
        await using SqliteCommand approvalCommand = connection.CreateCommand();
        approvalCommand.Transaction = transaction;
        approvalCommand.CommandText = """
            UPDATE tasks SET status = $readyStatus, updated_at = $updatedAt
            WHERE draft_batch_identifier = $batchIdentifier AND status = $draftStatus;
            SELECT changes();
            """;
        approvalCommand.Parameters.AddWithValue("$readyStatus", TaskConstants.ReadyStatus);
        approvalCommand.Parameters.AddWithValue("$updatedAt", FormatDate(DateTimeOffset.Now));
        approvalCommand.Parameters.AddWithValue("$batchIdentifier", batchIdentifier);
        approvalCommand.Parameters.AddWithValue("$draftStatus", TaskConstants.DraftStatus);
        int approvedCount = Convert.ToInt32(await approvalCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using SqliteCommand parentCommand = connection.CreateCommand();
        parentCommand.Transaction = transaction;
        parentCommand.CommandText = """
            UPDATE tasks SET status = $waitingStatus, updated_at = $updatedAt
            WHERE identifier = (SELECT parent_identifier FROM draft_batches WHERE identifier = $batchIdentifier)
              AND status NOT IN ($completedStatus, $cancelledStatus);
            """;
        parentCommand.Parameters.AddWithValue("$waitingStatus", TaskConstants.WaitingStatus);
        parentCommand.Parameters.AddWithValue("$updatedAt", FormatDate(DateTimeOffset.Now));
        parentCommand.Parameters.AddWithValue("$batchIdentifier", batchIdentifier);
        parentCommand.Parameters.AddWithValue("$completedStatus", TaskConstants.CompletedStatus);
        parentCommand.Parameters.AddWithValue("$cancelledStatus", TaskConstants.CancelledStatus);
        await parentCommand.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand batchCommand = connection.CreateCommand();
        batchCommand.Transaction = transaction;
        batchCommand.CommandText = "UPDATE draft_batches SET approved_at = $approvedAt WHERE identifier = $identifier;";
        batchCommand.Parameters.AddWithValue("$approvedAt", FormatDate(DateTimeOffset.Now));
        batchCommand.Parameters.AddWithValue("$identifier", batchIdentifier);
        await batchCommand.ExecuteNonQueryAsync(cancellationToken);
        await InsertHistoryAsync(connection, transaction, new HistoryRecord
        {
            OccurredAt = DateTimeOffset.Now,
            EventType = "下書き承認",
            Summary = $"{approvedCount}件を承認しました",
            Details = batchIdentifier,
            Source = TaskConstants.ScreenSource
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return approvedCount;
    }

    /// <summary>設定を保存用のキー値へ変換する。</summary>
    public static Dictionary<string, string> SerializeSettings(TaskManagerSettings settings)
    {
        // 数値はカルチャー非依存形式で保存する。
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["calendarIdentifiers"] = settings.CalendarIdentifiers,
            ["calendarEmbedUrl"] = settings.CalendarEmbedUrl,
            ["activityStart"] = settings.ActivityStart,
            ["activityEnd"] = settings.ActivityEnd,
            ["bufferRatio"] = settings.BufferRatio.ToString(CultureInfo.InvariantCulture),
            ["maximumWorkMinutes"] = settings.MaximumWorkMinutes.ToString(CultureInfo.InvariantCulture),
            ["minimumWorkMinutes"] = settings.MinimumWorkMinutes.ToString(CultureInfo.InvariantCulture),
            ["followUpGraceMinutes"] = settings.FollowUpGraceMinutes.ToString(CultureInfo.InvariantCulture),
            ["morningHour"] = settings.MorningHour.ToString(CultureInfo.InvariantCulture),
            ["calendarLookaheadDays"] = settings.CalendarLookaheadDays.ToString(CultureInfo.InvariantCulture),
            ["calendarBufferMinutes"] = settings.CalendarBufferMinutes.ToString(CultureInfo.InvariantCulture),
            ["deadlineWeight"] = settings.DeadlineWeight.ToString(CultureInfo.InvariantCulture),
            ["importanceWeight"] = settings.ImportanceWeight.ToString(CultureInfo.InvariantCulture),
            ["unblockWeight"] = settings.UnblockWeight.ToString(CultureInfo.InvariantCulture),
            ["deferralWeight"] = settings.DeferralWeight.ToString(CultureInfo.InvariantCulture),
            ["continuityWeight"] = settings.ContinuityWeight.ToString(CultureInfo.InvariantCulture),
            ["agingWeight"] = settings.AgingWeight.ToString(CultureInfo.InvariantCulture),
            ["currentContext"] = settings.CurrentContext,
            ["defaultPostponeMinutes"] = settings.DefaultPostponeMinutes.ToString(CultureInfo.InvariantCulture),
            ["futureDailyCapacityMinutes"] = settings.FutureDailyCapacityMinutes.ToString(CultureInfo.InvariantCulture),
            ["treatAllDayAsBusy"] = settings.TreatAllDayAsBusy.ToString(CultureInfo.InvariantCulture),
            ["timeZoneIdentifier"] = settings.TimeZoneIdentifier,
            ["notificationsEnabled"] = settings.NotificationsEnabled.ToString(CultureInfo.InvariantCulture),
            ["longTimerWarningMinutes"] = settings.LongTimerWarningMinutes.ToString(CultureInfo.InvariantCulture),
            ["codexThreadIdentifier"] = settings.CodexThreadIdentifier
        };
    }

    /// <summary>キー値から型付き設定を復元する。</summary>
    private static TaskManagerSettings DeserializeSettings(IReadOnlyDictionary<string, string> values)
    {
        // 不正値や不足値は既定値へ戻す。
        TaskManagerSettings settings = new();
        settings.CalendarIdentifiers = GetText(values, "calendarIdentifiers", settings.CalendarIdentifiers);
        settings.CalendarEmbedUrl = GetText(values, "calendarEmbedUrl", settings.CalendarEmbedUrl);
        settings.ActivityStart = GetText(values, "activityStart", settings.ActivityStart);
        settings.ActivityEnd = GetText(values, "activityEnd", settings.ActivityEnd);
        settings.BufferRatio = GetDouble(values, "bufferRatio", settings.BufferRatio);
        settings.MaximumWorkMinutes = GetInteger(values, "maximumWorkMinutes", settings.MaximumWorkMinutes);
        settings.MinimumWorkMinutes = GetInteger(values, "minimumWorkMinutes", settings.MinimumWorkMinutes);
        settings.FollowUpGraceMinutes = GetInteger(values, "followUpGraceMinutes", settings.FollowUpGraceMinutes);
        settings.MorningHour = GetInteger(values, "morningHour", settings.MorningHour);
        settings.CalendarLookaheadDays = GetInteger(values, "calendarLookaheadDays", settings.CalendarLookaheadDays);
        settings.CalendarBufferMinutes = GetInteger(values, "calendarBufferMinutes", settings.CalendarBufferMinutes);
        settings.DeadlineWeight = GetDouble(values, "deadlineWeight", settings.DeadlineWeight);
        settings.ImportanceWeight = GetDouble(values, "importanceWeight", settings.ImportanceWeight);
        settings.UnblockWeight = GetDouble(values, "unblockWeight", settings.UnblockWeight);
        settings.DeferralWeight = GetDouble(values, "deferralWeight", settings.DeferralWeight);
        settings.ContinuityWeight = GetDouble(values, "continuityWeight", settings.ContinuityWeight);
        settings.AgingWeight = GetDouble(values, "agingWeight", settings.AgingWeight);
        settings.CurrentContext = GetText(values, "currentContext", settings.CurrentContext);
        settings.DefaultPostponeMinutes = GetInteger(values, "defaultPostponeMinutes", settings.DefaultPostponeMinutes);
        settings.FutureDailyCapacityMinutes = GetInteger(values, "futureDailyCapacityMinutes", settings.FutureDailyCapacityMinutes);
        settings.TreatAllDayAsBusy = GetBoolean(values, "treatAllDayAsBusy", settings.TreatAllDayAsBusy);
        settings.TimeZoneIdentifier = GetText(values, "timeZoneIdentifier", settings.TimeZoneIdentifier);
        settings.NotificationsEnabled = GetBoolean(values, "notificationsEnabled", settings.NotificationsEnabled);
        settings.LongTimerWarningMinutes = GetInteger(values, "longTimerWarningMinutes", settings.LongTimerWarningMinutes);
        settings.CodexThreadIdentifier = GetText(values, "codexThreadIdentifier", settings.CodexThreadIdentifier);
        return settings;
    }

    /// <summary>タスク本体を追加または更新する。</summary>
    private static async Task UpsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ManagedTask task,
        CancellationToken cancellationToken)
    {
        // 全保存列を明示して計算列も一貫して保持する。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(
                identifier, project_identifier, category, title, details, parent_identifier, status, deadline_at, deadline_type,
                deadline_origin,
                estimated_minutes, remaining_minutes, importance, earliest_start_at, required_context,
                completion_condition, splittable, ai_confidence, ai_reference_key, source, created_at,
                updated_at, started_at, completed_at, follow_up_at, follow_up_notified_at, deferral_count,
                validation_result, suggested_minutes, priority_score, slack_minutes, recommendation_reason,
                draft_batch_identifier)
            VALUES (
                $identifier, $projectIdentifier, $category, $title, $details, $parentIdentifier, $status, $deadlineAt, $deadlineType,
                $deadlineOrigin,
                $estimatedMinutes, $remainingMinutes, $importance, $earliestStartAt, $requiredContext,
                $completionCondition, $splittable, $aiConfidence, $aiReferenceKey, $source, $createdAt,
                $updatedAt, $startedAt, $completedAt, $followUpAt, $followUpNotifiedAt, $deferralCount,
                $validationResult, $suggestedMinutes, $priorityScore, $slackMinutes, $recommendationReason,
                $draftBatchIdentifier)
            ON CONFLICT(identifier) DO UPDATE SET
                project_identifier = excluded.project_identifier, category = excluded.category,
                title = excluded.title, details = excluded.details,
                parent_identifier = excluded.parent_identifier, status = excluded.status,
                deadline_at = excluded.deadline_at, deadline_type = excluded.deadline_type,
                deadline_origin = excluded.deadline_origin,
                estimated_minutes = excluded.estimated_minutes, remaining_minutes = excluded.remaining_minutes,
                importance = excluded.importance, earliest_start_at = excluded.earliest_start_at,
                required_context = excluded.required_context, completion_condition = excluded.completion_condition,
                splittable = excluded.splittable, ai_confidence = excluded.ai_confidence,
                ai_reference_key = excluded.ai_reference_key, source = excluded.source,
                updated_at = excluded.updated_at, started_at = excluded.started_at,
                completed_at = excluded.completed_at, follow_up_at = excluded.follow_up_at,
                follow_up_notified_at = excluded.follow_up_notified_at, deferral_count = excluded.deferral_count,
                validation_result = excluded.validation_result, suggested_minutes = excluded.suggested_minutes,
                priority_score = excluded.priority_score, slack_minutes = excluded.slack_minutes,
                recommendation_reason = excluded.recommendation_reason,
                draft_batch_identifier = excluded.draft_batch_identifier;
            """;
        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>タスク状態遷移に対応する作業ログ更新を同一トランザクションへ適用する。</summary>
    private static async Task ApplyTimeEntryTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ManagedTask task,
        string eventType,
        string source,
        CancellationToken cancellationToken)
    {
        // 通常の編集や通知保存では作業ログを変更しない。
        DateTimeOffset transitionTime = task.UpdatedAt == default ? DateTimeOffset.Now : task.UpdatedAt;
        if (eventType == "開始")
        {
            // 新しい開始前に残存しているタスク・自由活動タイマーを必ず閉じる。
            await CloseActiveTimeEntryAsync(
                connection,
                transaction,
                null,
                transitionTime,
                TimeTrackingConstants.ReplacedStopReason,
                needsReview: false,
                reviewReason: string.Empty,
                source,
                cancellationToken);
            int warningMinutes = await GetLongTimerWarningMinutesAsync(connection, transaction, cancellationToken);
            string projectName = await GetProjectNameAsync(
                connection,
                transaction,
                task.ProjectIdentifier,
                cancellationToken);
            DateTimeOffset startAt = task.StartedAt ?? transitionTime;
            await using SqliteCommand insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO time_entries(
                    identifier, task_identifier, project_identifier, title, project_name_snapshot,
                    start_at, end_at, stop_reason, source, warning_at, next_warning_at,
                    needs_review, review_reason, created_at, updated_at, voided_at, active_key)
                VALUES(
                    $identifier, $taskIdentifier, $projectIdentifier, $title, $projectNameSnapshot,
                    $startAt, NULL, '', $source, NULL, $nextWarningAt,
                    0, '', $createdAt, $updatedAt, NULL, 1);
                """;
            insertCommand.Parameters.AddWithValue("$identifier", $"TIME-{Guid.NewGuid():N}".ToUpperInvariant());
            insertCommand.Parameters.AddWithValue("$taskIdentifier", task.Identifier);
            insertCommand.Parameters.AddWithValue("$projectIdentifier", (object?)task.ProjectIdentifier ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$title", task.Title);
            insertCommand.Parameters.AddWithValue("$projectNameSnapshot", projectName);
            insertCommand.Parameters.AddWithValue("$startAt", FormatUtcDate(startAt));
            insertCommand.Parameters.AddWithValue("$source", source);
            insertCommand.Parameters.AddWithValue("$nextWarningAt", FormatUtcDate(startAt.AddMinutes(warningMinutes)));
            insertCommand.Parameters.AddWithValue("$createdAt", FormatUtcDate(transitionTime));
            insertCommand.Parameters.AddWithValue("$updatedAt", FormatUtcDate(transitionTime));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        string? stopReason = eventType switch
        {
            "完了" => TimeTrackingConstants.CompletedStopReason,
            "中断" => TimeTrackingConstants.InterruptedStopReason,
            "自動中断" => TimeTrackingConstants.ReplacedStopReason,
            "延期" => TimeTrackingConstants.PostponedStopReason,
            "中止" => TimeTrackingConstants.CancelledStopReason,
            "タイマー自動中断" => TimeTrackingConstants.AutomaticTimeoutStopReason,
            _ => null
        };
        if (stopReason is not null)
        {
            bool needsReview = eventType == "タイマー自動中断";
            await CloseActiveTimeEntryAsync(
                connection,
                transaction,
                task.Identifier,
                transitionTime,
                stopReason,
                needsReview,
                needsReview ? "長時間警告後に操作がなかったため自動停止しました。" : string.Empty,
                source,
                cancellationToken);
        }
        else if (eventType == "続行")
        {
            // 長時間警告からの続行だけ次の警告を1時間後へ延長する。
            await using SqliteCommand extendCommand = connection.CreateCommand();
            extendCommand.Transaction = transaction;
            extendCommand.CommandText = """
                UPDATE time_entries
                SET warning_at = NULL,
                    next_warning_at = $nextWarningAt,
                    source = $source,
                    updated_at = $updatedAt
                WHERE task_identifier = $taskIdentifier
                  AND end_at IS NULL
                  AND voided_at IS NULL
                  AND warning_at IS NOT NULL;
                """;
            extendCommand.Parameters.AddWithValue("$nextWarningAt", FormatUtcDate(transitionTime.AddMinutes(TimeTrackingConstants.WarningGraceMinutes)));
            extendCommand.Parameters.AddWithValue("$source", source);
            extendCommand.Parameters.AddWithValue("$updatedAt", FormatUtcDate(transitionTime));
            extendCommand.Parameters.AddWithValue("$taskIdentifier", task.Identifier);
            await extendCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>条件に一致する実行中作業ログを終了する。</summary>
    private static async Task CloseActiveTimeEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? taskIdentifier,
        DateTimeOffset endAt,
        string stopReason,
        bool needsReview,
        string reviewReason,
        string source,
        CancellationToken cancellationToken)
    {
        // タスクID未指定時は自由活動を含む現在の1件を対象にする。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE time_entries
            SET end_at = CASE WHEN julianday(start_at) > julianday($endAt) THEN start_at ELSE $endAt END,
                stop_reason = $stopReason,
                source = $source,
                warning_at = NULL,
                next_warning_at = NULL,
                needs_review = $needsReview,
                review_reason = $reviewReason,
                updated_at = $endAt
            WHERE end_at IS NULL
              AND voided_at IS NULL
              AND ($taskIdentifier = '' OR task_identifier = $taskIdentifier);
            """;
        command.Parameters.AddWithValue("$endAt", FormatUtcDate(endAt));
        command.Parameters.AddWithValue("$stopReason", stopReason);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$needsReview", needsReview ? 1 : 0);
        command.Parameters.AddWithValue("$reviewReason", reviewReason);
        command.Parameters.AddWithValue("$taskIdentifier", taskIdentifier ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>長時間タイマー警告の設定分数をトランザクション内で取得する。</summary>
    private static async Task<int> GetLongTimerWarningMinutesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // 未設定や不正値は既定の180分へ戻す。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT setting_value FROM settings WHERE setting_key = 'longTimerWarningMinutes';";
        string? value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int warningMinutes)
            && warningMinutes >= 15
            ? warningMinutes
            : 180;
    }

    /// <summary>プロジェクトIDから正式名称をトランザクション内で取得する。</summary>
    private static async Task<string> GetProjectNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? projectIdentifier,
        CancellationToken cancellationToken)
    {
        // プロジェクト未割当では空文字を保存する。
        if (string.IsNullOrWhiteSpace(projectIdentifier))
        {
            return string.Empty;
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_name FROM projects WHERE identifier = $identifier;";
        command.Parameters.AddWithValue("$identifier", projectIdentifier);
        return Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>タスク保存コマンドへ全パラメーターを設定する。</summary>
    private static void AddTaskParameters(SqliteCommand command, ManagedTask task)
    {
        // NULL許容日時と文字列をSQLite互換値へ変換する。
        command.Parameters.AddWithValue("$identifier", task.Identifier);
        command.Parameters.AddWithValue("$projectIdentifier", (object?)task.ProjectIdentifier ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", task.Category);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$details", task.Details);
        command.Parameters.AddWithValue("$parentIdentifier", (object?)task.ParentIdentifier ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", task.Status);
        command.Parameters.AddWithValue("$deadlineAt", ToDatabaseValue(task.DeadlineAt));
        command.Parameters.AddWithValue("$deadlineType", task.DeadlineType);
        string persistedDeadlineOrigin = task.DeadlineOrigin == ProjectConstants.AutomaticDeadlineOrigin
            ? task.DeadlineAt.HasValue
                ? ProjectConstants.ExplicitDeadlineOrigin
                : ProjectConstants.NoDeadlineOrigin
            : task.DeadlineOrigin;
        command.Parameters.AddWithValue("$deadlineOrigin", persistedDeadlineOrigin);
        command.Parameters.AddWithValue("$estimatedMinutes", task.EstimatedMinutes);
        command.Parameters.AddWithValue("$remainingMinutes", task.RemainingMinutes);
        command.Parameters.AddWithValue("$importance", task.Importance);
        command.Parameters.AddWithValue("$earliestStartAt", ToDatabaseValue(task.EarliestStartAt));
        command.Parameters.AddWithValue("$requiredContext", task.RequiredContext);
        command.Parameters.AddWithValue("$completionCondition", task.CompletionCondition);
        command.Parameters.AddWithValue("$splittable", task.Splittable ? 1 : 0);
        command.Parameters.AddWithValue("$aiConfidence", task.AiConfidence);
        command.Parameters.AddWithValue("$aiReferenceKey", task.AiReferenceKey);
        command.Parameters.AddWithValue("$source", task.Source);
        command.Parameters.AddWithValue("$createdAt", FormatDate(task.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(task.UpdatedAt));
        command.Parameters.AddWithValue("$startedAt", ToDatabaseValue(task.StartedAt));
        command.Parameters.AddWithValue("$completedAt", ToDatabaseValue(task.CompletedAt));
        command.Parameters.AddWithValue("$followUpAt", ToDatabaseValue(task.FollowUpAt));
        command.Parameters.AddWithValue("$followUpNotifiedAt", ToDatabaseValue(task.FollowUpNotifiedAt));
        command.Parameters.AddWithValue("$deferralCount", task.DeferralCount);
        command.Parameters.AddWithValue("$validationResult", task.ValidationResult);
        command.Parameters.AddWithValue("$suggestedMinutes", task.SuggestedMinutes);
        command.Parameters.AddWithValue("$priorityScore", task.PriorityScore);
        command.Parameters.AddWithValue("$slackMinutes", task.SlackMinutes.HasValue ? task.SlackMinutes.Value : DBNull.Value);
        command.Parameters.AddWithValue("$recommendationReason", task.RecommendationReason);
        command.Parameters.AddWithValue("$draftBatchIdentifier", (object?)task.DraftBatchIdentifier ?? DBNull.Value);
    }

    /// <summary>1件のタスクの依存関係を置き換える。</summary>
    private static async Task ReplaceDependenciesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ManagedTask task,
        CancellationToken cancellationToken)
    {
        // 古い関連を削除して重複のない依存先を登録する。
        await using (SqliteCommand deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM task_dependencies WHERE task_identifier = $taskIdentifier;";
            deleteCommand.Parameters.AddWithValue("$taskIdentifier", task.Identifier);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (string dependencyIdentifier in task.DependencyIdentifiers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using SqliteCommand insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = "INSERT INTO task_dependencies(task_identifier, dependency_identifier) VALUES ($taskIdentifier, $dependencyIdentifier);";
            insertCommand.Parameters.AddWithValue("$taskIdentifier", task.Identifier);
            insertCommand.Parameters.AddWithValue("$dependencyIdentifier", dependencyIdentifier);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>履歴をトランザクション内へ追加する。</summary>
    private static async Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoryRecord historyRecord,
        CancellationToken cancellationToken)
    {
        // 履歴本文と操作元を改変せず保存する。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO history(
                occurred_at, event_type, task_identifier, project_identifier, summary, details, source)
            VALUES (
                $occurredAt, $eventType, $taskIdentifier, $projectIdentifier, $summary, $details, $source);
            """;
        command.Parameters.AddWithValue("$occurredAt", FormatDate(historyRecord.OccurredAt));
        command.Parameters.AddWithValue("$eventType", historyRecord.EventType);
        command.Parameters.AddWithValue("$taskIdentifier", historyRecord.TaskIdentifier);
        command.Parameters.AddWithValue("$projectIdentifier", historyRecord.ProjectIdentifier);
        command.Parameters.AddWithValue("$summary", historyRecord.Summary);
        command.Parameters.AddWithValue("$details", historyRecord.Details);
        command.Parameters.AddWithValue("$source", historyRecord.Source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>状態値を接続中のトランザクションから取得する。</summary>
    private static async Task<string?> GetStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string stateKey,
        CancellationToken cancellationToken)
    {
        // 存在しないキーはnullとして返す。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state_value FROM application_state WHERE state_key = $stateKey;";
        command.Parameters.AddWithValue("$stateKey", stateKey);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>状態値を接続中のトランザクションへ保存する。</summary>
    private static async Task SetStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stateKey,
        string stateValue,
        CancellationToken cancellationToken)
    {
        // 同じキーがある場合は値だけを更新する。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO application_state(state_key, state_value) VALUES ($stateKey, $stateValue) ON CONFLICT(state_key) DO UPDATE SET state_value = excluded.state_value;";
        command.Parameters.AddWithValue("$stateKey", stateKey);
        command.Parameters.AddWithValue("$stateValue", stateValue);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>保存済みバッチを下書き作成応答へ変換する。</summary>
    private static DraftBatchCreationResult CreateDraftCreationResult(
        DraftBatchRecord draftBatch,
        bool replayed)
    {
        // 互換IDと保存済みタスク対応表を含む統一応答を作成する。
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

    /// <summary>SQLite行を下書きバッチへ変換する。</summary>
    private static DraftBatchRecord ReadDraftBatch(SqliteDataReader reader)
    {
        // バッチ本体と保存後処理状態を列名から復元する。
        string batchIdentifier = reader.GetString(reader.GetOrdinal("identifier"));
        return new DraftBatchRecord
        {
            BatchIdentifier = batchIdentifier,
            Identifier = batchIdentifier,
            Title = reader.GetString(reader.GetOrdinal("title")),
            ParentIdentifier = ReadNullableString(reader, "parent_identifier"),
            IdempotencyKey = ReadNullableString(reader, "request_key"),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at")))!.Value,
            ApprovedAt = ReadNullableDate(reader, "approved_at"),
            Source = reader.GetString(reader.GetOrdinal("source")),
            PostProcessingSucceeded = reader.GetInt32(reader.GetOrdinal("post_processing_succeeded")) != 0,
            Warning = reader.GetString(reader.GetOrdinal("warning"))
        };
    }

    /// <summary>SQLite行をタスクへ変換する。</summary>
    private static ManagedTask ReadTask(SqliteDataReader reader)
    {
        // 列名で値を取得してスキーマ順序への依存を避ける。
        DateTimeOffset createdAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at")))!.Value;
        ManagedTask task = new()
        {
            Identifier = reader.GetString(reader.GetOrdinal("identifier")),
            ProjectIdentifier = ReadNullableString(reader, "project_identifier"),
            Category = reader.GetString(reader.GetOrdinal("category")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Details = reader.GetString(reader.GetOrdinal("details")),
            ParentIdentifier = ReadNullableString(reader, "parent_identifier"),
            Status = reader.GetString(reader.GetOrdinal("status")),
            DeadlineAt = ReadNullableDate(reader, "deadline_at"),
            DeadlineType = reader.GetString(reader.GetOrdinal("deadline_type")),
            DeadlineOrigin = reader.GetString(reader.GetOrdinal("deadline_origin")),
            EstimatedMinutes = reader.GetInt32(reader.GetOrdinal("estimated_minutes")),
            RemainingMinutes = reader.GetInt32(reader.GetOrdinal("remaining_minutes")),
            Importance = reader.GetInt32(reader.GetOrdinal("importance")),
            EarliestStartAt = ReadNullableDate(reader, "earliest_start_at"),
            RequiredContext = reader.GetString(reader.GetOrdinal("required_context")),
            CompletionCondition = reader.GetString(reader.GetOrdinal("completion_condition")),
            Splittable = reader.GetInt32(reader.GetOrdinal("splittable")) != 0,
            AiConfidence = reader.GetDouble(reader.GetOrdinal("ai_confidence")),
            AiReferenceKey = reader.GetString(reader.GetOrdinal("ai_reference_key")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            CreatedAt = createdAt,
            UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))!.Value,
            StartedAt = ReadNullableDate(reader, "started_at"),
            CompletedAt = ReadNullableDate(reader, "completed_at"),
            FollowUpAt = ReadNullableDate(reader, "follow_up_at"),
            FollowUpNotifiedAt = ReadNullableDate(reader, "follow_up_notified_at"),
            DeferralCount = reader.GetInt32(reader.GetOrdinal("deferral_count")),
            ValidationResult = reader.GetString(reader.GetOrdinal("validation_result")),
            SuggestedMinutes = reader.GetInt32(reader.GetOrdinal("suggested_minutes")),
            PriorityScore = reader.GetDouble(reader.GetOrdinal("priority_score")),
            SlackMinutes = reader.IsDBNull(reader.GetOrdinal("slack_minutes")) ? null : reader.GetInt32(reader.GetOrdinal("slack_minutes")),
            RecommendationReason = reader.GetString(reader.GetOrdinal("recommendation_reason")),
            DraftBatchIdentifier = ReadNullableString(reader, "draft_batch_identifier")
        };
        // 旧データの未設定値は登録日時で補完し、経過評価の基準を一貫させる。
        task.EarliestStartAt ??= createdAt;
        return task;
    }

    /// <summary>SQLite行を履歴へ変換する。</summary>
    private static HistoryRecord ReadHistory(SqliteDataReader reader)
    {
        // 履歴画面で必要な列をすべて復元する。
        return new HistoryRecord
        {
            Identifier = reader.GetInt64(reader.GetOrdinal("identifier")),
            OccurredAt = ParseDate(reader.GetString(reader.GetOrdinal("occurred_at")))!.Value,
            EventType = reader.GetString(reader.GetOrdinal("event_type")),
            TaskIdentifier = reader.GetString(reader.GetOrdinal("task_identifier")),
            ProjectIdentifier = reader.GetString(reader.GetOrdinal("project_identifier")),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            Details = reader.GetString(reader.GetOrdinal("details")),
            Source = reader.GetString(reader.GetOrdinal("source"))
        };
    }

    /// <summary>NULL許容文字列を読み取る。</summary>
    private static string? ReadNullableString(SqliteDataReader reader, string columnName)
    {
        // DBNullをC#のnullへ変換する。
        int columnOrdinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(columnOrdinal) ? null : reader.GetString(columnOrdinal);
    }

    /// <summary>NULL許容日時を読み取る。</summary>
    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string columnName)
    {
        // DBNullまたは不正値をnullへ変換する。
        string? dateText = ReadNullableString(reader, columnName);
        return ParseDate(dateText);
    }

    /// <summary>ISO日時文字列をDateTimeOffsetへ変換する。</summary>
    private static DateTimeOffset? ParseDate(string? dateText)
    {
        // タイムゾーン情報を保持したラウンドトリップ形式で解析する。
        return DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsedDate)
            ? parsedDate
            : null;
    }

    /// <summary>DateTimeOffsetを保存用ISO文字列へ変換する。</summary>
    private static string FormatDate(DateTimeOffset dateValue)
    {
        // ミリ秒とオフセットを失わないラウンドトリップ形式を使用する。
        return dateValue.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>作業ログ用日時をUTCのISO文字列へ変換する。</summary>
    private static string FormatUtcDate(DateTimeOffset dateValue)
    {
        // 自動記録と画面手入力で保存オフセットが混在しないようUTCへ統一する。
        return dateValue.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>NULL許容日時をSQLiteパラメーター値へ変換する。</summary>
    private static object ToDatabaseValue(DateTimeOffset? dateValue)
    {
        // 値がない場合はDBNullを返す。
        return dateValue.HasValue ? FormatDate(dateValue.Value) : DBNull.Value;
    }

    /// <summary>設定文字列を取得する。</summary>
    private static string GetText(IReadOnlyDictionary<string, string> values, string key, string defaultValue)
    {
        // 空文字は既定値へ戻す。
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
    }

    /// <summary>設定整数を取得する。</summary>
    private static int GetInteger(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        // 不正な整数は既定値へ戻す。
        return values.TryGetValue(key, out string? value) && int.TryParse(value, CultureInfo.InvariantCulture, out int parsedValue)
            ? parsedValue
            : defaultValue;
    }

    /// <summary>設定小数を取得する。</summary>
    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
    {
        // 不正な小数は既定値へ戻す。
        return values.TryGetValue(key, out string? value) && double.TryParse(value, CultureInfo.InvariantCulture, out double parsedValue)
            ? parsedValue
            : defaultValue;
    }

    /// <summary>設定真偽値を取得する。</summary>
    private static bool GetBoolean(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        // 不正な真偽値は既定値へ戻す。
        return values.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsedValue)
            ? parsedValue
            : defaultValue;
    }
}
