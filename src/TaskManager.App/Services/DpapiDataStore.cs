using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;
using TaskManager.Configuration;

namespace TaskManager.Services;

/// <summary>Google OAuthトークンをWindowsユーザー単位で暗号化保存する。</summary>
public sealed class DpapiDataStore(TaskManagerPaths taskManagerPaths) : IDataStore
{
    // 暗号化トークンの保存先を保持する。
    private readonly string tokenDirectory = taskManagerPaths.TokenDirectory;
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("TaskManager.GoogleOAuth.v1");

    /// <summary>指定キーのOAuthデータを暗号化して保存する。</summary>
    public async Task StoreAsync<T>(string key, T value)
    {
        // JSONを現在のWindowsユーザーだけが復号できる形式へ変換する。
        Directory.CreateDirectory(tokenDirectory);
        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] protectedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(GetPath(key), protectedBytes);
    }

    /// <summary>指定キーのOAuthデータを復号して取得する。</summary>
    public async Task<T?> GetAsync<T>(string key)
    {
        // ファイルがない場合は未認証としてnullを返す。
        string tokenPath = GetPath(key);
        if (!File.Exists(tokenPath))
        {
            return default;
        }
        byte[] protectedBytes = await File.ReadAllBytesAsync(tokenPath);
        byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<T>(plainBytes);
    }

    /// <summary>指定キーのOAuthデータを削除する。</summary>
    public Task DeleteAsync<T>(string key)
    {
        // 再認証できるよう対象ファイルだけを削除する。
        string tokenPath = GetPath(key);
        if (File.Exists(tokenPath))
        {
            File.Delete(tokenPath);
        }
        return Task.CompletedTask;
    }

    /// <summary>保存済みOAuthデータをすべて削除する。</summary>
    public Task ClearAsync()
    {
        // 認証解除時にトークンディレクトリ内だけを削除する。
        if (Directory.Exists(tokenDirectory))
        {
            foreach (string tokenPath in Directory.GetFiles(tokenDirectory, "*.bin"))
            {
                File.Delete(tokenPath);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>キーから安全な保存ファイル名を作成する。</summary>
    private string GetPath(string key)
    {
        // ファイル名に利用できない文字を下線へ置換する。
        string safeKey = string.Concat(key.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(tokenDirectory, $"{safeKey}.bin");
    }
}
