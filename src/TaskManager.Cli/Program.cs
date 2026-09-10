using TaskManager.Cli;

namespace TaskManager.CliHost;

/// <summary>taskctlコンソールアプリのエントリーポイントを提供する。</summary>
public static class Program
{
    /// <summary>CLI引数をローカルAPIクライアントへ渡す。</summary>
    public static async Task<int> Main(string[] arguments)
    {
        // 標準入力のJSONと出力をUTF-8に揃え、日本語を外部プロセスと受け渡す。
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using StreamReader standardInput = new(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        // Ctrl+CをキャンセルとしてCLI処理へ伝える。
        using CancellationTokenSource cancellationSource = new();
        Console.CancelKeyPress += (_, eventArguments) =>
        {
            eventArguments.Cancel = true;
            cancellationSource.Cancel();
        };
        CliRunner runner = new(inputReader: standardInput);
        return await runner.RunAsync(arguments, cancellationSource.Token);
    }
}
