using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TaskManager.Services;

/// <summary>音声とWebから共用できるApp Serverの通信境界を定義する。</summary>
public interface ICodexAppServer : IAsyncDisposable
{
    // サーバーから届く通知・確認要求と切断通知を公開する。
    event Action<JsonElement>? MessageReceived;
    event Action? Disconnected;
    Task<JsonElement> CallAsync(string method, object parameters);
    Task ReplyAsync(JsonElement identifier, object result);
}

/// <summary>Codex App Serverを非公開の標準入出力で管理する。</summary>
public sealed class CodexAppServer : ICodexAppServer
{
    // 要求と応答の対応、および起動・書込みの排他を保持する。
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim startup = new(1, 1);
    private readonly SemaphoreSlim writer = new(1, 1);
    // 子プロセス、初期化状態、終了状態を保持する。
    private Process? process;
    // 切断通知が完了してから次の子プロセスを起動するための読取タスクを保持する。
    private Task? reader;
    private bool initialized;
    private bool disposed;
    public event Action<JsonElement>? MessageReceived;
    public event Action? Disconnected;

    public async Task<JsonElement> CallAsync(string method, object parameters)
    {
        // 初回だけ起動とプロトコルの初期化を行う。
        await startup.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is not null && process.HasExited)
            {
                if (reader is not null) await reader;
                process.Dispose();
                process = null;
                initialized = false;
            }
            if (process is null)
            {
                ProcessStartInfo information = new(CodexExecutableLocator.Resolve())
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                information.ArgumentList.Add("app-server");
                information.ArgumentList.Add("--listen");
                information.ArgumentList.Add("stdio://");
                process = Process.Start(information) ?? throw new InvalidOperationException("Codexを起動できません。");
                reader = ReadAsync(process);
                _ = DrainErrorsAsync(process);
            }
            if (!initialized)
            {
                await RequestAsync("initialize", new { clientInfo = new { name = "unitodo", version = "1.0.0" } });
                await WriteAsync(new { method = "initialized" });
                initialized = true;
            }
        }
        finally { startup.Release(); }
        return await RequestAsync(method, parameters);
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        // 応答待ちは時間制限し、タイムアウト後の自動再送は行わない。
        string identifier = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[identifier] = completion;
        try
        {
            await WriteAsync(new { id = identifier, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        finally { pending.TryRemove(identifier, out _); }
    }

    public Task ReplyAsync(JsonElement identifier, object result)
    {
        // 承認はWebが保持する識別子から復元した要求にだけ返信する。
        return WriteAsync(new { id = identifier, result });
    }

    private async Task WriteAsync(object message)
    {
        // JSON行が並行書込みで混ざらないよう直列化する。
        await writer.WaitAsync();
        try
        {
            if (process is null || process.HasExited) throw new InvalidOperationException("Codexとの接続が終了しました。会話を開き直してください。");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();
        }
        finally { writer.Release(); }
    }

    private async Task ReadAsync(Process child)
    {
        // 応答は呼出元へ、通知と確認要求はサービスへ渡す。
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement message = document.RootElement;
                if (message.TryGetProperty("method", out _)) MessageReceived?.Invoke(message.Clone());
                else if (message.TryGetProperty("id", out JsonElement identifier)
                    && identifier.ValueKind == JsonValueKind.String
                    && pending.TryRemove(identifier.GetString()!, out TaskCompletionSource<JsonElement>? completion))
                {
                    if (message.TryGetProperty("error", out JsonElement failure))
                    {
                        string explanation = failure.TryGetProperty("message", out JsonElement detail) ? detail.GetString() ?? "要求に失敗しました。" : "要求に失敗しました。";
                        completion.TrySetException(new InvalidOperationException("Codex: " + explanation[..Math.Min(explanation.Length, 500)]));
                    }
                    else completion.TrySetResult(message.GetProperty("result").Clone());
                }
            }
        }
        catch (Exception) { /* 通信不良は切断状態として通知する。 */ }
        finally
        {
            foreach (TaskCompletionSource<JsonElement> completion in pending.Values)
                completion.TrySetException(new InvalidOperationException("Codexとの接続が切れました。送信済みの依頼は再送せず、履歴を確認してください。"));
            if (!disposed) Disconnected?.Invoke();
        }
    }

    private static async Task DrainErrorsAsync(Process child)
    {
        // 個人情報を含み得る標準エラーを保存せず、パイプの詰まりを防ぐ。
        try
        {
            char[] buffer = new char[2048];
            while (await child.StandardError.ReadAsync(buffer) > 0) { }
        }
        catch (Exception) { /* 子プロセス終了時のパイプ切断を許容する。 */ }
    }

    public async ValueTask DisposeAsync()
    {
        // アプリ終了時に所有する子プロセスとその子孫だけを停止する。
        disposed = true;
        if (process is not null)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}
