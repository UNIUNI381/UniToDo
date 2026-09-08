using System.Net;
using System.Text.Json;
using TaskManager.Services;

namespace TaskManager.Api;

/// <summary>ローカル接続と更新リクエストの送信元を検証する。</summary>
public sealed class LocalRequestMiddleware(RequestDelegate next)
{
    // 次のHTTP処理を保持する。
    private readonly RequestDelegate nextMiddleware = next;

    /// <summary>ローカル以外の接続と保護ヘッダーのない更新を拒否する。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Kestrel設定に加えて接続元IPでも外部アクセスを防ぐ。
        IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "ローカル接続だけを許可しています。" });
            return;
        }

        // DNS Rebinding攻撃を防ぐためHostヘッダーのホスト名とポート番号を検証する。
        HostString requestHost = context.Request.Host;
        string hostName = requestHost.Host;
        bool isAllowedHost = string.Equals(hostName, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostName, "[::1]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostName, "::1", StringComparison.OrdinalIgnoreCase);
        bool isAllowedPort = !requestHost.Port.HasValue || requestHost.Port.Value == 48120;
        if (!isAllowedHost || !isAllowedPort)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "不正なHostヘッダーです。" });
            return;
        }

        // 外部Webサイトからのクロスサイト要求を遮断する。
        if (context.Request.Headers.TryGetValue("Sec-Fetch-Site", out var secFetchSite)
            && string.Equals(secFetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "クロスオリジン要求は禁止されています。" });
            return;
        }

        bool isMutation = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
        if (isMutation && context.Request.Headers["X-TaskManager-Request"] != "local")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "ローカル更新ヘッダーがありません。" });
            return;
        }
        try
        {
            await nextMiddleware(context);
        }
        catch (Exception requestError)
        {
            // 既知の入力エラーと予期しないエラーを同じJSON形式で返す。
            if (requestError is TimeEntryOverlapException overlapError)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new
                    {
                        error = overlapError.Message,
                        requiresConfirmation = true,
                        conflicts = overlapError.Conflicts
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
                    context.RequestAborted);
                return;
            }
            context.Response.StatusCode = requestError switch
            {
                KeyNotFoundException => StatusCodes.Status404NotFound,
                InvalidOperationException or ArgumentException or FileNotFoundException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new { error = requestError.Message },
                cancellationToken: context.RequestAborted);
        }
    }
}
