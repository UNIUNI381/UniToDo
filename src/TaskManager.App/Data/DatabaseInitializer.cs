using Microsoft.Data.Sqlite;
using TaskManager.Configuration;
using TaskManager.Domain;

namespace TaskManager.Data;

/// <summary>SQLiteの接続設定とスキーマ更新を管理する。</summary>
public sealed class DatabaseInitializer(TaskManagerPaths taskManagerPaths)
{
    // データベース保存先を保持する。
    private readonly TaskManagerPaths paths = taskManagerPaths;
    private static readonly object ProviderLock = new();
    private static bool providerInitialized;

    /// <summary>SQLite接続を作成して共通設定を適用する。</summary>
    public SqliteConnection OpenConnection()
    {
        // 同時操作に耐えられるよう共有キャッシュと待機時間を設定する。
        EnsureSqliteProvider();
        SqliteConnectionStringBuilder connectionBuilder = new()
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        SqliteConnection connection = new(connectionBuilder.ToString());
        connection.Open();
        using SqliteCommand configurationCommand = connection.CreateCommand();
        configurationCommand.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        configurationCommand.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Windows内蔵SQLiteのプロバイダーを1回だけ初期化する。</summary>
    private static void EnsureSqliteProvider()
    {
        // 脆弱なネイティブSQLiteを同梱せずWindows 10/11のwinsqlite3を利用する。
        lock (ProviderLock)
        {
            if (providerInitialized)
            {
                return;
            }
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            providerInitialized = true;
        }
    }

    /// <summary>初回スキーマと既定設定を作成する。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // ディレクトリと基礎スキーマを準備し、必要な版だけ順番に適用する。
        paths.EnsureDirectories();
        bool existingDatabase = File.Exists(paths.DatabasePath);
        int currentVersion;
        await using (SqliteConnection schemaConnection = OpenConnection())
        {
            await using SqliteCommand schemaCommand = schemaConnection.CreateCommand();
            schemaCommand.CommandText = SchemaSql;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
            currentVersion = await GetCurrentSchemaVersionAsync(schemaConnection, cancellationToken);
        }
        if (currentVersion < 2)
        {
            // 既存データベースの構造変更前にオンラインバックアップを作成する。
            if (existingDatabase)
            {
                await CreateBackupAsync("schema-v2", cancellationToken);
            }
            await ApplyVersionTwoAsync(cancellationToken);
            currentVersion = 2;
        }
        if (currentVersion < 3)
        {
            // 下書きバッチの冪等キー追加前に既存データベースを退避する。
            if (existingDatabase)
            {
                await CreateBackupAsync("schema-v3", cancellationToken);
            }
            await ApplyVersionThreeAsync(cancellationToken);
            currentVersion = 3;
        }
        if (currentVersion < 4)
        {
            // 作業時間ログ表の追加前に既存データベースを退避する。
            if (existingDatabase)
            {
                await CreateBackupAsync("schema-v4", cancellationToken);
            }
            await ApplyVersionFourAsync(cancellationToken);
            currentVersion = 4;
        }
        if (currentVersion < 5)
        {
            // プロジェクト表示色の追加前に既存データベースを退避する。
            if (existingDatabase)
            {
                await CreateBackupAsync("schema-v5", cancellationToken);
            }
            await ApplyVersionFiveAsync(cancellationToken);
        }

        if (currentVersion < 6)
        {
            // 旧同期ログを整理する前に、復元可能なSQLiteバックアップを保存する。
            if (existingDatabase)
            {
                await CreateBackupAsync("schema-v6", cancellationToken);
            }
            await ApplyVersionSixAsync(cancellationToken);
        }

        TaskManagerSettings defaultSettings = new();
        Dictionary<string, string> settingValues = TaskRepository.SerializeSettings(defaultSettings);
        await using SqliteConnection settingsConnection = OpenConnection();
        await using SqliteTransaction transaction = settingsConnection.BeginTransaction();
        foreach (KeyValuePair<string, string> settingValue in settingValues)
        {
            await using SqliteCommand settingCommand = settingsConnection.CreateCommand();
            settingCommand.Transaction = transaction;
            settingCommand.CommandText = "INSERT OR IGNORE INTO settings(setting_key, setting_value) VALUES ($key, $value);";
            settingCommand.Parameters.AddWithValue("$key", settingValue.Key);
            settingCommand.Parameters.AddWithValue("$value", settingValue.Value);
            await settingCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>現在適用済みの最大スキーマ版を取得する。</summary>
    private static async Task<int> GetCurrentSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // 未登録の場合は基礎版として0を返す。
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COALESCE(MAX(version_number), 0) FROM schema_versions;";
        object? versionValue = await versionCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(versionValue, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>プロジェクト管理を追加するスキーマ版2を適用する。</summary>
    private async Task ApplyVersionTwoAsync(CancellationToken cancellationToken)
    {
        // プロジェクト表と既存表の追加列を単一トランザクションで確定する。
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = VersionTwoMigrationSql;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>下書きバッチの冪等性と後処理状態を追加するスキーマ版3を適用する。</summary>
    private async Task ApplyVersionThreeAsync(CancellationToken cancellationToken)
    {
        // 下書きバッチの追加列と一意索引を単一トランザクションで確定する。
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = VersionThreeMigrationSql;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>作業時間ログと長時間警告状態を追加するスキーマ版4を適用する。</summary>
    private async Task ApplyVersionFourAsync(CancellationToken cancellationToken)
    {
        // 作業ログ表と検索・同時実行制約を単一トランザクションで確定する。
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = VersionFourMigrationSql;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>プロジェクトのHSV表示色を追加するスキーマ版5を適用する。</summary>
    private async Task ApplyVersionFiveAsync(CancellationToken cancellationToken)
    {
        // 既存プロジェクトへ淡い固定彩度・明度とランダム色相を一括設定する。
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = VersionFiveMigrationSql;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>旧同期ログを整理してデータベースの空き領域を回収する。</summary>
    private async Task ApplyVersionSixAsync(CancellationToken cancellationToken)
    {
        // 変更内容を持たない旧形式の正常同期ログだけを最新1件に絞る。
        await using SqliteConnection connection = OpenConnection();
        await using (SqliteCommand cleanupCommand = connection.CreateCommand())
        {
            cleanupCommand.CommandText = """
                DELETE FROM history
                WHERE event_type = 'カレンダー同期' AND source = 'システム'
                  AND details = '' AND summary GLOB '[0-9]*件の予定を取得しました'
                  AND identifier <> (
                    SELECT identifier FROM history
                    WHERE event_type = 'カレンダー同期' AND source = 'システム'
                      AND details = '' AND summary GLOB '[0-9]*件の予定を取得しました'
                    ORDER BY occurred_at DESC, identifier DESC LIMIT 1);
                """;
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // VACUUMはトランザクション外で実行し、完了後だけ版を記録して失敗時に再試行する。
        await using SqliteCommand compactCommand = connection.CreateCommand();
        compactCommand.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
        await compactCommand.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = """
            INSERT INTO schema_versions(version_number, applied_at) VALUES (6, CURRENT_TIMESTAMP);
            """;
        await versionCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>SQLiteのオンラインバックアップを作成して古い世代を整理する。</summary>
    public async Task<string> CreateBackupAsync(string reason, CancellationToken cancellationToken = default)
    {
        // 実行中の書き込みと整合するSQLiteバックアップAPIを利用する。
        paths.EnsureDirectories();
        string safeReason = string.Concat(reason.Where(character => char.IsLetterOrDigit(character) || character == '-'));
        string backupPath = Path.Combine(
            paths.BackupDirectory,
            $"task-manager-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{safeReason}.db");
        await using SqliteConnection sourceConnection = OpenConnection();
        SqliteConnectionStringBuilder backupBuilder = new() { DataSource = backupPath };
        await using SqliteConnection backupConnection = new(backupBuilder.ToString());
        await backupConnection.OpenAsync(cancellationToken);
        sourceConnection.BackupDatabase(backupConnection);
        CleanupBackups();
        return backupPath;
    }

    /// <summary>直近30件を残して古いバックアップを削除する。</summary>
    private void CleanupBackups()
    {
        // 作成日時の新しい順に並べて保持上限を適用する。
        FileInfo[] backupFiles = new DirectoryInfo(paths.BackupDirectory)
            .GetFiles("task-manager-*.db")
            .OrderByDescending(backupFile => backupFile.CreationTimeUtc)
            .ToArray();
        foreach (FileInfo obsoleteFile in backupFiles.Skip(30))
        {
            obsoleteFile.Delete();
        }
    }

    // SQLiteスキーマを1回の実行で冪等に作成する。
    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS schema_versions (
            version_number INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        );
        INSERT OR IGNORE INTO schema_versions(version_number, applied_at) VALUES (1, CURRENT_TIMESTAMP);

        CREATE TABLE IF NOT EXISTS tasks (
            identifier TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            title TEXT NOT NULL,
            details TEXT NOT NULL DEFAULT '',
            parent_identifier TEXT NULL,
            status TEXT NOT NULL,
            deadline_at TEXT NULL,
            deadline_type TEXT NOT NULL,
            estimated_minutes INTEGER NOT NULL,
            remaining_minutes INTEGER NOT NULL,
            importance INTEGER NOT NULL,
            earliest_start_at TEXT NULL,
            required_context TEXT NOT NULL DEFAULT '',
            completion_condition TEXT NOT NULL DEFAULT '',
            splittable INTEGER NOT NULL,
            ai_confidence REAL NOT NULL DEFAULT 0,
            ai_reference_key TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            follow_up_at TEXT NULL,
            follow_up_notified_at TEXT NULL,
            deferral_count INTEGER NOT NULL DEFAULT 0,
            validation_result TEXT NOT NULL DEFAULT '',
            suggested_minutes INTEGER NOT NULL DEFAULT 0,
            priority_score REAL NOT NULL DEFAULT 0,
            slack_minutes INTEGER NULL,
            recommendation_reason TEXT NOT NULL DEFAULT '',
            draft_batch_identifier TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS index_tasks_status ON tasks(status);
        CREATE INDEX IF NOT EXISTS index_tasks_parent ON tasks(parent_identifier);
        CREATE INDEX IF NOT EXISTS index_tasks_deadline ON tasks(deadline_at);

        CREATE TABLE IF NOT EXISTS task_dependencies (
            task_identifier TEXT NOT NULL,
            dependency_identifier TEXT NOT NULL,
            PRIMARY KEY(task_identifier, dependency_identifier),
            FOREIGN KEY(task_identifier) REFERENCES tasks(identifier) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS settings (
            setting_key TEXT PRIMARY KEY,
            setting_value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS history (
            identifier INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at TEXT NOT NULL,
            event_type TEXT NOT NULL,
            task_identifier TEXT NOT NULL DEFAULT '',
            summary TEXT NOT NULL DEFAULT '',
            details TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS index_history_occurred ON history(occurred_at DESC);

        CREATE TABLE IF NOT EXISTS calendar_events (
            event_identifier TEXT NOT NULL,
            calendar_identifier TEXT NOT NULL,
            title TEXT NOT NULL DEFAULT '',
            description TEXT NOT NULL DEFAULT '',
            start_at TEXT NOT NULL,
            end_at TEXT NOT NULL,
            location TEXT NOT NULL DEFAULT '',
            is_all_day INTEGER NOT NULL,
            is_busy INTEGER NOT NULL,
            external_updated_at TEXT NULL,
            PRIMARY KEY(event_identifier, calendar_identifier)
        );

        CREATE TABLE IF NOT EXISTS draft_batches (
            identifier TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            parent_identifier TEXT NULL,
            created_at TEXT NOT NULL,
            approved_at TEXT NULL,
            source TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notification_ledger (
            notification_key TEXT PRIMARY KEY,
            notification_type TEXT NOT NULL,
            task_identifier TEXT NOT NULL DEFAULT '',
            notified_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS application_state (
            state_key TEXT PRIMARY KEY,
            state_value TEXT NOT NULL
        );
        """;

    // プロジェクト背景情報とタスク関連付けを追加する版2移行SQL。
    private const string VersionTwoMigrationSql = """
        CREATE TABLE projects (
            identifier TEXT PRIMARY KEY,
            canonical_name TEXT NOT NULL,
            normalized_name TEXT NOT NULL UNIQUE,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX index_projects_status ON projects(status);

        CREATE TABLE project_aliases (
            identifier INTEGER PRIMARY KEY AUTOINCREMENT,
            project_identifier TEXT NOT NULL,
            alias_text TEXT NOT NULL,
            normalized_alias TEXT NOT NULL,
            source TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_used_at TEXT NULL,
            UNIQUE(project_identifier, normalized_alias),
            FOREIGN KEY(project_identifier) REFERENCES projects(identifier) ON DELETE CASCADE
        );
        CREATE INDEX index_project_aliases_normalized ON project_aliases(normalized_alias);

        CREATE TABLE project_context_documents (
            identifier TEXT PRIMARY KEY,
            project_identifier TEXT NOT NULL,
            title TEXT NOT NULL,
            content_markdown TEXT NOT NULL,
            priority INTEGER NOT NULL,
            enabled INTEGER NOT NULL,
            valid_from TEXT NULL,
            valid_until TEXT NULL,
            source TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(project_identifier) REFERENCES projects(identifier) ON DELETE CASCADE
        );
        CREATE INDEX index_project_context_project ON project_context_documents(project_identifier, enabled);

        CREATE TABLE project_deadline_rules (
            project_identifier TEXT PRIMARY KEY,
            weekday INTEGER NOT NULL,
            local_time TEXT NOT NULL,
            deadline_type TEXT NOT NULL,
            enabled INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(project_identifier) REFERENCES projects(identifier) ON DELETE CASCADE
        );

        ALTER TABLE tasks ADD COLUMN project_identifier TEXT NULL
            REFERENCES projects(identifier) ON DELETE SET NULL;
        ALTER TABLE tasks ADD COLUMN deadline_origin TEXT NOT NULL DEFAULT 'none';
        UPDATE tasks
        SET deadline_origin = CASE WHEN deadline_at IS NULL THEN 'none' ELSE 'explicit' END;
        CREATE INDEX index_tasks_project ON tasks(project_identifier);

        ALTER TABLE history ADD COLUMN project_identifier TEXT NOT NULL DEFAULT '';
        CREATE INDEX index_history_project ON history(project_identifier);

        INSERT INTO schema_versions(version_number, applied_at) VALUES (2, CURRENT_TIMESTAMP);
        """;

    // 下書きバッチの再送防止と後処理状態を追加する版3移行SQL。
    private const string VersionThreeMigrationSql = """
        ALTER TABLE draft_batches ADD COLUMN request_key TEXT NULL;
        ALTER TABLE draft_batches ADD COLUMN post_processing_succeeded INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE draft_batches ADD COLUMN warning TEXT NOT NULL DEFAULT '';
        CREATE UNIQUE INDEX index_draft_batches_request_key
            ON draft_batches(request_key)
            WHERE request_key IS NOT NULL;

        INSERT INTO schema_versions(version_number, applied_at) VALUES (3, CURRENT_TIMESTAMP);
        """;

    // 作業時間ログ、確認状態、単一実行制約を追加する版4移行SQL。
    private const string VersionFourMigrationSql = """
        CREATE TABLE time_entries (
            identifier TEXT PRIMARY KEY,
            task_identifier TEXT NULL REFERENCES tasks(identifier) ON DELETE SET NULL,
            project_identifier TEXT NULL REFERENCES projects(identifier) ON DELETE SET NULL,
            title TEXT NOT NULL,
            project_name_snapshot TEXT NOT NULL DEFAULT '',
            start_at TEXT NOT NULL,
            end_at TEXT NULL,
            stop_reason TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL,
            warning_at TEXT NULL,
            next_warning_at TEXT NULL,
            needs_review INTEGER NOT NULL DEFAULT 0,
            review_reason TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            voided_at TEXT NULL,
            active_key INTEGER NOT NULL DEFAULT 1 CHECK(active_key = 1)
        );
        CREATE INDEX index_time_entries_range ON time_entries(start_at, end_at);
        CREATE INDEX index_time_entries_project ON time_entries(project_identifier, start_at);
        CREATE INDEX index_time_entries_task ON time_entries(task_identifier, start_at);
        CREATE INDEX index_time_entries_review ON time_entries(needs_review, start_at);
        CREATE UNIQUE INDEX unique_active_time_entry
            ON time_entries(active_key)
            WHERE end_at IS NULL AND voided_at IS NULL;

        INSERT INTO schema_versions(version_number, applied_at) VALUES (4, CURRENT_TIMESTAMP);
        """;

    // プロジェクトごとのHSV表示色を追加する版5移行SQL。
    private const string VersionFiveMigrationSql = """
        ALTER TABLE projects ADD COLUMN color_hue INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE projects ADD COLUMN color_saturation INTEGER NOT NULL DEFAULT 50;
        ALTER TABLE projects ADD COLUMN color_value INTEGER NOT NULL DEFAULT 78;
        UPDATE projects
        SET color_hue = abs(random() % 360),
            color_saturation = 50,
            color_value = 78;

        INSERT INTO schema_versions(version_number, applied_at) VALUES (5, CURRENT_TIMESTAMP);
        """;
}
