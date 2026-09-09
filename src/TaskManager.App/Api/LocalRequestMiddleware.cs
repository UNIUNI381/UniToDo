using System.Net;
using System.Text.Json;
using TaskManager.Services;
using TaskManager.Configuration;

namespace TaskManager.Api;

/// <summary>ローカル接続と更新リクエストの送信元を検証する。</summary>
public sealed class LocalRequestMiddleware(RequestDelegate next, RemoteAccessSettings remoteSettings)
{
    // 次のHTTP処理を保持する。
    private readonly RequestDelegate nextMiddleware = next;
    // 起動時に読み込んだServeの許可設定を保持する。
    private readonly RemoteAccessSettings remoteAccess = remoteSettings;
    // 後続APIへ接続経路を伝えるキーを保持する。
    public const string RemoteAccessItem = "TaskManager.RemoteAccess";

    /// <summary>ローカル以外の接続と保護ヘッダーのない更新を拒否する。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Kestrel設定に加えて接続元IPでも外部アクセスを防ぐ。
        IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "ローカル接続だけを許可しています。" });
            return;
        }
        bool isMutation = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
        // ServeはHostを維持して中継する。転送ヘッダー付き要求をローカル扱いへ降格しない。
        string requestAuthority = context.Request.Host.Value ?? "";
        bool hasProxyHeaders = context.Request.Headers.Keys.Any(name =>
            name.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
        bool isLocal = !hasProxyHeaders && context.Request.Scheme == "http"
            && (requestAuthority.Equals("127.0.0.1:48120", StringComparison.OrdinalIgnoreCase)
                || requestAuthority.Equals("localhost:48120", StringComparison.OrdinalIgnoreCase));
        bool isRemote = !isLocal && remoteAccess.IsConfigured()
            && requestAuthority.Equals(new Uri(remoteAccess.ServeOrigin).Authority, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers["Tailscale-User-Login"].Count == 1
            && string.Equals(context.Request.Headers["Tailscale-User-Login"], remoteAccess.AllowedLogin, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers["X-Forwarded-Proto"] == "https";
        string expectedOrigin = isLocal ? $"http://{requestAuthority}" : remoteAccess.ServeOrigin;
        string suppliedOrigin = context.Request.Headers.Origin.ToString();
        bool invalidOrigin = suppliedOrigin.Length > 0
            && !string.Equals(suppliedOrigin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
        // Androidのホーム起動など、別サイト扱いになる最上位の画面表示だけを許可する。
        bool isDocumentNavigation = HttpMethods.IsGet(context.Request.Method)
            && context.Request.Headers["Sec-Fetch-Mode"] == "navigate"
            && context.Request.Headers["Sec-Fetch-Dest"] == "document"
            && (context.Request.Path == "/" || context.Request.Path == "/index.html");
        if ((!isLocal && !isRemote) || invalidOrigin
            || (isRemote && isMutation && suppliedOrigin.Length == 0)
            || (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" && !isDocumentNavigation))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "接続元または送信元URLが許可されていません。" });
            return;
        }
        // PCでのみ完結する操作をブラウザの表示制御とは独立して拒否する。
        if (isRemote && IsLocalOnlyOperation(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "この操作はPCのローカル画面から実行してください。" });
            return;
        }
        context.Items[RemoteAccessItem] = isRemote;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
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

    /// <summary>PC固有APIと設定変更・バックアップ操作を判定する。</summary>
    private static bool IsLocalOnlyOperation(HttpRequest request)
    {
        // 大文字小文字や末尾スラッシュでも経路制限を回避できないよう区切り単位で比較する。
        return request.Path.StartsWithSegments("/api/v1/codex", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/v1/calendar/credentials", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/v1/calendar/connect", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/v1/backup", StringComparison.OrdinalIgnoreCase)
            || (request.Path.StartsWithSegments("/api/v1/settings", StringComparison.OrdinalIgnoreCase)
                && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method));
    }
}
