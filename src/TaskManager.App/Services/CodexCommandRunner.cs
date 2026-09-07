using System.Diagnostics;
using System.Text;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>Codex CLIへの送信処理を抽象化する。</summary>
public interface ICodexCommandRunner
{
    /// <summary>本文を送信せずCLIを標準入力待ちで準備する。</summary>
    void Prepare(string threadIdentifier);

    /// <summary>指定タスクへ編集済み本文を送信する。</summary>
    Task<CodexCommandResult> SendAsync(CodexSubmission submission, CancellationToken cancellationToken);
}

/// <summary>外部プロセスの標準入出力実行を抽象化する。</summary>
public interface ICodexProcessExecutor
{
    /// <summary>後続の送信で再利用するプロセスを入力待ちで起動する。</summary>
    void Prepare(ProcessStartInfo startInformation);

    /// <summary>指定プロセスへ標準入力を渡して終了結果を返す。</summary>
    Task<CodexCommandResult> ExecuteAsync(
        ProcessStartInfo startInformation,
        string standardInput,
        CancellationToken cancellationToken);
}

/// <summary>外部プロセスから実行可能なCodex CLIの場所を解決する。</summary>
public static class CodexExecutableLocator
{
    /// <summary>明示指定、公式スタンドアロン版、PATHの順でCodex CLIを解決する。</summary>
    public static string Resolve()
    {
        // 管理者が明示した実行ファイルを最優先する。
        string? configuredPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        }

        // binだけを指す既知のジャンクション問題を避け、リソースと同階層にある実パッケージを優先する。
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string packagePath = Path.Combine(userProfile, ".codex", "packages", "standalone", "current", "bin", "codex.exe");
        if (File.Exists(packagePath))
        {
            return packagePath;
        }

        // 旧インストーラーや将来の修正版と互換性を保つため通常の公開パスへフォールバックする。
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string standalonePath = Path.Combine(localApplicationData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(standalonePath) ? standalonePath : "codex.exe";
    }
}

/// <summary>Codex CLIの引数と標準入力を安全に組み立てる。</summary>
public sealed class CodexCommandRunner(ICodexProcessExecutor processExecutor) : ICodexCommandRunner
{
    // 音声入力から送信するCodexモデルを保持する。
    private const string CodexModel = "gpt-5.6-luna";

    // 音声入力から送信する推論レベル設定を保持する。
    private const string ReasoningConfiguration = "model_reasoning_effort=\"low\"";

    // 音声入力から送信するサービス速度設定を保持する。
    private const string ServiceTierConfiguration = "service_tier=\"fast\"";

    // 高速モードの機能名を保持する。
    private const string FastModeFeature = "fast_mode";

    // 音声送信先タスク内だけでGoogle Calendarアプリの確認を自動承認する設定を保持する。
    private const string GoogleCalendarApprovalConfiguration =
        "apps.connector_947e0d954944416db111db556030eea6.default_tools_approval_mode=\"approve\"";

    // 実際のプロセス起動処理を保持する。
    private readonly ICodexProcessExecutor executor = processExecutor;

    /// <summary>指定タスクへ編集済み本文を非対話で送信する。</summary>
    public Task<CodexCommandResult> SendAsync(
        CodexSubmission submission,
        CancellationToken cancellationToken)
    {
        // 事前起動時と同一の引数で、確認済み本文だけを送信する。
        return executor.ExecuteAsync(CreateStartInformation(submission.ThreadIdentifier), submission.Text, cancellationToken);
    }

    /// <summary>送信先を検証して本文なしのCLI事前起動を要求する。</summary>
    public void Prepare(string threadIdentifier)
    {
        // 空または不正な送信先ではプロセスを作成しない。
        executor.Prepare(CreateStartInformation(threadIdentifier));
    }

    /// <summary>事前起動と通常送信で共通のCLI起動設定を構築する。</summary>
    private static ProcessStartInfo CreateStartInformation(string threadIdentifier)
    {
        // プロセス作成前に送信先を正規化する。
        string normalizedIdentifier = CodexThreadIdentifier.Normalize(threadIdentifier);
        // 本文を引数へ含めず、ハイフン指定した標準入力だけへ渡す。
        ProcessStartInfo startInformation = new()
        {
            FileName = CodexExecutableLocator.Resolve(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInformation.ArgumentList.Add("exec");
        // この送信経路だけLuna・低推論・高速モードを明示し、利用者のグローバル設定は変更しない。
        startInformation.ArgumentList.Add("--model");
        startInformation.ArgumentList.Add(CodexModel);
        startInformation.ArgumentList.Add("--config");
        startInformation.ArgumentList.Add(ReasoningConfiguration);
        startInformation.ArgumentList.Add("--config");
        startInformation.ArgumentList.Add(ServiceTierConfiguration);
        // 非対話CLIでは表示できないGoogle Calendar確認だけを、この送信処理の有効期間に限定して自動承認する。
        startInformation.ArgumentList.Add("--config");
        startInformation.ArgumentList.Add(GoogleCalendarApprovalConfiguration);
        startInformation.ArgumentList.Add("--enable");
        startInformation.ArgumentList.Add(FastModeFeature);
        // 常駐アプリのGit管理外インストール先から実行できるよう、リポジトリ検査だけを省略する。
        startInformation.ArgumentList.Add("--skip-git-repo-check");
        startInformation.ArgumentList.Add("--add-dir");
        startInformation.ArgumentList.Add(GetTaskManagerDirectory());
        startInformation.ArgumentList.Add("resume");
        startInformation.ArgumentList.Add(normalizedIdentifier);
        startInformation.ArgumentList.Add("-");
        return startInformation;
    }

    /// <summary>Codexからtaskctlを実行できるようTask Managerの配置先を返す。</summary>
    private static string GetTaskManagerDirectory()
    {
        // 危険なサンドボックス解除を避け、taskctlがある専用ディレクトリだけを追加許可する。
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "Programs", "TaskManager");
    }
}

/// <summary>Windows上でCodex CLIプロセスを起動して標準入出力を処理する。</summary>
public sealed class CodexProcessExecutor : ICodexProcessExecutor, IDisposable
{
    // 未送信プロセスを最大1個だけ保持する事前起動プールを保持する。
    private readonly CodexProcessPreparation preparation = new();

