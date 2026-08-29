namespace TaskManager.Services;

/// <summary>音声入力の準備状況を表す。</summary>
public enum VoiceInputStatusKind
{
    Progress,
    Success,
    Error
}

/// <summary>利用者へ表示する音声入力の準備状況を保持する。</summary>
public sealed record VoiceInputStatus(
    string Message,
    VoiceInputStatusKind Kind,
    string Heading = "",
    bool IsDetailed = false);

/// <summary>音声入力に必要なローカルアプリとTypeWhisper API操作を抽象化する。</summary>
public interface IVoiceInputRuntime
{
    /// <summary>OllamaのローカルAPIが応答可能かを返す。</summary>
    Task<bool> IsOllamaReadyAsync(CancellationToken cancellationToken);

    /// <summary>Ollamaのプロセスが起動中かを返す。</summary>
    bool IsOllamaRunning();

    /// <summary>Ollamaをループバック限定で起動する。</summary>
    void StartOllama();

    /// <summary>音声校正モデルをOllamaへ事前ロードする。</summary>
    Task LoadOllamaModelAsync(CancellationToken cancellationToken);

    /// <summary>TypeWhisperのプロセスが起動中かを返す。</summary>
    bool IsTypeWhisperRunning();

    /// <summary>TypeWhisperを起動する。</summary>
    void StartTypeWhisper();

    /// <summary>TypeWhisper APIと選択中の認識モデルが録音開始可能かを返す。</summary>
    Task<bool> IsTypeWhisperReadyAsync(CancellationToken cancellationToken);

    /// <summary>TypeWhisperの音声校正ワークフローで録音を開始する。</summary>
    Task StartTypeWhisperRecognitionAsync(CancellationToken cancellationToken);

    /// <summary>TypeWhisperが現在録音中かを返す。</summary>
    Task<bool> IsTypeWhisperRecordingAsync(CancellationToken cancellationToken);

    /// <summary>TypeWhisperの録音を停止する。</summary>
    Task StopTypeWhisperRecognitionAsync(CancellationToken cancellationToken);
}

/// <summary>音声入力の起動待ち時間を保持する。</summary>
public sealed class VoiceInputCoordinatorOptions
{
    // OllamaまたはTypeWhisperの個別準備完了を待つ上限時間を保持する。
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // 準備状況を再確認する間隔を保持する。
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

}

