using System.Globalization;
using Microsoft.Data.Sqlite;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>任意フォルダへ整合性確認済みのDBを時間別・日別に保存する。</summary>
public sealed class ExternalBackupService(
    TaskRepository repository,
    DatabaseInitializer initializer,
    TaskManagerPaths paths,
    ILogger<ExternalBackupService> logger)
{
    // 自動処理と手動実行の排他、直近の結果、ファイル名のUTC日時形式を保持する。
    private readonly SemaphoreSlim executionLock = new(1, 1);
    private ExternalBackupStatus latestStatus = new(false, string.Empty, null, null, null, string.Empty);
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    /// <summary>設定を検証し、保存先を絶対パスへ正規化する。</summary>
    public static void ValidateSettings(TaskManagerSettings settings, TaskManagerPaths paths)
    {
        // OFFでは存在確認も接続もせず、未指定のまま保存できる。
        settings.ExternalBackupDirectory = settings.ExternalBackupDirectory?.Trim() ?? string.Empty;
        if (!settings.ExternalBackupEnabled) return;
        string directory = settings.ExternalBackupDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
            || directory.StartsWith(@"\\?\", StringComparison.Ordinal)
            || directory.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("バックアップ先は通常のフォルダの絶対パスで指定してください。");
        }
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataDirectory));
        if (normalizedDirectory.Equals(Path.GetPathRoot(normalizedDirectory), StringComparison.OrdinalIgnoreCase)
            || normalizedDirectory.Equals(dataDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedDirectory.StartsWith(dataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ドライブ直下や正本データ領域ではなく、バックアップ専用フォルダを指定してください。");
        }
        settings.ExternalBackupDirectory = normalizedDirectory;
    }

    /// <summary>保存済み設定に対応する直近の実行結果を返す。</summary>
    public async Task<ExternalBackupStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // 保存先を変更した直後に以前の保存先の成功状態を表示しない。
        TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
        ExternalBackupStatus snapshot = Volatile.Read(ref latestStatus);
        return settings.ExternalBackupEnabled && snapshot.Enabled
            && settings.ExternalBackupDirectory.Equals(snapshot.Directory, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : new(settings.ExternalBackupEnabled, settings.ExternalBackupDirectory, null, null, null, string.Empty);
    }

    /// <summary>期限が来た世代を作成し、失敗時は成功済み世代を残す。</summary>
    public async Task<ExternalBackupStatus> RunAsync(
        DateTimeOffset currentTime, bool force = false, CancellationToken cancellationToken = default)
    {
        // 排他取得後に設定を読み、OFFの間はバックアップ先へアクセスしない。
        await executionLock.WaitAsync(cancellationToken);
        try
        {
            TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
            ExternalBackupStatus status = Volatile.Read(ref latestStatus);
            if (!settings.ExternalBackupEnabled)
            {
                status = new(false, settings.ExternalBackupDirectory, null, null, null, string.Empty);
                Volatile.Write(ref latestStatus, status);
                return status;
            }
            if (!status.Enabled || !status.Directory.Equals(settings.ExternalBackupDirectory, StringComparison.OrdinalIgnoreCase))
            {
                status = new(true, settings.ExternalBackupDirectory, null, null, null, string.Empty);
            }
            try
            {
                ValidateSettings(settings, paths);
                string hourlyDirectory = Path.Combine(settings.ExternalBackupDirectory, "hourly");
                string dailyDirectory = Path.Combine(settings.ExternalBackupDirectory, "daily");
                Directory.CreateDirectory(hourlyDirectory);
                Directory.CreateDirectory(dailyDirectory);
                List<BackupFile> hourlyFiles = ReadBackups(hourlyDirectory);
                List<BackupFile> dailyFiles = ReadBackups(dailyDirectory);
                status = status with { LastHourlyAt = hourlyFiles.FirstOrDefault()?.CreatedAt, LastDailyAt = dailyFiles.FirstOrDefault()?.CreatedAt };

                // 変更なしも確認成功として周期を進め、毎分の再比較を防ぐ。再起動時は保存時刻から再開する。
                DateTimeOffset? hourlyCheckedAt = status.LastHourlyCheckedAt ?? status.LastHourlyAt;
                DateTimeOffset? dailyCheckedAt = status.LastDailyCheckedAt ?? status.LastDailyAt;
                bool hourlyDue = force || status.LastHourlyAt is null || hourlyCheckedAt is null || currentTime - hourlyCheckedAt >= TimeSpan.FromHours(1);
                bool dailyDue = status.LastDailyAt is null || dailyCheckedAt is null || dailyCheckedAt.Value.ToLocalTime().Date < currentTime.ToLocalTime().Date;
                if (hourlyDue)
                {
                    bool saved = await CreateSnapshotAsync(hourlyDirectory, hourlyFiles.FirstOrDefault()?.Path, currentTime, cancellationToken);
                    if (saved) PruneBackups(hourlyDirectory, 6);
                    status = status with { LastHourlyAt = saved ? currentTime : status.LastHourlyAt,
                        LastHourlyCheckedAt = currentTime, HourlyResult = saved ? "saved" : "unchanged" };
                }
                if (dailyDue)
                {
                    bool saved = await CreateSnapshotAsync(dailyDirectory, dailyFiles.FirstOrDefault()?.Path, currentTime, cancellationToken);
                    if (saved) PruneBackups(dailyDirectory, 30);
                    status = status with { LastDailyAt = saved ? currentTime : status.LastDailyAt,
                        LastDailyCheckedAt = currentTime, DailyResult = saved ? "saved" : "unchanged" };
                }

                // 比較していない周期は確認時刻を進めず、既存世代にも触れない。
                status = status with { LastCheckedAt = hourlyDue || dailyDue ? currentTime : status.LastCheckedAt, Error = string.Empty };
            }
            catch (Exception backupError) when (backupError is not OperationCanceledException)
            {
                logger.LogWarning(backupError, "External database backup failed.");
                status = status with { LastCheckedAt = currentTime, Error = backupError.Message };
            }
            Volatile.Write(ref latestStatus, status);
            return status;
        }
        finally
        {
            executionLock.Release();
        }
    }

    /// <summary>正本DBだけを一時ファイルへ複製し、検証してから完成名へ変更する。</summary>
    private async Task<bool> CreateSnapshotAsync(string directory, string? previousPath, DateTimeOffset currentTime, CancellationToken cancellationToken)
    {
        // 過去のバックアップや資格情報は取り込まず、SQLiteオンラインバックアップで整合性を保つ。
        string destination = Path.Combine(directory, $"unitodo-{currentTime.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.db");
        string temporary = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (SqliteConnection source = initializer.OpenConnection())
            await using (SqliteConnection target = new(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
                target.CreateCollation("TASK_IDENTIFIER", StringComparer.OrdinalIgnoreCase.Compare);
                await using SqliteCommand command = target.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("バックアップDBの整合性検証に失敗しました。");
                }
            }
            // 同じ区分の直近世代と論理内容が一致すれば、一時ファイルを回収して保存を省略する。
            if (previousPath is not null && await HasSameContentsAsync(temporary, previousPath, cancellationToken)) return false;
            // SQLiteの接続を閉じてディスクへ反映した後だけ、世代管理対象に加える。
            using (FileStream completedFile = new(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                completedFile.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return true;
        }
        finally
        {
            // 今回作成した未完成ファイルだけを回収し、既存の完成世代には触れない。
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                File.Delete(temporary + suffix);
            }
        }
    }

    /// <summary>DB構造と全テーブルの値を比較し、物理配置や更新カウンターの差を除外する。</summary>
    private static async Task<bool> HasSameContentsAsync(string currentPath, string previousPath, CancellationToken cancellationToken)
    {
        // どちらも完成したスナップショットを読取専用で開き、元DBや比較対象を変更しない。
        await using SqliteConnection current = OpenSnapshot(currentPath);
        await using SqliteConnection previous = OpenSnapshot(previousPath);
        const string schemaQuery = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type COLLATE BINARY, name COLLATE BINARY;";
        if (!await HasSameRowsAsync(current, previous, schemaQuery, cancellationToken)) return false;
        await using SqliteCommand tables = current.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
        List<string> tableNames = [];
        await using (SqliteDataReader reader = await tables.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) tableNames.Add(reader.GetString(0));
        }
        foreach (string tableName in tableNames)
        {
            // 全列で並べて重複行も比較し、ID用の大文字小文字を無視する照合は使用しない。
            string quotedTable = QuoteIdentifier(tableName);
            await using SqliteCommand columns = current.CreateCommand();
            columns.CommandText = $"SELECT * FROM {quotedTable} LIMIT 0;";
            await using SqliteDataReader reader = await columns.ExecuteReaderAsync(cancellationToken);
            string ordering = string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                .Select(column => QuoteIdentifier(reader.GetName(column)) + " COLLATE BINARY"));
            if (!await HasSameRowsAsync(current, previous, $"SELECT * FROM {quotedTable} ORDER BY {ordering};", cancellationToken)) return false;
        }
        return true;
    }

    /// <summary>比較用のSQLite接続に読取専用設定とアプリの照合順序を設定する。</summary>
    private static SqliteConnection OpenSnapshot(string path)
    {
        // 古い世代のファイルを変更せず、比較後にファイルハンドルを確実に解放する。
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        connection.CreateCollation("TASK_IDENTIFIER", StringComparer.OrdinalIgnoreCase.Compare);
        return connection;
    }

    /// <summary>同じ順序で取得した行を型と値の両方で比較する。</summary>
    private static async Task<bool> HasSameRowsAsync(SqliteConnection current, SqliteConnection previous, string query, CancellationToken cancellationToken)
    {
        // 全件をメモリに展開せず、各行のNULL・数値・文字列・バイナリ値を比較する。
        await using SqliteCommand currentCommand = current.CreateCommand();
        await using SqliteCommand previousCommand = previous.CreateCommand();
        currentCommand.CommandText = query;
        previousCommand.CommandText = query;
        await using SqliteDataReader currentReader = await currentCommand.ExecuteReaderAsync(cancellationToken);
        await using SqliteDataReader previousReader = await previousCommand.ExecuteReaderAsync(cancellationToken);
        if (currentReader.FieldCount != previousReader.FieldCount) return false;
        while (await currentReader.ReadAsync(cancellationToken))
        {
            if (!await previousReader.ReadAsync(cancellationToken)) return false;
            for (int column = 0; column < currentReader.FieldCount; column++)
            {
                object currentValue = currentReader.GetValue(column);
                object previousValue = previousReader.GetValue(column);
                bool same = currentValue is byte[] currentBytes && previousValue is byte[] previousBytes
                    ? currentBytes.AsSpan().SequenceEqual(previousBytes) : Equals(currentValue, previousValue);
                if (!same) return false;
            }
        }
        return !await previousReader.ReadAsync(cancellationToken);
    }

    /// <summary>DBから取得した識別子をSQL内で安全に引用する。</summary>
    private static string QuoteIdentifier(string identifier)
    {
        // 名前中の引用符を二重化し、列名やテーブル名をSQLとして解釈させない。
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>この機能が生成する厳密な名前の完成DBだけを新しい順に取得する。</summary>
    private static List<BackupFile> ReadBackups(string directory)
    {
        // 作成日時属性に依存せず、コピーや再起動後もファイル名のUTC日時で判定する。
        List<BackupFile> files = [];
        foreach (string file in Directory.EnumerateFiles(directory, "unitodo-*.db"))
        {
            string timestamp = Path.GetFileNameWithoutExtension(file)["unitodo-".Length..];
            if (DateTimeOffset.TryParseExact(timestamp, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset createdAt))
            {
                files.Add(new(file, createdAt));
            }
        }
        return files.OrderByDescending(file => file.CreatedAt).ToList();
    }

    /// <summary>管理対象の完成DBを指定世代数まで保持する。</summary>
    private static void PruneBackups(string directory, int retentionCount)
    {
        // 管理外ファイルやサブフォルダを削除せず、超過した古い完成DBだけを削除する。
        foreach (BackupFile file in ReadBackups(directory).Skip(retentionCount)) File.Delete(file.Path);
    }

    // 世代ファイルのパスと保存時刻を保持する。
    private sealed record BackupFile(string Path, DateTimeOffset CreatedAt);
}

/// <summary>追加バックアップの設定と直近の確認結果を表す。</summary>
public sealed record ExternalBackupStatus(
    bool Enabled, string Directory, DateTimeOffset? LastHourlyAt, DateTimeOffset? LastDailyAt,
    DateTimeOffset? LastCheckedAt, string Error)
{
    // 区分ごとの確認成功時刻と保存・変更なしの結果を保持する。
    public DateTimeOffset? LastHourlyCheckedAt { get; init; }
    public DateTimeOffset? LastDailyCheckedAt { get; init; }
    public string HourlyResult { get; init; } = string.Empty;
    public string DailyResult { get; init; } = string.Empty;
}
