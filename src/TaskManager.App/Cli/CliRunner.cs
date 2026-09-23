using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using TaskManager.Domain;

namespace TaskManager.Cli;

/// <summary>別Codexスレッドから利用するtaskctlコマンドを実行する。</summary>
public sealed class CliRunner
{
    // 保存後の再取得結果と、再送判断に必要な保存応答を保持する。
    private sealed class VerifiedWriteResult
    {
        // 保存APIが成功応答を返したか保持する。
        public bool Saved { get; init; } = true;
        // 独立した読戻しとの照合結果を保持する。
        public bool Verified { get; init; }
        // 再送を防ぐため保存対象IDを保持する。
        public string Identifier { get; init; } = string.Empty;
        // 保存直後にAPIが返した内容を保持する。
        public JsonElement? WriteResult { get; init; }
        // 独立した読戻し内容を保持する。
        public JsonElement? Readback { get; init; }
        // 確認できなかった理由を保持する。
        public string Warning { get; init; } = string.Empty;
    }

    // JSON入出力とローカルAPI接続設定を保持する。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // CLIのJSONはHTMLへ埋め込まないため、日本語のUnicodeエスケープを省いて出力量を減らす。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly HttpClient httpClient;
    // 標準入力JSONを読み取るUTF-8リーダーを保持する。
    private readonly TextReader standardInput;

    /// <summary>ローカルAPIへ接続するHTTPクライアントを初期化する。</summary>
    public CliRunner(HttpClient? localHttpClient = null, TextReader? inputReader = null)
    {
        // 通常実行は固定ローカルURLを使い、テスト時だけ応答ハンドラーの差替えを許可する。
        httpClient = localHttpClient ?? new HttpClient();
        standardInput = inputReader ?? Console.In;
        httpClient.BaseAddress ??= new Uri(TaskConstants.LocalAddress);
        httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>コマンドライン引数を解析して対応APIを呼び出す。</summary>
    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        // 共通ヘッダーを設定し、サーバー起動を確認してから処理する。
        httpClient.DefaultRequestHeaders.Add("X-TaskManager-Request", "local");
        httpClient.DefaultRequestHeaders.Add("X-TaskManager-Source", "Codex");
        if (arguments.Length == 0 || arguments[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }
        if (TryPrintCommandHelp(arguments))
        {
            return 0;
        }
        try
        {
            string commandName = arguments[0].ToLowerInvariant();
            bool jsonOutput = arguments.Contains("--json", StringComparer.OrdinalIgnoreCase);
            bool verify = arguments.Contains("--verify", StringComparer.OrdinalIgnoreCase);
            if (verify && commandName is not ("add" or "update" or "draft-create" or "start" or "complete" or "continue" or "interrupt" or "postpone" or "cancel"))
                throw new InvalidOperationException("--verifyはタスク・下書きの書込みと状態操作だけで使用できます。");
            ValidateFileOption(arguments);
            // 出力指定の誤りは更新APIを呼ぶ前に拒否する。
            string[]? fields = CliJsonOutput.ParseFields(arguments);
            if (fields is not null && !IsReadCommand(arguments))
                throw new InvalidOperationException("--fieldsは読取コマンドだけで使用できます。更新応答は省略しません。");
            await EnsureServerAsync(cancellationToken);
            object? response = commandName switch
            {
                "now" => await GetAsync("/api/v1/dashboard", cancellationToken),
                "list" => await ListAsync(arguments, cancellationToken),
                "get" => await GetAsync($"/api/v1/tasks/{Uri.EscapeDataString(RequirePosition(arguments, 1, "取得するタスクIDを指定してください。"))}", cancellationToken),
                "add" => await AddAsync(arguments, cancellationToken),
                "update" => await UpdateAsync(arguments, cancellationToken),
                "delete" => await DeleteAsync(arguments, cancellationToken),
                "start" or "complete" or "continue" or "interrupt" or "postpone" or "cancel"
                    => await ActionAsync(commandName, arguments, cancellationToken),
                "draft-create" => await CreateDraftAsync(arguments, cancellationToken),
                "draft" => await DraftAsync(arguments, cancellationToken),
                "project" => await ProjectAsync(arguments, cancellationToken),
                "time" => await TimeAsync(arguments, cancellationToken),
                "calendar-sync" => await PostAsync("/api/v1/calendar/synchronize", new { }, cancellationToken),
                _ => throw new InvalidOperationException($"不明なコマンドです: {commandName}")
            };
            WriteResponse(fields is null ? response : CliJsonOutput.SelectFields(response, fields), jsonOutput || fields is not null, commandName);
            WriteWarning(response, commandName);
            if (response is VerifiedWriteResult verification && !verification.Verified) return 3;
            return IsProjectClarificationRequired(commandName, response) ? 2 : 0;
        }
        catch (Exception commandError)
        {
            Console.Error.WriteLine($"エラー: {commandError.Message}");
            return 1;
        }
    }

    /// <summary>タスク一覧の絞込条件を組み立てて取得する。</summary>
    private async Task<object?> ListAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // 状態と検索語をURLクエリへ安全に変換する。
        string? status = GetOption(arguments, "--status");
        string? search = GetOption(arguments, "--search");
        string? projectReference = GetOption(arguments, "--project");
        List<string> queryParts = [];
        if (!string.IsNullOrWhiteSpace(status))
        {
            queryParts.Add($"status={Uri.EscapeDataString(status)}");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            queryParts.Add($"search={Uri.EscapeDataString(search)}");
        }
        if (!string.IsNullOrWhiteSpace(projectReference))
        {
            string projectIdentifier = await ResolveProjectIdentifierAsync(projectReference, cancellationToken);
            queryParts.Add($"projectIdentifier={Uri.EscapeDataString(projectIdentifier)}");
        }
        string path = "/api/v1/tasks" + (queryParts.Count > 0 ? $"?{string.Join('&', queryParts)}" : string.Empty);
        return await GetAsync(path, cancellationToken);
    }

