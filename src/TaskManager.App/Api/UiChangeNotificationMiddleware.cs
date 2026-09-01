using TaskManager.Domain;
using TaskManager.Services;

namespace TaskManager.Api;

/// <summary>成功したデータ更新APIを画面の変更通知へ変換する。</summary>
public sealed class UiChangeNotificationMiddleware(RequestDelegate next)
{
    // 次のHTTP処理を保持する。
    private readonly RequestDelegate nextMiddleware = next;

    /// <summary>データ更新APIの正常終了後に変更元と画面クライアントを通知する。</summary>
    public async Task InvokeAsync(HttpContext context, UiChangeNotifier changeNotifier)
    {
        // 業務処理がすべて完了した後だけ画面へ再取得を要求する。
        await nextMiddleware(context);
        if (!IsUserInterfaceDataMutation(context.Request)
            || context.Response.StatusCode is < StatusCodes.Status200OK or >= StatusCodes.Status400BadRequest)
        {
            return;
        }
        string source = string.Equals(
            context.Request.Headers["X-TaskManager-Source"],
            "Codex",
            StringComparison.OrdinalIgnoreCase)
            ? TaskConstants.CodexSource
            : TaskConstants.ScreenSource;
        string clientIdentifier = context.Request.Headers["X-TaskManager-Client"].ToString();
        changeNotifier.Publish(source, clientIdentifier);
    }

    /// <summary>要求が画面表示へ影響する更新APIかを判定する。</summary>
    private static bool IsUserInterfaceDataMutation(HttpRequest request)
    {
        // 読取要求とバックアップ、Codex起動・音声受付を通知対象から除外する。
        bool isMutation = HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method);
        if (!isMutation || !request.Path.StartsWithSegments("/api/v1", out PathString remainingPath))
        {
            return false;
        }
        return remainingPath.StartsWithSegments("/tasks")
            || remainingPath.StartsWithSegments("/time-entries")
            || remainingPath.StartsWithSegments("/projects")
            || remainingPath.StartsWithSegments("/settings")
            || remainingPath.StartsWithSegments("/draft-batches")
            || remainingPath.StartsWithSegments("/calendar")
            || remainingPath.StartsWithSegments("/system-incidents");
    }
}
