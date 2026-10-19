using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskManager.Cli;

namespace TaskManager.Tests;

/// <summary>CLI内の保存後読戻しと再送防止応答を検証する。</summary>
public static class CliVerifiedWriteTests
{
    private const string SavedTask = """
        {"identifier":"TASK-101","projectIdentifier":"PROJECT-1","status":"実行可能","title":"資料を確認する","deadlineAt":null,"deadlineType":"目安","deadlineOrigin":"auto","estimatedMinutes":30,"importance":3,"completionCondition":"確認結果を記録する","dependencyIdentifiers":[],"details":"変更後"}
        """;

    /// <summary>追加、更新、状態変更、下書きの検証結果を確認する。</summary>
    public static async Task VerifyAsync()
    {
        // 依存一覧だけの順序差と、実際の保存内容の相違を区別する。
        await VerifyDependenciesAsync();

        // タスク追加と部分更新は保存応答と独立したGETが一致した場合だけ検証済みにする。
        using (ResponseHandler handler = new(SavedTask, SavedTask))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["add", "--title", "資料を確認する", "--verify", "--json"], string.Empty);
            Assert(exitCode == 0 && output.GetProperty("verified").GetBoolean(), "追加の読戻しが検証済みになりません。");
            Assert(output.GetProperty("identifier").GetString() == "TASK-101", "追加結果のIDが失われました。");
            Assert(handler.Mutations == 1 && handler.Readbacks == 1, "追加の保存と読戻しの回数が不正です。");
        }
        using (ResponseHandler handler = new(SavedTask, SavedTask))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["update", "TASK-101", "--file", "-", "--verify", "--json"],
                "{\"Details\":\"変更後\"}");
            Assert(exitCode == 0 && output.GetProperty("verified").GetBoolean(), "部分更新の変更項目を照合できません。");
            Assert(handler.Mutations == 1 && handler.Readbacks == 1, "更新の保存と読戻しの回数が不正です。");
        }

        // 更新後に内容が食い違った場合も保存成功を保持し、再送を促さない。
        using (ResponseHandler handler = new(SavedTask, SavedTask.Replace("変更後", "旧内容", StringComparison.Ordinal)))
        {
            (int exitCode, JsonElement output, string error) = await RunAsync(handler,
                ["update", "TASK-101", "--file", "-", "--verify", "--json"],
                "{\"details\":\"変更後\"}");
            Assert(exitCode == 3 && output.GetProperty("saved").GetBoolean()
                && !output.GetProperty("verified").GetBoolean(), "不一致時の部分成功が保持されません。");
            Assert(error.Contains("自動再送せず", StringComparison.Ordinal) && handler.Mutations == 1,
                "不一致時に再送防止警告がありません。");
        }
        using (ResponseHandler handler = new(SavedTask, string.Empty, HttpStatusCode.NotFound))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["add", "--title", "資料を確認する", "--verify", "--json"], string.Empty);
            Assert(exitCode == 3 && output.GetProperty("saved").GetBoolean()
                && output.GetProperty("writeResult").GetProperty("identifier").GetString() == "TASK-101",
                "読戻し失敗時に保存済みIDが失われました。");
            Assert(handler.Mutations == 1, "読戻し失敗で追加を再送しました。");
        }

        // 状態操作は推薦応答だけに依存せず、対象タスクの状態を読戻す。
        const string runningTask = """{"identifier":"TASK-101","status":"実行中","startedAt":"2026-09-23T10:00:00+09:00"}""";
        using (ResponseHandler handler = new("{}", runningTask))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["start", "TASK-101", "--verify", "--json"], string.Empty);
            Assert(exitCode == 0 && output.GetProperty("verified").GetBoolean()
                && handler.Readbacks == 1, "開始後の状態確認ができません。");
        }
        using (ResponseHandler handler = new("{}", """{"identifier":"TASK-101","status":"実行可能"}"""))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["start", "TASK-101", "--verify", "--json"], string.Empty);
            Assert(exitCode == 3 && !output.GetProperty("verified").GetBoolean(), "未反映の開始を成功と判定しました。");
        }

        // 下書きは件数、仮参照キーの対応、解決後の依存先まで照合する。
        const string draftRequest = """
            {"title":"分解","tasks":[
              {"aiReferenceKey":"STEP-1","title":"先行"},
              {"aiReferenceKey":"STEP-2","title":"後続","dependencyIdentifiers":["STEP-1"]}
            ]}
            """;
        const string draftResponse = """
            {"batchIdentifier":"BATCH-1","identifier":"BATCH-1","saved":true,"taskCount":2,
             "postProcessingSucceeded":true,"taskMappings":[
              {"aiReferenceKey":"STEP-1","taskIdentifier":"TASK-1"},
              {"aiReferenceKey":"STEP-2","taskIdentifier":"TASK-2"}]}
            """;
        const string draftReadback = """
            {"batchIdentifier":"BATCH-1","taskCount":2,"postProcessingSucceeded":true,
             "taskMappings":[{"aiReferenceKey":"STEP-1","taskIdentifier":"TASK-1"},
                             {"aiReferenceKey":"STEP-2","taskIdentifier":"TASK-2"}],
             "tasks":[{"identifier":"TASK-1","aiReferenceKey":"STEP-1","dependencyIdentifiers":[]},
                      {"identifier":"TASK-2","aiReferenceKey":"STEP-2","dependencyIdentifiers":["TASK-1"]}]}
            """;
        using (ResponseHandler handler = new(draftResponse, draftReadback))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["draft-create", "--file", "-", "--idempotency-key", "TEST-1", "--verify", "--json"], draftRequest);
            Assert(exitCode == 0 && output.GetProperty("verified").GetBoolean()
                && handler.Mutations == 1 && handler.Readbacks == 1, "下書きの読戻しが検証済みになりません。");
        }
        string warningResponse = draftResponse
            .Replace("\"saved\":true", "\"saved\":true,\"warning\":\"保存済みだが後処理失敗\"", StringComparison.Ordinal)
            .Replace("\"postProcessingSucceeded\":true", "\"postProcessingSucceeded\":false", StringComparison.Ordinal);
        string warningReadback = draftReadback.Replace(
            "\"postProcessingSucceeded\":true", "\"postProcessingSucceeded\":false", StringComparison.Ordinal);
        using (ResponseHandler handler = new(warningResponse, warningReadback))
        {
            (int exitCode, JsonElement output, string error) = await RunAsync(handler,
                ["draft-create", "--file", "-", "--idempotency-key", "TEST-1", "--verify", "--json"], draftRequest);
            Assert(exitCode == 0 && output.GetProperty("verified").GetBoolean()
                && error.Contains("保存済みだが後処理失敗", StringComparison.Ordinal),
                "保存後警告を重複登録せず表示できません。");
        }
        using (ResponseHandler handler = new(draftResponse, draftReadback.Replace("TASK-1\"]}]", "TASK-OTHER\"]}]", StringComparison.Ordinal)))
        {
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["draft-create", "--file", "-", "--idempotency-key", "TEST-1", "--verify", "--json"], draftRequest);
            Assert(exitCode == 3 && !output.GetProperty("verified").GetBoolean(), "下書き依存の不一致を見逃しました。");
        }
    }

    /// <summary>依存IDの順序、件数、値と他項目の相違をCLI境界で検証する。</summary>
    private static async Task VerifyDependenciesAsync()
    {
        // 実データを使わず、入力順とDBの読戻し順が異なる応答を再現する。
        string[] dependencies = ["TASK-300", "DRAFT-100", "TASK-200"];
        foreach ((string[] expected, string[] actual, bool verified) in new (string[], string[], bool)[]
        {
            (dependencies, ["DRAFT-100", "TASK-200", "TASK-300"], true),
            (dependencies, ["task-200", "task-300", "draft-100"], true),
            ([], [], true),
            (["TASK-300"], ["TASK-300"], true),
            (dependencies, ["DRAFT-100", "TASK-300"], false),
            (dependencies, ["DRAFT-100", "TASK-200", "TASK-OTHER"], false),
            (dependencies, ["DRAFT-100", "TASK-200", "TASK-300", "TASK-OTHER"], false),
            (dependencies, ["DRAFT-100", "TASK-200", "TASK-300", "TASK-300"], false),
            (["TASK-300", "TASK-300", "TASK-200"], ["TASK-300", "TASK-200", "TASK-200"], false)
        })
        {
            JsonObject writeResult = JsonNode.Parse(SavedTask)!.AsObject();
            writeResult["dependencyIdentifiers"] = JsonSerializer.SerializeToNode(expected);
            JsonObject readback = writeResult.DeepClone().AsObject();
            readback["dependencyIdentifiers"] = JsonSerializer.SerializeToNode(actual);
            foreach (string command in new[] { "add", "update" })
            {
                using ResponseHandler handler = new(writeResult.ToJsonString(), readback.ToJsonString());
                string[] arguments = command == "add" ? ["add"] : ["update", "TASK-101"];
                (int exitCode, JsonElement output, string error) = await RunAsync(handler,
                    [.. arguments, "--file", "-", "--verify", "--json"],
                    JsonSerializer.Serialize(new { DependencyIdentifiers = expected }));
                Assert(exitCode == (verified ? 0 : 3) && output.GetProperty("saved").GetBoolean()
                    && output.GetProperty("verified").GetBoolean() == verified,
                    $"{command}の依存ID検証が不正です: {readback}");
                Assert(handler.Mutations == 1 && handler.Readbacks == 1, "保存または読戻しを繰り返しました。");
                Assert(verified || error.Contains("自動再送せず", StringComparison.Ordinal), "不一致時の再送防止警告が失われました。");
                Assert(output.GetProperty("writeResult").GetProperty("dependencyIdentifiers").GetArrayLength() == expected.Length,
                    "比較処理が保存応答を書き換えました。");
            }
        }

        // 同じ依存関係でも、変更した他項目や依存配列の不正な型は不一致として扱う。
        JsonObject saved = JsonNode.Parse(SavedTask)!.AsObject();
        saved["dependencyIdentifiers"] = JsonSerializer.SerializeToNode(dependencies);
        foreach ((string field, JsonNode? value) in new (string, JsonNode?)[]
        {
            ("details", JsonValue.Create("旧内容")),
            ("deadlineAt", JsonValue.Create("2026-10-20T20:00:00+09:00")),
            ("projectIdentifier", JsonValue.Create("PROJECT-OTHER")),
            ("dependencyIdentifiers", null),
            ("dependencyIdentifiers", JsonNode.Parse("[1,2,3]"))
        })
        {
            JsonObject readback = saved.DeepClone().AsObject();
            readback["dependencyIdentifiers"] = JsonSerializer.SerializeToNode(dependencies.Reverse());
            readback[field] = value;
            using ResponseHandler handler = new(saved.ToJsonString(), readback.ToJsonString());
            (int exitCode, JsonElement output, _) = await RunAsync(handler,
                ["update", "TASK-101", "--file", "-", "--verify", "--json"], saved.ToJsonString());
            Assert(exitCode == 3 && output.GetProperty("saved").GetBoolean()
                && !output.GetProperty("verified").GetBoolean(), $"{field}の不一致を見逃しました。");
        }
    }

    /// <summary>模擬HTTP応答でCLIを実行し、JSONと標準エラーを取得する。</summary>
    internal static async Task<(int ExitCode, JsonElement Output, string Error)> RunAsync(
        HttpMessageHandler handler,
        string[] arguments,
        string input)
    {
        // コンソール差替えを必ず戻し、正本APIへの接続を防ぐ。
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new();
        using StringWriter error = new();
        using StringReader source = new(input);
        using HttpClient client = new(handler, disposeHandler: false);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = await new CliRunner(client, source).RunAsync(arguments);
            return (exitCode, JsonSerializer.Deserialize<JsonElement>(output.ToString()), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    /// <summary>条件が満たされない場合にテストを失敗させる。</summary>
    private static void Assert(bool condition, string message)
    {
        // 原因が分かる短いメッセージを返す。
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ResponseHandler(
        string mutationResponse,
        string readbackResponse,
        HttpStatusCode readbackStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        // 保存応答、読戻し応答、読戻し状態と要求回数を保持する。
        private readonly string mutation = mutationResponse;
        private readonly string readback = readbackResponse;
        private readonly HttpStatusCode status = readbackStatus;
        public int Mutations { get; private set; }
        public int Readbacks { get; private set; }

        /// <summary>ヘルス、保存、保存後の読戻しへ合成応答を返す。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // ヘルス確認を除き、読戻しと保存の呼出し回数を区別する。
            if (request.RequestUri!.AbsolutePath == "/api/v1/health")
                return Task.FromResult(CreateResponse(HttpStatusCode.OK, "{}"));
            if (request.Method == HttpMethod.Get)
            {
                Readbacks++;
                return Task.FromResult(CreateResponse(status, readback));
            }
            Mutations++;
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, mutation));
        }

        /// <summary>指定した状態と本文のHTTP応答を作る。</summary>
        private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body)
        {
            // JSON応答で実際のCLI境界と同じ読取りを通す。
            return new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
