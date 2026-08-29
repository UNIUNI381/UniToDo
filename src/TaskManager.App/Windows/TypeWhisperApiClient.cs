using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TaskManager.Windows;

/// <summary>TypeWhisperのループバックAPIでモデル準備と音声校正録音を制御する。</summary>
public sealed class TypeWhisperApiClient(HttpClient httpClient)
{
    private const string CorrectionWorkflowName = "音声校正";
    private const int DefaultApiPort = 8978;

    // TypeWhisper APIとの通信に使うHTTPクライアントを保持する。
    private readonly HttpClient client = httpClient;

    /// <summary>TypeWhisper APIと選択中の認識モデルが録音開始可能かを返す。</summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        // API起動とモデルのダウンロード済み状態を確認し、遅延ロード自体は録音開始APIへ任せる。
        using CancellationTokenSource readinessCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessCancellation.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/status");
            using HttpResponseMessage response = await client.SendAsync(request, readinessCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using HttpRequestMessage modelsRequest = CreateRequest(HttpMethod.Get, "v1/models");
            using HttpResponseMessage modelsResponse = await client.SendAsync(
                modelsRequest,
                readinessCancellation.Token);
            if (!modelsResponse.IsSuccessStatusCode)
            {
                return false;
            }
            using JsonDocument modelsDocument = JsonDocument.Parse(
                await modelsResponse.Content.ReadAsStreamAsync(readinessCancellation.Token));
            if (!modelsDocument.RootElement.TryGetProperty("models", out JsonElement modelsElement)
                || modelsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            return modelsElement.EnumerateArray().Any(modelElement =>
                modelElement.TryGetProperty("selected", out JsonElement selectedElement)
                && selectedElement.ValueKind == JsonValueKind.True
                && string.Equals(
                    GetOptionalString(modelElement, "status"),
                    "ready",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>有効な音声校正ワークフローを指定して録音を開始する。</summary>
    public async Task StartCorrectionRecordingAsync(CancellationToken cancellationToken)
    {
        // 表示名から現在のワークフローIDを解決し、設定更新後も固定IDへ依存しない。
        string workflowIdentifier = await ResolveCorrectionWorkflowIdentifierAsync(cancellationToken);
        string requestBody = JsonSerializer.Serialize(new { workflow_id = workflowIdentifier });
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "v1/dictation/start");
        request.Content = new StringContent(requestBody, new UTF8Encoding(false), "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        using JsonDocument responseDocument = await ReadSuccessDocumentAsync(
            response,
            "TypeWhisperの音声校正を開始できませんでした。",
            cancellationToken);
        JsonElement responseRoot = responseDocument.RootElement;
        string? status = GetOptionalString(responseRoot, "status");
        string? startedWorkflowIdentifier = GetOptionalString(responseRoot, "workflow_id");
        if (!string.Equals(status, "recording", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(startedWorkflowIdentifier, workflowIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TypeWhisperが音声校正ワークフローの録音開始を確認できませんでした。");
        }
    }

    /// <summary>TypeWhisperへ録音停止を要求し、受付結果を検証する。</summary>
    public async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        // ホットキーの取りこぼしを避け、録音中セッションへAPIで直接停止を要求する。
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "v1/dictation/stop");
        request.Content = new ByteArrayContent([]);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        using JsonDocument responseDocument = await ReadSuccessDocumentAsync(
            response,
            "TypeWhisperの録音を停止できませんでした。",
            cancellationToken);
        string? status = GetOptionalString(responseDocument.RootElement, "status");
        if (!string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TypeWhisperが録音停止を確認できませんでした。");
        }
    }

    /// <summary>TypeWhisperが現在録音中かをAPIから取得する。</summary>
    public async Task<bool> IsRecordingAsync(CancellationToken cancellationToken)
    {
        // API応答が不明な場合に停止成功と誤認しないよう、通信・JSONエラーは呼出し元へ返す。
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/dictation/status");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        using JsonDocument responseDocument = await ReadSuccessDocumentAsync(
            response,
            "TypeWhisperの録音状態を確認できませんでした。",
            cancellationToken);
        if (!responseDocument.RootElement.TryGetProperty("is_recording", out JsonElement recordingElement)
            || recordingElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException("TypeWhisperの録音状態応答が不正です。");
        }
        return recordingElement.GetBoolean();
    }

    /// <summary>TypeWhisperのルール一覧から有効な音声校正ワークフローIDを取得する。</summary>
    private async Task<string> ResolveCorrectionWorkflowIdentifierAsync(CancellationToken cancellationToken)
    {
        // 導入スクリプトが構成した名前と有効状態をAPI側の現在値で検証する。
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/rules");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        using JsonDocument responseDocument = await ReadSuccessDocumentAsync(
            response,
            "TypeWhisperの音声校正ワークフローを確認できませんでした。",
            cancellationToken);
        if (!responseDocument.RootElement.TryGetProperty("rules", out JsonElement rulesElement)
            || rulesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("TypeWhisperのワークフロー一覧応答が不正です。");
        }

        foreach (JsonElement ruleElement in rulesElement.EnumerateArray())
        {
            string? ruleName = GetOptionalString(ruleElement, "name");
            if (!string.Equals(ruleName, CorrectionWorkflowName, StringComparison.Ordinal))
            {
                continue;
            }
            bool enabled = ruleElement.TryGetProperty("is_enabled", out JsonElement enabledElement)
                && enabledElement.ValueKind == JsonValueKind.True;
            string? identifier = GetOptionalString(ruleElement, "id");
            if (!enabled)
            {
                throw new InvalidOperationException("TypeWhisperの音声校正ワークフローが無効です。");
            }
            return !string.IsNullOrWhiteSpace(identifier)
                ? identifier
                : throw new InvalidOperationException("TypeWhisperの音声校正ワークフローIDがありません。");
        }
        throw new InvalidOperationException("TypeWhisperに音声校正ワークフローがありません。連携設定を再適用してください。");
    }

    /// <summary>TypeWhisperの接続先と認証情報を付けたHTTP要求を生成する。</summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        // 起動後のディスカバリーファイルを優先し、未生成時は既定ポートへ接続する。
        TypeWhisperApiConnection connection = ResolveConnection();
        HttpRequestMessage request = new(method, new Uri(connection.BaseAddress, relativePath));
        if (!string.IsNullOrWhiteSpace(connection.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        }
        return request;
    }

    /// <summary>TypeWhisperのディスカバリーファイルから現在のポートとトークンを解決する。</summary>
    private TypeWhisperApiConnection ResolveConnection()
    {
        // 製品版の新旧データ配置を順に確認し、壊れたファイルは既定値へ安全に戻す。
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] discoveryPaths =
        [
            Path.Combine(localApplicationData, "TypeWhisper-UserData", "api-discovery.json"),
            Path.Combine(localApplicationData, "TypeWhisper", "api-discovery.json")
        ];
        foreach (string discoveryPath in discoveryPaths)
        {
            try
            {
                if (!File.Exists(discoveryPath))
                {
                    continue;
                }
                using JsonDocument discoveryDocument = JsonDocument.Parse(File.ReadAllText(discoveryPath));
                JsonElement discoveryRoot = discoveryDocument.RootElement;
                int port = discoveryRoot.TryGetProperty("port", out JsonElement portElement)
                    && portElement.TryGetInt32(out int discoveredPort)
                    && discoveredPort is > 0 and <= 65535
                        ? discoveredPort
                        : DefaultApiPort;
                string? token = GetOptionalString(discoveryRoot, "token");
                return new TypeWhisperApiConnection(
                    new Uri($"http://127.0.0.1:{port}/"),
                    token);
            }
            catch (IOException)
            {
                // 起動処理が同時にファイルを書いている場合は次回ポーリングで再読込する。
            }
            catch (UnauthorizedAccessException)
            {
                // 読取不能時も認証不要の既定APIへ接続を試みる。
            }
            catch (JsonException)
            {
                // 不完全なJSONは起動途中として扱い、既定APIへ接続を試みる。
            }
        }
        return new TypeWhisperApiConnection(
            client.BaseAddress ?? new Uri($"http://127.0.0.1:{DefaultApiPort}/"),
            null);
    }

