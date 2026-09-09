using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskManager.Configuration;

namespace TaskManager.Services;

// Webと将来の音声入力で共用する送信・確認・表示データを定義する。
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
    // 通信先、更新通知、保存先と固定スキルの場所を保持する。
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

    public CodexChatService(ICodexAppServer server, TaskManagerPaths paths, UiChangeNotifier changes)
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
        try { await EnsureReadyAsync(); return Snapshot(); }
        finally { actions.Release(); }
    }

    public ChatSnapshot Snapshot()
    {
        // 更新中のコレクションを直接公開せずコピーする。
        lock (stateLock) return new(stored.Conversation, status, error, messages.ToArray(), prompts.Values.Select(value => value.Prompt).ToArray());
    }

    private async Task EnsureReadyAsync()
    {
        // 作業ディレクトリ・スキル・権限はサーバー側で固定し、ブラウザ指定を受け付けない。
        if (ready) return;
        Directory.CreateDirectory(workspace);
        if (!File.Exists(skillPath)) throw new InvalidOperationException("タスク操作スキルが見つかりません。UniToDoを再インストールしてください。");
        string instructions = "あなたはUniToDoのタスク管理アシスタントです。日本語で短く回答してください。"
            + "タスク操作には必ず $manage-local-tasks を読み、その scripts/invoke-taskctl.ps1 のみを使ってください。"
            + "通常のタスク操作に必要なスキル読取りとラッパー実行は承認済みです。スクリプトを実行してよいかの確認は不要です。"
            + "PowerShellスクリプトは powershell -NoProfile -ExecutionPolicy Bypass -File に絶対パスを渡して実行してください。"
            + "ただし対象や依頼内容が曖昧な場合、削除やスキルで指定された業務上の確認は省略しないでください。"
            + "SQLite・APIの直接操作、ソース編集、プログラム開発は行わないでください。"
            + "実行できない処理は制約を説明してください。既存の音声用会話や他のCodex会話を操作しないでください。"
            + "確認が必要な場合は質問して回答を待ってください。スキル絶対パス: " + skillPath;
        Dictionary<string, object?> configuration = new()
        {
            ["cwd"] = workspace, ["model"] = "gpt-5.6-luna", ["sandbox"] = "workspace-write",
            ["approvalPolicy"] = "never", ["approvalsReviewer"] = "user", ["developerInstructions"] = instructions,
            ["config"] = new Dictionary<string, object>
            {
                ["model_reasoning_effort"] = "low",
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
                    input = new object[] { new { type = "text", text = submission.Text }, new { type = "skill", name = "manage-local-tasks", path = skillPath } },
                    effort = "low", approvalPolicy = "never", approvalsReviewer = "user",
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
        finally { actions.Release(); }
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
                if (status != "idle") throw new InvalidOperationException("停止または現在の処理が完了してから新しい会話を開始してください。");
                stored = new(Guid.NewGuid().ToString("N"), null, []);
                Save();
                ready = false;
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
        // 外部MCPの複雑な入力やログインはPCで行い、この画面から暗黙承認しない。
        if (method == "mcpServer/elicitation/request" && answer.Action != "accept")
            return new { action = answer.Action, content = (object?)null };
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
            string notificationTurn = Text(parameters, "turnId");
            if (turn is not null && notificationTurn.Length > 0 && notificationTurn != turn) return;
            if (envelope.TryGetProperty("id", out JsonElement identifier))
            {
                string promptIdentifier = Guid.NewGuid().ToString("N");
                string kind = method == "item/tool/requestUserInput" ? "question"
                    : method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval" ? "approval" : "unsupported";
                string description = Text(parameters, "reason");
                if (parameters.TryGetProperty("command", out JsonElement command)) description += "\n" + command.ToString();
                if (parameters.TryGetProperty("permissions", out JsonElement permissions)) description += "\n" + permissions.ToString();
                string? activity = messages.FirstOrDefault(message => message.Identifier == Text(parameters, "itemId"))?.Text;
                if (activity is not null) description += "\n" + activity;
                if (parameters.TryGetProperty("networkApprovalContext", out JsonElement network)) description += "\n" + network.ToString();
                if (kind == "unsupported") description = "この確認はこの画面では対応していません。PCでの操作が必要な場合があります。\n" + Text(parameters, "message");
                JsonElement? questions = parameters.TryGetProperty("questions", out JsonElement requestedQuestions) ? requestedQuestions.Clone() : null;
                ChatPrompt prompt = new(promptIdentifier, kind, Limit(description), questions);
                prompts[promptIdentifier] = (identifier.Clone(), method, parameters.Clone(), prompt);
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
            status = "uncertain";
            error = "Codexとの接続が切れました。履歴を確認するまで依頼を再送しないでください。";
            prompts.Clear();
            ready = false;
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
