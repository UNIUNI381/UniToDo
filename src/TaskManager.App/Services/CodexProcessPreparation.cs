using System.Diagnostics;

namespace TaskManager.Services;

/// <summary>標準入力を開いたままのCLIを最大1個、期限付きで保持する。</summary>
public sealed class CodexProcessPreparation : IDisposable
{
    // 待機プロセスの受渡しと期限切れを直列化するロックを保持する。
    private readonly object synchronization = new();
    // 待機の上限時間を保持する。
    private readonly TimeSpan idleTimeout;
    // 期限切れ時に未送信プロセスを回収するタイマーを保持する。
    private readonly System.Threading.Timer expiration;
    // 送信待ちプロセスと終了済み状態を保持する。
    private CodexPreparedProcess? prepared;
    private bool disposed;

    /// <summary>標準2分の待機プールを構築する。</summary>
    public CodexProcessPreparation() : this(TimeSpan.FromMinutes(2))
    {
        // 通常利用では録音と確認待ちを含め2分で未使用プロセスを回収する。
    }

    /// <summary>指定期限の待機プールを構築する。</summary>
    public CodexProcessPreparation(TimeSpan idleTimeout)
    {
        // タイマーは事前起動したときだけ有効にする。
        this.idleTimeout = idleTimeout;
        expiration = new System.Threading.Timer(Expire, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>同じ起動設定の待機プロセスを再利用または作成する。</summary>
    public void Prepare(ProcessStartInfo information)
    {
        // 標準入力を閉じないため、確認前の推論やツール実行は開始しない。
        lock (synchronization)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (prepared?.Matches(information) == true)
            {
                return;
            }
            prepared?.Dispose();
            prepared = null;
            prepared = new CodexPreparedProcess(information);
            expiration.Change(idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>一致する待機プロセスの所有権を移し、なければ通常起動する。</summary>
    public CodexPreparedProcess TakeOrStart(ProcessStartInfo information)
    {
        // 期限切れ、早期終了、送信先変更では本文を渡す前に新規起動へ戻す。
        lock (synchronization)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            expiration.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            CodexPreparedProcess? candidate = prepared;
            prepared = null;
            if (candidate?.Matches(information) == true)
            {
                return candidate;
            }
            candidate?.Dispose();
            return new CodexPreparedProcess(information);
        }
    }

    /// <summary>期限切れの未送信プロセスを終了する。</summary>
    private void Expire(object? state)
    {
        // 送信側へ移管済みのプロセスには触れない。
        lock (synchronization)
        {
            // 古いタイマー通知が新しい準備に遅れて到着した場合は残り時間を待つ。
            if (prepared is not null && prepared.Age < idleTimeout)
            {
                expiration.Change(idleTimeout - prepared.Age, Timeout.InfiniteTimeSpan);
                return;
            }
            prepared?.Dispose();
            prepared = null;
        }
    }

    /// <summary>待機タイマーと所有中のプロセスを破棄する。</summary>
    public void Dispose()
    {
        // 終了後に事前起動要求が到着しても新しいプロセスを作成しない。
        lock (synchronization)
        {
            disposed = true;
            expiration.Dispose();
            prepared?.Dispose();
            prepared = null;
        }
    }
}

/// <summary>CLIプロセスと起動直後から読み取る標準出力を所有する。</summary>
public sealed class CodexPreparedProcess : IDisposable
{
    // 再利用時に比較する実行ファイル、作業先、引数を保持する。
    private readonly string executable;
    private readonly string directory;
    private readonly string[] arguments;
    // 経過時間とプロセス停止の排他制御を保持する。
    private readonly Stopwatch age = Stopwatch.StartNew();
    private readonly object synchronization = new();
    private bool disposed;

    // 本文待機プロセスと、その出力読取りタスクを保持する。
    public Process Process { get; }
    public Task<string> Output { get; }
    public Task<string> Error { get; }
    public TimeSpan Age => age.Elapsed;

    /// <summary>シェルを介さず起動し、本文なしで標準入力を待つ。</summary>
    public CodexPreparedProcess(ProcessStartInfo information)
    {
        // 出力パイプを直ちに読み取り、待機中のバッファ詰まりを防ぐ。
        executable = information.FileName;
        directory = information.WorkingDirectory;
        arguments = information.ArgumentList.ToArray();
        Process = new Process { StartInfo = information };
        try
        {
            if (!Process.Start())
            {
                throw new InvalidOperationException("Codex CLIを起動できませんでした。");
            }
            Output = Process.StandardOutput.ReadToEndAsync();
            Error = Process.StandardError.ReadToEndAsync();
        }
        catch
        {
            Process.Dispose();
            throw;
        }
    }

    /// <summary>生存中で起動設定が一致するかを判定する。</summary>
    public bool Matches(ProcessStartInfo information)
    {
        // 本文は比較や引数へ含めず、別の送信先への誤送信を防止する。
        return !Process.HasExited
            && executable == information.FileName
            && directory == information.WorkingDirectory
            && arguments.SequenceEqual(information.ArgumentList);
    }

    /// <summary>所有するプロセスだけを子プロセスごと停止する。</summary>
    public void Stop()
    {
        // 期限切れやアプリ終了で空入力を送らず、対象プロセスを直接終了する。
        lock (synchronization)
        {
            if (disposed)
            {
                return;
            }
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 終了直後との競合は停止済みとして扱う。
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // OS側で先に終了した場合もタイマー例外でアプリを停止させない。
            }
        }
    }

    /// <summary>プロセスを停止しOSハンドルを解放する。</summary>
    public void Dispose()
    {
        // 取消しコールバックとの競合を防ぎ、二重解放しない。
        lock (synchronization)
        {
            if (disposed)
            {
                return;
            }
            Stop();
            disposed = true;
            Process.Dispose();
        }
    }
}
