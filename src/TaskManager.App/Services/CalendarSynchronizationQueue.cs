using System.Threading.Channels;

namespace TaskManager.Services;

/// <summary>Calendar操作後の同期要求をまとめ、会話の通知受信から切り離す。</summary>
public sealed class CalendarSynchronizationQueue
{
    // 待機中の要求は1件にまとめ、同期中に届いた要求は次の同期へ残す。
    private readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    public void Request()
    {
        // 会話の受信処理を待たせず、再同期の必要性だけを通知する。
        requests.Writer.TryWrite(true);
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        // 次の同期要求かアプリ終了を待ち、受け取った要求を消費する。
        await requests.Reader.ReadAsync(cancellationToken);
    }
}