    /// <summary>本文を渡さずにCLIを起動する。</summary>
    public void Prepare(ProcessStartInfo startInformation)
    {
        // 既存の待機プロセスが一致する場合は重複起動しない。
        preparation.Prepare(startInformation);
    }

    /// <summary>アプリ終了時に未送信の待機プロセスを終了する。</summary>
    public void Dispose()
    {
        // 本文未送信のプロセスだけを回収する。
        preparation.Dispose();
    }

    /// <summary>プロセスへ本文を渡し、終了コードと短いエラーを返す。</summary>
    public async Task<CodexCommandResult> ExecuteAsync(
        ProcessStartInfo startInformation,
        string standardInput,
        CancellationToken cancellationToken)
    {
        // シェルを介さずにCLIを起動し、出力をファイルへ残さず一時表示用に読み取る。
        cancellationToken.ThrowIfCancellationRequested();
        CodexPreparedProcess preparedProcess;
        try
        {
            preparedProcess = preparation.TakeOrStart(startInformation);
        }
        catch (Exception startError) when (startError is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // PATHやWindowsAppsの実行権限問題を利用者が判別できる形で返す。
            return new CodexCommandResult
            {
                IsSuccess = false,
                ExitCode = -1,
                ErrorMessage = ShortenError($"Codex CLIを起動できません: {startError.Message}")
            };
        }

        using CodexPreparedProcess ownedProcess = preparedProcess;
        Process process = ownedProcess.Process;
        // アプリ終了時は送信中の子プロセスも終了させ、孤立させない。
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(ownedProcess.Stop);
        Task<string> outputTask = ownedProcess.Output;
        Task<string> errorTask = ownedProcess.Error;
        string inputError = string.Empty;
        try
        {
            // 本文をUTF-8標準入力へ書き込み、CLIへ入力終了を通知する。
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        catch (Exception writeError) when (writeError is IOException or InvalidOperationException)
        {
            // CLIが早期終了した場合も終了コードと標準エラーを回収する。
            inputError = writeError.Message;
        }
        await process.WaitForExitAsync(cancellationToken);
        string standardOutput = await outputTask;
        string errorOutput = await errorTask;

        // 通知に収まる範囲へエラーを短縮し、CLIの全出力は保持しない。
        string errorMessage = ShortenError(errorOutput);
        if (string.IsNullOrWhiteSpace(errorMessage) && !string.IsNullOrWhiteSpace(inputError))
        {
            errorMessage = ShortenError($"Codex CLIへの標準入力に失敗しました: {inputError}");
        }
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = $"Codex CLIが終了コード{process.ExitCode}を返しました。";
        }
        return new CodexCommandResult
        {
            IsSuccess = process.ExitCode == 0 && string.IsNullOrWhiteSpace(inputError),
            ExitCode = process.ExitCode,
            ErrorMessage = errorMessage,
            OutputMessage = ShortenOutput(standardOutput)
        };
    }

    /// <summary>中央下ダイアログへ表示するCodex応答を安全な長さへ整える。</summary>
    private static string ShortenOutput(string output)
    {
        // 最終回答を優先するため、長い出力は末尾4,000文字だけを一時表示へ渡す。
        string normalizedOutput = output.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        const int maximumOutputLength = 4000;
        return normalizedOutput.Length > maximumOutputLength
            ? $"…（前半を省略）{Environment.NewLine}{normalizedOutput[^maximumOutputLength..]}"
            : normalizedOutput;
    }

    /// <summary>Windows通知に収まる長さへエラーを短縮する。</summary>
    private static string ShortenError(string error)
    {
        // 改行を空白へ寄せ、長いCLI出力を400文字で切り詰める。
        string normalizedError = error.ReplaceLineEndings(" ").Trim();
        return normalizedError.Length > 400 ? normalizedError[..400] : normalizedError;
    }
}
