using System.Text.Json;
using TaskManager.Configuration;
using TaskManager.Services;

namespace TaskManager.Tests;

/// <summary>実タスクへ触れず会話の競合、復旧、承認境界を検証する。</summary>
public static class CodexChatTests
{
    public static async Task VerifyAsync()
    {
        // 一時領域と偽サーバーで送信、承認、切断を再現する。
        string? previousDirectory = Environment.GetEnvironmentVariable("TASKMANAGER_DATA_DIR");
        string directory = Path.Combine(Path.GetTempPath(), "unitodo-chat-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", directory);
        try
        {
            FakeServer server = new();
            CodexChatService service = new(server, new TaskManagerPaths(), new UiChangeNotifier());
            ChatSnapshot initial = await service.GetAsync();
            Assert(server.StartConfiguration.GetProperty("sandbox").GetString() == "workspace-write", "Sandbox must remain restricted to workspace");
            Assert(server.StartConfiguration.GetProperty("approvalPolicy").GetString() == "never", "Routine execution must not prompt");
            Assert(server.StartConfiguration.GetProperty("serviceTier").GetString() == "fast", "Web conversation must request fast service tier");
            JsonElement configuration = server.StartConfiguration.GetProperty("config");
            Assert(configuration.GetProperty("shell_environment_policy.set").GetProperty("PATH").GetString()!.Split(Path.PathSeparator)[0]
                == AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "Dedicated PATH must prioritize the installed CLI");
            Assert(!configuration.GetProperty("sandbox_workspace_write.network_access").GetBoolean(), "Network must remain restricted");
            Assert(configuration.GetProperty("sandbox_workspace_write.writable_roots").EnumerateArray().Single().GetString() == AppContext.BaseDirectory, "Only CLI installation may be added");
            ChatSubmission request = new(initial.Conversation, Guid.NewGuid().ToString(), "テスト依頼本文");
            await Task.WhenAll(service.SendAsync(request), service.SendAsync(request));
            Assert(server.SendCount == 1, "Duplicate requests must not run twice");
            Assert(server.TurnConfiguration.GetProperty("approvalPolicy").GetString() == "never", "Each turn must retain no-prompt policy");
            Assert(server.TurnConfiguration.GetProperty("serviceTier").GetString() == "fast", "Each turn must retain fast service tier");
            Assert(!server.TurnConfiguration.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean(), "Each turn must retain network restriction");
            await RejectAsync(() => service.SendAsync(request with { Text = "別の依頼" }));
            await RejectAsync(() => service.SendAsync(request with { RequestIdentifier = Guid.NewGuid().ToString() }));
            await RejectAsync(() => service.NewAsync(initial.Conversation));
            server.Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "other-thread", turnId = "turn-one", itemId = "message", delta = "別会話" } });
            server.Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "test-thread", turnId = "turn-one", itemId = "message", delta = "応答" } });
            Assert(service.Snapshot().Messages.Single().Text == "応答", "Unowned notifications must not appear");
            server.Emit(new { id = "approval-one", method = "item/commandExecution/requestApproval", @params = new { threadId = "test-thread", turnId = "turn-one", itemId = "command", command = "read-only-test", reason = "確認用" } });
            string approval = service.Snapshot().Prompts.Single().Identifier;
            await service.AnswerAsync(new(approval, "accept", null));
            await RejectAsync(() => service.AnswerAsync(new(approval, "accept", null)));
            Assert(server.Replies.Count == 1 && server.Replies[0].GetProperty("decision").GetString() == "accept", "Approval must be single-use");
            server.Emit(new { id = 42, method = "item/tool/requestUserInput", @params = new { threadId = "test-thread", turnId = "turn-one", itemId = "question", questions = new[] { new { id = "choice", header = "確認", question = "選択してください" } } } });
            string question = service.Snapshot().Prompts.Single().Identifier;
            await RejectAsync(() => service.AnswerAsync(new(question, "accept", null)));
            Assert(service.Snapshot().Prompts.Length == 1, "Validation must not consume a question");
            await service.AnswerAsync(new(question, "accept", new() { ["choice"] = ["回答"] }));
            Assert(server.Replies[1].GetProperty("answers").GetProperty("choice").GetProperty("answers")[0].GetString() == "回答", "Question answer must match protocol");
            await service.InterruptAsync(initial.Conversation);
            Assert(service.Snapshot().Status == "idle", "Interrupt must release active turn");
            string stored = File.ReadAllText(Path.Combine(directory, "codex-chat-state.json"));
            Assert(!stored.Contains("テスト依頼本文") && !stored.Contains("回答"), "App state must not persist conversation content");
            CodexChatService restored = new(new FakeServer(), new TaskManagerPaths(), new UiChangeNotifier());
            await restored.GetAsync();
            await restored.SendAsync(request);
            Assert(restored.Snapshot().Status == "idle", "Restart must retain deduplication receipts");
            ChatSnapshot next = await service.NewAsync(initial.Conversation);
            await RejectAsync(() => service.SendAsync(request));
            server.FailSend = true;
            ChatSubmission uncertain = new(next.Conversation, Guid.NewGuid().ToString(), "通信不明テスト");
            await RejectAsync(() => service.SendAsync(uncertain));
            Assert(service.Snapshot().Status == "uncertain", "Unknown result must not report success");
            await service.ReconnectAsync(next.Conversation);
            await service.SendAsync(uncertain);
            Assert(server.SendCount == 2, "Unknown request must not be automatically retried");
            server.CutConnection();
            Assert(service.Snapshot().Status == "uncertain", "Disconnect must surface uncertainty");
            await service.GetAsync();
            Assert(service.Snapshot().Status == "idle", "Reconnect must recover history");

            // 音声も同じ会話と単一実行制御を使い、完了応答をWindows表示へ渡す。
            CodexVoiceChatRunner voice = new(service);
            TaskManager.Domain.CodexSubmission voiceSubmission = new()
            {
                ThreadIdentifier = service.Snapshot().Conversation,
                ReviewIdentifier = Guid.NewGuid().ToString("N"),
                Text = "音声の確認済み本文"
            };
            server.FailSend = false;
            Task<TaskManager.Domain.CodexCommandResult> voiceResult = voice.SendAsync(voiceSubmission, CancellationToken.None);
            Assert(server.TurnConfiguration.GetProperty("threadId").GetString() == "test-thread", "Voice must use the Web thread");
            Assert(server.TurnConfiguration.GetProperty("input")[0].GetProperty("text").GetString() == voiceSubmission.Text, "Voice text must reach App Server unchanged");
            await RejectAsync(() => service.SendAsync(new(voiceSubmission.ThreadIdentifier, Guid.NewGuid().ToString(), "同時Web送信")));
            server.Emit(new { method = "turn/completed", @params = new { threadId = "test-thread", turn = new { id = "turn-one", status = "completed", items = new[] { new { id = "voice-response", type = "agentMessage", text = "音声への回答" } } } } });
            TaskManager.Domain.CodexCommandResult completedVoice = await voiceResult;
            Assert(completedVoice.IsSuccess && completedVoice.OutputMessage == "音声への回答", "Voice must display its response");

            // 追加質問はWebへ引き継ぎ、勝手な回答や再送を行わない。
            voiceSubmission = new() { ThreadIdentifier = service.Snapshot().Conversation, ReviewIdentifier = Guid.NewGuid().ToString("N"), Text = "追加確認の依頼" };
            voiceResult = voice.SendAsync(voiceSubmission, CancellationToken.None);
            server.Emit(new { id = "voice-question", method = "item/tool/requestUserInput", @params = new { threadId = "test-thread", turnId = "turn-one", questions = new[] { new { id = "choice", question = "確認" } } } });
            completedVoice = await voiceResult;
            Assert(completedVoice.IsSuccess && service.Snapshot().Prompts.Length == 1, "Voice questions must remain available in Web UI");
            await service.InterruptAsync(voiceSubmission.ThreadIdentifier);

            // 送信結果不明は確認キューへ戻せない結果として返す。
            server.FailSend = true;
            completedVoice = await voice.SendAsync(new() { ThreadIdentifier = service.Snapshot().Conversation, ReviewIdentifier = Guid.NewGuid().ToString("N"), Text = "結果不明の音声" }, CancellationToken.None);
            Assert(!completedVoice.IsSuccess && !completedVoice.CanRetry, "Uncertain voice submissions must not be requeued");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", previousDirectory);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public static async Task<int> LiveAsync(bool verifyTaskCommand = false)
    {
        // 実環境のログインとプロトコルだけを検証し、タスクの読取や変更は行わない。
        string? previousDirectory = Environment.GetEnvironmentVariable("TASKMANAGER_DATA_DIR");
        string directory = Path.Combine(Path.GetTempPath(), "unitodo-codex-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", directory);
        try
        {
            await using CodexAppServer server = new();
            // 実CLI試験では成功したコマンド出力を確認し、モデルの自己申告だけに依存しない。
            bool commandSucceeded = false;
            server.MessageReceived += envelope =>
            {
                // 実タスク本文は出力せず、ラッパー経由の読取成功マーカーだけを検査する。
                if (envelope.TryGetProperty("method", out JsonElement method) && method.GetString() == "item/completed")
                {
                    JsonElement item = envelope.GetProperty("params").GetProperty("item");
                    // 読取試験の失敗時だけシェル診断を示し、成功時の実データは表示しない。
                    if (item.GetProperty("type").GetString() == "commandExecution"
                        && item.TryGetProperty("exitCode", out JsonElement failureCode)
                        && failureCode.ValueKind == JsonValueKind.Number && failureCode.GetInt32() != 0)
                        Console.WriteLine(item.GetProperty("aggregatedOutput").GetString());
                    if (item.GetProperty("type").GetString() == "commandExecution"
                        && item.TryGetProperty("exitCode", out JsonElement exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() == 0
                        && item.GetProperty("command").GetString()!.Contains("taskctl")
                        && !item.GetProperty("command").GetString()!.Contains("invoke-taskctl.ps1")
                        && item.GetProperty("aggregatedOutput").GetString()!.Contains("TASKCTL_OK")) commandSucceeded = true;
                }
            };
            CodexChatService service = new(server, new TaskManagerPaths(), new UiChangeNotifier());
            ChatSnapshot initial = await service.GetAsync();
            string prompt = verifyTaskCommand
                ? "通常のタスク操作権限を確認します。taskctl list --fields identifier を1回だけ直接実行してください。ラッパーや絶対パス、追加のpowershell起動は使わないでください。出力はPowerShellの変数に格納し、終了コード0かつJSON解析成功のときだけTASKCTL_OKを出力してください。タスク名・内容やJSON原文は出力しないでください。データは変更しないでください。成功した場合は「接続確認できました」とだけ回答してください。"
                : "接続の動作確認です。ツールやスキルの読込み、外部データの参照・変更は一切せず、「接続確認できました」とだけ回答してください。";
            await service.SendAsync(new(initial.Conversation, Guid.NewGuid().ToString(), prompt));
            for (int attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(1000);
                ChatSnapshot state = service.Snapshot();
                if (state.Prompts.Length > 0) throw new InvalidOperationException("Unexpected approval during no-tool smoke test");
                if (state.Status == "idle")
                {
                    Assert(state.Error is null && state.Messages.Any(message => message.Role == "assistant" && message.Text.Contains("接続確認")), "Live Codex response missing: " + state.Error);
                    Assert(!verifyTaskCommand || commandSucceeded, "Routine task CLI must succeed without approval");
                    Console.WriteLine("PASS live App Server: initialization, turn streaming, completion");
                    await service.ReconnectAsync(initial.Conversation);
                    Assert(service.Snapshot().Messages.Any(message => message.Role == "assistant"), "Live history missing");
                    Console.WriteLine("PASS live App Server: durable history read");
                    return 0;
                }
            }
            throw new TimeoutException("Live Codex turn did not complete");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASKMANAGER_DATA_DIR", previousDirectory);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        // 期待した状態でなければテストを失敗させる。
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task<ChatSnapshot>> action)
    {
        // 利用者向け検証エラーで拒否されることを確認する。
        try { await action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected rejection");
    }

    private sealed class FakeServer : ICodexAppServer
    {
        // 要求回数、返信、設定と障害注入状態を保持する。
        public int SendCount { get; private set; }
        public bool FailSend { get; set; }
        public List<JsonElement> Replies { get; } = [];
        public JsonElement StartConfiguration { get; private set; }
        // 各送信で適用された権限設定を保持する。
        public JsonElement TurnConfiguration { get; private set; }
        public event Action<JsonElement>? MessageReceived;
        public event Action? Disconnected;

        public Task<JsonElement> CallAsync(string method, object parameters)
        {
            // 実通信せず、順序の異なる通知と応答を再現する。
            if (method is "thread/start" or "thread/resume" or "thread/read")
            {
                StartConfiguration = JsonSerializer.SerializeToElement(parameters);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { thread = new { id = "test-thread", turns = Array.Empty<object>() } }));
            }
            if (method == "turn/start")
            {
                TurnConfiguration = JsonSerializer.SerializeToElement(parameters);
                SendCount++;
                if (FailSend) throw new InvalidOperationException("Simulated send failure");
                Emit(new { method = "turn/started", @params = new { threadId = "test-thread", turn = new { id = "turn-one", status = "inProgress" } } });
            }
            if (method == "turn/interrupt") Emit(new { method = "turn/completed", @params = new { threadId = "test-thread", turn = new { id = "turn-one", status = "interrupted", items = Array.Empty<object>() } } });
            return Task.FromResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-one", status = "inProgress" } }));
        }

        public Task ReplyAsync(JsonElement identifier, object result)
        {
            // 承認内容の検証用に返信だけを記録する。
            Replies.Add(JsonSerializer.SerializeToElement(result));
            return Task.CompletedTask;
        }

        public void Emit(object message)
        {
            // 通知やサーバー要求をサービスへ即時配送する。
            MessageReceived?.Invoke(JsonSerializer.SerializeToElement(message));
        }

        public void CutConnection()
        {
            // 通信断通知を意図的に発生させる。
            Disconnected?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            // 偽サーバーには解放する外部資源がない。
            return ValueTask.CompletedTask;
        }
    }
}
