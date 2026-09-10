using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskManager.Configuration;

namespace TaskManager.Services;

// Webと音声入力で共用する送信・確認・表示データを定義する。
public sealed record ChatSubmission(string Conversation, string RequestIdentifier, string Text);
public sealed record ChatAnswer(string Identifier, string Action, Dictionary<string, string[]>? Answers);
public sealed record ChatMessage(string Identifier, string Role, string Text);
public sealed record ChatPrompt(string Identifier, string Kind, string Description, JsonElement? Questions);
public sealed record ChatSnapshot(string Conversation, string Status, string? Error, ChatMessage[] Messages, ChatPrompt[] Prompts);
public sealed record ChatReceipt(string Identifier, string Hash);
public sealed record ChatStoredState(string Conversation, string? Thread, ChatReceipt[] Receipts);

/// <summary>UIに依存しない単一会話・実行・確認待ちの状態を管理する。</summary>
public sealed class CodexChatService
{
    // 通信先、更新通知、保存先と投入するスキルの場所を保持する。
    private readonly ICodexAppServer server;
    private readonly UiChangeNotifier changes;
    private readonly string statePath;
    private readonly string workspace;
    private readonly string skillPath;
    // 通知の同期と利用者の操作の直列化を別々に保持する。
    private readonly object stateLock = new();
    private readonly SemaphoreSlim actions = new(1, 1);
    // 表示用履歴と応答待ちRPC、永続化する送信受付記録を保持する。
    private readonly List<ChatMessage> messages = [];
    private readonly Dictionary<string, (JsonElement Identifier, string Method, JsonElement Parameters, ChatPrompt Prompt)> prompts = [];
    private ChatStoredState stored;
    // 現在の実行と復元状態を保持する。
    private string status = "idle";
    private string? turn;
    private string? error;
    private bool ready;

    // 音声送信へ完了・確認待ち・切断時点の表示内容を渡す通知を保持する。
    public event Action<ChatSnapshot>? ResponseAvailable;

    public CodexChatService(
        ICodexAppServer server,
        TaskManagerPaths paths,
        UiChangeNotifier changes)
    {
        // 会話内容はCodex側へ保存し、アプリには識別子と送信重複防止情報だけを保存する。
        this.server = server;
        this.changes = changes;
        statePath = Path.Combine(paths.DataDirectory, "codex-chat-state.json");
        workspace = Path.Combine(paths.DataDirectory, "assistant-workspace");
        skillPath = Path.Combine(AppContext.BaseDirectory, "assistant-skill", "SKILL.md");
        stored = File.Exists(statePath)
            ? JsonSerializer.Deserialize<ChatStoredState>(File.ReadAllText(statePath)) ?? throw new InvalidOperationException("Codex会話情報を読み込めません。")
            : new(Guid.NewGuid().ToString("N"), null, []);
        server.MessageReceived += Receive;
        server.Disconnected += Disconnect;
    }

    public async Task<ChatSnapshot> GetAsync()
    {
        // 初回表示で履歴を復元し、待機中は同じスナップショットだけを返す。
        await actions.WaitAsync();
        try
        {
            if (Snapshot().Status != "archived") await EnsureReadyAsync();
            return Snapshot();
        }
        catch (InvalidOperationException exception) when (TryMarkArchived(exception)) { return Snapshot(); }
        finally { actions.Release(); }
    }

    public ChatSnapshot Snapshot()
    {
        // 更新中のコレクションを直接公開せずコピーする。
        lock (stateLock) return new(stored.Conversation, status, error, messages.ToArray(), prompts.Values.Select(value => value.Prompt).ToArray());
    }

    public bool HasReceipt(string conversation, string requestIdentifier)
    {
        // 現在の会話に保存済みの受付があるかを照合する。
        lock (stateLock) return stored.Conversation == conversation
            && stored.Receipts.Any(receipt => receipt.Identifier == requestIdentifier.Replace("-", ""));
    }

