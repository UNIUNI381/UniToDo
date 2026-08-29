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

/// <summary>TypeWhisperの文字起こしセッション結果種別を表す。</summary>
public enum TypeWhisperRecognitionResultKind
{
    Processing,
    Completed,
    NoSpeech,
    Failed
}

/// <summary>TypeWhisperの文字起こしセッション結果を保持する。</summary>
public sealed record TypeWhisperRecognitionResult(
    TypeWhisperRecognitionResultKind Kind,
    string ErrorMessage = "");

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

    /// <summary>TypeWhisperの録音を停止し、文字起こしセッションIDを返す。</summary>
    Task<Guid> StopTypeWhisperRecognitionAsync(CancellationToken cancellationToken);

    /// <summary>TypeWhisperの文字起こしセッション結果を返す。</summary>
    Task<TypeWhisperRecognitionResult> GetTypeWhisperRecognitionResultAsync(
        Guid sessionIdentifier,
        CancellationToken cancellationToken);
}

/// <summary>音声入力の起動待ち時間を保持する。</summary>
public sealed class VoiceInputCoordinatorOptions
{
    // OllamaまたはTypeWhisperの個別準備完了を待つ上限時間を保持する。
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // 準備状況を再確認する間隔を保持する。
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    // 録音停止後に文字起こし結果を待つ上限時間を保持する。
    public TimeSpan RecognitionResultTimeout { get; init; } = TimeSpan.FromMinutes(2);

}

