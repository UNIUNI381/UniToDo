using System.Net;
using System.Text;
using System.Text.Json;
using TaskManager.Cli;

namespace TaskManager.Tests;

/// <summary>CLIの標準入力・選択出力を実際の引数解析とHTTP境界で検証する。</summary>
public static class CliJsonTests
{
    public static async Task VerifyAsync()
    {
        // 各JSON入力コマンドがファイルなしで日本語・引用符・nullを保持することを確認する。
        string[][] commands = [
            ["add"], ["update", "sample"], ["draft-create"], ["project", "create"],
            ["project", "update", "sample"], ["project", "context-add", "sample"],
            ["project", "context-update", "sample", "context"], ["time", "add"], ["time", "update", "sample"]
        ];
        foreach (string[] command in commands)
        {
            using RecordingHandler handler = new();
            string input = command[0] == "update"
                ? "{\"title\":\"日本語と\\\"引用\\\"😀\",\"deadlineAt\":null}"
                : "{\"title\":\"日本語と\\\"引用\\\"😀\"}";
            (int exitCode, string output) = await RunAsync(handler, [.. command, "--file", "-", "--json"], input);
            Assert(exitCode == 0 && handler.Mutations == 1, "標準入力から更新APIへ到達しませんでした。");
            JsonElement body = JsonSerializer.Deserialize<JsonElement>(handler.Body!);
            if (command[0] == "update")
            {
                Assert(body.GetProperty("title").GetString() == "日本語と\"引用\"😀", "日本語または引用符が変わりました。");
                Assert(body.GetProperty("deadlineAt").ValueKind == JsonValueKind.Null && !body.TryGetProperty("status", out _), "部分更新のnullと省略が失われました。");
            }
            Assert(output.Trim() == "{}", "応答JSONが変わりました。");
        }

        // 日付だけの期限を20時へ補い、明示された0時は変更しない。
        foreach (string deadline in new[] { "2026-09-12", "2026-09-12T00:00:00+09:00" })
        {
            using RecordingHandler handler = new();
            (int exitCode, _) = await RunAsync(handler, ["add", "--title", "期限検証", "--deadline", deadline, "--json"], "");
            DateTimeOffset actualDeadline = JsonSerializer.Deserialize<JsonElement>(handler.Body!).GetProperty("deadlineAt").GetDateTimeOffset();
            Assert(exitCode == 0 && actualDeadline.Day == 12 && actualDeadline.Hour == (deadline.Length == 10 ? 20 : 0), "CLIの期限時刻が不正です。");
        }

        // 空・破損JSONと出力指定の誤りでは、更新リクエストを送らない。
        using (RecordingHandler handler = new())
        {
            (int exitCode, _) = await RunAsync(handler, ["update", "sample", "--file", "-", "--json"], "\uFEFF{\"details\":\"日本語\"}");
            Assert(exitCode == 0 && JsonSerializer.Deserialize<JsonElement>(handler.Body!).GetProperty("details").GetString() == "日本語", "UTF-8 BOM付きパイプ入力を解析できません。");
        }
        foreach (string input in new[] { "", "{", "null", "[]" })
        {
            using RecordingHandler handler = new();
            (int exitCode, _) = await RunAsync(handler, ["add", "--file", "-", "--json"], input);
            Assert(exitCode != 0 && handler.Mutations == 0, "不正な標準入力で登録されました。");
        }
        foreach (string[] arguments in new string[][] {
            ["add", "--title", "sample", "--fields", "title"],
            ["add", "--title", "sample", "--fields"],
            ["add", "--title", "sample", "--file"],
            ["add", "--file", "-", "--file", "other.json"],
            ["project", "prepare", "sample", "--fields", "resolution.status"] })
        {
            using RecordingHandler handler = new();
            (int exitCode, _) = await RunAsync(handler, arguments, "{}");
            Assert(exitCode != 0 && handler.Requests == 0, "不正オプションでAPIが呼ばれました。");
        }

        // 配列・ネスト・nullの形を維持し、不要項目と整形用文字を除去する。
        using (RecordingHandler handler = new() { Response = "[{\"identifier\":\"sample\",\"title\":\"日本語\",\"details\":\"非表示\"}]" })
        {
            (int exitCode, string output) = await RunAsync(handler, ["list", "--fields", "identifier,title"], "");
            Assert(exitCode == 0 && output.Trim() == "[{\"identifier\":\"sample\",\"title\":\"日本語\"}]", "選択出力または日本語の簡潔なJSONが不正です。");
        }
        using (RecordingHandler handler = new() { Response = "{\"recommendation\":{\"task\":{\"title\":\"日本語\",\"details\":\"非表示\"}},\"emptyReason\":null}" })
        {
            (int exitCode, string output) = await RunAsync(handler, ["now", "--fields", "recommendation.task.title,emptyReason"], "");
            Assert(exitCode == 0 && !output.Contains("details") && output.Contains("\"emptyReason\":null"), "ネスト選択またはnull保持が不正です。");
        }
        foreach (string response in new[] { "[]", "null" })
        {
            using RecordingHandler handler = new() { Response = response };
            (int exitCode, string output) = await RunAsync(handler, ["time", "active", "--fields", "identifier"], "");
            Assert(exitCode == 0 && output.Trim() == response, "空の形が変わりました。");
        }
        using (RecordingHandler handler = new() { Response = "{\"identifier\":\"sample\"}" })
        {
            (int exitCode, _) = await RunAsync(handler, ["get", "sample", "--fields", "identifier"], "");
            Assert(exitCode == 0 && handler.LastPath == "/api/v1/tasks/sample", "単一タスクを取得していません。");
            (exitCode, _) = await RunAsync(handler, ["get", "sample", "--fields", "unknown"], "");
            Assert(exitCode != 0, "存在しない項目が無視されました。");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(RecordingHandler handler, string[] arguments, string input)
    {
        // コンソールだけを一時置換し、本番と同じCLI経路を偽HTTPへ接続する。
        TextReader originalInput = Console.In;
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using StringReader source = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        using HttpClient client = new(handler, disposeHandler: false);
        try
        {
            Console.SetIn(source);
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = await new CliRunner(client).RunAsync(arguments);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static void Assert(bool condition, string message)
    {
        // 期待値と異なる場合はテストを失敗させる。
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        // HTTP応答と、最後の要求・更新回数を保持する。
        public string Response { get; init; } = "{}";
        public string? Body { get; private set; }
        public string? LastPath { get; private set; }
        public int Mutations { get; private set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 実サーバーに接続せず、送信JSONと取得先を観測する。
            Requests++;
            LastPath = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Mutations++;
                Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
}