    private async Task EnsureReadyAsync()
    {
        // 作業ディレクトリ・スキル・権限はサーバー側で固定し、ブラウザ指定を受け付けない。
        if (ready) return;
        Directory.CreateDirectory(workspace);
        if (!File.Exists(skillPath)) throw new InvalidOperationException("タスク操作スキルが見つかりません。UniToDoを再インストールしてください。");
        string instructions = "あなたはUniToDoのタスク管理アシスタントです。日本語で短く回答してください。"
            + "これはUniToDo専用会話です。タスク操作には $manage-local-tasks の業務規則を守り、専用PATHの taskctl を直接実行してください。"
            + "taskctl now --json のように呼び、絶対パス、invoke-taskctl.ps1、追加のpowershell起動は不要です。"
            + "通常のスキル読取りとCLI実行は承認済みです。JSON入力は --file -、読取は --fields を必要時に使い、一時ファイルを減らしてください。"
            + "PowerShell 5.1で日本語JSONをパイプ入力する場合は $OutputEncoding=[Text.Encoding]::UTF8 を同じコマンド内で先に設定してください。"
            + "ただし対象や依頼内容が曖昧な場合、削除やスキルで指定された業務上の確認は省略しないでください。"
            + "SQLite・APIの直接操作、ソース編集、プログラム開発は行わないでください。"
            + "接続アプリの参照は利用可能な機能の提示です。利用するかは送信本文と会話履歴から判断し、無関係な連携を呼ばないでください。"
            + "Google Calendarへの予定登録・更新・削除を明示された場合は接続済みプラグインを使用してください。"
            + "本体のcalendar.readonlyはローカル同期だけの制約で、接続プラグインには適用しません。"
            + "連携の可否は現在のツール情報と実行結果で判断し、過去の利用不可という回答を根拠に拒否しないでください。"
            + "実行できない処理は制約を説明してください。他のCodex会話を操作しないでください。"
            + "確認が必要な場合は質問して回答を待ってください。スキル絶対パス: " + skillPath;
        Dictionary<string, object?> configuration = new()
        {
            ["cwd"] = workspace, ["model"] = "gpt-5.6-luna", ["serviceTier"] = "fast", ["sandbox"] = "workspace-write",
            ["approvalPolicy"] = "never", ["approvalsReviewer"] = "user", ["developerInstructions"] = instructions,
            ["config"] = new Dictionary<string, object>
            {
                ["model_reasoning_effort"] = "low",
                // 専用会話の子シェルだけで同梱CLIを優先し、利用者全体のPATHは変更しない。
                ["shell_environment_policy.set"] = new Dictionary<string, string>
                {
                    ["PATH"] = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
                        + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
                },
                // CLI配置先だけを追加し、正本DBや開発リポジトリへの書込み権限は付けない。
                ["sandbox_workspace_write.writable_roots"] = new[] { AppContext.BaseDirectory },
                ["sandbox_workspace_write.network_access"] = false
            }
        };
        string? existingThread;
        lock (stateLock) existingThread = stored.Thread;
        if (existingThread is not null) configuration["threadId"] = existingThread;
        JsonElement response = await server.CallAsync(existingThread is null ? "thread/start" : "thread/resume", configuration);
        JsonElement thread = response.GetProperty("thread");
        lock (stateLock)
        {
            stored = stored with { Thread = thread.GetProperty("id").GetString()! };
            Save();
            messages.Clear();
            if (existingThread is null) prompts.Clear();
            turn = null;
            status = "idle";
            error = null;
            if (thread.TryGetProperty("turns", out JsonElement turns))
                foreach (JsonElement historyTurn in turns.EnumerateArray())
                {
                    if (historyTurn.TryGetProperty("items", out JsonElement items))
                        foreach (JsonElement item in items.EnumerateArray()) ApplyItem(item);
                    if (Text(historyTurn, "status") == "inProgress")
                    {
                        turn = Text(historyTurn, "id");
                        status = "running";
                    }
                    else error = Text(historyTurn, "status") == "failed" ? "前回の処理は失敗しています。履歴とタスクの状態を確認してください。"
                        : Text(historyTurn, "status") == "interrupted" ? "前回の処理は中断されています。実行済みの変更は取り消されません。" : null;
                }
            ready = true;
        }
    }

