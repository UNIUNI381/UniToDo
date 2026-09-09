using TaskManager.Services;

namespace TaskManager.Api;

/// <summary>ブラウザに必要な会話操作だけを公開する。</summary>
public static class CodexChatEndpoints
{
    public sealed record ConversationRequest(string Conversation);

    public static void MapCodexChat(this WebApplication application)
    {
        // 任意のRPC、実行パス、会話ID、モデル設定はHTTP入力に含めない。
        RouteGroupBuilder assistant = application.MapGroup("/api/v1/assistant");
        assistant.MapGet("/state", async (CodexChatService service, HttpContext context) =>
        {
            // 会話内容と確認情報をブラウザや中継へキャッシュさせない。
            context.Response.Headers.CacheControl = "no-store";
            return await service.GetAsync();
        });
        assistant.MapPost("/messages", async (ChatSubmission submission, CodexChatService service) =>
        {
            // 同じサービス契約を将来の音声入力からも利用できるようにする。
            return await service.SendAsync(submission);
        });
        assistant.MapPost("/answers", async (ChatAnswer answer, CodexChatService service) =>
        {
            // 確認待ち識別子で回答対象を限定する。
            return await service.AnswerAsync(answer);
        });
        assistant.MapPost("/interrupt", async (ConversationRequest request, CodexChatService service) =>
        {
            // 実行中の処理へ停止要求を送る。
            return await service.InterruptAsync(request.Conversation);
        });
        assistant.MapPost("/new", async (ConversationRequest request, CodexChatService service) =>
        {
            // 会話切替は利用者の明示操作として扱う。
            return await service.NewAsync(request.Conversation);
        });
        assistant.MapPost("/reconnect", async (ConversationRequest request, CodexChatService service) =>
        {
            // 通信結果が不明な場合は再送せず履歴を照合する。
            return await service.ReconnectAsync(request.Conversation);
        });
    }
}
