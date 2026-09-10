using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>確認済み音声をWebと同じ会話サービスへ送り、結果を通知用に整える。</summary>
public sealed class CodexVoiceChatRunner(CodexChatService chatService) : ICodexCommandRunner
{
    // Webと音声が共有する会話と受付記録を保持する。
    private readonly CodexChatService chat = chatService;

    public void Prepare(string threadIdentifier)
    {
        // 会話の準備はトレイからGetAsyncで待機し、旧CLIを起動しない。
    }

    public async Task<CodexCommandResult> SendAsync(CodexSubmission submission, CancellationToken cancellationToken)
    {
        // 受付済みの依頼は失敗や切断でも確認キューへ戻さず、重複実行を防ぐ。
        bool accepted = false;
        TaskCompletionSource<ChatSnapshot> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReceiveResponse(ChatSnapshot snapshot)
        {
            // 次のWeb送信が始まる前に今回の終端表示を確保する。
            if (snapshot.Conversation == submission.ThreadIdentifier
                && chat.HasReceipt(submission.ThreadIdentifier, submission.ReviewIdentifier))
                response.TrySetResult(snapshot);
        }
        chat.ResponseAvailable += ReceiveResponse;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatSnapshot initial = await chat.GetAsync();
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> previousMessages = initial.Messages.Select(message => message.Identifier).ToHashSet();
            ChatSnapshot snapshot = await chat.SendAsync(new(submission.ThreadIdentifier, submission.ReviewIdentifier, submission.Text));
            accepted = true;
            if (snapshot.Status == "running" && snapshot.Prompts.Length == 0)
                snapshot = await response.Task.WaitAsync(cancellationToken);
            if (snapshot.Prompts.Length > 0)
                return new() { IsSuccess = true, OutputMessage = "Codexから確認があります。Web画面の「Codexでタスク管理」を開いて回答してください。" };
            if (snapshot.Status == "uncertain" || snapshot.Error is not null)
                return new() { CanRetry = false, ErrorMessage = snapshot.Error ?? "結果が不明です。Web画面で履歴を確認してください。" };
            string responseText = string.Join(Environment.NewLine, snapshot.Messages
                .Where(message => message.Role == "assistant" && !previousMessages.Contains(message.Identifier))
                .Select(message => message.Text));
            return new() { IsSuccess = true, OutputMessage = responseText.Length > 4000 ? responseText[^4000..] : responseText };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // アプリ停止時は送信を再投入しない。
            throw;
        }
        catch (Exception)
        {
            // 例外に本文を含めず、送信前の拒否と送信後の結果不明を区別する。
            accepted = accepted || chat.HasReceipt(submission.ThreadIdentifier, submission.ReviewIdentifier);
            return new()
            {
                CanRetry = !accepted,
                ErrorMessage = accepted
                    ? "送信結果を確認できません。再送せず、Web画面の「Codexでタスク管理」で履歴を確認してください。"
                    : "送信できませんでした。Web画面で処理中の依頼・接続状態を確認してから、本文を再確認してください。"
            };
        }
        finally
        {
            // 完了・失敗・取消しのいずれでも送信単位の購読を解除する。
            chat.ResponseAvailable -= ReceiveResponse;
        }
    }
}