    /// <summary>成功したTypeWhisper応答をJSONとして読み、不成功時は本文付き例外へ変換する。</summary>
    private static async Task<JsonDocument> ReadSuccessDocumentAsync(
        HttpResponseMessage response,
        string operationMessage,
        CancellationToken cancellationToken)
    {
        // API本文は利用者の音声を含まない制御応答だけを短く保持する。
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operationMessage}（HTTP {(int)response.StatusCode}）: {ShortenResponse(responseText)}");
        }
        try
        {
            return JsonDocument.Parse(responseText);
        }
        catch (JsonException responseError)
        {
            throw new InvalidOperationException("TypeWhisper APIの応答を解析できませんでした。", responseError);
        }
    }

    /// <summary>JSONオブジェクトから任意の文字列プロパティを取得する。</summary>
    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        // nullや文字列以外を未指定として扱い、APIの追加項目には影響されない。
        return element.TryGetProperty(propertyName, out JsonElement propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : null;
    }

    /// <summary>TypeWhisperのエラー応答を状態画面へ収まる長さに整える。</summary>
    private static string ShortenResponse(string response)
    {
        // 改行を空白へ寄せ、長すぎる応答は先頭200文字へ制限する。
        string normalizedResponse = response.ReplaceLineEndings(" ").Trim();
        return normalizedResponse.Length > 200 ? normalizedResponse[..200] : normalizedResponse;
    }

    /// <summary>TypeWhisper APIの接続先と任意の認証トークンを保持する。</summary>
    private sealed record TypeWhisperApiConnection(Uri BaseAddress, string? Token);
}
