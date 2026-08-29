using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TaskManager.Services;

namespace TaskManager.Windows;

/// <summary>Windows上でOllamaとTypeWhisperの起動・API操作を行う。</summary>
public sealed class VoiceInputRuntime(
    HttpClient ollamaClient,
    TypeWhisperApiClient typeWhisperApiClient) : IVoiceInputRuntime
{
    private const string OllamaModelName = "qwen3-typewhisper:latest";

    // OllamaとTypeWhisperのループバックAPIクライアントを保持する。
    private readonly HttpClient client = ollamaClient;
    private readonly TypeWhisperApiClient typeWhisper = typeWhisperApiClient;

    /// <summary>OllamaのローカルAPIが応答可能かを返す。</summary>
    public async Task<bool> IsOllamaReadyAsync(CancellationToken cancellationToken)
    {
        // モデル一覧APIの成功応答をサーバー準備完了として扱い、確認だけは短時間で打ち切る。
        using CancellationTokenSource readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessCancellation.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            using HttpResponseMessage response = await client.GetAsync("api/tags", readinessCancellation.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>音声校正モデルを生成なしでOllamaへ事前ロードする。</summary>
    public async Task LoadOllamaModelAsync(CancellationToken cancellationToken)
    {
        // 空プロンプトのgenerate要求でモデルをGPUへ読み込み、録音後まで5分間維持する。
        string requestBody = JsonSerializer.Serialize(new
        {
            model = OllamaModelName,
            keep_alive = "5m"
        });
        using StringContent requestContent = new(requestBody, new UTF8Encoding(false), "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            "api/generate",
            requestContent,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Ollama校正モデルを読み込めませんでした（HTTP {(int)response.StatusCode}）: {ShortenResponse(responseText)}");
        }
    }

    /// <summary>Ollamaのプロセスが起動中かを返す。</summary>
    public bool IsOllamaRunning()
    {
        // 公式アプリとAPIサーバーのどちらかが存在すれば再起動しない。
        return IsProcessRunning("ollama") || IsProcessRunning("ollama app");
    }

    /// <summary>Ollama APIサーバーをループバック限定で非表示起動する。</summary>
    public void StartOllama()
    {
        // シェルを介さず、今回の子プロセスへだけ安全な待受アドレスを設定する。
        string executablePath = ResolveOllamaExecutable();
        ProcessStartInfo startInformation = new()
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInformation.ArgumentList.Add("serve");
        startInformation.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";
        StartProcess(startInformation, "Ollama");
    }

    /// <summary>TypeWhisperのプロセスが起動中かを返す。</summary>
    public bool IsTypeWhisperRunning()
    {
        // 実行ファイル名で同じWindowsユーザーの既存プロセスを確認する。
        return IsProcessRunning("TypeWhisper");
    }

    /// <summary>TypeWhisperを非アクティブ状態で起動する。</summary>
    public void StartTypeWhisper()
    {
        // current配下の公式ランチャーを使い、更新後も同じパスで追従する。
        string executablePath = ResolveTypeWhisperExecutable();
        ProcessStartInfo startInformation = new()
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        StartProcess(startInformation, "TypeWhisper");
    }

    /// <summary>TypeWhisper APIと選択中の認識モデルが録音開始可能かを返す。</summary>
    public Task<bool> IsTypeWhisperReadyAsync(CancellationToken cancellationToken)
    {
        // API起動と選択モデルのダウンロード済み状態を準備条件として確認する。
        return typeWhisper.IsReadyAsync(cancellationToken);
    }

    /// <summary>TypeWhisperの音声校正ワークフローで録音を開始する。</summary>
    public Task StartTypeWhisperRecognitionAsync(CancellationToken cancellationToken)
    {
        // ホットキーを模擬せず、解決したワークフローIDをAPIへ直接指定する。
        return typeWhisper.StartCorrectionRecordingAsync(cancellationToken);
    }

    /// <summary>TypeWhisperが現在録音中かを返す。</summary>
    public Task<bool> IsTypeWhisperRecordingAsync(CancellationToken cancellationToken)
    {
        // 表示上の状態ではなくTypeWhisper本体が保持する録音状態を取得する。
        return typeWhisper.IsRecordingAsync(cancellationToken);
    }

    /// <summary>TypeWhisperの録音を停止し、文字起こしセッションIDを返す。</summary>
    public Task<Guid> StopTypeWhisperRecognitionAsync(CancellationToken cancellationToken)
    {
        // F15の取りこぼしを避け、現在の録音セッションをAPIで直接停止する。
        return typeWhisper.StopRecordingAsync(cancellationToken);
    }

    /// <summary>TypeWhisperの文字起こしセッション結果を返す。</summary>
    public Task<TypeWhisperRecognitionResult> GetTypeWhisperRecognitionResultAsync(
        Guid sessionIdentifier,
        CancellationToken cancellationToken)
    {
        // 停止APIで得たIDを使い、無音を含む文字起こしの終端状態を取得する。
        return typeWhisper.GetRecognitionResultAsync(sessionIdentifier, cancellationToken);
    }

    /// <summary>指定名のプロセスが1件以上存在するかを返す。</summary>
    private static bool IsProcessRunning(string processName)
    {
        // Process資源を確実に解放しながら存在だけを判定する。
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>公式配置または明示設定からOllama実行ファイルを解決する。</summary>
    private static string ResolveOllamaExecutable()
    {
        // 明示設定、公式ユーザー配置の順に安全な実ファイルを探す。
        string? configuredPath = Environment.GetEnvironmentVariable("OLLAMA_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expandedPath))
            {
                return expandedPath;
            }
        }
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installedPath = Path.Combine(localApplicationData, "Programs", "Ollama", "ollama.exe");
        if (File.Exists(installedPath))
        {
            return installedPath;
        }
        throw new FileNotFoundException("Ollamaが見つかりません。公式Windows版をインストールしてください。", installedPath);
    }

    /// <summary>公式のcurrent配置からTypeWhisper実行ファイルを解決する。</summary>
    private static string ResolveTypeWhisperExecutable()
    {
        // TypeWhisper更新後も維持されるcurrent配下の実行ファイルを確認する。
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string executablePath = Path.Combine(localApplicationData, "TypeWhisper", "current", "TypeWhisper.exe");
        return File.Exists(executablePath)
            ? executablePath
            : throw new FileNotFoundException("TypeWhisperが見つかりません。TypeWhisperをインストールしてください。", executablePath);
    }

    /// <summary>Ollamaのエラー応答を状態画面へ収まる長さに整える。</summary>
    private static string ShortenResponse(string response)
    {
        // 改行を空白へ寄せ、詳細が長い場合は先頭200文字へ制限する。
        string normalizedResponse = response.ReplaceLineEndings(" ").Trim();
        return normalizedResponse.Length > 200 ? normalizedResponse[..200] : normalizedResponse;
    }

    /// <summary>外部アプリを開始し、起動不能なら用途を含む例外を返す。</summary>
    private static void StartProcess(ProcessStartInfo startInformation, string applicationName)
    {
        // 起動直後のProcessハンドルは保持せず、アプリを独立して実行させる。
        using Process? process = Process.Start(startInformation);
        if (process is null)
        {
            throw new InvalidOperationException($"{applicationName}を起動できませんでした。");
        }
    }

}
