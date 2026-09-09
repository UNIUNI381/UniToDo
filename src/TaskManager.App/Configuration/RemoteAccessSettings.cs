using System.Text.Json;

namespace TaskManager.Configuration;

/// <summary>Tailscale Serveの正規URLと許可する本人を端末内で保持する。</summary>
public sealed class RemoteAccessSettings
{
    // HTTPSの正規オリジンとTailscaleのログイン名を保持する。
    public string ServeOrigin { get; init; } = "";
    public string AllowedLogin { get; init; } = "";

    /// <summary>設定がHTTPSの標準ポートと単一の本人識別子を指定しているか返す。</summary>
    public bool IsConfigured()
    {
        // 未設定や不正な設定ではリモート接続を許可しない。
        return Uri.TryCreate(ServeOrigin, UriKind.Absolute, out Uri? origin)
            && origin.Scheme == "https" && origin.Port == 443
            && origin.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/"
            && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && string.Equals(ServeOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(AllowedLogin)
            && !AllowedLogin.Any(char.IsWhiteSpace) && !AllowedLogin.Contains(',');
    }

    /// <summary>端末専用ファイルを読み、読取不能ならリモート接続だけ無効にする。</summary>
    public static RemoteAccessSettings Load(string path, ILogger logger)
    {
        // 個人識別子をログへ出さず、既存のローカル運用を継続する。
        if (!File.Exists(path)) return new();
        try
        {
            RemoteAccessSettings settings = JsonSerializer.Deserialize<RemoteAccessSettings>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
            if (settings.IsConfigured()) return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 不正または読取不能の設定は許可設定として使わない。
        }
        logger.LogWarning("remote-access.jsonを読み取れないか設定が不正です。リモート接続を無効にします。");
        return new();
    }
}
