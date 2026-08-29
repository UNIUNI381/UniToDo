using System.Threading.Channels;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>Codex CLIへ順番に渡す送信要求を管理する。</summary>
public sealed class CodexSubmissionQueue
{
    // 複数画面から受け付けて単一ワーカーが読むチャネルを保持する。
    private readonly Channel<CodexSubmission> submissionChannel = Channel.CreateUnbounded<CodexSubmission>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>編集済み本文をCLI送信待ちへ追加する。</summary>
    public void Enqueue(CodexSubmission submission)
    {
        // アプリ終了後などチャネルが閉じた場合は受付失敗を呼び出し元へ返す。
        if (!submissionChannel.Writer.TryWrite(submission))
        {
            throw new InvalidOperationException("Codex送信キューへ追加できませんでした。");
        }
    }

    /// <summary>送信要求を受付順に非同期列挙する。</summary>
    public IAsyncEnumerable<CodexSubmission> ReadAllAsync(CancellationToken cancellationToken)
    {
        // バックグラウンドワーカーだけが読み取るストリームを返す。
        return submissionChannel.Reader.ReadAllAsync(cancellationToken);
    }
}
