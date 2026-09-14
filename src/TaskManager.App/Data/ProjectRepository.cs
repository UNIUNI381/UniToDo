using System.Globalization;
using Microsoft.Data.Sqlite;
using TaskManager.Domain;

namespace TaskManager.Data;

/// <summary>SQLiteに対するプロジェクト情報の読み書きを提供する。</summary>
public sealed class ProjectRepository(DatabaseInitializer databaseInitializer)
{
    // SQLite接続の生成元を保持する。
    private readonly DatabaseInitializer initializer = databaseInitializer;

    /// <summary>プロジェクトと別名、背景情報、期限規則を読み込む。</summary>
    public async Task<List<ProjectRecord>> GetProjectsAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        // プロジェクト本体を読み込んでから関連表を一括で結合する。
        await using SqliteConnection connection = initializer.OpenConnection();
        List<ProjectRecord> projects = [];
        await using SqliteCommand projectCommand = connection.CreateCommand();
        projectCommand.CommandText = includeArchived
            ? "SELECT * FROM projects ORDER BY updated_at DESC, identifier;"
            : "SELECT * FROM projects WHERE status <> $archivedStatus ORDER BY updated_at DESC, identifier;";
        if (!includeArchived)
        {
            projectCommand.Parameters.AddWithValue("$archivedStatus", ProjectConstants.ArchivedStatus);
        }
        await using (SqliteDataReader projectReader = await projectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await projectReader.ReadAsync(cancellationToken))
            {
                projects.Add(ReadProject(projectReader));
            }
        }
        if (projects.Count == 0)
        {
            return projects;
        }

        Dictionary<string, ProjectRecord> projectMap = projects.ToDictionary(
            project => project.Identifier,
            StringComparer.OrdinalIgnoreCase);
        await LoadAliasesAsync(connection, projectMap, cancellationToken);
        await LoadContextsAsync(connection, projectMap, cancellationToken);
        await LoadDeadlineRulesAsync(connection, projectMap, cancellationToken);
        return projects;
    }

    /// <summary>指定IDのプロジェクトを関連情報付きで取得する。</summary>
    public async Task<ProjectRecord?> GetProjectAsync(
        string projectIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 対象プロジェクトとその背景・別名・期限規則だけを索引で取得する。
        await using SqliteConnection connection = initializer.OpenConnection();
        ProjectRecord project;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM projects WHERE identifier COLLATE TASK_IDENTIFIER = $identifier LIMIT 1;";
            command.Parameters.AddWithValue("$identifier", projectIdentifier);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            project = ReadProject(reader);
        }
        Dictionary<string, ProjectRecord> projectMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [project.Identifier] = project
        };
        await LoadAliasesAsync(connection, projectMap, cancellationToken, project.Identifier);
        await LoadContextsAsync(connection, projectMap, cancellationToken, project.Identifier);
        await LoadDeadlineRulesAsync(connection, projectMap, cancellationToken, project.Identifier);
        return project;
    }

    /// <summary>プロジェクト本体を追加または更新して履歴を記録する。</summary>
    public async Task SaveProjectAsync(
        ProjectRecord project,
        string eventType,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 本体と履歴を同一トランザクションで確定する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand projectCommand = connection.CreateCommand();
        projectCommand.Transaction = transaction;
        projectCommand.CommandText = """
            INSERT INTO projects(
                identifier, canonical_name, normalized_name, status,
                color_hue, color_saturation, color_value, created_at, updated_at)
            VALUES (
                $identifier, $canonicalName, $normalizedName, $status,
                $colorHue, $colorSaturation, $colorValue, $createdAt, $updatedAt)
            ON CONFLICT(identifier) DO UPDATE SET
                canonical_name = excluded.canonical_name,
                normalized_name = excluded.normalized_name,
                status = excluded.status,
                color_hue = excluded.color_hue,
                color_saturation = excluded.color_saturation,
                color_value = excluded.color_value,
                updated_at = excluded.updated_at;
            """;
        projectCommand.Parameters.AddWithValue("$identifier", project.Identifier);
        projectCommand.Parameters.AddWithValue("$canonicalName", project.CanonicalName);
        projectCommand.Parameters.AddWithValue("$normalizedName", project.NormalizedName);
        projectCommand.Parameters.AddWithValue("$status", project.Status);
        projectCommand.Parameters.AddWithValue("$colorHue", project.ColorHue!.Value);
        projectCommand.Parameters.AddWithValue("$colorSaturation", project.ColorSaturation!.Value);
        projectCommand.Parameters.AddWithValue("$colorValue", project.ColorValue!.Value);
        projectCommand.Parameters.AddWithValue("$createdAt", FormatDate(project.CreatedAt));
        projectCommand.Parameters.AddWithValue("$updatedAt", FormatDate(project.UpdatedAt));
        await projectCommand.ExecuteNonQueryAsync(cancellationToken);
        await InsertHistoryAsync(
            connection,
            transaction,
            eventType,
            project.Identifier,
            project.CanonicalName,
            string.Empty,
            source,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>プロジェクトへ確認済みの別名を追加する。</summary>
    public async Task<ProjectAlias> AddAliasAsync(
        ProjectAlias projectAlias,
        CancellationToken cancellationToken = default)
    {
        // 別名と履歴を同一トランザクションで保存する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand aliasCommand = connection.CreateCommand();
        aliasCommand.Transaction = transaction;
        aliasCommand.CommandText = """
            INSERT INTO project_aliases(
                project_identifier, alias_text, normalized_alias, source, created_at, last_used_at)
            VALUES (
                $projectIdentifier, $aliasText, $normalizedAlias, $source, $createdAt, $lastUsedAt);
            SELECT last_insert_rowid();
            """;
        aliasCommand.Parameters.AddWithValue("$projectIdentifier", projectAlias.ProjectIdentifier);
        aliasCommand.Parameters.AddWithValue("$aliasText", projectAlias.AliasText);
        aliasCommand.Parameters.AddWithValue("$normalizedAlias", projectAlias.NormalizedAlias);
        aliasCommand.Parameters.AddWithValue("$source", projectAlias.Source);
        aliasCommand.Parameters.AddWithValue("$createdAt", FormatDate(projectAlias.CreatedAt));
        aliasCommand.Parameters.AddWithValue(
            "$lastUsedAt",
            projectAlias.LastUsedAt.HasValue ? FormatDate(projectAlias.LastUsedAt.Value) : DBNull.Value);
        projectAlias.Identifier = Convert.ToInt64(
            await aliasCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        await InsertHistoryAsync(
            connection,
            transaction,
            "プロジェクト別名追加",
            projectAlias.ProjectIdentifier,
            projectAlias.AliasText,
            string.Empty,
            projectAlias.Source,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return projectAlias;
    }

    /// <summary>プロジェクトから指定別名を解除する。</summary>
    public async Task RemoveAliasAsync(
        string projectIdentifier,
        long aliasIdentifier,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 所属プロジェクトを条件へ含めて別プロジェクトの別名を誤って解除しない。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand lookupCommand = connection.CreateCommand();
        lookupCommand.Transaction = transaction;
        lookupCommand.CommandText = """
            SELECT alias_text FROM project_aliases
            WHERE identifier = $aliasIdentifier AND project_identifier = $projectIdentifier;
            """;
        lookupCommand.Parameters.AddWithValue("$aliasIdentifier", aliasIdentifier);
        lookupCommand.Parameters.AddWithValue("$projectIdentifier", projectIdentifier);
        string? aliasText = Convert.ToString(
            await lookupCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(aliasText))
        {
            throw new KeyNotFoundException("解除するプロジェクト別名が見つかりません。");
        }
        await using SqliteCommand deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = """
            DELETE FROM project_aliases
            WHERE identifier = $aliasIdentifier AND project_identifier = $projectIdentifier;
            """;
        deleteCommand.Parameters.AddWithValue("$aliasIdentifier", aliasIdentifier);
        deleteCommand.Parameters.AddWithValue("$projectIdentifier", projectIdentifier);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        await InsertHistoryAsync(
            connection,
            transaction,
            "プロジェクト別名解除",
            projectIdentifier,
            aliasText,
            string.Empty,
            source,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>名前解決に利用した別名の最終使用日時を更新する。</summary>
    public async Task TouchAliasAsync(
        long aliasIdentifier,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken = default)
    {
        // 監査履歴を増やさず利用日時だけを軽量更新する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand aliasCommand = connection.CreateCommand();
        aliasCommand.CommandText = """
            UPDATE project_aliases
            SET last_used_at = $usedAt
            WHERE identifier = $aliasIdentifier;
            """;
        aliasCommand.Parameters.AddWithValue("$usedAt", FormatDate(usedAt));
        aliasCommand.Parameters.AddWithValue("$aliasIdentifier", aliasIdentifier);
        await aliasCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>プロジェクト背景情報を追加または更新する。</summary>
    public async Task SaveContextDocumentAsync(
        ProjectContextDocument contextDocument,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        // 本文と履歴を同一トランザクションで確定する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand contextCommand = connection.CreateCommand();
        contextCommand.Transaction = transaction;
        contextCommand.CommandText = """
            INSERT INTO project_context_documents(
                identifier, project_identifier, title, content_markdown, priority, enabled,
                valid_from, valid_until, source, created_at, updated_at)
            VALUES (
                $identifier, $projectIdentifier, $title, $contentMarkdown, $priority, $enabled,
                $validFrom, $validUntil, $source, $createdAt, $updatedAt)
            ON CONFLICT(identifier) DO UPDATE SET
                title = excluded.title,
                content_markdown = excluded.content_markdown,
                priority = excluded.priority,
                enabled = excluded.enabled,
                valid_from = excluded.valid_from,
                valid_until = excluded.valid_until,
                source = excluded.source,
                updated_at = excluded.updated_at;
            """;
        contextCommand.Parameters.AddWithValue("$identifier", contextDocument.Identifier);
        contextCommand.Parameters.AddWithValue("$projectIdentifier", contextDocument.ProjectIdentifier);
        contextCommand.Parameters.AddWithValue("$title", contextDocument.Title);
        contextCommand.Parameters.AddWithValue("$contentMarkdown", contextDocument.ContentMarkdown);
        contextCommand.Parameters.AddWithValue("$priority", contextDocument.Priority);
        contextCommand.Parameters.AddWithValue("$enabled", contextDocument.Enabled ? 1 : 0);
        contextCommand.Parameters.AddWithValue("$validFrom", ToDatabaseValue(contextDocument.ValidFrom));
        contextCommand.Parameters.AddWithValue("$validUntil", ToDatabaseValue(contextDocument.ValidUntil));
        contextCommand.Parameters.AddWithValue("$source", contextDocument.Source);
        contextCommand.Parameters.AddWithValue("$createdAt", FormatDate(contextDocument.CreatedAt));
        contextCommand.Parameters.AddWithValue("$updatedAt", FormatDate(contextDocument.UpdatedAt));
        await contextCommand.ExecuteNonQueryAsync(cancellationToken);
        await InsertHistoryAsync(
            connection,
            transaction,
            eventType,
            contextDocument.ProjectIdentifier,
            contextDocument.Title,
            $"有効={contextDocument.Enabled}, 優先度={contextDocument.Priority}",
            contextDocument.Source,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>プロジェクトの毎週の既定期限を保存する。</summary>
    public async Task SaveDeadlineRuleAsync(
        ProjectDeadlineRule deadlineRule,
        string source,
        CancellationToken cancellationToken = default)
    {
        // 1プロジェクト1規則として本体と履歴を一括更新する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand ruleCommand = connection.CreateCommand();
        ruleCommand.Transaction = transaction;
        ruleCommand.CommandText = """
            INSERT INTO project_deadline_rules(
                project_identifier, weekday, local_time, deadline_type, enabled, created_at, updated_at)
            VALUES (
                $projectIdentifier, $weekday, $localTime, $deadlineType, $enabled, $createdAt, $updatedAt)
            ON CONFLICT(project_identifier) DO UPDATE SET
                weekday = excluded.weekday,
                local_time = excluded.local_time,
                deadline_type = excluded.deadline_type,
                enabled = excluded.enabled,
                updated_at = excluded.updated_at;
            """;
        ruleCommand.Parameters.AddWithValue("$projectIdentifier", deadlineRule.ProjectIdentifier);
        ruleCommand.Parameters.AddWithValue("$weekday", deadlineRule.Weekday);
        ruleCommand.Parameters.AddWithValue("$localTime", deadlineRule.LocalTime);
        ruleCommand.Parameters.AddWithValue("$deadlineType", deadlineRule.DeadlineType);
        ruleCommand.Parameters.AddWithValue("$enabled", deadlineRule.Enabled ? 1 : 0);
        ruleCommand.Parameters.AddWithValue("$createdAt", FormatDate(deadlineRule.CreatedAt));
        ruleCommand.Parameters.AddWithValue("$updatedAt", FormatDate(deadlineRule.UpdatedAt));
        await ruleCommand.ExecuteNonQueryAsync(cancellationToken);
        await InsertHistoryAsync(
            connection,
            transaction,
            "プロジェクト期限規則更新",
            deadlineRule.ProjectIdentifier,
            deadlineRule.Enabled ? $"毎週{deadlineRule.Weekday} {deadlineRule.LocalTime}" : "無効",
            deadlineRule.DeadlineType,
            source,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>全プロジェクトの別名を読み込んで関連付ける。</summary>
    private static async Task LoadAliasesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ProjectRecord> projectMap,
        CancellationToken cancellationToken, string? selectedIdentifier = null)
    {
        // プロジェクトID順で別名を読み込む。
        await using SqliteCommand aliasCommand = connection.CreateCommand();
        aliasCommand.CommandText = selectedIdentifier is null
            ? "SELECT * FROM project_aliases ORDER BY project_identifier, created_at;"
            : "SELECT * FROM project_aliases WHERE project_identifier = $identifier ORDER BY project_identifier, created_at;";
        aliasCommand.Parameters.AddWithValue("$identifier", (object?)selectedIdentifier ?? DBNull.Value);
        await using SqliteDataReader aliasReader = await aliasCommand.ExecuteReaderAsync(cancellationToken);
        while (await aliasReader.ReadAsync(cancellationToken))
        {
            string projectIdentifier = aliasReader.GetString(aliasReader.GetOrdinal("project_identifier"));
            if (projectMap.TryGetValue(projectIdentifier, out ProjectRecord? project))
            {
                project.Aliases.Add(new ProjectAlias
                {
                    Identifier = aliasReader.GetInt64(aliasReader.GetOrdinal("identifier")),
                    ProjectIdentifier = projectIdentifier,
                    AliasText = aliasReader.GetString(aliasReader.GetOrdinal("alias_text")),
                    NormalizedAlias = aliasReader.GetString(aliasReader.GetOrdinal("normalized_alias")),
                    Source = aliasReader.GetString(aliasReader.GetOrdinal("source")),
                    CreatedAt = ParseDate(aliasReader.GetString(aliasReader.GetOrdinal("created_at"))),
                    LastUsedAt = ReadNullableDate(aliasReader, "last_used_at")
                });
            }
        }
    }

    /// <summary>全プロジェクトの背景情報を読み込んで関連付ける。</summary>
    private static async Task LoadContextsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ProjectRecord> projectMap,
        CancellationToken cancellationToken, string? selectedIdentifier = null)
    {
        // 高優先度、新しい更新日時の順で背景情報を読み込む。
        await using SqliteCommand contextCommand = connection.CreateCommand();
        contextCommand.CommandText = """
            SELECT * FROM project_context_documents
            ORDER BY project_identifier, priority DESC, updated_at DESC;
            """;
        if (selectedIdentifier is not null)
        {
            contextCommand.CommandText = "SELECT * FROM project_context_documents WHERE project_identifier = $identifier ORDER BY project_identifier, priority DESC, updated_at DESC;";
            contextCommand.Parameters.AddWithValue("$identifier", selectedIdentifier);
        }
        await using SqliteDataReader contextReader = await contextCommand.ExecuteReaderAsync(cancellationToken);
        while (await contextReader.ReadAsync(cancellationToken))
        {
            string projectIdentifier = contextReader.GetString(contextReader.GetOrdinal("project_identifier"));
            if (projectMap.TryGetValue(projectIdentifier, out ProjectRecord? project))
            {
                project.ContextDocuments.Add(new ProjectContextDocument
                {
                    Identifier = contextReader.GetString(contextReader.GetOrdinal("identifier")),
                    ProjectIdentifier = projectIdentifier,
                    Title = contextReader.GetString(contextReader.GetOrdinal("title")),
                    ContentMarkdown = contextReader.GetString(contextReader.GetOrdinal("content_markdown")),
                    Priority = contextReader.GetInt32(contextReader.GetOrdinal("priority")),
                    Enabled = contextReader.GetInt32(contextReader.GetOrdinal("enabled")) != 0,
                    ValidFrom = ReadNullableDate(contextReader, "valid_from"),
                    ValidUntil = ReadNullableDate(contextReader, "valid_until"),
                    Source = contextReader.GetString(contextReader.GetOrdinal("source")),
                    CreatedAt = ParseDate(contextReader.GetString(contextReader.GetOrdinal("created_at"))),
                    UpdatedAt = ParseDate(contextReader.GetString(contextReader.GetOrdinal("updated_at")))
                });
            }
        }
    }

    /// <summary>全プロジェクトの期限規則を読み込んで関連付ける。</summary>
    private static async Task LoadDeadlineRulesAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, ProjectRecord> projectMap,
        CancellationToken cancellationToken, string? selectedIdentifier = null)
    {
        // プロジェクトごとに最大1件の期限規則を復元する。
        await using SqliteCommand ruleCommand = connection.CreateCommand();
        ruleCommand.CommandText = selectedIdentifier is null
            ? "SELECT * FROM project_deadline_rules ORDER BY project_identifier;"
            : "SELECT * FROM project_deadline_rules WHERE project_identifier = $identifier;";
        ruleCommand.Parameters.AddWithValue("$identifier", (object?)selectedIdentifier ?? DBNull.Value);
        await using SqliteDataReader ruleReader = await ruleCommand.ExecuteReaderAsync(cancellationToken);
        while (await ruleReader.ReadAsync(cancellationToken))
        {
            string projectIdentifier = ruleReader.GetString(ruleReader.GetOrdinal("project_identifier"));
            if (projectMap.TryGetValue(projectIdentifier, out ProjectRecord? project))
            {
                project.DeadlineRule = new ProjectDeadlineRule
                {
                    ProjectIdentifier = projectIdentifier,
                    Weekday = ruleReader.GetInt32(ruleReader.GetOrdinal("weekday")),
                    LocalTime = ruleReader.GetString(ruleReader.GetOrdinal("local_time")),
                    DeadlineType = ruleReader.GetString(ruleReader.GetOrdinal("deadline_type")),
                    Enabled = ruleReader.GetInt32(ruleReader.GetOrdinal("enabled")) != 0,
                    CreatedAt = ParseDate(ruleReader.GetString(ruleReader.GetOrdinal("created_at"))),
                    UpdatedAt = ParseDate(ruleReader.GetString(ruleReader.GetOrdinal("updated_at")))
                };
            }
        }
    }

    /// <summary>SQLite行をプロジェクトへ変換する。</summary>
    private static ProjectRecord ReadProject(SqliteDataReader reader)
    {
        // 列名で値を取得してスキーマ順序への依存を避ける。
        return new ProjectRecord
        {
            Identifier = reader.GetString(reader.GetOrdinal("identifier")),
            CanonicalName = reader.GetString(reader.GetOrdinal("canonical_name")),
            NormalizedName = reader.GetString(reader.GetOrdinal("normalized_name")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            ColorHue = reader.GetInt32(reader.GetOrdinal("color_hue")),
            ColorSaturation = reader.GetInt32(reader.GetOrdinal("color_saturation")),
            ColorValue = reader.GetInt32(reader.GetOrdinal("color_value")),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    /// <summary>プロジェクト変更履歴をトランザクション内へ追加する。</summary>
    private static async Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        string projectIdentifier,
        string summary,
        string details,
        string source,
        CancellationToken cancellationToken)
    {
        // タスクIDを空にしてプロジェクトIDを明示的に保存する。
        await using SqliteCommand historyCommand = connection.CreateCommand();
        historyCommand.Transaction = transaction;
        historyCommand.CommandText = """
            INSERT INTO history(
                occurred_at, event_type, task_identifier, project_identifier, summary, details, source)
            VALUES (
                $occurredAt, $eventType, '', $projectIdentifier, $summary, $details, $source);
            """;
        historyCommand.Parameters.AddWithValue("$occurredAt", FormatDate(DateTimeOffset.Now));
        historyCommand.Parameters.AddWithValue("$eventType", eventType);
        historyCommand.Parameters.AddWithValue("$projectIdentifier", projectIdentifier);
        historyCommand.Parameters.AddWithValue("$summary", summary);
        historyCommand.Parameters.AddWithValue("$details", details);
        historyCommand.Parameters.AddWithValue("$source", source);
        await historyCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>NULL許容日時をSQLiteパラメーター値へ変換する。</summary>
    private static object ToDatabaseValue(DateTimeOffset? dateValue)
    {
        // 値がない場合はDBNullを返す。
        return dateValue.HasValue ? FormatDate(dateValue.Value) : DBNull.Value;
    }

    /// <summary>SQLiteのNULL許容日時を読み取る。</summary>
    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string columnName)
    {
        // DBNullの場合は値なしとして返す。
        int columnOrdinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(columnOrdinal)
            ? null
            : ParseDate(reader.GetString(columnOrdinal));
    }

    /// <summary>ISO日時文字列をDateTimeOffsetへ変換する。</summary>
    private static DateTimeOffset ParseDate(string dateText)
    {
        // DB内の日時をラウンドトリップ形式で復元する。
        return DateTimeOffset.Parse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>DateTimeOffsetを保存用ISO文字列へ変換する。</summary>
    private static string FormatDate(DateTimeOffset dateValue)
    {
        // ミリ秒とオフセットを失わない形式を使用する。
        return dateValue.ToString("O", CultureInfo.InvariantCulture);
    }
}
