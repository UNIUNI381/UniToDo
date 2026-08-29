using TaskManager.Cli;

namespace TaskManager.CliHost;

/// <summary>taskctlコンソールアプリのエントリーポイントを提供する。</summary>
public static class Program
{
    /// <summary>CLI引数をローカルAPIクライアントへ渡す。</summary>
    public static async Task<int> Main(string[] arguments)
    {
        // Ctrl+CをキャンセルとしてCLI処理へ伝える。
        using CancellationTokenSource cancellationSource = new();
        Console.CancelKeyPress += (_, eventArguments) =>
        {
            eventArguments.Cancel = true;
            cancellationSource.Cancel();
        };
        CliRunner runner = new();
        return await runner.RunAsync(arguments, cancellationSource.Token);
    }
}