/// <summary>TypeWhisperの準備後に録音を開始し、Ollamaの校正準備を並行実行する。</summary>
public sealed class VoiceInputCoordinator(
    IVoiceInputRuntime voiceInputRuntime,
    VoiceInputCoordinatorOptions coordinatorOptions,
    TimeProvider timeProvider,
    ILogger<VoiceInputCoordinator> logger)
{
    // 実行環境、待機設定、時刻、ログ、単一実行制御、Ollama準備処理、録音状態を保持する。
    private readonly IVoiceInputRuntime runtime = voiceInputRuntime;
    private readonly VoiceInputCoordinatorOptions options = coordinatorOptions;
    private readonly TimeProvider clock = timeProvider;
    private readonly ILogger<VoiceInputCoordinator> applicationLogger = logger;
    private readonly SemaphoreSlim executionLock = new(1, 1);
    private readonly object ollamaPreparationLock = new();
    private Task? ollamaPreparationTask;
    private int recordingActive;

    /// <summary>画面へ通知する準備状況の変更を公開する。</summary>
    public event Action<VoiceInputStatus>? StatusChanged;

    /// <summary>TypeWhisperの準備完了後に録音を開始し、Ollamaを並行して準備する。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 連打時は二重起動せず、既存の準備処理が進行中であることだけを表示する。
        if (!await executionLock.WaitAsync(0, cancellationToken))
        {
            PublishStatus("音声入力を準備中です…", VoiceInputStatusKind.Progress);
            return;
        }

        try
        {
            // 録音中の2回目のF13はAPIで停止し、TypeWhisperの実状態が停止するまで確認する。
            if (Volatile.Read(ref recordingActive) == 1)
            {
                PublishStatus("音声認識を停止しています…", VoiceInputStatusKind.Progress);
                await runtime.StopTypeWhisperRecognitionAsync(cancellationToken);
                await WaitUntilTypeWhisperRecordingStateAsync(false, cancellationToken);
                Volatile.Write(ref recordingActive, 0);
                _ = StartOllamaPreparation(cancellationToken, false);
                PublishStatus("音声認識を停止しました。文字起こしと校正を待っています…", VoiceInputStatusKind.Progress);
                return;
            }

            PublishStatus("音声入力を準備しています…", VoiceInputStatusKind.Progress);
            bool typeWhisperRunning = runtime.IsTypeWhisperRunning();

            // OllamaのAPI起動とモデルロードは待たずに開始し、TypeWhisper準備と並行させる。
            _ = StartOllamaPreparation(cancellationToken, true);
            if (!typeWhisperRunning)
            {
                PublishStatus("TypeWhisperを起動しています…", VoiceInputStatusKind.Progress);
                runtime.StartTypeWhisper();
            }

            PublishStatus("TypeWhisperの準備を待っています…", VoiceInputStatusKind.Progress);
            await WaitUntilTypeWhisperReadyAsync(cancellationToken);
            PublishStatus("TypeWhisperの認識モデルを読み込んでいます…", VoiceInputStatusKind.Progress);
            await runtime.StartTypeWhisperRecognitionAsync(cancellationToken);
            await WaitUntilTypeWhisperRecordingStateAsync(true, cancellationToken);
            Volatile.Write(ref recordingActive, 1);
            PublishStatus("音声認識を開始しました", VoiceInputStatusKind.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // アプリ終了時の取消しは利用者向けエラーとして扱わない。
        }
        catch (Exception startError)
        {
            applicationLogger.LogError(startError, "Voice input could not be started.");
            PublishStatus($"音声入力を操作できません: {ShortenError(startError.Message)}", VoiceInputStatusKind.Error);
        }
        finally
        {
            executionLock.Release();
        }
    }

    /// <summary>TypeWhisperから校正本文を受け取った時点で録音中状態を解除する。</summary>
    public void CompleteRecognition()
    {
        // 自動停止やTypeWhisper側での停止後も次回F13を新規開始として扱う。
        Volatile.Write(ref recordingActive, 0);
    }

    /// <summary>Ollamaの起動と校正モデルのロードを録音処理と独立して開始する。</summary>
    private Task StartOllamaPreparation(CancellationToken cancellationToken, bool refreshCompletedPreparation)
    {
        // 実行中の準備は再利用し、新しい録音開始時だけ完了済みモデルの常駐時間を更新する。
        lock (ollamaPreparationLock)
        {
            bool shouldStartPreparation = ollamaPreparationTask is null
                || ollamaPreparationTask.IsCanceled
                || ollamaPreparationTask.IsFaulted
                || (refreshCompletedPreparation && ollamaPreparationTask.IsCompletedSuccessfully);
            if (shouldStartPreparation)
            {
                ollamaPreparationTask = PrepareOllamaAsync(cancellationToken);
                _ = ObserveOllamaPreparationAsync(ollamaPreparationTask);
            }
            Task currentPreparationTask = ollamaPreparationTask
                ?? throw new InvalidOperationException("Ollama準備処理を開始できませんでした。");
            return currentPreparationTask;
        }
    }

    /// <summary>Ollama APIを必要時に起動し、応答後に校正モデルを事前ロードする。</summary>
    private async Task PrepareOllamaAsync(CancellationToken cancellationToken)
    {
        // 起動済みAPIは再利用し、未起動の場合だけループバック限定サーバーを開始する。
        bool ollamaReady = await runtime.IsOllamaReadyAsync(cancellationToken);
        if (!ollamaReady && !runtime.IsOllamaRunning())
        {
            runtime.StartOllama();
        }
        if (!ollamaReady)
        {
            await WaitUntilOllamaReadyAsync(cancellationToken);
        }
        await runtime.LoadOllamaModelAsync(cancellationToken);
    }

    /// <summary>バックグラウンドのOllama準備失敗を監視してログへ残す。</summary>
    private async Task ObserveOllamaPreparationAsync(Task preparationTask)
    {
        // 未監視例外を発生させず、録音開始を止めない範囲で校正準備の障害を記録する。
        try
        {
            await preparationTask;
        }
        catch (OperationCanceledException)
        {
            // アプリ終了時や次処理の取消しは録音開始側へ通知しない。
        }
        catch (Exception preparationError)
        {
            applicationLogger.LogError(preparationError, "Ollama correction model could not be prepared in background.");
        }
    }

    /// <summary>TypeWhisper APIと選択中の認識モデルが録音開始可能になるまで待つ。</summary>
    private async Task WaitUntilTypeWhisperReadyAsync(CancellationToken cancellationToken)
    {
        // プロセス生存だけで開始せず、API起動と選択モデルのダウンロード済み状態を確認する。
        DateTimeOffset deadline = clock.GetUtcNow().Add(options.ReadinessTimeout);
        while (clock.GetUtcNow() < deadline)
        {
            if (runtime.IsTypeWhisperRunning()
                && await runtime.IsTypeWhisperReadyAsync(cancellationToken))
            {
                return;
            }
            await Task.Delay(options.PollingInterval, clock, cancellationToken);
        }
        throw new TimeoutException($"TypeWhisper APIと選択モデルを{options.ReadinessTimeout.TotalSeconds:0}秒以内に確認できませんでした。");
    }

    /// <summary>TypeWhisperの録音状態が期待値へ変わるまで上限時間待つ。</summary>
    private async Task WaitUntilTypeWhisperRecordingStateAsync(
        bool expectedRecording,
        CancellationToken cancellationToken)
    {
        // API受付だけで成功扱いせず、実際の録音開始または停止を一定間隔で確認する。
        DateTimeOffset deadline = clock.GetUtcNow().Add(options.ReadinessTimeout);
        while (clock.GetUtcNow() < deadline)
        {
            if (await runtime.IsTypeWhisperRecordingAsync(cancellationToken) == expectedRecording)
            {
                return;
            }
            await Task.Delay(options.PollingInterval, clock, cancellationToken);
        }
        string operationName = expectedRecording ? "開始" : "停止";
        throw new TimeoutException($"TypeWhisperの録音{operationName}を{options.ReadinessTimeout.TotalSeconds:0}秒以内に確認できませんでした。");
    }

    /// <summary>Ollama APIが応答するまでバックグラウンドで上限時間待機する。</summary>
    private async Task WaitUntilOllamaReadyAsync(CancellationToken cancellationToken)
    {
        // 起動直後の接続拒否を一定間隔で再確認し、モデルロード要求をAPI準備後に限定する。
        DateTimeOffset deadline = clock.GetUtcNow().Add(options.ReadinessTimeout);
        while (clock.GetUtcNow() < deadline)
        {
            if (await runtime.IsOllamaReadyAsync(cancellationToken))
            {
                return;
            }
            await Task.Delay(options.PollingInterval, clock, cancellationToken);
        }
        throw new TimeoutException($"Ollamaの準備を{options.ReadinessTimeout.TotalSeconds:0}秒以内に確認できませんでした。");
    }

    /// <summary>音声入力の準備状況を購読中の画面へ通知する。</summary>
    private void PublishStatus(string message, VoiceInputStatusKind kind)
    {
        // UI実装へ依存せず、現在の状態だけをイベントで伝える。
        StatusChanged?.Invoke(new VoiceInputStatus(message, kind));
    }

    /// <summary>画面内に収まるよう例外メッセージを短縮する。</summary>
    private static string ShortenError(string message)
    {
        // 改行を除去し、長すぎる詳細は先頭240文字へ制限する。
        string normalizedMessage = message.ReplaceLineEndings(" ").Trim();
        return normalizedMessage.Length > 240 ? normalizedMessage[..240] : normalizedMessage;
    }
}
