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

                // 直近成功から1時間経過した場合と、当日の保存がない場合だけ正本を複製する。
                bool hourlyDue = force || status.LastHourlyAt is null || currentTime - status.LastHourlyAt >= TimeSpan.FromHours(1);
                bool dailyDue = status.LastDailyAt is null || status.LastDailyAt.Value.ToLocalTime().Date < currentTime.ToLocalTime().Date;
                if (hourlyDue)
                {
                    await CreateSnapshotAsync(hourlyDirectory, currentTime, cancellationToken);
                    status = status with { LastHourlyAt = currentTime };
                }
                if (dailyDue)
                {
                    await CreateSnapshotAsync(dailyDirectory, currentTime, cancellationToken);
                    status = status with { LastDailyAt = currentTime };
                }

                // 完成したファイルのみを対象に、時間別6世代・日別30世代へ整理する。
                PruneBackups(hourlyDirectory, 6);
                PruneBackups(dailyDirectory, 30);
                status = status with { LastCheckedAt = currentTime, Error = string.Empty };
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
    private async Task CreateSnapshotAsync(string directory, DateTimeOffset currentTime, CancellationToken cancellationToken)
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
            // SQLiteの接続を閉じてディスクへ反映した後だけ、世代管理対象に加える。
            using (FileStream completedFile = new(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                completedFile.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
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
    DateTimeOffset? LastCheckedAt, string Error);
