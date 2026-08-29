using System.Text.Json;
using TaskManager.Configuration;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>異常終了情報をプロセス間で保存し、通知と確認状態を管理する。</summary>
public sealed class SystemIncidentService(TaskManagerPaths paths, TimeProvider timeProvider)
{
    // 保存先、現在時刻、同一プロセス内の排他制御、JSON設定を保持する。
    private readonly TaskManagerPaths applicationPaths = paths;
    private readonly TimeProvider applicationTimeProvider = timeProvider;
    private readonly SemaphoreSlim incidentLock = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    /// <summary>監視プロセスが検出した異常終了を未確認情報として保存する。</summary>
    public async Task<SystemIncidentRecord> RecordFailureAsync(
        int exitCode,
        int restartCount,
        CancellationToken cancellationToken = default)
    {
        // 当日のクラッシュログが存在する場合だけ共有可能なパスとして記録する。
        DateTimeOffset currentTime = applicationTimeProvider.GetLocalNow();
        string crashLogPath = Path.Combine(applicationPaths.LogDirectory, $"crash-{currentTime:yyyy-MM-dd}.log");
        string recoveryLogPath = Path.Combine(applicationPaths.LogDirectory, $"recovery-{currentTime:yyyy-MM-dd}.log");
        SystemIncidentRecord incident = new()
        {
            Identifier = $"INCIDENT-{Guid.NewGuid():N}",
            OccurredAt = currentTime,
            ExitCode = exitCode,
            RestartCount = restartCount,
            Message = "Task Manager本体が異常終了しました。",
            CrashLogPath = File.Exists(crashLogPath) ? crashLogPath : string.Empty,
            RecoveryLogPath = recoveryLogPath
        };
        await SaveAsync(incident, cancellationToken);
        return incident;
    }

    /// <summary>未確認の異常終了情報を返す。</summary>
    public async Task<SystemIncidentRecord?> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        // 確認済みの情報はダッシュボードへ再表示しない。
        SystemIncidentRecord? incident = await ReadAsync(cancellationToken);
        return incident?.AcknowledgedAt is null ? incident : null;
    }

    /// <summary>復旧日時を保存し、初回だけWindows通知対象として返す。</summary>
    public async Task<SystemIncidentRecord?> MarkRecoveredAsync(CancellationToken cancellationToken = default)
    {
        // 復旧後の最初の起動だけ通知し、未確認カード自体は確認操作まで維持する。
        await incidentLock.WaitAsync(cancellationToken);
        try
        {
            SystemIncidentRecord? incident = await ReadWithoutLockAsync(cancellationToken);
            if (incident is null || incident.AcknowledgedAt is not null)
            {
                return null;
            }
            DateTimeOffset currentTime = applicationTimeProvider.GetLocalNow();
            incident.RecoveredAt ??= currentTime;
            bool shouldNotify = incident.NotifiedAt is null;
            incident.NotifiedAt ??= currentTime;
            await SaveWithoutLockAsync(incident, cancellationToken);
            return shouldNotify ? incident : null;
        }
        finally
        {
            incidentLock.Release();
        }
    }

    /// <summary>指定した異常終了情報をユーザー確認済みにする。</summary>
    public async Task<SystemIncidentRecord> AcknowledgeAsync(
        string incidentIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 表示中のIDと最新情報が一致する場合だけ確認日時を保存する。
        await incidentLock.WaitAsync(cancellationToken);
        try
        {
            SystemIncidentRecord? incident = await ReadWithoutLockAsync(cancellationToken);
            if (incident is null
                || !string.Equals(incident.Identifier, incidentIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyNotFoundException("確認するシステムエラー情報が見つかりません。");
            }
            incident.AcknowledgedAt = applicationTimeProvider.GetLocalNow();
            await SaveWithoutLockAsync(incident, cancellationToken);
            return incident;
        }
        finally
        {
            incidentLock.Release();
        }
    }

    /// <summary>異常終了情報を排他制御付きで保存する。</summary>
    private async Task SaveAsync(SystemIncidentRecord incident, CancellationToken cancellationToken)
    {
        // 監視親とアプリ本体の受け渡しファイルを完全なJSONとして書き換える。
        await incidentLock.WaitAsync(cancellationToken);
        try
        {
            await SaveWithoutLockAsync(incident, cancellationToken);
        }
        finally
        {
            incidentLock.Release();
        }
    }

    /// <summary>異常終了情報を排他制御付きで読み取る。</summary>
    private async Task<SystemIncidentRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        // 同一プロセス内の更新途中を読み取らないようロックを共有する。
        await incidentLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadWithoutLockAsync(cancellationToken);
        }
        finally
        {
            incidentLock.Release();
        }
    }

    /// <summary>ロック取得済みの状態で異常終了情報を読み取る。</summary>
    private async Task<SystemIncidentRecord?> ReadWithoutLockAsync(CancellationToken cancellationToken)
    {
        // 保存ファイルがない初回起動では異常終了なしとして扱う。
        string incidentPath = GetIncidentPath();
        if (!File.Exists(incidentPath))
        {
            return null;
        }
        await using FileStream incidentStream = File.OpenRead(incidentPath);
        return await JsonSerializer.DeserializeAsync<SystemIncidentRecord>(incidentStream, jsonOptions, cancellationToken);
    }

    /// <summary>ロック取得済みの状態で異常終了情報を原子的に保存する。</summary>
    private async Task SaveWithoutLockAsync(SystemIncidentRecord incident, CancellationToken cancellationToken)
    {
        // 一時ファイルを完成後に置換し、強制終了時にも途中のJSONを残さない。
        applicationPaths.EnsureDirectories();
        string incidentPath = GetIncidentPath();
        string temporaryPath = $"{incidentPath}.{Environment.ProcessId}.tmp";
        await using (FileStream incidentStream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(incidentStream, incident, jsonOptions, cancellationToken);
            await incidentStream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, incidentPath, true);
    }

    /// <summary>最新の異常終了情報を保存するファイルパスを返す。</summary>
    private string GetIncidentPath()
    {
        // DBが利用できない起動失敗でも保存できるようログ領域を使用する。
        return Path.Combine(applicationPaths.LogDirectory, "latest-system-incident.json");
    }
}