/// <summary>TypeWhisperの準備後に録音を開始し、Ollamaの校正準備を並行実行する。</summary>
public sealed class VoiceInputCoordinator(
    IVoiceInputRuntime voiceInputRuntime,
    VoiceInputCoordinatorOptions coordinatorOptions,
    TimeProvider timeProvider,
    ILogger<VoiceInputCoordinator> logger)
{
    // 音声入力の実行環境を保持する。
    private readonly IVoiceInputRuntime runtime = voiceInputRuntime;

    // 音声入力の待機設定を保持する。
    private readonly VoiceInputCoordinatorOptions options = coordinatorOptions;

    // 待機期限の判定に使う時刻を保持する。
    private readonly TimeProvider clock = timeProvider;

    // 音声入力の障害を記録するログを保持する。
    private readonly ILogger<VoiceInputCoordinator> applicationLogger = logger;

    // 開始または停止処理の単一実行を制御するロックを保持する。
    private readonly SemaphoreSlim executionLock = new(1, 1);

    // Ollama準備処理の共有を制御するロックを保持する。
    private readonly object ollamaPreparationLock = new();

    // 録音開始途中の取消しを制御するロックを保持する。
    private readonly object recognitionStateLock = new();

    // 実行中のOllama準備処理を保持する。
    private Task? ollamaPreparationTask;

    // 実行中のTypeWhisper録音開始処理を取り消すための情報を保持する。
    private CancellationTokenSource? recognitionStartCancellationSource;

    // TypeWhisperへ録音開始要求を送信済みかを保持する。
    private int recognitionStartRequested;

    // 録音開始途中に利用者から停止が要求されたかを保持する。
    private int stopRequestedDuringStart;

    // 最後に確認できたTypeWhisperの録音状態を保持する。
    private int recordingActive;

    // 新しい録音が古い文字起こし結果に表示を上書きされないための世代を保持する。
    private int recognitionGeneration;

    /// <summary>画面へ通知する準備状況の変更を公開する。</summary>
    public event Action<VoiceInputStatus>? StatusChanged;

    /// <summary>TypeWhisperの準備完了後に録音を開始し、Ollamaを並行して準備する。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 開始途中の2回目のF13は準備または開始APIを取り消し、録音停止処理へ移行させる。
        if (!await executionLock.WaitAsync(0, cancellationToken))
        {
            if (TryPrepareRecognitionStartCancellation(
                out CancellationTokenSource cancellationSource,
                out bool recognitionWasRequested))
            {
                string message = recognitionWasRequested
                    ? "音声認識を停止しています…"
                    : "音声入力の準備を取り消しています…";
                PublishStatus(message, VoiceInputStatusKind.Progress);
                CancelRecognitionStart(cancellationSource);
            }
            else
            {
                string message = Volatile.Read(ref recordingActive) == 1
                    ? "音声認識を停止しています…"
                    : "音声入力を準備中です…";
                PublishStatus(message, VoiceInputStatusKind.Progress);
            }
            return;
        }

        try
        {
            // 内部フラグよりTypeWhisper APIの実状態を優先し、Escなど本体側の停止を同期する。
            if (await ResolveTypeWhisperRecordingStateAsync(cancellationToken))
            {
                await StopActiveRecognitionAsync(cancellationToken);
                return;
            }
            await StartNewRecognitionAsync(cancellationToken);
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

    /// <summary>TypeWhisperを必要時に起動し、実録音開始を確認して開始状態を通知する。</summary>
    private async Task StartNewRecognitionAsync(CancellationToken cancellationToken)
    {
        // 2回目のF13だけで開始待ちを取り消せるよう、アプリ終了トークンとは別の取消し元を登録する。
        Interlocked.Increment(ref recognitionGeneration);
        using CancellationTokenSource startCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterRecognitionStart(startCancellation);
        try
        {
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
            await WaitUntilTypeWhisperReadyAsync(startCancellation.Token);
            PublishStatus("TypeWhisperの認識モデルを読み込んでいます…", VoiceInputStatusKind.Progress);
            Volatile.Write(ref recognitionStartRequested, 1);
            Task startRequest = runtime.StartTypeWhisperRecognitionAsync(startCancellation.Token);

            // 開始APIの完了がモデルロードで遅れても、実録音状態を並行確認して表示を先に進める。
            await WaitUntilTypeWhisperRecordingStartsAsync(startRequest, startCancellation.Token);
            if (Volatile.Read(ref stopRequestedDuringStart) == 1)
            {
                startCancellation.Cancel();
                await HandleInterruptedRecognitionStartAsync(cancellationToken);
                return;
            }
            Volatile.Write(ref recordingActive, 1);
            PublishStatus("音声認識を開始しました", VoiceInputStatusKind.Success);

            // API応答の検証も完了させるが、表示とF13停止は実録音状態を正本にする。
            await startRequest;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref stopRequestedDuringStart) == 1)
        {
            // 開始途中の2回目のF13はエラーにせず、録音開始要求の送信有無に応じて安全に停止する。
            await HandleInterruptedRecognitionStartAsync(cancellationToken);
        }
        finally
        {
            ClearRecognitionStart(startCancellation);
        }
    }

    /// <summary>録音中のTypeWhisperを停止し、文字起こしと校正待ちへ進める。</summary>
    private async Task StopActiveRecognitionAsync(CancellationToken cancellationToken)
    {
        // 停止APIの応答だけでなく実録音状態がfalseになるまで確認する。
        PublishStatus("音声認識を停止しています…", VoiceInputStatusKind.Progress);
        Guid? sessionIdentifier = await StopRecognitionOrConfirmStoppedAsync(cancellationToken);
        Volatile.Write(ref recordingActive, 0);
        _ = StartOllamaPreparation(cancellationToken, false);
        PublishStatus("音声認識を停止しました。文字起こしと校正を待っています…", VoiceInputStatusKind.Progress);
        StartRecognitionResultObservation(sessionIdentifier, Volatile.Read(ref recognitionGeneration), cancellationToken);
    }

    /// <summary>開始途中の取消しを録音停止または準備取消しとして完了させる。</summary>
    private async Task HandleInterruptedRecognitionStartAsync(CancellationToken cancellationToken)
    {
        // 開始API送信後は本体で録音が始まっている可能性があるため、停止状態を必ず確認する。
        if (Volatile.Read(ref recognitionStartRequested) == 1)
        {
            Guid? sessionIdentifier = await StopRecognitionOrConfirmStoppedAsync(cancellationToken);
            Volatile.Write(ref recordingActive, 0);
            _ = StartOllamaPreparation(cancellationToken, false);
            PublishStatus("音声認識を停止しました。文字起こしと校正を待っています…", VoiceInputStatusKind.Progress);
            StartRecognitionResultObservation(sessionIdentifier, Volatile.Read(ref recognitionGeneration), cancellationToken);
            return;
        }
        Volatile.Write(ref recordingActive, 0);
        PublishStatus("音声入力の準備を取り消しました", VoiceInputStatusKind.Success);
    }

    /// <summary>停止要求を送り、すでに停止済みの場合も正常な停止として扱う。</summary>
    private async Task<Guid?> StopRecognitionOrConfirmStoppedAsync(CancellationToken cancellationToken)
    {
        // Esc停止や開始取消しとの競合時は、API実状態が停止済みなら停止APIの不成功をエラーにしない。
        bool? recordingBeforeStop = await TryReadTypeWhisperRecordingStateAsync(cancellationToken);
        if (recordingBeforeStop == false)
        {
            return null;
        }
        Guid sessionIdentifier;
        try
        {
            sessionIdentifier = await runtime.StopTypeWhisperRecognitionAsync(cancellationToken);
        }
        catch (Exception stopError) when (IsRecoverableTypeWhisperStatusError(stopError))
        {
            bool? recordingAfterError = await TryReadTypeWhisperRecordingStateAsync(cancellationToken);
            if (recordingAfterError == false)
            {
                return null;
            }
            throw;
        }
        await WaitUntilTypeWhisperRecordingStateAsync(false, cancellationToken);
        return sessionIdentifier;
    }

    /// <summary>録音停止後の文字起こし結果監視をバックグラウンドで開始する。</summary>
    private void StartRecognitionResultObservation(
        Guid? sessionIdentifier,
        int stoppedRecognitionGeneration,
        CancellationToken cancellationToken)
    {
        // API停止前に本体側ですでに停止していた場合は、追跡可能なセッションIDがないため監視しない。
        if (sessionIdentifier is null)
        {
            return;
        }
        _ = ObserveRecognitionResultAsync(
            sessionIdentifier.Value,
            stoppedRecognitionGeneration,
            cancellationToken);
    }

    /// <summary>TypeWhisperの文字起こしセッションを終端状態まで監視する。</summary>
    private async Task ObserveRecognitionResultAsync(
        Guid sessionIdentifier,
        int stoppedRecognitionGeneration,
        CancellationToken cancellationToken)
    {
        // 新しい録音開始後は古い結果で現在の状態表示を上書きしない。
        DateTimeOffset deadline = clock.GetUtcNow() + options.RecognitionResultTimeout;
        while (clock.GetUtcNow() < deadline)
        {
            if (Volatile.Read(ref recognitionGeneration) != stoppedRecognitionGeneration)
            {
                return;
            }

            try
            {
                TypeWhisperRecognitionResult result =
                    await runtime.GetTypeWhisperRecognitionResultAsync(sessionIdentifier, cancellationToken);
                if (Volatile.Read(ref recognitionGeneration) != stoppedRecognitionGeneration)
                {
                    return;
                }
                if (result.Kind == TypeWhisperRecognitionResultKind.Processing)
                {
                    await Task.Delay(options.PollingInterval, clock, cancellationToken);
                    continue;
                }
                if (result.Kind == TypeWhisperRecognitionResultKind.Completed)
                {
                    // 本文ありの完了時は直後に出力プラグインが確認APIを呼ぶため、表示を変更しない。
                    return;
                }
                if (result.Kind == TypeWhisperRecognitionResultKind.NoSpeech)
                {
                    PublishStatus(
                        "音声が検出されなかったため、音声入力を終了しました",
                        VoiceInputStatusKind.Success);
                    return;
                }

                string failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "TypeWhisperが文字起こしを完了できませんでした。"
                    : ShortenError(result.ErrorMessage);
                PublishStatus($"文字起こしを完了できません: {failureMessage}", VoiceInputStatusKind.Error);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception resultError) when (IsRecoverableTypeWhisperStatusError(resultError))
            {
                // 処理中の一時的なAPI障害は期限まで再試行し、無音判定の取りこぼしを避ける。
                applicationLogger.LogDebug(
                    resultError,
                    "TypeWhisper recognition result could not be read temporarily for session {SessionIdentifier}.",
                    sessionIdentifier);
                await Task.Delay(options.PollingInterval, clock, cancellationToken);
            }
        }

        if (Volatile.Read(ref recognitionGeneration) == stoppedRecognitionGeneration)
        {
            PublishStatus(
                "TypeWhisperの文字起こし結果を確認できませんでした。もう一度音声入力をお試しください",
                VoiceInputStatusKind.Error);
        }
    }

    /// <summary>TypeWhisper APIの実録音状態を内部状態へ同期して返す。</summary>
    private async Task<bool> ResolveTypeWhisperRecordingStateAsync(CancellationToken cancellationToken)
    {
        // プロセスがなければ過去の録音フラグを破棄し、未起動からの新規開始へ進める。
        if (!runtime.IsTypeWhisperRunning())
        {
            Volatile.Write(ref recordingActive, 0);
            return false;
        }
        bool? recordingState = await TryReadTypeWhisperRecordingStateAsync(cancellationToken);
        if (recordingState.HasValue)
        {
            Volatile.Write(ref recordingActive, recordingState.Value ? 1 : 0);
            return recordingState.Value;
        }

        // 一時的に状態APIだけ失敗した場合は、最後に確認できた状態を安全側の補助情報として使う。
        return Volatile.Read(ref recordingActive) == 1;
    }

    /// <summary>TypeWhisperの実録音状態を取得し、一時的なAPI障害時は不明を返す。</summary>
    private async Task<bool?> TryReadTypeWhisperRecordingStateAsync(CancellationToken cancellationToken)
    {
        // 利用者取消しは伝播し、起動途中や一時的な不正応答だけを状態不明として扱う。
        try
        {
            return await runtime.IsTypeWhisperRecordingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception statusError) when (IsRecoverableTypeWhisperStatusError(statusError))
        {
            applicationLogger.LogDebug(statusError, "TypeWhisper recording state could not be read temporarily.");
            return null;
        }
    }

    /// <summary>録音開始処理へ専用の取消し元を登録する。</summary>
    private void RegisterRecognitionStart(CancellationTokenSource cancellationSource)
    {
        // F13処理間で参照する開始状態を一つのロック内で初期化する。
        lock (recognitionStateLock)
        {
            recognitionStartCancellationSource = cancellationSource;
            Volatile.Write(ref recognitionStartRequested, 0);
            Volatile.Write(ref stopRequestedDuringStart, 0);
        }
    }

    /// <summary>録音開始途中の2回目のF13から取消し対象と開始要求状態を取得する。</summary>
    private bool TryPrepareRecognitionStartCancellation(
        out CancellationTokenSource cancellationSource,
        out bool recognitionWasRequested)
    {
        // 状態表示後に取消しを実行できるよう、ロック内では停止要求の記録と対象取得だけを行う。
        lock (recognitionStateLock)
        {
            if (recognitionStartCancellationSource is null)
            {
                cancellationSource = null!;
                recognitionWasRequested = false;
                return false;
            }
            cancellationSource = recognitionStartCancellationSource;
            recognitionWasRequested = Volatile.Read(ref recognitionStartRequested) == 1;
            Volatile.Write(ref stopRequestedDuringStart, 1);
        }
        return true;
    }

    /// <summary>取得済みの録音開始処理を安全に取り消す。</summary>
    private static void CancelRecognitionStart(CancellationTokenSource cancellationSource)
    {
        // 状態表示を先に確定し、取消し後の停止完了表示で正しく上書きされる順序を守る。
        try
        {
            cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完了直後の競合では後続のF13がAPI実状態を再確認するため追加処理しない。
        }
    }

    /// <summary>完了した録音開始処理の取消し情報を解除する。</summary>
    private void ClearRecognitionStart(CancellationTokenSource cancellationSource)
    {
        // 後続のF13が古い取消し元へ触れないよう、同じ開始処理の情報だけを消去する。
        lock (recognitionStateLock)
        {
            if (!ReferenceEquals(recognitionStartCancellationSource, cancellationSource))
            {
                return;
            }
            recognitionStartCancellationSource = null;
            Volatile.Write(ref recognitionStartRequested, 0);
            Volatile.Write(ref stopRequestedDuringStart, 0);
        }
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

    /// <summary>開始APIの失敗を監視しながらTypeWhisperの実録音開始を待つ。</summary>
    private async Task WaitUntilTypeWhisperRecordingStartsAsync(
        Task startRequest,
        CancellationToken cancellationToken)
    {
        // モデルロードで開始APIの応答が遅れても、録音状態を一定間隔で先に確認する。
        DateTimeOffset deadline = clock.GetUtcNow().Add(options.ReadinessTimeout);
        while (clock.GetUtcNow() < deadline)
        {
            if (startRequest.IsFaulted || startRequest.IsCanceled)
            {
                await startRequest;
            }
            if (await runtime.IsTypeWhisperRecordingAsync(cancellationToken))
            {
                return;
            }
            await Task.Delay(options.PollingInterval, clock, cancellationToken);
        }
        throw new TimeoutException($"TypeWhisperの録音開始を{options.ReadinessTimeout.TotalSeconds:0}秒以内に確認できませんでした。");
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

    /// <summary>TypeWhisperの実状態再確認で回復できる通信・応答エラーかを返す。</summary>
    private static bool IsRecoverableTypeWhisperStatusError(Exception error)
    {
        // API起動途中の通信失敗と競合時の不正状態だけを再確認対象に限定する。
        return error is HttpRequestException or InvalidOperationException;
    }
}
