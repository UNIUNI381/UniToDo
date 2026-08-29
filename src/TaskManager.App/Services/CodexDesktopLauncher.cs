namespace TaskManager.Services;

/// <summary>ブラウザからCodexの指定タスクを表示するための情報を作成する。</summary>
public sealed class CodexDesktopLauncher
{
    /// <summary>ブラウザへ返すCodexディープリンク情報を作成する。</summary>
    public CodexDesktopLaunchResult OpenThread(string threadLink)
    {
        // 外部プロセスは起動せず、利用者が操作したブラウザへ安全なURIだけを返す。
        return new CodexDesktopLaunchResult
        {
            Opened = true,
            LaunchMethod = "browser-protocol",
            ThreadLink = threadLink
        };
    }
}

/// <summary>Codexデスクトップアプリの起動結果を表す。</summary>
public sealed class CodexDesktopLaunchResult
{
    // 表示可否と使用した起動経路を保持する。
    public bool Opened { get; init; }
    public string LaunchMethod { get; init; } = string.Empty;
    public string ThreadLink { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
}
