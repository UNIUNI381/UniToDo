using TaskManager.Domain;

namespace TaskManager.Configuration;

/// <summary>アプリケーションが利用する保存先を一元管理する。</summary>
public sealed class TaskManagerPaths
{
    // 本番データと認証・ログ関連ファイルの絶対パスを保持する。
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string BackupDirectory { get; }
    public string CredentialPath { get; }
    public string TokenDirectory { get; }
    public string LogDirectory { get; }

    /// <summary>環境変数またはユーザー領域から保存先を決定する。</summary>
    public TaskManagerPaths()
    {
        // テスト時だけ環境変数で保存先を差し替えられるようにする。
        string? configuredDirectory = Environment.GetEnvironmentVariable("TASKMANAGER_DATA_DIR");
        string localApplicationDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = Path.GetFullPath(configuredDirectory ?? Path.Combine(localApplicationDirectory, "TaskManager"));
        DatabasePath = Path.Combine(DataDirectory, "task-manager.db");
        BackupDirectory = Path.Combine(DataDirectory, "backups");
        CredentialPath = Path.Combine(DataDirectory, "credentials.json");
        TokenDirectory = Path.Combine(DataDirectory, "tokens");
        LogDirectory = Path.Combine(DataDirectory, "logs");
    }

    /// <summary>アプリケーションが利用するディレクトリを作成する。</summary>
    public void EnsureDirectories()
    {
        // 必要な保存先を起動時にまとめて準備する。
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(TokenDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>ローカルWeb画面のURLを返す。</summary>
    public static string GetLocalAddress()
    {
        // CLIとトレイ画面で同じ接続先を利用する。
        return TaskConstants.LocalAddress;
    }
}
