using System.Globalization;
using Microsoft.Data.Sqlite;
using TaskManager.Domain;

namespace TaskManager.Data;

/// <summary>SQLiteに対する作業時間ログの読み書きを提供する。</summary>
public sealed class TimeEntryRepository(DatabaseInitializer databaseInitializer)
{
    // SQLite接続の生成元を保持する。
    private readonly DatabaseInitializer initializer = databaseInitializer;

    /// <summary>指定期間へ重なる作業ログを新しい順で取得する。</summary>
    public async Task<List<TimeEntryRecord>> GetEntriesAsync(
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd,
        string? projectIdentifier = null,
        string? taskIdentifier = null,
        bool includeVoided = false,
        CancellationToken cancellationToken = default)
    {
        // 任意の期間・関連先・無効化条件をSQLへ組み立てる。
        await using SqliteConnection connection = initializer.OpenConnection();
        List<string> conditions = [];
        await using SqliteCommand command = connection.CreateCommand();
        if (rangeStart.HasValue)
        {
            // オフセット表記が異なる日時も同じ絶対時刻として比較する。
            conditions.Add("(end_at IS NULL OR julianday(end_at) > julianday($rangeStart))");
            command.Parameters.AddWithValue("$rangeStart", FormatDate(rangeStart.Value));
        }
        if (rangeEnd.HasValue)
        {
            conditions.Add("julianday(start_at) < julianday($rangeEnd)");
            command.Parameters.AddWithValue("$rangeEnd", FormatDate(rangeEnd.Value));
        }
        if (string.Equals(
            projectIdentifier,
            TimeTrackingConstants.UnassignedProjectIdentifier,
            StringComparison.Ordinal))
        {
            // 未割当用の固定値はDB上のNULLまたは空文字へ変換して検索する。
            conditions.Add("(project_identifier IS NULL OR trim(project_identifier) = '')");
        }
        else if (!string.IsNullOrWhiteSpace(projectIdentifier))
        {
            conditions.Add("project_identifier = $projectIdentifier");
            command.Parameters.AddWithValue("$projectIdentifier", projectIdentifier);
        }
        if (!string.IsNullOrWhiteSpace(taskIdentifier))
        {
            conditions.Add("task_identifier = $taskIdentifier");
            command.Parameters.AddWithValue("$taskIdentifier", taskIdentifier);
        }
        if (!includeVoided)
        {
            conditions.Add("voided_at IS NULL");
        }
        string whereClause = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
        command.CommandText = $"{SelectColumnsSql} {whereClause} ORDER BY julianday(start_at) DESC, identifier DESC;";
        return await ReadEntriesAsync(command, cancellationToken);
    }

