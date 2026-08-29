using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>CodexタスクIDとディープリンクを検証して正規化する。</summary>
public static class CodexThreadIdentifier
{
    /// <summary>UUIDまたはCodexディープリンクから正規化済みタスクIDを返す。</summary>
    public static string Normalize(string? value)
    {
        // 設定画面へ貼り付けられたディープリンクからUUID部分だけを取り出す。
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            throw new InvalidOperationException(
                "Codex音声入力を使用するには、設定画面の「Codex音声入力」で送信先タスクIDを設定してください。");
        }
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? threadUri)
            && string.Equals(threadUri.Scheme, "codex", StringComparison.OrdinalIgnoreCase)
            && string.Equals(threadUri.Host, "threads", StringComparison.OrdinalIgnoreCase))
        {
            candidate = threadUri.AbsolutePath.Trim('/');
        }

        // Codexのセッション識別子として扱える標準UUIDだけを許可する。
        if (!Guid.TryParseExact(candidate, "D", out Guid threadIdentifier))
        {
            throw new InvalidOperationException("CodexタスクIDはUUIDまたはcodex://threads/<UUID>で指定してください。");
        }
        return threadIdentifier.ToString("D");
    }

    /// <summary>設定済みタスクIDからCodexデスクトップ用ディープリンクを作成する。</summary>
    public static string CreateDeepLink(string value)
    {
        // 正規化済みUUIDだけをURIへ組み込み、不正なスキーム注入を防ぐ。
        return $"codex://threads/{Normalize(value)}";
    }
}
