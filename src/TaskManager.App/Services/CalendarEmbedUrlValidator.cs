namespace TaskManager.Services;

/// <summary>Google Calendar埋め込みURLを安全な形式へ検証する。</summary>
public static class CalendarEmbedUrlValidator
{
    /// <summary>埋め込みURLを検証して保存用の形式へ正規化する。</summary>
    public static string Normalize(string? calendarEmbedUrl)
    {
        // 空欄は埋め込み未設定として許可する。
        if (string.IsNullOrWhiteSpace(calendarEmbedUrl))
        {
            return string.Empty;
        }

        // iframe本文の保存を避け、Googleが発行したURLだけを受け付ける。
        string normalizedUrl = calendarEmbedUrl.Trim().Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        if (normalizedUrl.Contains("<", StringComparison.Ordinal)
            || normalizedUrl.Contains(">", StringComparison.Ordinal)
            || !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? calendarUri)
            || calendarUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(calendarUri.Host, "calendar.google.com", StringComparison.OrdinalIgnoreCase)
            || !calendarUri.IsDefaultPort
            || !string.Equals(calendarUri.AbsolutePath, "/calendar/embed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(calendarUri.UserInfo)
            || !ContainsCalendarSource(calendarUri.Query))
        {
            throw new InvalidOperationException(
                "埋め込みURLにはGoogle Calendarの設定画面で生成したhttps://calendar.google.com/calendar/embed?...形式のURLを指定してください。");
        }

        return calendarUri.AbsoluteUri;
    }

    /// <summary>クエリ文字列に空でないカレンダー指定があるか確認する。</summary>
    private static bool ContainsCalendarSource(string query)
    {
        // 複数カレンダーを含む場合も1件以上のsrcがあれば有効とする。
        foreach (string queryPart in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = queryPart.Split('=', 2);
            if (pair.Length == 2
                && string.Equals(Uri.UnescapeDataString(pair[0]), "src", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(pair[1])))
            {
                return true;
            }
        }
        return false;
    }
}