    /// <summary>現在実行中の作業ログを取得する。</summary>
    public async Task<TimeEntryRecord?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        // 部分一意索引で最大1件に制限された未終了ログを返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"{SelectColumnsSql} WHERE end_at IS NULL AND voided_at IS NULL LIMIT 1;";
        List<TimeEntryRecord> entries = await ReadEntriesAsync(command, cancellationToken);
        return entries.SingleOrDefault();
    }

    /// <summary>未確認の作業ログを新しい順で取得する。</summary>
    public async Task<List<TimeEntryRecord>> GetNeedsReviewAsync(
        CancellationToken cancellationToken = default)
    {
        // 無効化されていない要確認ログだけをダッシュボード向けに返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumnsSql}
            WHERE needs_review = 1 AND voided_at IS NULL
            ORDER BY julianday(start_at) DESC, identifier DESC;
            """;
        return await ReadEntriesAsync(command, cancellationToken);
    }

    /// <summary>指定IDの作業ログを取得する。</summary>
    public async Task<TimeEntryRecord?> GetByIdentifierAsync(
        string timeEntryIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 無効化済みを含めて識別子完全一致の1件を返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"{SelectColumnsSql} WHERE identifier = $identifier LIMIT 1;";
        command.Parameters.AddWithValue("$identifier", timeEntryIdentifier);
        List<TimeEntryRecord> entries = await ReadEntriesAsync(command, cancellationToken);
        return entries.SingleOrDefault();
    }

    /// <summary>指定区間と重なる有効な作業ログを取得する。</summary>
    public async Task<List<TimeEntryRecord>> FindOverlapsAsync(
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? excludedIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 半開区間として端点が接するだけのログは重複から除外する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumnsSql}
            WHERE voided_at IS NULL
              AND julianday(start_at) < julianday($endAt)
              AND (end_at IS NULL OR julianday(end_at) > julianday($startAt))
              AND ($excludedIdentifier = '' OR identifier <> $excludedIdentifier)
            ORDER BY julianday(start_at), identifier;
            """;
        command.Parameters.AddWithValue("$startAt", FormatDate(startAt));
        command.Parameters.AddWithValue("$endAt", FormatDate(endAt));
        command.Parameters.AddWithValue("$excludedIdentifier", excludedIdentifier ?? string.Empty);
        return await ReadEntriesAsync(command, cancellationToken);
    }

    /// <summary>自由活動または移行復旧用の実行中ログを開始する。</summary>
    public async Task<TimeEntryRecord> StartAsync(
        string? taskIdentifier,
        string? projectIdentifier,
        string title,
        DateTimeOffset startAt,
        DateTimeOffset nextWarningAt,
        bool needsReview,
        string reviewReason,
        string source,
        CancellationToken cancellationToken = default)
    {
        // プロジェクト名をスナップショット化して未終了ログを作成する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        string projectName = await GetProjectNameAsync(connection, transaction, projectIdentifier, cancellationToken);
        TimeEntryRecord timeEntry = new()
        {
            Identifier = $"TIME-{Guid.NewGuid():N}".ToUpperInvariant(),
            TaskIdentifier = string.IsNullOrWhiteSpace(taskIdentifier) ? null : taskIdentifier,
            ProjectIdentifier = string.IsNullOrWhiteSpace(projectIdentifier) ? null : projectIdentifier,
            Title = title.Trim(),
            ProjectNameSnapshot = projectName,
            StartAt = startAt,
            Source = source,
            NextWarningAt = nextWarningAt,
            NeedsReview = needsReview,
            ReviewReason = reviewReason,
            CreatedAt = startAt,
            UpdatedAt = startAt
        };
        await InsertAsync(connection, transaction, timeEntry, cancellationToken);
        await InsertHistoryAsync(
            connection,
            transaction,
            timeEntry,
            needsReview ? "作業ログ復旧" : "自由活動開始",
            needsReview ? reviewReason : string.Empty,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>完了済み作業ログを手入力で追加する。</summary>
    public async Task<TimeEntryRecord> AddManualAsync(
        TimeEntryMutationRequest request,
        string projectName,
        string source,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 正規化済みの入力を履歴と同一トランザクションで保存する。
        TimeEntryRecord timeEntry = new()
        {
            Identifier = $"TIME-{Guid.NewGuid():N}".ToUpperInvariant(),
            TaskIdentifier = string.IsNullOrWhiteSpace(request.TaskIdentifier) ? null : request.TaskIdentifier,
            ProjectIdentifier = string.IsNullOrWhiteSpace(request.ProjectIdentifier) ? null : request.ProjectIdentifier,
            Title = request.Title.Trim(),
            ProjectNameSnapshot = projectName,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
            StopReason = TimeTrackingConstants.ManualStopReason,
            Source = source,
            CreatedAt = currentTime,
            UpdatedAt = currentTime
        };
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await InsertAsync(connection, transaction, timeEntry, cancellationToken);
        await InsertHistoryAsync(connection, transaction, timeEntry, "作業ログ追加", string.Empty, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>既存の完了済み作業ログを更新する。</summary>
    public async Task<TimeEntryRecord> UpdateManualAsync(
        TimeEntryRecord existingEntry,
        TimeEntryMutationRequest request,
        string projectName,
        string source,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 表示・集計項目を更新し、修正済みとして要確認を解除する。
        existingEntry.TaskIdentifier = string.IsNullOrWhiteSpace(request.TaskIdentifier) ? null : request.TaskIdentifier;
        existingEntry.ProjectIdentifier = string.IsNullOrWhiteSpace(request.ProjectIdentifier) ? null : request.ProjectIdentifier;
        existingEntry.Title = request.Title.Trim();
        existingEntry.ProjectNameSnapshot = projectName;
        existingEntry.StartAt = request.StartAt;
        existingEntry.EndAt = request.EndAt;
        existingEntry.StopReason = TimeTrackingConstants.ManualStopReason;
        existingEntry.Source = source;
        existingEntry.WarningAt = null;
        existingEntry.NextWarningAt = null;
        existingEntry.NeedsReview = false;
        existingEntry.ReviewReason = string.Empty;
        existingEntry.UpdatedAt = currentTime;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, existingEntry, cancellationToken);
        await InsertHistoryAsync(connection, transaction, existingEntry, "作業ログ修正", string.Empty, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existingEntry;
    }

    /// <summary>実行中ログと関連タスクの開始基準時刻を同時に更新する。</summary>
    public async Task<TimeEntryRecord> UpdateActiveStartAsync(
        TimeEntryRecord existingEntry,
        ManagedTask? linkedTask,
        DateTimeOffset startAt,
        TimeSpan startTimeDifference,
        string source,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 長時間警告予定を開始時刻の修正差分だけ移動し、既存の延長状態も維持する。
        DateTimeOffset previousStartAt = existingEntry.StartAt;
        existingEntry.StartAt = startAt;
        existingEntry.WarningAt = existingEntry.WarningAt?.Add(startTimeDifference);
        existingEntry.NextWarningAt = existingEntry.NextWarningAt?.Add(startTimeDifference);
        existingEntry.Source = source;
        existingEntry.UpdatedAt = currentTime;

        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, existingEntry, cancellationToken);
        if (linkedTask is not null)
        {
            // タスクカードの経過表示と完了確認予定を作業ログと同じ開始基準へそろえる。
            linkedTask.StartedAt = startAt;
            linkedTask.FollowUpAt = linkedTask.FollowUpAt?.Add(startTimeDifference);
            linkedTask.FollowUpNotifiedAt = null;
            linkedTask.UpdatedAt = currentTime;
            await using SqliteCommand taskCommand = connection.CreateCommand();
            taskCommand.Transaction = transaction;
            taskCommand.CommandText = """
                UPDATE tasks
                SET started_at = $startedAt,
                    follow_up_at = $followUpAt,
                    follow_up_notified_at = NULL,
                    updated_at = $updatedAt
                WHERE identifier = $identifier AND status = $inProgressStatus;
                """;
            taskCommand.Parameters.AddWithValue("$startedAt", FormatDate(linkedTask.StartedAt.Value));
            taskCommand.Parameters.AddWithValue("$followUpAt", ToDatabaseValue(linkedTask.FollowUpAt));
            taskCommand.Parameters.AddWithValue("$updatedAt", FormatDate(linkedTask.UpdatedAt));
            taskCommand.Parameters.AddWithValue("$identifier", linkedTask.Identifier);
            taskCommand.Parameters.AddWithValue("$inProgressStatus", TaskConstants.InProgressStatus);
            int updatedTaskCount = await taskCommand.ExecuteNonQueryAsync(cancellationToken);
            if (updatedTaskCount != 1)
            {
                throw new InvalidOperationException("関連する実行中タスクの開始時刻を更新できませんでした。");
            }
        }
        string historyDetails = $"開始日時を{FormatDate(previousStartAt)}から{FormatDate(startAt)}へ変更しました。";
        await InsertHistoryAsync(
            connection,
            transaction,
            existingEntry,
            "実行中ログ開始時刻修正",
            historyDetails,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existingEntry;
    }

    /// <summary>実行中ログを指定時刻で終了する。</summary>
    public async Task<TimeEntryRecord> StopAsync(
        TimeEntryRecord timeEntry,
        DateTimeOffset endAt,
        string stopReason,
        bool needsReview,
        string reviewReason,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 終了理由と確認状態を更新して履歴を残す。
        timeEntry.EndAt = endAt < timeEntry.StartAt ? timeEntry.StartAt : endAt;
        timeEntry.StopReason = stopReason;
        timeEntry.Source = source;
        timeEntry.WarningAt = null;
        timeEntry.NextWarningAt = null;
        timeEntry.NeedsReview = needsReview;
        timeEntry.ReviewReason = reviewReason;
        timeEntry.UpdatedAt = endAt;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, timeEntry, cancellationToken);
        string eventType = needsReview ? "タイマー自動停止" : "作業ログ停止";
        await InsertHistoryAsync(connection, transaction, timeEntry, eventType, reviewReason, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>長時間警告を記録して自動停止期限を確定する。</summary>
    public async Task<TimeEntryRecord> MarkWarningAsync(
        TimeEntryRecord timeEntry,
        DateTimeOffset warningAt,
        CancellationToken cancellationToken = default)
    {
        // 同じ警告を再送しないよう警告日時をログへ保存する。
        timeEntry.WarningAt = warningAt;
        timeEntry.UpdatedAt = warningAt;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, timeEntry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>実行中ログの長時間警告を解除して1時間延長する。</summary>
    public async Task<TimeEntryRecord> ExtendAsync(
        TimeEntryRecord timeEntry,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 警告状態を消し、固定猶予の60分後を次回確認時刻とする。
        timeEntry.WarningAt = null;
        timeEntry.NextWarningAt = currentTime.AddMinutes(TimeTrackingConstants.WarningGraceMinutes);
        timeEntry.UpdatedAt = currentTime;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, timeEntry, cancellationToken);
        await InsertHistoryAsync(connection, transaction, timeEntry, "長時間タイマー延長", "次の警告を1時間後へ延長しました。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>要確認ログを現在内容のまま確定する。</summary>
    public async Task<TimeEntryRecord> ConfirmAsync(
        TimeEntryRecord timeEntry,
        string source,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 修正不要の明示確認として確認状態だけを解除する。
        timeEntry.NeedsReview = false;
        timeEntry.ReviewReason = string.Empty;
        timeEntry.Source = source;
        timeEntry.UpdatedAt = currentTime;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, timeEntry, cancellationToken);
        await InsertHistoryAsync(connection, transaction, timeEntry, "作業ログ確認", "現在の記録内容で確定しました。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>作業ログを集計対象外として論理的に無効化する。</summary>
    public async Task<TimeEntryRecord> VoidAsync(
        TimeEntryRecord timeEntry,
        string source,
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        // 実行中なら現在時刻で閉じたうえで無効化日時を設定する。
        timeEntry.EndAt ??= currentTime;
        timeEntry.StopReason = TimeTrackingConstants.VoidedStopReason;
        timeEntry.Source = source;
        timeEntry.WarningAt = null;
        timeEntry.NextWarningAt = null;
        timeEntry.VoidedAt = currentTime;
        timeEntry.UpdatedAt = currentTime;
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await UpdateAsync(connection, transaction, timeEntry, cancellationToken);
        await InsertHistoryAsync(connection, transaction, timeEntry, "作業ログ無効化", "集計対象外にしました。", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return timeEntry;
    }

    /// <summary>プロジェクトの正式名称を取得する。</summary>
    public async Task<string> GetProjectNameAsync(
        string? projectIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 未割当は空文字、それ以外は現行の正式名称を返す。
        await using SqliteConnection connection = initializer.OpenConnection();
        return await GetProjectNameAsync(connection, null, projectIdentifier, cancellationToken);
    }

    /// <summary>作業ログ一覧をコマンドから読み取る。</summary>
    private static async Task<List<TimeEntryRecord>> ReadEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        // 全行を型付きログへ変換する。
        List<TimeEntryRecord> entries = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntry(reader));
        }
        return entries;
    }

    /// <summary>SQLite行を作業ログへ変換する。</summary>
    private static TimeEntryRecord ReadEntry(SqliteDataReader reader)
    {
        // NULL許容列を確認しながら全表示・集計項目を復元する。
        return new TimeEntryRecord
        {
            Identifier = reader.GetString(reader.GetOrdinal("identifier")),
            TaskIdentifier = ReadNullableString(reader, "task_identifier"),
            ProjectIdentifier = ReadNullableString(reader, "project_identifier"),
            Title = reader.GetString(reader.GetOrdinal("title")),
            ProjectNameSnapshot = reader.GetString(reader.GetOrdinal("project_name_snapshot")),
            StartAt = ParseDate(reader.GetString(reader.GetOrdinal("start_at"))),
            EndAt = ReadNullableDate(reader, "end_at"),
            StopReason = reader.GetString(reader.GetOrdinal("stop_reason")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            WarningAt = ReadNullableDate(reader, "warning_at"),
            NextWarningAt = ReadNullableDate(reader, "next_warning_at"),
            NeedsReview = reader.GetInt32(reader.GetOrdinal("needs_review")) != 0,
            ReviewReason = reader.GetString(reader.GetOrdinal("review_reason")),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("updated_at"))),
            VoidedAt = ReadNullableDate(reader, "voided_at")
        };
    }

    /// <summary>新しい作業ログを接続中のトランザクションへ追加する。</summary>
    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeEntryRecord timeEntry,
        CancellationToken cancellationToken)
    {
        // すべての永続列を明示して保存する。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO time_entries(
                identifier, task_identifier, project_identifier, title, project_name_snapshot,
                start_at, end_at, stop_reason, source, warning_at, next_warning_at,
                needs_review, review_reason, created_at, updated_at, voided_at, active_key)
            VALUES(
                $identifier, $taskIdentifier, $projectIdentifier, $title, $projectNameSnapshot,
                $startAt, $endAt, $stopReason, $source, $warningAt, $nextWarningAt,
                $needsReview, $reviewReason, $createdAt, $updatedAt, $voidedAt, 1);
            """;
        AddParameters(command, timeEntry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>既存作業ログを接続中のトランザクションで更新する。</summary>
    private static async Task UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeEntryRecord timeEntry,
        CancellationToken cancellationToken)
    {
        // 識別子以外の全永続列を最新内容へ置き換える。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE time_entries
            SET task_identifier = $taskIdentifier,
                project_identifier = $projectIdentifier,
                title = $title,
                project_name_snapshot = $projectNameSnapshot,
                start_at = $startAt,
                end_at = $endAt,
                stop_reason = $stopReason,
                source = $source,
                warning_at = $warningAt,
                next_warning_at = $nextWarningAt,
                needs_review = $needsReview,
                review_reason = $reviewReason,
                created_at = $createdAt,
                updated_at = $updatedAt,
                voided_at = $voidedAt
            WHERE identifier = $identifier;
            """;
        AddParameters(command, timeEntry);
        int updatedCount = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updatedCount != 1)
        {
            throw new KeyNotFoundException($"作業ログが見つかりません: {timeEntry.Identifier}");
        }
    }

    /// <summary>作業ログ保存パラメータをコマンドへ追加する。</summary>
    private static void AddParameters(SqliteCommand command, TimeEntryRecord timeEntry)
    {
        // 日時とNULL許容識別子をSQLite向けの値へ変換する。
        command.Parameters.AddWithValue("$identifier", timeEntry.Identifier);
        command.Parameters.AddWithValue("$taskIdentifier", (object?)timeEntry.TaskIdentifier ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectIdentifier", (object?)timeEntry.ProjectIdentifier ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", timeEntry.Title);
        command.Parameters.AddWithValue("$projectNameSnapshot", timeEntry.ProjectNameSnapshot);
        command.Parameters.AddWithValue("$startAt", FormatDate(timeEntry.StartAt));
        command.Parameters.AddWithValue("$endAt", ToDatabaseValue(timeEntry.EndAt));
        command.Parameters.AddWithValue("$stopReason", timeEntry.StopReason);
        command.Parameters.AddWithValue("$source", timeEntry.Source);
        command.Parameters.AddWithValue("$warningAt", ToDatabaseValue(timeEntry.WarningAt));
        command.Parameters.AddWithValue("$nextWarningAt", ToDatabaseValue(timeEntry.NextWarningAt));
        command.Parameters.AddWithValue("$needsReview", timeEntry.NeedsReview ? 1 : 0);
        command.Parameters.AddWithValue("$reviewReason", timeEntry.ReviewReason);
        command.Parameters.AddWithValue("$createdAt", FormatDate(timeEntry.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(timeEntry.UpdatedAt));
        command.Parameters.AddWithValue("$voidedAt", ToDatabaseValue(timeEntry.VoidedAt));
    }

    /// <summary>プロジェクトIDから正式名称を接続中のトランザクションで取得する。</summary>
    private static async Task<string> GetProjectNameAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? projectIdentifier,
        CancellationToken cancellationToken)
    {
        // プロジェクト未指定は空文字として保存する。
        if (string.IsNullOrWhiteSpace(projectIdentifier))
        {
            return string.Empty;
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_name FROM projects WHERE identifier = $identifier;";
        command.Parameters.AddWithValue("$identifier", projectIdentifier);
        string? projectName = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return projectName ?? throw new KeyNotFoundException($"プロジェクトが見つかりません: {projectIdentifier}");
    }

    /// <summary>作業ログ操作を一般履歴へ追加する。</summary>
    private static async Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeEntryRecord timeEntry,
        string eventType,
        string details,
        CancellationToken cancellationToken)
    {
        // 履歴画面で任意表示できるよう作業ログIDも詳細へ含める。
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO history(
                occurred_at, event_type, task_identifier, project_identifier, summary, details, source)
            VALUES(
                $occurredAt, $eventType, $taskIdentifier, $projectIdentifier, $summary, $details, $source);
            """;
        command.Parameters.AddWithValue("$occurredAt", FormatDate(timeEntry.UpdatedAt));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$taskIdentifier", timeEntry.TaskIdentifier ?? string.Empty);
        command.Parameters.AddWithValue("$projectIdentifier", timeEntry.ProjectIdentifier ?? string.Empty);
        command.Parameters.AddWithValue("$summary", timeEntry.Title);
        command.Parameters.AddWithValue(
            "$details",
            string.IsNullOrWhiteSpace(details)
                ? $"作業ログ: {timeEntry.Identifier}"
                : $"{details} 作業ログ: {timeEntry.Identifier}");
        command.Parameters.AddWithValue("$source", timeEntry.Source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>ISO日時をSQLite保存文字列へ変換する。</summary>
    private static string FormatDate(DateTimeOffset value)
    {
        // 今後の文字列表記を統一しつつ、読込時は既存の任意オフセットも受け入れる。
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>NULL許容日時をSQLite値へ変換する。</summary>
    private static object ToDatabaseValue(DateTimeOffset? value)
    {
        // 未指定はDBNull、それ以外はISO文字列を返す。
        return value.HasValue ? FormatDate(value.Value) : DBNull.Value;
    }

    /// <summary>SQLite文字列を日時へ変換する。</summary>
    private static DateTimeOffset ParseDate(string value)
    {
        // ISO形式をカルチャー非依存で復元する。
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>SQLite行のNULL許容日時を読み取る。</summary>
    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string columnName)
    {
        // NULLは未指定、それ以外はISO日時として返す。
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
    }

    /// <summary>SQLite行のNULL許容文字列を読み取る。</summary>
    private static string? ReadNullableString(SqliteDataReader reader, string columnName)
    {
        // NULLはそのまま、それ以外は文字列として返す。
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // 作業ログ読込で共通利用する列一覧を保持する。
    private const string SelectColumnsSql = """
        SELECT identifier, task_identifier, project_identifier, title, project_name_snapshot,
               start_at, end_at, stop_reason, source, warning_at, next_warning_at,
               needs_review, review_reason, created_at, updated_at, voided_at
        FROM time_entries
        """;
}