    /// <summary>JSONファイルまたは主要オプションからタスクを登録する。</summary>
    private async Task<object?> AddAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // AIが安全に値を渡せるようファイル入力と明示オプションを用意する。
        string? jsonPath = GetOption(arguments, "--file");
        ManagedTask task;
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            task = await ReadJsonFileAsync<ManagedTask>(arguments, cancellationToken);
        }
        else
        {
            string title = GetOption(arguments, "--title") ?? throw new InvalidOperationException("--titleを指定してください。");
            task = new ManagedTask
            {
                Identifier = GetOption(arguments, "--id") ?? string.Empty,
                ProjectIdentifier = await ResolveOptionalProjectIdentifierAsync(
                    GetOption(arguments, "--project"),
                    cancellationToken),
                Title = title,
                Category = GetOption(arguments, "--category") ?? "仕事",
                Details = GetOption(arguments, "--details") ?? string.Empty,
                EstimatedMinutes = ParseInteger(GetOption(arguments, "--minutes"), 30),
                RemainingMinutes = ParseInteger(GetOption(arguments, "--minutes"), 30),
                Importance = ParseInteger(GetOption(arguments, "--importance"), 3),
                DeadlineAt = ParseDate(GetOption(arguments, "--deadline")),
                DeadlineType = GetOption(arguments, "--deadline-type") ?? TaskConstants.TargetDeadlineType,
                ParentIdentifier = GetOption(arguments, "--parent"),
                DependencyIdentifiers = SplitIdentifiers(GetOption(arguments, "--dependencies")),
                CompletionCondition = GetOption(arguments, "--completion") ?? string.Empty,
                RequiredContext = GetOption(arguments, "--context") ?? "PC",
                DeadlineOrigin = HasOption(arguments, "--no-deadline")
                    ? ProjectConstants.NoDeadlineOrigin
                    : ProjectConstants.AutomaticDeadlineOrigin,
                Source = TaskConstants.CodexSource
            };
        }
        string? fileProjectReference = GetOption(arguments, "--project");
        if (!string.IsNullOrWhiteSpace(fileProjectReference))
        {
            task.ProjectIdentifier = await ResolveProjectIdentifierAsync(fileProjectReference, cancellationToken);
        }
        if (HasOption(arguments, "--no-deadline"))
        {
            task.DeadlineAt = null;
            task.DeadlineType = TaskConstants.NoDeadlineType;
            task.DeadlineOrigin = ProjectConstants.NoDeadlineOrigin;
        }
        object? response = await PostAsync("/api/v1/tasks", task, cancellationToken);
        return HasOption(arguments, "--verify")
            ? await VerifyTaskWriteAsync(response, null, null, cancellationToken)
            : response;
    }

    /// <summary>プロジェクトの参照、登録、背景情報更新を実行する。</summary>
    private async Task<object?> ProjectAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // サブコマンドごとにローカルAPIの安全な経路へ変換する。
        if (arguments.Length < 2)
        {
            throw new InvalidOperationException("projectのサブコマンドを指定してください。");
        }
        string subcommandName = arguments[1].ToLowerInvariant();
        if (subcommandName == "list")
        {
            string query = HasOption(arguments, "--include-archived") ? "?includeArchived=true" : string.Empty;
            return await GetAsync($"/api/v1/projects{query}", cancellationToken);
        }
        if (subcommandName is "resolve" or "prepare")
        {
            string projectReference = RequirePosition(arguments, 2, "解決するプロジェクト表記を指定してください。");
            return await GetAsync(
                $"/api/v1/projects/{subcommandName}?reference={Uri.EscapeDataString(projectReference)}",
                cancellationToken);
        }
        if (subcommandName == "create")
        {
            ProjectRecord project = await ReadJsonFileAsync<ProjectRecord>(arguments, cancellationToken);
            return await PostAsync("/api/v1/projects", project, cancellationToken);
        }
        if (subcommandName == "update")
        {
            string projectIdentifier = RequirePosition(arguments, 2, "更新するプロジェクトIDを指定してください。");
            ProjectRecord project = await ReadJsonFileAsync<ProjectRecord>(arguments, cancellationToken);
            return await PutAsync(
                $"/api/v1/projects/{Uri.EscapeDataString(projectIdentifier)}",
                project,
                cancellationToken);
        }
        if (subcommandName == "archive")
        {
            string projectIdentifier = RequirePosition(arguments, 2, "アーカイブするプロジェクトIDを指定してください。");
            return await PostAsync(
                $"/api/v1/projects/{Uri.EscapeDataString(projectIdentifier)}/archive",
                new { },
                cancellationToken);
        }
        if (subcommandName == "alias-add")
        {
            string projectIdentifier = RequirePosition(arguments, 2, "別名を追加するプロジェクトIDを指定してください。");
            string aliasText = RequirePosition(arguments, 3, "ユーザー確認済みの別名を指定してください。");
            return await PostAsync(
                $"/api/v1/projects/{Uri.EscapeDataString(projectIdentifier)}/aliases",
                new ProjectAlias { AliasText = aliasText },
                cancellationToken);
        }
        if (subcommandName == "context-add")
        {
            string projectIdentifier = RequirePosition(arguments, 2, "背景情報を追加するプロジェクトIDを指定してください。");
            ProjectContextDocument contextDocument = await ReadJsonFileAsync<ProjectContextDocument>(
                arguments,
                cancellationToken);
            return await PostAsync(
                $"/api/v1/projects/{Uri.EscapeDataString(projectIdentifier)}/contexts",
                contextDocument,
                cancellationToken);
        }
        if (subcommandName == "context-update")
        {
            string projectIdentifier = RequirePosition(arguments, 2, "背景情報のプロジェクトIDを指定してください。");
            string contextIdentifier = RequirePosition(arguments, 3, "更新する背景情報IDを指定してください。");
            ProjectContextDocument contextDocument = await ReadJsonFileAsync<ProjectContextDocument>(
                arguments,
                cancellationToken);
            return await PutAsync(
                $"/api/v1/projects/{Uri.EscapeDataString(projectIdentifier)}/contexts/{Uri.EscapeDataString(contextIdentifier)}",
                contextDocument,
                cancellationToken);
        }
        throw new InvalidOperationException($"不明なprojectサブコマンドです: {subcommandName}");
    }

    /// <summary>JSONファイルまたは標準入力を指定型として読み込む。</summary>
    private async Task<TValue> ReadJsonFileAsync<TValue>(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        // 単一のJSONオブジェクトだけを受け付け、空入力や配列を更新APIへ渡さない。
        string jsonPath = GetOption(arguments, "--file")
            ?? throw new InvalidOperationException("--fileを指定してください。");
        string jsonText = jsonPath == "-" ? await standardInput.ReadToEndAsync(cancellationToken) : await File.ReadAllTextAsync(jsonPath, cancellationToken);
        // PowerShellのUTF-8パイプが付ける先頭BOMだけを取り除き、本文は変更しない。
        if (jsonText.StartsWith('\uFEFF')) jsonText = jsonText[1..];
        using JsonDocument document = JsonDocument.Parse(jsonText);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("入力JSONは単一のオブジェクトにしてください。");
        return document.RootElement.Deserialize<TValue>(JsonOptions)
            ?? throw new InvalidOperationException("指定JSONを解析できません。");
    }

    /// <summary>任意のプロジェクト表記を解決してIDを返す。</summary>
    private async Task<string?> ResolveOptionalProjectIdentifierAsync(
        string? projectReference,
        CancellationToken cancellationToken)
    {
        // 未指定時はプロジェクトなしとして返す。
        return string.IsNullOrWhiteSpace(projectReference)
            ? null
            : await ResolveProjectIdentifierAsync(projectReference, cancellationToken);
    }

    /// <summary>プロジェクト表記を一意なIDへ解決する。</summary>
    private async Task<string> ResolveProjectIdentifierAsync(
        string projectReference,
        CancellationToken cancellationToken)
    {
        // あいまいまたは該当なしの場合はタスク更新を行わず候補確認を促す。
        object? response = await GetAsync(
            $"/api/v1/projects/resolve?reference={Uri.EscapeDataString(projectReference)}",
            cancellationToken);
        if (response is not JsonElement resolutionElement)
        {
            throw new InvalidOperationException("プロジェクト解決結果を読み取れません。");
        }
        string status = resolutionElement.GetProperty("status").GetString() ?? string.Empty;
        if (status == ProjectConstants.ResolvedResolution)
        {
            return resolutionElement.GetProperty("project").GetProperty("identifier").GetString()
                ?? throw new InvalidOperationException("解決済みプロジェクトIDがありません。");
        }
        string candidates = resolutionElement.TryGetProperty("candidates", out JsonElement candidateElements)
            ? string.Join(
                ", ",
                candidateElements.EnumerateArray().Select(candidate =>
                    $"{candidate.GetProperty("canonicalName").GetString()} [{candidate.GetProperty("projectIdentifier").GetString()}]"))
            : string.Empty;
        throw new InvalidOperationException(status == ProjectConstants.AmbiguousResolution
            ? $"プロジェクトを一意に決められません。候補: {candidates}"
            : string.IsNullOrWhiteSpace(candidates)
                ? $"プロジェクトが見つかりません: {projectReference}"
                : $"プロジェクトを自動確定できません。低確信候補: {candidates}");
    }

    /// <summary>JSONファイルの内容で既存タスクを更新する。</summary>
    private async Task<object?> UpdateAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // 省略項目を既定値で補わず、指定されたJSONだけを送る。
        if (arguments.Length < 2)
        {
            throw new InvalidOperationException("更新対象のタスクIDを指定してください。");
        }
        string identifier = arguments[1];
        JsonElement task = await ReadJsonFileAsync<JsonElement>(arguments, cancellationToken);
        object? response = await PutAsync($"/api/v1/tasks/{Uri.EscapeDataString(identifier)}", task, cancellationToken);
        return HasOption(arguments, "--verify")
            ? await VerifyTaskWriteAsync(response, identifier, task, cancellationToken)
            : response;
    }

    /// <summary>同じIDの明示確認を要求してタスクを完全削除する。</summary>
    private async Task<object?> DeleteAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // 誤削除を防ぐため位置引数と--confirmの完全一致を必須とする。
        if (arguments.Length < 2)
        {
            throw new InvalidOperationException("削除対象のタスクIDを指定してください。");
        }
        string identifier = arguments[1];
        string confirmationIdentifier = GetOption(arguments, "--confirm")
            ?? throw new InvalidOperationException("完全削除には--confirm TASK-IDが必要です。通常はcancelを使用してください。");
        if (!string.Equals(identifier, confirmationIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("削除対象IDと--confirmのIDが一致しません。");
        }
        return await DeleteAsync(
            $"/api/v1/tasks/{Uri.EscapeDataString(identifier)}?confirmationIdentifier={Uri.EscapeDataString(confirmationIdentifier)}",
            cancellationToken);
    }

    /// <summary>開始・完了などの状態操作を実行する。</summary>
    private async Task<object?> ActionAsync(string actionName, string[] arguments, CancellationToken cancellationToken)
    {
        // 誤操作防止のため対象IDを必須とする。
        if (arguments.Length < 2)
        {
            throw new InvalidOperationException("操作対象のタスクIDを指定してください。");
        }
        string identifier = arguments[1];
        int? postponeMinutes = actionName == "postpone"
            ? ParseInteger(GetOption(arguments, "--minutes"), 60)
            : null;
        object? response = await PostAsync(
            $"/api/v1/tasks/{Uri.EscapeDataString(identifier)}/actions/{actionName}",
            new { postponeMinutes },
            cancellationToken);
        return HasOption(arguments, "--verify")
            ? await VerifyReadbackAsync(response, identifier,
                $"/api/v1/tasks/{Uri.EscapeDataString(identifier)}",
                (writeResult, readback) => VerifyActionReadback(actionName, identifier, readback),
                cancellationToken)
            : response;
    }

    /// <summary>AI分解結果のJSONファイルを下書き登録する。</summary>
    private async Task<object?> CreateDraftAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // 下書きはCLIから承認せず、任意の冪等キーを付けて画面確認へ回す。
        DraftBatchRequest request = await ReadJsonFileAsync<DraftBatchRequest>(arguments, cancellationToken);
        request.IdempotencyKey = GetOption(arguments, "--idempotency-key") ?? request.IdempotencyKey;
        object? response = await PostAsync("/api/v1/draft-batches", request, cancellationToken);
        if (!HasOption(arguments, "--verify")) return response;
        string batchIdentifier = response is JsonElement responseElement
            ? GetString(responseElement, "batchIdentifier") ?? GetString(responseElement, "identifier") ?? string.Empty
            : string.Empty;
        return await VerifyReadbackAsync(response, batchIdentifier,
            $"/api/v1/draft-batches/{Uri.EscapeDataString(batchIdentifier)}",
            (writeResult, readback) => VerifyDraftReadback(writeResult, readback, request),
            cancellationToken);
    }

    /// <summary>保存済みタスクを独立して取得し、保存応答と変更項目を照合する。</summary>
    private Task<VerifiedWriteResult> VerifyTaskWriteAsync(
        object? response,
        string? requestedIdentifier,
        JsonElement? changes,
        CancellationToken cancellationToken)
    {
        // 追加は生成ID、更新は指定IDを使い、対象の取り違えも検出する。
        string identifier = response is JsonElement responseElement
            ? GetString(responseElement, "identifier") ?? requestedIdentifier ?? string.Empty
            : requestedIdentifier ?? string.Empty;
        string[] fields = changes is null
            ? ["identifier", "projectIdentifier", "status", "title", "deadlineAt", "deadlineType",
                "deadlineOrigin", "estimatedMinutes", "importance", "completionCondition", "dependencyIdentifiers"]
            : ["identifier", "projectIdentifier", "status", .. changes.Value.EnumerateObject().Select(property => property.Name)];
        return VerifyReadbackAsync(response, identifier,
            $"/api/v1/tasks/{Uri.EscapeDataString(identifier)}",
            (writeResult, readback) =>
                (requestedIdentifier is null || string.Equals(identifier, requestedIdentifier, StringComparison.OrdinalIgnoreCase))
                && MatchProperties(writeResult, readback, fields),
            cancellationToken);
    }

    /// <summary>保存応答を保持したまま再取得の成否と内容を検証する。</summary>
    private async Task<VerifiedWriteResult> VerifyReadbackAsync(
        object? response,
        string identifier,
        string path,
        Func<JsonElement, JsonElement, bool> matches,
        CancellationToken cancellationToken)
    {
        // HTTP成功後の確認失敗は未保存と扱わず、再送を防ぐ情報を返す。
        JsonElement? writeResult = response is JsonElement responseElement ? responseElement : null;
        bool saved = writeResult?.ValueKind == JsonValueKind.Object
            && writeResult.Value.TryGetProperty("saved", out JsonElement savedValue)
            ? savedValue.ValueKind == JsonValueKind.True
            : true;
        if (string.IsNullOrWhiteSpace(identifier) || writeResult?.ValueKind != JsonValueKind.Object)
            return new VerifiedWriteResult
            {
                Saved = saved,
                Identifier = identifier,
                WriteResult = writeResult,
                Warning = "保存応答から確認対象を特定できません。自動再送せず状態を確認してください。"
            };
        JsonElement? readback = null;
        string warning = string.Empty;
        try
        {
            object? readResponse = await GetAsync(path, cancellationToken);
            readback = readResponse is JsonElement readElement ? readElement : null;
            if (readback?.ValueKind != JsonValueKind.Object || !matches(writeResult.Value, readback.Value))
                warning = "保存後の再取得内容が一致しません。自動再送せず状態を確認してください。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            warning = $"保存後の再取得に失敗しました: {exception.Message}。自動再送せず状態を確認してください。";
        }
        return new VerifiedWriteResult
        {
            Saved = saved,
            Identifier = identifier,
            WriteResult = writeResult,
            Readback = readback,
            Verified = string.IsNullOrEmpty(warning),
            Warning = warning
        };
    }

    /// <summary>保存応答と再取得結果の指定項目が一致するか調べる。</summary>
    private static bool MatchProperties(JsonElement expected, JsonElement actual, IEnumerable<string> fields)
    {
        // 正規化済みの保存応答を基準に、ID・状態と依頼で変更した項目を比較する。
        if (expected.ValueKind != JsonValueKind.Object || actual.ValueKind != JsonValueKind.Object) return false;
        foreach (string field in fields.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetProperty(expected, field, out JsonElement expectedValue)
                || !TryGetProperty(actual, field, out JsonElement actualValue)
                || !JsonElement.DeepEquals(expectedValue, actualValue)) return false;
        }
        return true;
    }

    /// <summary>更新入力と同じ規則でJSON項目を大文字小文字を問わず探す。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        // API応答のcamelCaseと利用者入力の表記差を吸収する。
        if (element.TryGetProperty(name, out value)) return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>状態操作の対象IDと到達すべき状態を検証する。</summary>
    private static bool VerifyActionReadback(string actionName, string identifier, JsonElement readback)
    {
        // 推薦結果には対象タスクが含まれないため、操作名に対応する保存状態を調べる。
        string? expectedStatus = actionName switch
        {
            "start" or "continue" => TaskConstants.InProgressStatus,
            "complete" => TaskConstants.CompletedStatus,
            "interrupt" or "postpone" => TaskConstants.ReadyStatus,
            "cancel" => TaskConstants.CancelledStatus,
            _ => null
        };
        if (!string.Equals(GetString(readback, "identifier"), identifier, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(GetString(readback, "status"), expectedStatus, StringComparison.Ordinal)) return false;
        return actionName switch
        {
            "start" => HasValue(readback, "startedAt"),
            "complete" => HasValue(readback, "completedAt")
                && readback.TryGetProperty("remainingMinutes", out JsonElement remainingMinutes)
                && remainingMinutes.ValueKind == JsonValueKind.Number && remainingMinutes.GetInt32() == 0,
            "continue" => HasValue(readback, "followUpAt"),
            "interrupt" => !HasValue(readback, "startedAt"),
            "postpone" => HasValue(readback, "earliestStartAt"),
            _ => true
        };
    }

    /// <summary>下書きの件数、ID対応、依存関係を保存後のバッチと照合する。</summary>
    private static bool VerifyDraftReadback(
        JsonElement writeResult,
        JsonElement readback,
        DraftBatchRequest request)
    {
        // 冪等再送でも同じ仮参照キーと実IDの対応だけを成功として扱う。
        DraftBatchCreationResult? created = writeResult.Deserialize<DraftBatchCreationResult>(JsonOptions);
        DraftBatchRecord? batch = readback.Deserialize<DraftBatchRecord>(JsonOptions);
        if (created is null || batch is null || !created.Saved
            || !string.Equals(created.BatchIdentifier, batch.BatchIdentifier, StringComparison.Ordinal)
            || created.TaskCount != request.Tasks.Count || batch.TaskCount != created.TaskCount
            || batch.Tasks.Count != created.TaskCount || batch.TaskMappings.Count != created.TaskMappings.Count
            || batch.PostProcessingSucceeded != created.PostProcessingSucceeded) return false;
        Dictionary<string, string> mappings = created.TaskMappings.ToDictionary(
            mapping => mapping.AiReferenceKey,
            mapping => mapping.TaskIdentifier,
            StringComparer.OrdinalIgnoreCase);
        if (mappings.Count != request.Tasks.Count) return false;
        foreach (DraftTaskMapping savedMapping in batch.TaskMappings)
        {
            if (!mappings.TryGetValue(savedMapping.AiReferenceKey, out string? identifier)
                || !string.Equals(identifier, savedMapping.TaskIdentifier, StringComparison.OrdinalIgnoreCase)) return false;
        }
        foreach (ManagedTask requestedTask in request.Tasks)
        {
            if (!mappings.TryGetValue(requestedTask.AiReferenceKey, out string? identifier)) return false;
            ManagedTask? savedTask = batch.Tasks.FirstOrDefault(task =>
                string.Equals(task.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
            if (savedTask is null || !string.Equals(savedTask.AiReferenceKey, requestedTask.AiReferenceKey, StringComparison.OrdinalIgnoreCase))
                return false;
            HashSet<string> expectedDependencies = requestedTask.DependencyIdentifiers
                .Select(dependency => mappings.GetValueOrDefault(dependency) ?? dependency)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedDependencies.SetEquals(savedTask.DependencyIdentifiers)) return false;
        }
        return true;
    }

    /// <summary>JSONオブジェクトの文字列項目を安全に取り出す。</summary>
    private static string? GetString(JsonElement element, string name)
    {
        // 欠落やnullは確認不能として返す。
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>JSONオブジェクトの項目に値があるか調べる。</summary>
    private static bool HasValue(JsonElement element, string name)
    {
        // 状態変更で設定される日時をnullと区別する。
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    /// <summary>保存済み下書きバッチの一覧または詳細を取得する。</summary>
    private async Task<object?> DraftAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // listとgetだけを提供し、CLIからの承認操作は追加しない。
        string subcommandName = RequirePosition(arguments, 1, "draftのサブコマンドを指定してください。").ToLowerInvariant();
        if (subcommandName == "list")
        {
            string? projectReference = GetOption(arguments, "--project");
            string query = string.Empty;
            if (!string.IsNullOrWhiteSpace(projectReference))
            {
                string projectIdentifier = await ResolveProjectIdentifierAsync(projectReference, cancellationToken);
                query = $"?projectIdentifier={Uri.EscapeDataString(projectIdentifier)}";
            }
            return await GetAsync($"/api/v1/draft-batches{query}", cancellationToken);
        }
        if (subcommandName == "get")
        {
            string batchIdentifier = RequirePosition(arguments, 2, "取得する下書きバッチIDを指定してください。");
            return await GetAsync(
                $"/api/v1/draft-batches/{Uri.EscapeDataString(batchIdentifier)}",
                cancellationToken);
        }
        throw new InvalidOperationException($"不明なdraftサブコマンドです: {subcommandName}");
    }

    /// <summary>作業タイマー、手入力ログ、期間集計のサブコマンドを実行する。</summary>
    private async Task<object?> TimeAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // サブコマンドを作業時間APIの読取または更新経路へ変換する。
        string subcommandName = RequirePosition(
            arguments,
            1,
            "timeのサブコマンドを指定してください。").ToLowerInvariant();
        if (subcommandName == "active")
        {
            return await GetAsync("/api/v1/time-entries/active", cancellationToken);
        }
        if (subcommandName == "list")
        {
            List<string> queryParts = [];
            AddQueryOption(queryParts, "rangeStart", GetOption(arguments, "--from"));
            AddQueryOption(queryParts, "rangeEnd", GetOption(arguments, "--to"));
            AddQueryOption(queryParts, "taskIdentifier", GetOption(arguments, "--task"));
            string? projectReference = GetOption(arguments, "--project");
            if (!string.IsNullOrWhiteSpace(projectReference))
            {
                string projectIdentifier = await ResolveProjectIdentifierAsync(projectReference, cancellationToken);
                AddQueryOption(queryParts, "projectIdentifier", projectIdentifier);
            }
            if (HasOption(arguments, "--include-voided"))
            {
                queryParts.Add("includeVoided=true");
            }
            string query = queryParts.Count == 0 ? string.Empty : $"?{string.Join('&', queryParts)}";
            return await GetAsync($"/api/v1/time-entries{query}", cancellationToken);
        }
        if (subcommandName == "start")
        {
            string title = GetOption(arguments, "--title")
                ?? throw new InvalidOperationException("--titleを指定してください。");
            string? projectIdentifier = await ResolveOptionalProjectIdentifierAsync(
                GetOption(arguments, "--project"),
                cancellationToken);
            return await PostAsync(
                "/api/v1/time-entries/start",
                new TimeEntryStartRequest { Title = title, ProjectIdentifier = projectIdentifier },
                cancellationToken);
        }
        if (subcommandName is "stop" or "extend" or "confirm" or "void")
        {
            string timeEntryIdentifier = RequirePosition(
                arguments,
                2,
                "操作する作業ログIDを指定してください。");
            return await PostAsync(
                $"/api/v1/time-entries/{Uri.EscapeDataString(timeEntryIdentifier)}/{subcommandName}",
                new { },
                cancellationToken);
        }
        if (subcommandName == "add")
        {
            TimeEntryMutationRequest mutationRequest = await ReadTimeEntryMutationAsync(
                arguments,
                cancellationToken);
            return await PostAsync("/api/v1/time-entries", mutationRequest, cancellationToken);
        }
        if (subcommandName == "update")
        {
            string timeEntryIdentifier = RequirePosition(
                arguments,
                2,
                "更新する作業ログIDを指定してください。");
            TimeEntryMutationRequest mutationRequest = await ReadTimeEntryMutationAsync(
                arguments,
                cancellationToken);
            return await PutAsync(
                $"/api/v1/time-entries/{Uri.EscapeDataString(timeEntryIdentifier)}",
                mutationRequest,
                cancellationToken);
        }
        if (subcommandName == "report")
        {
            string period = GetOption(arguments, "--period") ?? "day";
            List<string> queryParts = [$"period={Uri.EscapeDataString(period)}"];
            AddQueryOption(queryParts, "anchor", GetOption(arguments, "--anchor"));
            string? projectReference = GetOption(arguments, "--project");
            if (!string.IsNullOrWhiteSpace(projectReference))
            {
                string projectIdentifier = await ResolveProjectIdentifierAsync(projectReference, cancellationToken);
                AddQueryOption(queryParts, "projectIdentifier", projectIdentifier);
            }
            return await GetAsync(
                $"/api/v1/time-reports?{string.Join('&', queryParts)}",
                cancellationToken);
        }
        throw new InvalidOperationException($"不明なtimeサブコマンドです: {subcommandName}");
    }

    /// <summary>作業ログ追加・更新用JSONを読み込みCLIオプションを適用する。</summary>
    private async Task<TimeEntryMutationRequest> ReadTimeEntryMutationAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        // 複雑な日時入力はJSONファイルに限定して誤解釈を防ぐ。
        TimeEntryMutationRequest mutationRequest =
            await ReadJsonFileAsync<TimeEntryMutationRequest>(arguments, cancellationToken);
        string? projectReference = GetOption(arguments, "--project");
        if (!string.IsNullOrWhiteSpace(projectReference))
        {
            mutationRequest.ProjectIdentifier = await ResolveProjectIdentifierAsync(
                projectReference,
                cancellationToken);
        }
        if (HasOption(arguments, "--allow-overlap"))
        {
            mutationRequest.AllowOverlap = true;
        }
        return mutationRequest;
    }

    /// <summary>値が指定されたURLクエリ項目を一覧へ追加する。</summary>
    private static void AddQueryOption(List<string> queryParts, string name, string? value)
    {
        // 空文字を除外し、値をURLエンコードして追加する。
        if (!string.IsNullOrWhiteSpace(value))
        {
            queryParts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    /// <summary>ローカルサーバーが停止中なら発行済み実行ファイルから起動する。</summary>
    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        // 先にヘルスチェックし、自己完結実行時だけ監視付きの自動起動を試みる。
        if (await IsServerAvailableAsync(cancellationToken))
        {
            return;
        }
        string? processPath = Environment.ProcessPath;
        string applicationPath = Path.Combine(AppContext.BaseDirectory, "TaskManager.exe");
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(applicationPath))
        {
            // タスクスケジューラ経由ならCodexの実行ジョブから独立して監視親を起動できる。
            bool scheduledTaskStarted = await TryStartScheduledWatchdogAsync(cancellationToken);
            if (!scheduledTaskStarted)
            {
                // 未インストールの頒布物では監視親の直接起動へフォールバックする。
                ProcessStartInfo watchdogStartInformation = new(applicationPath)
                {
                    UseShellExecute = true
                };
                watchdogStartInformation.ArgumentList.Add("--watchdog");
                Process.Start(watchdogStartInformation);
            }
            for (int attemptNumber = 0; attemptNumber < 40; attemptNumber += 1)
            {
                await Task.Delay(250, cancellationToken);
                if (await IsServerAvailableAsync(cancellationToken))
                {
                    return;
                }
            }
        }
        throw new InvalidOperationException($"{TaskConstants.ApplicationDisplayName}が起動していません。");
    }

    /// <summary>登録済みWindowsタスクからCodexの実行ジョブ外へ監視親を起動する。</summary>
    private static async Task<bool> TryStartScheduledWatchdogAsync(CancellationToken cancellationToken)
    {
        // タスクが未登録またはタスクスケジューラが利用不能なら呼び出し元のフォールバックへ任せる。
        ProcessStartInfo schedulerStartInformation = new("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        schedulerStartInformation.ArgumentList.Add("/Run");
        schedulerStartInformation.ArgumentList.Add("/TN");
        schedulerStartInformation.ArgumentList.Add(TaskConstants.WatchdogScheduledTaskName);
        try
        {
            using Process schedulerProcess = new() { StartInfo = schedulerStartInformation };
            if (!schedulerProcess.Start())
            {
                return false;
            }
            await schedulerProcess.WaitForExitAsync(cancellationToken);
            return schedulerProcess.ExitCode == 0;
        }
        catch (Exception schedulerError) when (schedulerError is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>ヘルスAPIが応答するか確認する。</summary>
    private async Task<bool> IsServerAvailableAsync(CancellationToken cancellationToken)
    {
        // 接続エラーは停止中として扱う。
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync("/api/v1/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>GET APIを呼び出してJSONを返す。</summary>
    private async Task<object?> GetAsync(string path, CancellationToken cancellationToken)
    {
        // エラー本文を保持して共通処理へ渡す。
        using HttpResponseMessage response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    /// <summary>POST APIを呼び出してJSONを返す。</summary>
    private async Task<object?> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        // JSON本文で更新APIを呼び出す。
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    /// <summary>PUT APIを呼び出してJSONを返す。</summary>
    private async Task<object?> PutAsync(string path, object body, CancellationToken cancellationToken)
    {
        // JSON本文で完全更新APIを呼び出す。
        using HttpResponseMessage response = await httpClient.PutAsJsonAsync(path, body, JsonOptions, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    /// <summary>DELETE APIを呼び出して応答を返す。</summary>
    private async Task<object?> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        // 明示確認済みの完全削除だけをHTTP DELETEとして送信する。
        using HttpResponseMessage response = await httpClient.DeleteAsync(path, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    /// <summary>API応答をJSONとして読み取りエラーを例外へ変換する。</summary>
    private static async Task<object?> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // 成功時はJsonElement、失敗時は本文付き例外を返す。
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API {response.StatusCode}: {responseText}");
        }
        return string.IsNullOrWhiteSpace(responseText)
            ? null
            : JsonSerializer.Deserialize<JsonElement>(responseText, JsonOptions);
    }

    /// <summary>CLI出力をJSONまたは短い要約として表示する。</summary>
    private static void WriteResponse(object? response, bool jsonOutput, string commandName)
    {
        // AI向けJSONと人が読む短い通常出力を明確に分ける。
        if (response is VerifiedWriteResult verification)
        {
            Console.WriteLine(JsonSerializer.Serialize(verification, JsonOptions));
            return;
        }
        if (response is null)
        {
            Console.WriteLine(jsonOutput
                ? "null"
                : commandName == "delete"
                    ? "タスクを完全削除しました。"
                    : "完了しました。");
            return;
        }
        if (jsonOutput || response is not JsonElement responseElement)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return;
        }
        if (commandName == "now")
        {
            if (responseElement.TryGetProperty("recommendation", out JsonElement recommendationElement)
                && recommendationElement.ValueKind != JsonValueKind.Null)
            {
                JsonElement taskElement = recommendationElement.GetProperty("task");
                Console.WriteLine($"今やること: {taskElement.GetProperty("title").GetString()} [{taskElement.GetProperty("identifier").GetString()}]（{recommendationElement.GetProperty("suggestedMinutes").GetInt32()}分）");
            }
            else
            {
                Console.WriteLine($"今やること: {responseElement.GetProperty("emptyReason").GetString()}");
            }
            return;
        }
        if (commandName == "list" && responseElement.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator taskElements = responseElement.EnumerateArray();
            bool taskFound = false;
            foreach (JsonElement taskElement in taskElements)
            {
                taskFound = true;
                Console.WriteLine($"{taskElement.GetProperty("identifier").GetString()} [{taskElement.GetProperty("status").GetString()}] {taskElement.GetProperty("title").GetString()}");
            }
            if (!taskFound)
            {
                Console.WriteLine("該当するタスクはありません。");
            }
            return;
        }
        if (commandName is "add" or "update")
        {
            Console.WriteLine($"保存しました: {responseElement.GetProperty("title").GetString()} [{responseElement.GetProperty("identifier").GetString()}]");
            return;
        }
        if (commandName == "draft-create")
        {
            string batchIdentifier = responseElement.TryGetProperty(
                "batchIdentifier",
                out JsonElement batchIdentifierElement)
                ? batchIdentifierElement.GetString() ?? string.Empty
                : responseElement.GetProperty("identifier").GetString() ?? string.Empty;
            int taskCount = responseElement.TryGetProperty("taskCount", out JsonElement taskCountElement)
                ? taskCountElement.GetInt32()
                : 0;
            bool replayed = responseElement.TryGetProperty("replayed", out JsonElement replayedElement)
                && replayedElement.GetBoolean();
            string actionText = replayed ? "既存のAI下書きを確認しました" : "AI下書きを登録しました";
            Console.WriteLine($"{actionText}: {batchIdentifier}（{taskCount}件、承認はローカル画面から行ってください）");
            return;
        }
        if (commandName == "draft")
        {
            if (responseElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement draftElement in responseElement.EnumerateArray())
                {
                    string statusText = draftElement.GetProperty("postProcessingSucceeded").GetBoolean()
                        ? "正常"
                        : "警告";
                    Console.WriteLine(
                        $"{draftElement.GetProperty("batchIdentifier").GetString()} [{statusText}] {draftElement.GetProperty("title").GetString()}（{draftElement.GetProperty("taskCount").GetInt32()}件）");
                }
                return;
            }
            Console.WriteLine(
                $"{responseElement.GetProperty("batchIdentifier").GetString()} {responseElement.GetProperty("title").GetString()}（{responseElement.GetProperty("taskCount").GetInt32()}件）");
            return;
        }
        if (commandName == "calendar-sync")
        {
            Console.WriteLine($"カレンダーを同期しました: {responseElement.GetProperty("eventCount").GetInt32()}件");
            return;
        }
        if (commandName == "time")
        {
            WriteTimeResponse(responseElement);
            return;
        }
        if (commandName is "start" or "complete" or "continue" or "interrupt" or "postpone" or "cancel")
        {
            Console.WriteLine("タスクを更新し、推薦を再計算しました。");
            return;
        }
        if (commandName == "project")
        {
            if (responseElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement projectElement in responseElement.EnumerateArray())
                {
                    Console.WriteLine(
                        $"{projectElement.GetProperty("identifier").GetString()} [{projectElement.GetProperty("status").GetString()}] {projectElement.GetProperty("canonicalName").GetString()}");
                }
                return;
            }
            JsonElement resolutionElement = responseElement.TryGetProperty(
                "resolution",
                out JsonElement preparationResolution)
                ? preparationResolution
                : responseElement;
            if (resolutionElement.TryGetProperty("status", out JsonElement statusElement))
            {
                string resolutionStatus = statusElement.GetString() ?? string.Empty;
                if (resolutionStatus == ProjectConstants.ResolvedResolution)
                {
                    JsonElement projectElement = resolutionElement.GetProperty("project");
                    Console.WriteLine(
                        $"解決しました: {projectElement.GetProperty("canonicalName").GetString()} [{projectElement.GetProperty("identifier").GetString()}]");
                }
                else
                {
                    bool hasCandidates = resolutionElement.TryGetProperty(
                        "candidates",
                        out JsonElement resolutionCandidates)
                        && resolutionCandidates.GetArrayLength() > 0;
                    Console.WriteLine(resolutionStatus == ProjectConstants.AmbiguousResolution
                        ? "複数候補があります。--jsonで候補を確認してください。"
                        : hasCandidates
                            ? "自動確定できるプロジェクトはありません。--jsonで低確信候補を確認してください。"
                            : "該当するプロジェクトはありません。");
                }
                return;
            }
            if (responseElement.TryGetProperty("canonicalName", out JsonElement canonicalNameElement))
            {
                Console.WriteLine(
                    $"プロジェクトを保存しました: {canonicalNameElement.GetString()} [{responseElement.GetProperty("identifier").GetString()}]");
                return;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(responseElement, JsonOptions));
    }

    /// <summary>作業時間コマンドの通常出力を簡潔に表示する。</summary>
    private static void WriteTimeResponse(JsonElement responseElement)
    {
        // 配列、レポート、単一ログの順に応答形状を判定する。
        if (responseElement.ValueKind == JsonValueKind.Null)
        {
            Console.WriteLine("実行中のタイマーはありません。");
            return;
        }
        if (responseElement.ValueKind == JsonValueKind.Array)
        {
            bool entryFound = false;
            foreach (JsonElement entryElement in responseElement.EnumerateArray())
            {
                entryFound = true;
                Console.WriteLine(FormatTimeEntryLine(entryElement));
            }
            if (!entryFound)
            {
                Console.WriteLine("該当する作業ログはありません。");
            }
            return;
        }
        if (responseElement.TryGetProperty("totalSeconds", out JsonElement totalSecondsElement)
            && responseElement.TryGetProperty("period", out JsonElement periodElement))
        {
            Console.WriteLine(
                $"{periodElement.GetString()} 合計: {FormatDuration(totalSecondsElement.GetInt64())} "
                + $"（要確認 {responseElement.GetProperty("reviewCount").GetInt32()}件、"
                + $"重複 {responseElement.GetProperty("overlapCount").GetInt32()}件）");
            return;
        }
        Console.WriteLine(FormatTimeEntryLine(responseElement));
    }

    /// <summary>1件の作業ログを通常出力用の1行へ整形する。</summary>
    private static string FormatTimeEntryLine(JsonElement entryElement)
    {
        // 実行中、要確認、終了済みを短い状態表示へまとめる。
        bool isRunning = !entryElement.TryGetProperty("endAt", out JsonElement endElement)
            || endElement.ValueKind == JsonValueKind.Null;
        bool needsReview = entryElement.TryGetProperty("needsReview", out JsonElement reviewElement)
            && reviewElement.GetBoolean();
        string state = isRunning ? "実行中" : needsReview ? "要確認" : "終了";
        string projectName = entryElement.TryGetProperty("projectNameSnapshot", out JsonElement projectElement)
            && !string.IsNullOrWhiteSpace(projectElement.GetString())
            ? $" / {projectElement.GetString()}"
            : string.Empty;
        return $"{entryElement.GetProperty("identifier").GetString()} [{state}] "
            + $"{entryElement.GetProperty("title").GetString()}{projectName}";
    }

    /// <summary>秒数を時間と分の短い表記へ変換する。</summary>
    private static string FormatDuration(long totalSeconds)
    {
        // 秒を分へ切り捨て、60分以上は時間と余り分へ分解する。
        long totalMinutes = Math.Max(0, totalSeconds / 60);
        long hours = totalMinutes / 60;
        long minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours}時間{minutes}分" : $"{minutes}分";
    }

    /// <summary>成功応答に含まれる保存後警告を標準エラーへ表示する。</summary>
    private static void WriteWarning(object? response, string commandName)
    {
        // JSON標準出力を壊さず検証失敗と下書き保存後警告を標準エラーへ分離する。
        if (response is VerifiedWriteResult verification)
        {
            if (!string.IsNullOrWhiteSpace(verification.Warning))
                Console.Error.WriteLine($"警告: {verification.Warning}");
            response = verification.WriteResult;
        }
        if (commandName != "draft-create"
            || response is not JsonElement responseElement
            || !responseElement.TryGetProperty("warning", out JsonElement warningElement))
        {
            return;
        }
        string warning = warningElement.GetString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(warning))
        {
            Console.Error.WriteLine($"警告: {warning}");
        }
    }

    /// <summary>プロジェクト解決でユーザー確認が必要か判定する。</summary>
    private static bool IsProjectClarificationRequired(string commandName, object? response)
    {
        // resolveとprepareで確認が必要なあいまい・未解決結果を終了コード2へ変換する。
        if (commandName != "project" || response is not JsonElement responseElement)
        {
            return false;
        }
        if (responseElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        JsonElement resolutionElement = responseElement.TryGetProperty(
            "resolution",
            out JsonElement preparationResolution)
            ? preparationResolution
            : responseElement;
        if (!resolutionElement.TryGetProperty("status", out JsonElement statusElement))
        {
            return false;
        }
        string resolutionStatus = statusElement.GetString() ?? string.Empty;
        return resolutionStatus is ProjectConstants.AmbiguousResolution or ProjectConstants.NotFoundResolution;
    }

    /// <summary>指定名の次にあるオプション値を取得する。</summary>
    private static string? GetOption(IReadOnlyList<string> arguments, string optionName)
    {
        // 見つからない場合や値がない場合はnullを返す。
        for (int argumentIndex = 0; argumentIndex < arguments.Count - 1; argumentIndex += 1)
        {
            if (string.Equals(arguments[argumentIndex], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[argumentIndex + 1];
            }
        }
        return null;
    }

    /// <summary>指定オプションが引数に含まれるか確認する。</summary>
    private static bool HasOption(IReadOnlyList<string> arguments, string optionName)
    {
        // 大文字小文字を区別せずフラグの存在だけを判定する。
        return arguments.Contains(optionName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsReadCommand(IReadOnlyList<string> arguments)
    {
        // 省略表示によって保存後警告やプロジェクト解決の確認情報を隠さない。
        string command = arguments[0].ToLowerInvariant();
        string subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : "";
        return command is "now" or "list" or "get"
            || command == "project" && subcommand == "list"
            || command == "draft" && subcommand is "list" or "get"
            || command == "time" && subcommand is "active" or "list" or "report";
    }

    private static void ValidateFileOption(IReadOnlyList<string> arguments)
    {
        // 入力元が曖昧な指定や値の欠落を、登録オプションへのフォールバック前に拒否する。
        bool found = false;
        for (int position = 0; position < arguments.Count; position++)
        {
            if (!string.Equals(arguments[position], "--file", StringComparison.OrdinalIgnoreCase)) continue;
            if (found || position + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[position + 1])
                || arguments[position + 1].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException("--fileはJSONファイルのパスまたは - を1回だけ指定してください。");
            found = true;
        }
    }

    /// <summary>指定位置の必須引数を取得する。</summary>
    private static string RequirePosition(
        IReadOnlyList<string> arguments,
        int position,
        string errorMessage)
    {
        // 引数不足または空文字の場合は用途別メッセージを返す。
        if (arguments.Count <= position || string.IsNullOrWhiteSpace(arguments[position]))
        {
            throw new InvalidOperationException(errorMessage);
        }
        return arguments[position];
    }

    /// <summary>文字列を整数へ変換する。</summary>
    private static int ParseInteger(string? value, int defaultValue)
    {
        // 不正値は既定値として扱う。
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue)
            ? parsedValue
            : defaultValue;
    }

    /// <summary>文字列を日時へ変換する。</summary>
    private static DateTimeOffset? ParseDate(string? value)
    {
        // 日付だけの期限はJSON入力と同じローカル20時へ補完する。
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return JsonSerializer.Deserialize<ManagedTask>("{\"deadlineAt\":" + JsonSerializer.Serialize(value) + "}", JsonOptions)!.DeadlineAt;
        }
        // 未指定または不正値はnullとして扱う。
        return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsedValue)
            ? parsedValue
            : null;
    }

    /// <summary>依存関係文字列をID一覧へ変換する。</summary>
    private static List<string> SplitIdentifiers(string? value)
    {
        // カンマ、読点、改行を共通の区切りとして扱う。
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '、', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>対応するサブコマンドの詳細ヘルプを表示する。</summary>
    private static bool TryPrintCommandHelp(IReadOnlyList<string> arguments)
    {
        // --helpまたは-hがある場合だけサーバーへ接続せず用途別説明を返す。
        if (!HasOption(arguments, "--help") && !HasOption(arguments, "-h"))
        {
            return false;
        }
        string commandName = arguments[0].ToLowerInvariant();
        if (commandName == "draft-create")
        {
            PrintDraftCreateHelp();
            return true;
        }
        if (commandName == "add")
        {
            PrintAddHelp();
            return true;
        }
        if (commandName == "time")
        {
            PrintTimeHelp();
            return true;
        }
        if (commandName == "project"
            && arguments.Count > 1
            && string.Equals(arguments[1], "prepare", StringComparison.OrdinalIgnoreCase))
        {
            PrintProjectPrepareHelp();
            return true;
        }
        PrintHelp();
        return true;
    }

    /// <summary>draft-createの入力、応答、終了コードを表示する。</summary>
    private static void PrintDraftCreateHelp()
    {
        // 冪等再送とコミット後警告をCodexが判断できる情報量で示す。
        Console.WriteLine("""
            使用法:
              taskctl draft-create --file draft.json [--idempotency-key KEY] [--verify] [--json]

            入力:
              各tasks要素のaiReferenceKeyは必須かつバッチ内で一意です。
              identifierは空にでき、dependencyIdentifiersではaiReferenceKeyを仮参照に使えます。

            出力:
              --json  指定時はbatchIdentifier、identifier、saved、taskCount、
                      postProcessingSucceeded、warning、taskMappingsをJSONで返します。
              --verify  保存後にバッチを再取得し、件数・ID対応・依存を照合します。

            終了コード:
              0  保存成功。保存後の検証・推薦失敗も警告付きで0です。
              1  入力不正、未解決参照、コミット前またはコミット失敗です。
              3  保存応答は得られましたが読戻しを確認できません。再送しないでください。

            警告:
              保存後処理に失敗した場合も下書きは保存済みです。
              warningを標準エラーへ表示し、同じidempotency keyの再送では重複作成しません。
            """);
    }

    /// <summary>project prepareの名前解決と終了コードを表示する。</summary>
    private static void PrintProjectPrepareHelp()
    {
        // 自動確定とユーザー確認が必要な結果を区別して示す。
        Console.WriteLine("""
            使用法:
              taskctl project prepare "プロジェクト表記" [--json]

            出力:
              --json  解決状態、正式名称、候補、期限規則、背景情報をJSONで返します。

            終了コード:
              0  resolved。返されたproject.identifierを後続操作で使用してください。
              2  ambiguousまたはnot_found。候補を確認するまで変更しないでください。
              1  API接続または入力処理の失敗です。

            注意:
              not_foundでも低確信候補を最大3件返すことがありますが、自動確定や別名登録は行いません。
            """);
    }

    /// <summary>addの入力形式と終了コードを表示する。</summary>
    private static void PrintAddHelp()
    {
        // オプション入力とJSON入力で共通するプロジェクト規則を示す。
        Console.WriteLine("""
            使用法:
              taskctl add --title 名称 [--project IDまたは表記] [--minutes 30]
                          [--deadline 日時] [--importance 3] [--verify] [--json]
              taskctl add --file task.json [--project IDまたは表記] [--no-deadline] [--verify] [--json]

            出力:
              --json  保存後のタスク全項目をJSONで返します。
              --verify  保存後にタスクを再取得し、保存応答と照合したJSONを返します。

            終了コード:
              0  保存成功です。
              1  入力不正、プロジェクト未解決、保存失敗です。
              3  保存応答は得られましたが読戻しを確認できません。再送しないでください。

            注意:
              プロジェクト表記は事前にprepareし、解決済みIDを指定してください。
              期限未指定時はプロジェクトIDだけを渡し、既定期限の適用をサーバーへ任せます。
            """);
    }

    /// <summary>作業時間コマンドの入力、重複確認、終了コードを表示する。</summary>
    private static void PrintTimeHelp()
    {
        // CodexがDBを直接編集せず全操作をCLIで完結できる使用法を示す。
        Console.WriteLine("""
            使用法:
              taskctl time active [--json]
              taskctl time list [--from 日時] [--to 日時] [--project IDまたは表記] [--task TASK-ID] [--json]
              taskctl time start --title 活動名 [--project IDまたは表記] [--json]
              taskctl time stop|extend|confirm|void TIME-ID [--json]
              taskctl time add --file time-entry.json [--project IDまたは表記] [--allow-overlap] [--json]
              taskctl time update TIME-ID --file time-entry.json [--project IDまたは表記] [--allow-overlap] [--json]
              taskctl time report --period day|week|month [--anchor YYYY-MM-DD] [--project IDまたは表記] [--json]

            重複:
              未確認の重複区間はAPI 409で保存されません。内容を確認した場合だけ
              --allow-overlapを追加してください。承認済み重複は統計へ双方を合算します。

            終了コード:
              0  読取または保存成功です。
              1  入力不正、重複未確認、プロジェクト未解決、API失敗です。
            """);
    }

    /// <summary>利用可能なコマンドを表示する。</summary>
    private static void PrintHelp()
    {
        // Codexが誤解しにくい最小の使用例を表示する。
        Console.WriteLine("""
            taskctl now [--json]
            taskctl list [--status 状態] [--search 文字列] [--project IDまたは表記] [--json]
            taskctl get TASK-ID [--json]
            taskctl add --title 名称 [--project IDまたは表記] [--minutes 30] [--deadline 日時] [--importance 3] [--verify]
            taskctl add --file task.json [--project IDまたは表記] [--no-deadline] [--verify]
            taskctl update TASK-ID --file task.json [--verify]
              指定項目だけを更新。省略は維持、nullは解除。締切変更はdeadlineAtとdeadlineOrigin: explicitを指定。
            taskctl delete TASK-ID --confirm TASK-ID
            taskctl start|complete|continue|interrupt|postpone|cancel TASK-ID [--verify]
            taskctl draft-create --file draft.json [--idempotency-key KEY] [--verify] [--json]
            taskctl draft list [--project IDまたは表記] [--json]
            taskctl draft get BATCH-ID [--json]
            taskctl project list [--include-archived] [--json]
            taskctl project resolve|prepare "プロジェクト表記" [--json]
            taskctl project create --file project.json
            taskctl project update|archive PROJECT-ID [--file project.json]
            taskctl project alias-add PROJECT-ID "確認済み別名"
            taskctl project context-add PROJECT-ID --file context.json
            taskctl project context-update PROJECT-ID CONTEXT-ID --file context.json
            taskctl time active|list [--json]
            taskctl time start --title 活動名 [--project IDまたは表記] [--json]
            taskctl time stop|extend|confirm|void TIME-ID [--json]
            taskctl time add --file time-entry.json [--allow-overlap] [--json]
            taskctl time update TIME-ID --file time-entry.json [--allow-overlap] [--json]
            taskctl time report --period day|week|month [--anchor YYYY-MM-DD] [--json]
            taskctl calendar-sync

            JSON入力: --file - は標準入力のJSONを読む。--file パスも引き続き使用可能。
            JSON出力: --json は改行・字下げなし。読取では --fields identifier,title で必要項目だけ取得。
              --fields はJSON出力を兼ね、ネストは recommendation.task.title のように指定。
              配列は全要素に適用。nullと空配列を保持。存在しない項目はエラー。
              project resolve/prepareと更新コマンドは確認情報保護のため項目選択不可。
            """);
    }
}
