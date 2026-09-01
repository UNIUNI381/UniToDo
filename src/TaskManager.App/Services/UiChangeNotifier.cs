namespace TaskManager.Services;

/// <summary>画面へ通知するデータ変更を複数の接続へ配信する。</summary>
public sealed class UiChangeNotifier
{
    // 最新変更と待機中の画面を同期する状態を保持する。
    private readonly object changeLock = new();
    private long currentVersion;
    private UiChangeNotification? latestNotification;
    private TaskCompletionSource<UiChangeNotification> changeCompletion = CreateChangeCompletion();

    /// <summary>現在までに発行した変更通知の連番を返す。</summary>
    public long CurrentVersion
    {
        get
        {
            // 読み取り中に新しい通知と連番が食い違わないよう同じロックを使う。
            lock (changeLock)
            {
                return currentVersion;
            }
        }
    }

    /// <summary>データ変更を新しい連番で全待機接続へ通知する。</summary>
    public UiChangeNotification Publish(string source, string clientIdentifier)
    {
        // 新しい待機先へ切り替えてから以前の待機接続をまとめて再開する。
        TaskCompletionSource<UiChangeNotification> completedChange;
        UiChangeNotification notification;
        lock (changeLock)
        {
            currentVersion += 1;
            notification = new UiChangeNotification(currentVersion, source, clientIdentifier);
            latestNotification = notification;
            completedChange = changeCompletion;
            changeCompletion = CreateChangeCompletion();
        }
        completedChange.TrySetResult(notification);
        return notification;
    }

    /// <summary>指定連番より新しい変更が発生するまで待機する。</summary>
    public Task<UiChangeNotification> WaitForChangeAsync(
        long observedVersion,
        CancellationToken cancellationToken)
    {
        // 待機開始前に発行済みなら最新通知を直ちに返し、取りこぼしを防ぐ。
        Task<UiChangeNotification> changeTask;
        lock (changeLock)
        {
            if (currentVersion > observedVersion && latestNotification is not null)
            {
                return Task.FromResult(latestNotification);
            }
            changeTask = changeCompletion.Task;
        }
        return changeTask.WaitAsync(cancellationToken);
    }

    /// <summary>非同期継続をロック外で実行する変更完了通知を作成する。</summary>
    private static TaskCompletionSource<UiChangeNotification> CreateChangeCompletion()
    {
        // 通知発行処理を待機側の処理時間から分離する。
        return new TaskCompletionSource<UiChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>画面へ配信するデータ変更の識別情報を表す。</summary>
public sealed record UiChangeNotification(long Version, string Source, string ClientIdentifier);
