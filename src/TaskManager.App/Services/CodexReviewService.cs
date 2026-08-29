using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>音声入力の確認待ちをメモリ内で順番に管理する。</summary>
public sealed class CodexReviewService
{
    // 確認待ちキュー、現在表示中の項目、同期制御を保持する。
    private readonly LinkedList<CodexReviewItem> reviewQueue = [];
    private readonly object reviewLock = new();
    private CodexReviewItem? activeReview;

    // 音声入力として受け付ける本文の最大文字数を保持する。
    public const int MaximumTextLength = 50000;

    /// <summary>新しい確認項目が表示可能になったことを通知する。</summary>
    public event Action? ReviewAvailable;

    /// <summary>校正済み本文を確認待ちの末尾へ追加する。</summary>
    public CodexReviewReceipt Enqueue(string text)
    {
        // 空本文と過大な本文を確認画面へ入れない。
        ValidateText(text);
        CodexReviewItem reviewItem = new()
        {
            Identifier = Guid.NewGuid().ToString("N"),
            Text = text,
            CreatedAt = DateTimeOffset.Now
        };
        int queuePosition;
        lock (reviewLock)
        {
            reviewQueue.AddLast(reviewItem);
            queuePosition = reviewQueue.Count + (activeReview is null ? 0 : 1);
        }
        ReviewAvailable?.Invoke();
        return new CodexReviewReceipt
        {
            ReviewIdentifier = reviewItem.Identifier,
            QueuePosition = queuePosition
        };
    }

    /// <summary>表示中項目がなければ次の確認項目を取り出す。</summary>
    public bool TryActivateNext(out CodexReviewItem? reviewItem, out int waitingCount)
    {
        // 1つの確認画面だけがキュー先頭を所有するよう同期する。
        lock (reviewLock)
        {
            if (activeReview is not null || reviewQueue.First is null)
            {
                reviewItem = null;
                waitingCount = reviewQueue.Count;
                return false;
            }
            activeReview = reviewQueue.First.Value;
            reviewQueue.RemoveFirst();
            reviewItem = activeReview;
            waitingCount = reviewQueue.Count;
            return true;
        }
    }

    /// <summary>表示中項目を送信済みまたは破棄済みとして完了する。</summary>
    public bool CompleteActive(string reviewIdentifier)
    {
        // 異なる画面からの遅延イベントで別項目を完了しない。
        bool hasNextReview;
        lock (reviewLock)
        {
            if (activeReview is null
                || !string.Equals(activeReview.Identifier, reviewIdentifier, StringComparison.Ordinal))
            {
                return false;
            }
            activeReview = null;
            hasNextReview = reviewQueue.Count > 0;
        }
        if (hasNextReview)
        {
            ReviewAvailable?.Invoke();
        }
        return true;
    }

    /// <summary>表示準備に失敗した項目を確認待ちの先頭へ戻す。</summary>
    public void ReturnActiveToFront(string reviewIdentifier)
    {
        // 現在項目だけを先頭へ戻し、後続項目の順序を維持する。
        lock (reviewLock)
        {
            if (activeReview is null
                || !string.Equals(activeReview.Identifier, reviewIdentifier, StringComparison.Ordinal))
            {
                return;
            }
            reviewQueue.AddFirst(activeReview);
            activeReview = null;
        }
    }

    /// <summary>CLI送信に失敗した編集済み本文を確認待ちの先頭へ戻す。</summary>
    public void RequeueFailedSubmission(CodexSubmission submission)
    {
        // ユーザーが編集した最終本文を新しい確認項目として失わず復元する。
        CodexReviewItem reviewItem = new()
        {
            Identifier = submission.ReviewIdentifier,
            Text = submission.Text,
            CreatedAt = submission.CreatedAt
        };
        bool canPresent;
        lock (reviewLock)
        {
            reviewQueue.AddFirst(reviewItem);
            canPresent = activeReview is null;
        }
        if (canPresent)
        {
            ReviewAvailable?.Invoke();
        }
    }

    /// <summary>音声入力本文が確認・送信可能な範囲であることを検証する。</summary>
    public static void ValidateText(string? text)
    {
        // 空白だけの入力と異常に大きいローカル要求を拒否する。
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("確認する本文がありません。");
        }
        if (text.Length > MaximumTextLength)
        {
            throw new InvalidOperationException($"本文は{MaximumTextLength}文字以内で指定してください。");
        }
    }
}
