using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Services;

namespace TaskManager.Tests;

/// <summary>追加バックアップの保持・復元・失敗時動作を実SQLiteで検証する。</summary>
public static class ExternalBackupTests
{
    /// <summary>独立した一時DBで世代管理と復元可能性を確認する。</summary>
    public static async Task VerifyAsync()
    {
        // 実データへ接続せず、正本と別フォルダの一時領域だけを使用する。
        string directory = Path.Combine(Path.GetTempPath(), $"UniToDo-Backup-Test-{Guid.NewGuid():N}");
        string? originalDirectory = Environment.GetEnvironmentVariable("TASKMANAGER_DATA_DIR");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", Path.Combine(directory, "source"));
            TaskManagerPaths paths = new();
            DatabaseInitializer initializer = new(paths);
            await initializer.InitializeAsync();
            TaskRepository repository = new(initializer);
            ExternalBackupService backup = new(repository, initializer, paths, NullLogger<ExternalBackupService>.Instance);
            TaskManagerSettings settings = await repository.GetSettingsAsync();
            Assert(!settings.ExternalBackupEnabled && settings.ExternalBackupDirectory.Length == 0, "初期状態がOFF・保存先未指定ではありません。");
            settings.ExternalBackupDirectory = Path.Combine(directory, "destination");
            await repository.SaveSettingsAsync(settings, "test");
            DateTimeOffset currentTime = new(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(9));
            await backup.RunAsync(currentTime, force: true);
            Assert(!Directory.Exists(settings.ExternalBackupDirectory), "OFFで保存先を作成しました。");

            // 不正パスを拒否し、通常パスは設定の往復で保持する。
            settings.ExternalBackupEnabled = true;
            foreach (string invalidPath in new[] { "", "relative", paths.DataDirectory, paths.BackupDirectory, @"\\?\C:\backup" })
            {
                TaskManagerSettings invalidSettings = new() { ExternalBackupEnabled = true, ExternalBackupDirectory = invalidPath };
                bool rejected = false;
                try { ExternalBackupService.ValidateSettings(invalidSettings, paths); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "不正な保存先を許可しました。");
            }
            ExternalBackupService.ValidateSettings(settings, paths);
            await repository.SaveSettingsAsync(settings, "test");
            Assert((await repository.GetSettingsAsync()).ExternalBackupEnabled, "ON設定が保持されません。");

            // 開いたWAL接続で確定した最新データが、単独DBのバックアップに含まれることを検証する。
            await using (SqliteConnection activeConnection = initializer.OpenConnection())
            {
                await repository.SaveTaskAsync(new ManagedTask { Identifier = "BACKUP-TEST", Title = "復元確認", Status = TaskConstants.ReadyStatus }, "test", "test");
                ExternalBackupStatus firstStatus = await backup.RunAsync(currentTime);
                Assert(firstStatus.Error.Length == 0 && firstStatus.LastHourlyAt == currentTime && firstStatus.LastDailyAt == currentTime, firstStatus.Error);
            }
            string hourlyDirectory = Path.Combine(settings.ExternalBackupDirectory, "hourly");
            string dailyDirectory = Path.Combine(settings.ExternalBackupDirectory, "daily");
            string firstBackup = Directory.GetFiles(hourlyDirectory, "*.db").Single();
            await VerifyRestoreAsync(firstBackup, directory);
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", paths.DataDirectory);

            // 再起動・同時実行・1時間境界で不要な世代を増やさない。
            backup = new(repository, initializer, paths, NullLogger<ExternalBackupService>.Instance);
            await Task.WhenAll(backup.RunAsync(currentTime.AddMinutes(59)), backup.RunAsync(currentTime.AddMinutes(59)));
            Assert(Directory.GetFiles(hourlyDirectory, "*.db").Length == 1, "再起動・並行実行で世代が増えました。");
            ExternalBackupStatus unchanged = await backup.RunAsync(currentTime.AddHours(1));
            Assert(unchanged.Error.Length == 0 && unchanged.HourlyResult == "unchanged" && unchanged.LastHourlyAt == currentTime
                && unchanged.LastHourlyCheckedAt == currentTime.AddHours(1), "変更なしで保存時刻が進んだか、確認時刻が更新されません。");
            Assert((await backup.RunAsync(currentTime.AddMinutes(61))).LastCheckedAt == unchanged.LastCheckedAt, "変更なしの後も毎分比較しています。");
            backup = new(repository, initializer, paths, NullLogger<ExternalBackupService>.Instance);
            Assert((await backup.RunAsync(currentTime.AddMinutes(62))).HourlyResult == "unchanged", "再起動で重複保存しました。");
            for (int hour = 2; hour <= 8; hour++)
            {
                await ChangeTitleAsync(initializer, $"時間別 {hour}");
                Assert((await backup.RunAsync(currentTime.AddHours(hour))).Error.Length == 0, "時間別保存が失敗しました。");
            }
            Assert(Directory.GetFiles(hourlyDirectory, "*.db").Length == 6, "時間別6世代になりません。");
            Assert(Directory.GetFiles(dailyDirectory, "*.db").Length == 1, "同日の日別世代が重複しました。");

            // 完成名の衝突で確定に失敗しても、既存6世代の内容を一切変更しない。
            Dictionary<string, byte[]> existingBackups = Directory.GetFiles(hourlyDirectory, "*.db")
                .ToDictionary(file => file, File.ReadAllBytes);
            await ChangeTitleAsync(initializer, "衝突検証");
            ExternalBackupStatus collision = await backup.RunAsync(currentTime.AddHours(8), force: true);
            Assert(collision.Error.Length > 0 && Directory.GetFiles(hourlyDirectory, "*.db").Length == 6, "確定失敗で既存世代を減らしました。");
            Assert(existingBackups.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)), "確定失敗で既存の完成DBを上書きしました。");
            Assert((await backup.RunAsync(currentTime.AddHours(8).AddTicks(1), force: true)).Error.Length == 0, "確定失敗後に再試行できません。");

            // 既存バックアップや管理外ファイルを取り込まず、日別も30世代までに制限する。
            string unrelatedFile = Path.Combine(hourlyDirectory, "unitodo-unrelated.db");
            File.WriteAllText(unrelatedFile, "保持対象外");
            for (int day = 1; day <= 31; day++)
            {
                await ChangeTitleAsync(initializer, $"日別 {day}");
                Assert((await backup.RunAsync(currentTime.AddDays(day))).Error.Length == 0, "日別保存が失敗しました。");
            }
            Assert(Directory.GetFiles(dailyDirectory, "*.db").Length == 30, "日別30世代になりません。");
            Assert(Directory.GetFiles(hourlyDirectory, "*.db").Length == 7 && File.Exists(unrelatedFile), "管理外ファイルを削除しました。");
            Assert(!Directory.GetFiles(settings.ExternalBackupDirectory, "*.partial", SearchOption.AllDirectories).Any(), "未完成ファイルが残っています。");

            // 同じ値の再書込・物理再配置は変更とせず、両区分の既存ファイルをそのまま保持する。
            Dictionary<string, byte[]> preserved = Directory.GetFiles(settings.ExternalBackupDirectory, "*.db", SearchOption.AllDirectories)
                .ToDictionary(file => file, File.ReadAllBytes);
            await ChangeTitleAsync(initializer, "日別 31");
            await using (SqliteConnection connection = initializer.OpenConnection())
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "VACUUM;";
                await command.ExecuteNonQueryAsync();
            }
            ExternalBackupStatus dailyUnchanged = await backup.RunAsync(currentTime.AddDays(32));
            Assert(dailyUnchanged.Error.Length == 0 && dailyUnchanged.HourlyResult == "unchanged" && dailyUnchanged.DailyResult == "unchanged"
                && dailyUnchanged.LastDailyAt == currentTime.AddDays(31) && dailyUnchanged.LastDailyCheckedAt == currentTime.AddDays(32), "日別の変更なしを判定できません。");
            Assert(preserved.Count == Directory.GetFiles(settings.ExternalBackupDirectory, "*.db", SearchOption.AllDirectories).Length
                && preserved.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)), "変更なしで既存世代が変化しました。");
            Assert((await backup.RunAsync(currentTime.AddDays(32).AddMinutes(1), force: true)).HourlyResult == "unchanged", "手動確認で重複保存しました。");

            // 時間別と日別は各々の直近内容と比較し、時間別と同じでも古い日別は更新する。
            await ChangeTitleAsync(initializer, "Case");
            await backup.RunAsync(currentTime.AddDays(32).AddHours(2));
            ExternalBackupStatus independent = await backup.RunAsync(currentTime.AddDays(33));
            Assert(independent.HourlyResult == "unchanged" && independent.DailyResult == "saved", "時間別・日別の比較対象が混在しています。");
            await ChangeTitleAsync(initializer, "case");
            Assert((await backup.RunAsync(currentTime.AddDays(33).AddHours(1))).HourlyResult == "saved", "文字の大文字小文字の変更を見落としました。");

            // 書込失敗で古い世代を失わず、障害解消後の同じ周期で再試行できる。
            string changedDirectory = Path.Combine(directory, "unavailable");
            File.WriteAllText(changedDirectory, "保存先を塞ぐ");
            settings.ExternalBackupDirectory = changedDirectory;
            await repository.SaveSettingsAsync(settings, "test");
            Assert((await backup.GetStatusAsync()).LastHourlyAt is null, "変更前の保存先の成功時刻を表示しました。");
            ExternalBackupStatus failure = await backup.RunAsync(currentTime.AddDays(34));
            Assert(failure.Error.Length > 0 && failure.LastHourlyAt is null, "失敗を成功扱いしました。");
            File.Delete(changedDirectory);
            ExternalBackupStatus recovery = await backup.RunAsync(currentTime.AddDays(34));
            Assert(recovery.Error.Length == 0 && recovery.LastHourlyAt is not null, "同じ周期の再試行が成功しません。");
            Assert(Directory.GetFiles(dailyDirectory, "*.db").Length == 30, "保存先変更で旧世代を削除しました。");
            settings.ExternalBackupEnabled = false;
            await repository.SaveSettingsAsync(settings, "test");
            await backup.RunAsync(currentTime.AddDays(35), force: true);
            Assert(!(await backup.GetStatusAsync()).Enabled && Directory.GetFiles(Path.Combine(changedDirectory, "hourly"), "*.db").Length == 1, "OFFで書込み・削除が発生しました。");
        }
        finally
        {
            // 一時領域の接続を解放し、このテストが作成したディレクトリだけを削除する。
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", originalDirectory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>テスト用DBで単一の値だけを変更し、比較対象の差分を制御する。</summary>
    private static async Task ChangeTitleAsync(DatabaseInitializer initializer, string title)
    {
        // 履歴や日時の付随更新を発生させず、値の変更と同値再書込を検証する。
        await using SqliteConnection connection = initializer.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET title = $title WHERE identifier = 'BACKUP-TEST';";
        command.Parameters.AddWithValue("$title", title);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>バックアップDBだけを復元してアプリの保存層で読み書きできることを確認する。</summary>
    private static async Task VerifyRestoreAsync(string backupPath, string directory)
    {
        // WALや元フォルダをコピーせず、バックアップ単体から新規起動と読書きを検証する。
        string restoredDirectory = Path.Combine(directory, "restored");
        Directory.CreateDirectory(restoredDirectory);
        File.Copy(backupPath, Path.Combine(restoredDirectory, "task-manager.db"));
        Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", restoredDirectory);
        DatabaseInitializer restored = new(new TaskManagerPaths());
        await restored.InitializeAsync();
        TaskRepository repository = new(restored);
        ManagedTask? task = await repository.GetTaskAsync("BACKUP-TEST");
        Assert(task?.Title == "復元確認", "バックアップ単体から最新データを復元できません。");
        task!.Title = "復元後の更新";
        await repository.SaveTaskAsync(task, "test", "test");
        Assert((await repository.GetTaskAsync("BACKUP-TEST"))?.Title == task.Title, "復元後の書込みが失敗しました。");
    }

    /// <summary>条件不成立をテスト失敗として報告する。</summary>
    private static void Assert(bool condition, string message)
    {
        // 呼出元の検証意図をエラーに残す。
        if (!condition) throw new InvalidOperationException(message);
    }
}