    public async Task<ChatSnapshot> SendAsync(ChatSubmission submission)
    {
        // 操作を直列化し、送信前に永続化して通信切断時の重複実行を防ぐ。
        if (!Guid.TryParse(submission.RequestIdentifier, out Guid requestIdentifier) || string.IsNullOrWhiteSpace(submission.Text) || submission.Text.Length > 20000)
            throw new ArgumentException("送信内容は1～20,000文字、送信識別子は有効なUUIDにしてください。");
        submission = submission with { RequestIdentifier = requestIdentifier.ToString("N") };
        await actions.WaitAsync();
        try
        {
            if (Snapshot().Status == "archived") throw new InvalidOperationException("接続先はアーカイブ済みです。「新しい会話」を開始してください。");
            await EnsureReadyAsync();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(submission.Text)));
            lock (stateLock)
            {
                CheckConversation(submission.Conversation);
                ChatReceipt? previous = stored.Receipts.FirstOrDefault(receipt => receipt.Identifier == submission.RequestIdentifier);
                if (previous is not null)
                {
                    if (previous.Hash != hash) throw new ArgumentException("同じ送信識別子の内容が変わっています。");
                    return Snapshot();
                }
                if (status != "idle") throw new InvalidOperationException("現在の処理を終えてから送信してください。接続不明時は会話を開き直し、履歴を確認してください。");
                if (stored.Receipts.Length >= 1000) throw new InvalidOperationException("この会話の送信上限です。新しい会話を開始してください。");
            }
            // 接続情報の取得失敗は未送信として扱い、受付記録を消費しない。
            object[] input = await BuildTurnInputAsync(submission.Text);
            lock (stateLock)
            {
                if (!ready || status != "idle") throw new InvalidOperationException("Codexとの接続状態が変わりました。履歴を再取得してください。");
                stored = stored with { Receipts = [.. stored.Receipts, new(submission.RequestIdentifier, hash)] };
                Save();
                status = "running";
                error = null;
            }
            try
            {
                JsonElement result = await server.CallAsync("turn/start", new
                {
                    threadId = stored.Thread, clientUserMessageId = submission.RequestIdentifier,
                    input,
                    effort = "low", serviceTier = "fast", approvalPolicy = "never", approvalsReviewer = "user",
                    // 復元済み会話にも毎回同じ限定権限を適用し、旧設定へ戻ることを防ぐ。
                    sandboxPolicy = new { type = "workspaceWrite", writableRoots = new[] { workspace, AppContext.BaseDirectory }, networkAccess = false }
                });
                lock (stateLock)
                {
                    JsonElement startedTurn = result.GetProperty("turn");
                    // 完了通知が応答より早かった場合に実行中へ戻さない。
                    if (status == "running") turn = Text(startedTurn, "id");
                }
            }
            catch
            {
                lock (stateLock) { status = "uncertain"; error = "送信結果を確認できません。自動再送はしません。会話を開き直して履歴を確認してください。"; }
                throw;
            }
            return Snapshot();
        }
        catch (InvalidOperationException exception) when (TryMarkArchived(exception))
        {
            // 拒否された入力を成功扱いせず、画面の入力欄と受付済み記録を維持する。
            throw new InvalidOperationException("接続先はアーカイブ済みです。「新しい会話」を開始してください。", exception);
        }
        finally { actions.Release(); }
    }

    private bool TryMarkArchived(InvalidOperationException exception)
    {
        // CLIのアーカイブ専用エラーだけを識別し、通信障害や実行結果不明と区別する。
        lock (stateLock)
        {
            if (stored.Thread is null || !exception.Message.Contains($"session {stored.Thread} is archived", StringComparison.OrdinalIgnoreCase)) return false;
            MarkArchived();
            return true;
        }
    }

    private void MarkArchived()
    {
        // 会話識別子と受付を保持して新規作成への入口を残し、アーカイブ解除は行わない。
        status = "archived";
        error = "接続先の会話はアーカイブ済みです。「新しい会話」から再開できます。";
        ready = false;
        turn = null;
        prompts.Clear();
        ResponseAvailable?.Invoke(Snapshot());
    }

    private async Task<object[]> BuildTurnInputAsync(string text)
    {
        // キャッシュ配置や本文の単語に依存せず、現在呼出可能な接続アプリを毎回解決する。
        JsonElement response;
        try
        {
            response = await server.CallAsync("app/installed", new { threadId = stored.Thread, forceRefresh = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // 未導入と通信失敗を混同せず、本文を送信する前に再試行可能なエラーとして返す。
            throw new InvalidOperationException("接続アプリの状態を確認できなかったため、依頼は送信していません。接続を確認して再送してください。", exception);
        }
        if (!response.TryGetProperty("apps", out JsonElement applications) || applications.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("接続アプリの応答形式が不正なため、依頼は送信していません。Codex CLIを確認してください。");

        // 本文を改変せず、専用タスクスキルと実行可能なアプリ参照を別入力として渡す。
        List<object> input = [new { type = "text", text }, new { type = "skill", name = "manage-local-tasks", path = skillPath }];
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (JsonElement application in applications.EnumerateArray())
        {
            if (application.ValueKind != JsonValueKind.Object
                || !application.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind != JsonValueKind.True
                || !application.TryGetProperty("callable", out JsonElement callable) || callable.ValueKind != JsonValueKind.True) continue;
            string identifier = Text(application, "id");
            if (string.IsNullOrWhiteSpace(identifier) || !identifiers.Add(identifier)) continue;
            string name = Text(application, "runtimeName");
            input.Add(new { type = "mention", name = string.IsNullOrWhiteSpace(name) ? identifier : name, path = "app://" + identifier });
        }
        return input.ToArray();
    }

    public async Task<ChatSnapshot> NewAsync(string conversation)
    {
        // 実行中の会話は捨てず、明示操作時だけ新しい専用会話へ切り替える。
        await actions.WaitAsync();
        try
        {
            lock (stateLock)
            {
                CheckConversation(conversation);
                if (status is not ("idle" or "archived")) throw new InvalidOperationException("停止または現在の処理が完了してから新しい会話を開始してください。");
                stored = new(Guid.NewGuid().ToString("N"), null, []);
                Save();
                ready = false;
                status = "idle";
                error = null;
                turn = null;
                prompts.Clear();
                messages.Clear();
            }
            await EnsureReadyAsync();
            return Snapshot();
        }
        finally { actions.Release(); }
    }

    public async Task<ChatSnapshot> ReconnectAsync(string conversation)
    {
        // 結果不明時は履歴だけを照合し、元の依頼を再実行しない。
        await actions.WaitAsync();
        try
        {
            lock (stateLock) CheckConversation(conversation);
            if (!ready) await EnsureReadyAsync();
            else
            {
                JsonElement response = await server.CallAsync("thread/read", new { threadId = stored.Thread, includeTurns = true });
                lock (stateLock)
                {
                    messages.Clear();
                    status = "idle";
                    turn = null;
                    foreach (JsonElement historyTurn in response.GetProperty("thread").GetProperty("turns").EnumerateArray())
                    {
                        foreach (JsonElement item in historyTurn.GetProperty("items").EnumerateArray()) ApplyItem(item);
                        if (Text(historyTurn, "status") == "inProgress") { status = "running"; turn = Text(historyTurn, "id"); }
                    }
                    if (status == "idle") prompts.Clear();
                    error = "履歴を再取得しました。結果不明だった依頼の反映状況を確認してください。";
                }
            }
            return Snapshot();
        }
        catch (InvalidOperationException exception) when (TryMarkArchived(exception)) { return Snapshot(); }
        finally { actions.Release(); }
    }

    public async Task<ChatSnapshot> InterruptAsync(string conversation)
    {
        // 中断は処理の停止だけを要求し、完了済み操作は取り消さない。
        await actions.WaitAsync();
        try
        {
            string? currentTurn;
            lock (stateLock) { CheckConversation(conversation); currentTurn = turn; }
            if (currentTurn is not null) await server.CallAsync("turn/interrupt", new { threadId = stored.Thread, turnId = currentTurn });
            return Snapshot();
        }
        finally { actions.Release(); }
    }

    public async Task<ChatSnapshot> AnswerAsync(ChatAnswer answer)
    {
        // 複数端末から同じ確認へ回答しても最初の有効な回答だけを採用する。
        await actions.WaitAsync();
        try
        {
            (JsonElement Identifier, string Method, JsonElement Parameters, ChatPrompt Prompt) request;
            lock (stateLock)
                if (string.IsNullOrEmpty(answer.Identifier) || !prompts.TryGetValue(answer.Identifier, out request)) throw new InvalidOperationException("この確認は回答済み、または終了しています。");
            object result = BuildAnswer(request.Method, request.Parameters, answer);
            // 通信結果が不明でも承認を再送しない。
            lock (stateLock)
            {
                if (!prompts.Remove(answer.Identifier)) throw new InvalidOperationException("この確認は終了しました。最新の状態を確認してください。");
            }
            try { await server.ReplyAsync(request.Identifier, result); }
            catch { Disconnect(); throw; }
            return Snapshot();
        }
        finally { actions.Release(); }
    }

    public static object BuildAnswer(string method, JsonElement parameters, ChatAnswer answer)
    {
        // ブラウザから自由なRPCや権限設定を受けず、保存した要求の範囲内で応答を組み立てる。
        if (answer.Action is not ("accept" or "decline" or "cancel")) throw new ArgumentException("回答方法が不正です。");
        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            return new { decision = answer.Action };
        if (method == "item/permissions/requestApproval")
            return new { permissions = answer.Action == "accept" ? parameters.GetProperty("permissions") : JsonSerializer.SerializeToElement(new { }), scope = "turn" };
        if (method == "item/tool/requestUserInput")
        {
            Dictionary<string, object> answers = [];
            if (answer.Action != "accept") throw new ArgumentException("回答を入力するか、処理を停止してください。");
            foreach (JsonElement question in parameters.GetProperty("questions").EnumerateArray())
            {
                string identifier = Text(question, "id");
                if (answer.Answers is null || !answer.Answers.TryGetValue(identifier, out string[]? values)
                    || values.Length == 0 || values.Length > 20 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 4000))
                    throw new ArgumentException("すべての質問へ回答してください（各4,000文字以内）。");
                answers[identifier] = new { answers = values };
            }
            return new { answers };
        }
        // 標準MCPフォームは保存済みの要求に対する明示回答だけを返し、永続許可を作らない。
        if (method == "mcpServer/elicitation/request")
            return new { action = answer.Action, content = answer.Action == "accept" ? CodexMcpForm.BuildContent(parameters, answer.Answers) : null };
        throw new ArgumentException("この種類の確認はこの画面では承認できません。拒否または停止してください。");
    }

    private void Receive(JsonElement envelope)
    {
        // 所有する会話の通知だけを取り込み、内部推論やコマンド出力は表示しない。
        string method = Text(envelope, "method");
        if (!envelope.TryGetProperty("params", out JsonElement parameters)) return;
        lock (stateLock)
        {
            if (Text(parameters, "threadId") != stored.Thread) return;
            if (method == "thread/archived") { MarkArchived(); return; }
            if (status == "archived") return;
            string notificationTurn = Text(parameters, "turnId");
            if (turn is not null && notificationTurn.Length > 0 && notificationTurn != turn) return;
            if (envelope.TryGetProperty("id", out JsonElement identifier))
            {
                string promptIdentifier = Guid.NewGuid().ToString("N");
                string kind = method == "item/tool/requestUserInput" ? "question"
                    : method == "mcpServer/elicitation/request" && CodexMcpForm.IsSupported(parameters) ? "mcp-form"
                    : method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval" ? "approval" : "unsupported";
                string description = Text(parameters, "reason");
                if (parameters.TryGetProperty("command", out JsonElement command)) description += "\n" + command.ToString();
                if (parameters.TryGetProperty("permissions", out JsonElement permissions)) description += "\n" + permissions.ToString();
                string? activity = messages.FirstOrDefault(message => message.Identifier == Text(parameters, "itemId"))?.Text;
                if (activity is not null) description += "\n" + activity;
                if (parameters.TryGetProperty("networkApprovalContext", out JsonElement network)) description += "\n" + network.ToString();
                if (kind == "unsupported") description = "この確認はこの画面では対応していません。PCでの操作が必要な場合があります。\n" + Text(parameters, "message");
                if (kind == "mcp-form") description = Text(parameters, "message");
                JsonElement? questions = kind == "mcp-form" ? parameters.GetProperty("requestedSchema").Clone()
                    : parameters.TryGetProperty("questions", out JsonElement requestedQuestions) ? requestedQuestions.Clone() : null;
                ChatPrompt prompt = new(promptIdentifier, kind, Limit(description), questions);
                prompts[promptIdentifier] = (identifier.Clone(), method, parameters.Clone(), prompt);
                ResponseAvailable?.Invoke(Snapshot());
                return;
            }
            if (method == "serverRequest/resolved" && parameters.TryGetProperty("requestId", out JsonElement resolved))
                foreach (string key in prompts.Where(pair => pair.Value.Identifier.ToString() == resolved.ToString()).Select(pair => pair.Key).ToArray()) prompts.Remove(key);
            if (method == "turn/started") { turn = Text(parameters.GetProperty("turn"), "id"); status = "running"; }
            if (method is "item/started" or "item/completed") ApplyItem(parameters.GetProperty("item"));
            if (method == "item/agentMessage/delta")
            {
                string itemIdentifier = Text(parameters, "itemId");
                string previous = messages.FirstOrDefault(message => message.Identifier == itemIdentifier)?.Text ?? "";
                PutMessage(new(itemIdentifier, "assistant", Limit(previous + Text(parameters, "delta"))));
            }
            if (method == "turn/completed")
            {
                JsonElement completed = parameters.GetProperty("turn");
                if (turn is not null && Text(completed, "id") != turn) return;
                if (completed.TryGetProperty("items", out JsonElement items)) foreach (JsonElement item in items.EnumerateArray()) ApplyItem(item);
                status = "idle";
                error = Text(completed, "status") == "failed" ? "Codexの処理が失敗しました。履歴を確認してから次の依頼を送信してください。"
                    : Text(completed, "status") == "interrupted" ? "処理を停止しました。実行済みの変更は取り消されません。" : null;
                turn = null;
                prompts.Clear();
                ResponseAvailable?.Invoke(Snapshot());
                changes.Publish("codex", "");
            }
        }
    }

    private void ApplyItem(JsonElement item)
    {
        // 会話本文と実行の要点だけを表示対象にする。
        string kind = Text(item, "type");
        string identifier = Text(item, "id");
        if (kind == "agentMessage") PutMessage(new(identifier, "assistant", Limit(Text(item, "text"))));
        if (kind == "userMessage" && item.TryGetProperty("content", out JsonElement content))
            PutMessage(new(identifier, "user", Limit(string.Join("\n", content.EnumerateArray().Where(entry => Text(entry, "type") == "text").Select(entry => Text(entry, "text"))))));
        if (kind == "commandExecution") PutMessage(new(identifier, "activity", "コマンド " + Text(item, "status") + "\n" + Limit(Text(item, "command"))));
        if (kind == "fileChange") PutMessage(new(identifier, "activity", "ファイル変更 " + Text(item, "status") + "\n" + (item.TryGetProperty("changes", out JsonElement changes) ? Limit(changes.ToString()) : "")));
        if (kind == "mcpToolCall") PutMessage(new(identifier, "activity", Text(item, "server") + " / " + Text(item, "tool") + " " + Text(item, "status")));
    }

    private void PutMessage(ChatMessage message)
    {
        // 長い会話は末尾200項目だけをメモリーとWebへ保持する。
        int position = messages.FindIndex(existing => existing.Identifier == message.Identifier);
        if (position >= 0) messages[position] = message;
        else messages.Add(message);
        if (messages.Count > 200) messages.RemoveAt(0);
    }

    private void Disconnect()
    {
        // 接続切断時に承認を無効化し、再接続で履歴を読み直す。
        lock (stateLock)
        {
            if (status == "archived") return;
            status = "uncertain";
            error = "Codexとの接続が切れました。履歴を確認するまで依頼を再送しないでください。";
            prompts.Clear();
            ready = false;
            ResponseAvailable?.Invoke(Snapshot());
        }
    }

    private void Save()
    {
        // 一時ファイルを置換して書込み途中の会話状態が残ることを防ぐ。
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(stored));
        File.Move(statePath + ".tmp", statePath, true);
    }

    private void CheckConversation(string conversation)
    {
        // 古いタブの操作が新しい会話へ混入することを防ぐ。
        if (conversation != stored.Conversation) throw new InvalidOperationException("別の画面で会話が切り替わりました。再表示してください。");
    }

    private static string Text(JsonElement element, string property)
    {
        // 任意項目の欠落やnullを空文字として扱う。
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static string Limit(string value)
    {
        // 異常に大きな出力がブラウザの表示を圧迫しないよう制限する。
        return value.Length <= 64000 ? value : value[..64000] + "\n（表示を省略しました）";
    }
}
