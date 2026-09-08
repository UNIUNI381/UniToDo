using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;
using TaskManager.Configuration;

namespace TaskManager.Services;

/// <summary>Google OAuthトークンをOS保護機能またはAES-GCMで暗号化保存する。</summary>
public sealed class DpapiDataStore(TaskManagerPaths taskManagerPaths) : IDataStore
{
    // 暗号化トークンの保存先ディレクトリを保持する。
    private readonly string tokenDirectory = taskManagerPaths.TokenDirectory;
    // POSIX環境の暗号鍵保存ディレクトリを保持する。
    private readonly string keysDirectory = Path.Combine(taskManagerPaths.TokenDirectory, ".keys");
    // Windows DPAPI用の追加エントロピーを保持する。
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("TaskManager.GoogleOAuth.v1");
    // POSIX鍵生成の排他制御用オブジェクトを保持する。
    private static readonly object KeyLock = new();

    /// <summary>指定キーのOAuthデータを暗号化して保存する。</summary>
    public async Task StoreAsync<T>(string key, T value)
    {
        // ディレクトリを準備し、現在のユーザーのみアクセス可能な権限を設定する。
        EnsureSecureDirectories();
        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(value);
        string tokenPath = GetPath(key);

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            // WindowsではDPAPIを用いて現在のログインユーザー権限で保護する。
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(tokenPath, protectedBytes);
        }
        else
#endif
        {
            // POSIX環境では固有マスターキーによるAES-GCM暗号化と0600権限で保存する。
            byte[] masterKey = GetOrCreatePosixMasterKey();
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] cipherText = new byte[plainBytes.Length];
            using (AesGcm aesGcm = new(masterKey, 16))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherText, tag);
            }

            FileStreamOptions streamOptions = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using FileStream fileStream = new(tokenPath, streamOptions);
            await fileStream.WriteAsync(nonce);
            await fileStream.WriteAsync(tag);
            await fileStream.WriteAsync(cipherText);
        }
    }

    /// <summary>指定キーのOAuthデータを復号して取得する。</summary>
    public async Task<T?> GetAsync<T>(string key)
    {
        // ファイルが存在しない場合は未認証としてnullを返す。
        string tokenPath = GetPath(key);
        if (!File.Exists(tokenPath))
        {
            return default;
        }
        byte[] encryptedFileBytes = await File.ReadAllBytesAsync(tokenPath);

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            // WindowsではDPAPIで復号する。
            byte[] plainBytes = ProtectedData.Unprotect(encryptedFileBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plainBytes);
        }
        else
#endif
        {
            // POSIX環境ではNonce(12B) + Tag(16B) + 暗号文の形式から復号する。
            if (encryptedFileBytes.Length < 28)
            {
                return default;
            }
            byte[] masterKey = GetOrCreatePosixMasterKey();
            byte[] nonce = encryptedFileBytes[..12];
            byte[] tag = encryptedFileBytes[12..28];
            byte[] cipherText = encryptedFileBytes[28..];
            byte[] plainBytes = new byte[cipherText.Length];
            using (AesGcm aesGcm = new(masterKey, 16))
            {
                aesGcm.Decrypt(nonce, cipherText, tag, plainBytes);
            }
            return JsonSerializer.Deserialize<T>(plainBytes);
        }
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
        // 認証解除時にトークンディレクトリ内のデータファイルだけを削除する。
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

    /// <summary>POSIX環境向けに安全なパーミッションでディレクトリを確保する。</summary>
    private void EnsureSecureDirectories()
    {
        // ディレクトリを準備し、POSIXでは0700を設定する。
        Directory.CreateDirectory(tokenDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tokenDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(keysDirectory);
            File.SetUnixFileMode(keysDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>POSIX環境専用の256ビットマスター暗号鍵を取得または新規生成する。</summary>
    private byte[] GetOrCreatePosixMasterKey()
    {
        // 鍵ファイルが既存なら読み込み、未作成なら0600権限でアトミックに新規生成する。
        string masterKeyPath = Path.Combine(keysDirectory, "master.key");
        lock (KeyLock)
        {
            EnsureSecureDirectories();
            if (File.Exists(masterKeyPath))
            {
                return File.ReadAllBytes(masterKeyPath);
            }
            byte[] generatedKey = RandomNumberGenerator.GetBytes(32);
            FileStreamOptions streamOptions = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using FileStream keyStream = new(masterKeyPath, streamOptions);
            keyStream.Write(generatedKey);
            return generatedKey;
        }
    }
}
