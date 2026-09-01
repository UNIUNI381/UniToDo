namespace TaskManager.Domain;

/// <summary>タスク管理全体で利用する固定値を定義する。</summary>
public static class TaskConstants
{
    // 利用者へ表示する正式な製品名を定義する。
    public const string ApplicationDisplayName = "UniToDo";

    // 状態値を画面・CLI・保存層で共通化する。
    public const string InboxStatus = "受信箱";
    public const string DraftStatus = "下書き";
    public const string NeedsReviewStatus = "要確認";
    public const string ReadyStatus = "実行可能";
    public const string InProgressStatus = "実行中";
    public const string WaitingStatus = "待機中";
    public const string CompletedStatus = "完了";
    public const string CancelledStatus = "中止";

    // タスク区分を画面とAPIの絞り込みで共通化する。
    public const string WorkCategory = "仕事";
    public const string PrivateCategory = "私用";

    // 期限種別を優先度計算で共通化する。
    public const string StrictDeadlineType = "厳守";
    public const string TargetDeadlineType = "目安";
    public const string NoDeadlineType = "なし";

    // 入力元を履歴へ一貫して保存する。
    public const string ScreenSource = "画面";
    public const string CodexSource = "Codex";
    public const string SystemSource = "システム";

    // ローカルWebサーバーの固定接続先を定義する。
    public const int LocalPort = 48120;
    public const string LocalAddress = "http://127.0.0.1:48120";

    // Windowsタスクスケジューラへ登録する監視親の表示名を定義する。
    public const string WatchdogScheduledTaskName = "UniToDo Watchdog";

    // 音声入力の送信先が未設定であることを表す初期値を定義する。
    public const string DefaultCodexThreadIdentifier = "";
}
