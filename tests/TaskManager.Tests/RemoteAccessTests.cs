using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManager.Api;
using TaskManager.Configuration;

namespace TaskManager.Tests;

/// <summary>ローカル運用とServeの信頼境界を実際のミドルウェアで検証する。</summary>
public static class RemoteAccessTests
{
    /// <summary>正規接続、なりすまし、送信元違い、PC専用操作の拒否を確認する。</summary>
    public static async Task VerifyAsync()
    {
        // テスト専用の架空URLと識別子を使い、実タスクや資格情報へ触れない。
        RemoteAccessSettings settings = new() { ServeOrigin = "https://desktop.example.ts.net", AllowedLogin = "owner@example.com" };
        await CheckAsync("local CLI", settings, false, "/api/v1/tasks", "POST", null, null, 204);
        await CheckAsync("local browser", settings, false, "/api/v1/tasks", "POST", "http://127.0.0.1:48120", null, 204);
        await CheckAsync("remote read", settings, true, "/api/v1/tasks", "GET", null, null, 204);
        await CheckAsync("remote edit", settings, true, "/api/v1/tasks/task-1", "PUT", settings.ServeOrigin, null, 204);
        await CheckAsync("remote SSE", settings, true, "/api/v1/changes", "GET", null, null, 204);
        await CheckAsync("remote synchronize", settings, true, "/api/v1/calendar/synchronize", "POST", settings.ServeOrigin, null, 204);
        await CheckAsync("remote settings read", settings, true, "/api/v1/settings", "GET", null, null, 204);
        await CheckAsync("disabled", new(), true, "/api/v1/tasks", "GET", null, null, 403);
        await CheckAsync("other user", settings, true, "/api/v1/tasks", "GET", null, "other-user", 403);
        await CheckAsync("missing identity", settings, true, "/api/v1/tasks", "GET", null, "missing-user", 403);
        await CheckAsync("duplicate identity", settings, true, "/api/v1/tasks", "GET", null, "duplicate-user", 403);
        await CheckAsync("missing proto", settings, true, "/api/v1/tasks", "GET", null, "missing-proto", 403);
        await CheckAsync("wrong host", settings, true, "/api/v1/tasks", "GET", null, "wrong-host", 403);
        await CheckAsync("local downgrade", settings, true, "/api/v1/tasks", "GET", null, "local-host", 403);
        await CheckAsync("direct LAN", settings, false, "/api/v1/tasks", "GET", null, "lan", 403);
        await CheckAsync("unknown peer", settings, false, "/api/v1/tasks", "GET", null, "no-peer", 403);
        await CheckAsync("cross origin read", settings, true, "/api/v1/tasks", "GET", "https://attacker.example", null, 403);
        await CheckAsync("missing remote origin", settings, true, "/api/v1/tasks", "POST", null, null, 403);
        await CheckAsync("null origin", settings, true, "/api/v1/tasks", "POST", "null", null, 403);
        await CheckAsync("missing update header", settings, true, "/api/v1/tasks", "POST", settings.ServeOrigin, "missing-header", 403);
        await CheckAsync("local cross origin", settings, false, "/api/v1/tasks", "POST", "https://attacker.example", null, 403);
        await CheckAsync("cross site navigation", settings, false, "/", "GET", null, "cross-site", 403);
        foreach (string path in new[] { "/api/v1/codex/reviews", "/API/V1/CODEX/task-thread/open/", "/api/v1/calendar/credentials", "/api/v1/calendar/connect/", "/api/v1/backup", "/api/v1/settings/" })
        {
            // UIの非表示だけに依存せず、同じ経路の直接呼出しも拒否する。
            await CheckAsync(path, settings, true, path, "POST", settings.ServeOrigin, null, 403);
            await CheckAsync(path, settings, false, path, "POST", null, null, 204);
        }

        // 未設定・不正設定でローカル起動を妨げずリモートだけ閉じる。
        string temporaryPath = Path.Combine(Path.GetTempPath(), $"remote-access-test-{Guid.NewGuid():N}.json");
        try
        {
            if (RemoteAccessSettings.Load(temporaryPath, NullLogger.Instance).IsConfigured()) throw new InvalidOperationException("未設定が許可されました。");
            await File.WriteAllTextAsync(temporaryPath, "{");
            if (RemoteAccessSettings.Load(temporaryPath, NullLogger.Instance).IsConfigured()) throw new InvalidOperationException("不正JSONが許可されました。");
            foreach (string origin in new[] { "http://desktop.example.ts.net", "https://desktop.example.ts.net:444", "https://desktop.example.ts.net/path", "https://desktop.example.ts.net?query=1", "https://desktop.example.ts.net/", "https://attacker.example" })
            {
                if (new RemoteAccessSettings { ServeOrigin = origin, AllowedLogin = "owner@example.com" }.IsConfigured()) throw new InvalidOperationException("不正URLが許可されました。");
            }
        }
        finally
        {
            // この試験で作った一時ファイルだけを後片付けする。
            File.Delete(temporaryPath);
        }
    }

    /// <summary>実際のHTTPコンテキストへ要求を設定して応答と経路分類を検証する。</summary>
    private static async Task CheckAsync(string label, RemoteAccessSettings settings, bool remote, string path, string method, string? origin, string? variation, int expectedStatus)
    {
        // JSON拒否応答も本番と同じ経路で生成できる最小サービスを用意する。
        using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        DefaultHttpContext context = new() { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(remote ? "desktop.example.ts.net" : "127.0.0.1:48120");
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Headers["X-TaskManager-Request"] = "local";
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (remote)
        {
            context.Request.Headers["Tailscale-User-Login"] = "owner@example.com";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "100.64.0.2";
        }
        switch (variation)
        {
            case "other-user": context.Request.Headers["Tailscale-User-Login"] = "other@example.com"; break;
            case "missing-user": context.Request.Headers.Remove("Tailscale-User-Login"); break;
            case "duplicate-user": context.Request.Headers.Append("Tailscale-User-Login", "owner@example.com"); break;
            case "missing-proto": context.Request.Headers.Remove("X-Forwarded-Proto"); break;
            case "wrong-host": context.Request.Host = new HostString("attacker.example"); break;
            case "local-host": context.Request.Host = new HostString("127.0.0.1:48120"); break;
            case "lan": context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.2"); break;
            case "no-peer": context.Connection.RemoteIpAddress = null; break;
            case "missing-header": context.Request.Headers.Remove("X-TaskManager-Request"); break;
            case "cross-site": context.Request.Headers["Sec-Fetch-Site"] = "cross-site"; break;
        }
        LocalRequestMiddleware middleware = new(accepted =>
        {
            // 許可された要求だけが後続の業務処理へ到達する。
            if (!Equals(accepted.Items[LocalRequestMiddleware.RemoteAccessItem], remote)) throw new InvalidOperationException("経路分類が違います。");
            accepted.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, settings);
        await middleware.InvokeAsync(context);
        if (context.Response.StatusCode != expectedStatus) throw new InvalidOperationException($"{label}: expected {expectedStatus}, actual {context.Response.StatusCode}");
        await context.Response.Body.DisposeAsync();
    }
}
