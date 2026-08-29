using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaskManager.Windows;

/// <summary>監視親の終了時に監視対象も終了させるWindows Jobを管理する。</summary>
internal sealed class ChildProcessJob : IDisposable
{
    // 監視対象プロセスを束ねるWindows Jobの安全なハンドルを保持する。
    private readonly SafeFileHandle jobHandle;

    // Jobハンドル終了時に所属プロセスを終了する制限値を保持する。
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    /// <summary>親のハンドル消失時に全子プロセスを終了するWindows Jobを作成する。</summary>
    public ChildProcessJob()
    {
        // 監視親が強制終了しても本体だけが孤児化しない制限を設定する。
        jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "監視対象用Windows Jobを作成できませんでした。",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        JobObjectExtendedLimitInformation limitInformation = new();
        limitInformation.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(
            jobHandle,
            JobObjectInformationClass.ExtendedLimitInformation,
            ref limitInformation,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int nativeErrorCode = Marshal.GetLastWin32Error();
            jobHandle.Dispose();
            throw new InvalidOperationException(
                "監視対象用Windows Jobを設定できませんでした。",
                new Win32Exception(nativeErrorCode));
        }
    }

    /// <summary>起動したアプリ本体を監視親専用のWindows Jobへ所属させる。</summary>
    public void Assign(Process applicationProcess)
    {
        // Windows 10以降の入れ子Jobを使い、外側のタスクスケジューラから子の寿命を分離する。
        if (!AssignProcessToJobObject(jobHandle, applicationProcess.Handle))
        {
            throw new InvalidOperationException(
                "Task Manager本体を監視対象用Windows Jobへ登録できませんでした。",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>Windows Jobハンドルを閉じ、残存している監視対象を終了させる。</summary>
    public void Dispose()
    {
        // SafeHandleへ解放を委譲し、異常経路でもKILL_ON_JOB_CLOSEを発動させる。
        jobHandle.Dispose();
    }

    /// <summary>Windows Jobへ設定する情報種別を定義する。</summary>
    private enum JobObjectInformationClass
    {
        // 拡張制限情報のWindows API識別値を保持する。
        ExtendedLimitInformation = 9
    }

    /// <summary>Windows Jobの基本制限情報を表す。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        // プロセス単位のCPU時間制限を保持する。
        public long PerProcessUserTimeLimit;
        // Job全体のCPU時間制限を保持する。
        public long PerJobUserTimeLimit;
        // 有効な制限フラグを保持する。
        public uint LimitFlags;
        // 最小ワーキングセットサイズを保持する。
        public UIntPtr MinimumWorkingSetSize;
        // 最大ワーキングセットサイズを保持する。
        public UIntPtr MaximumWorkingSetSize;
        // 最大アクティブプロセス数を保持する。
        public uint ActiveProcessLimit;
        // プロセッサ親和性を保持する。
        public UIntPtr Affinity;
        // 基本優先度を保持する。
        public uint PriorityClass;
        // スケジューリングクラスを保持する。
        public uint SchedulingClass;
    }

    /// <summary>Windows Jobの入出力統計領域を表す。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        // 読み取り操作数を保持する。
        public ulong ReadOperationCount;
        // 書き込み操作数を保持する。
        public ulong WriteOperationCount;
        // その他の操作数を保持する。
        public ulong OtherOperationCount;
        // 読み取りバイト数を保持する。
        public ulong ReadTransferCount;
        // 書き込みバイト数を保持する。
        public ulong WriteTransferCount;
        // その他の転送バイト数を保持する。
        public ulong OtherTransferCount;
    }

    /// <summary>Windows Jobへ渡す拡張制限情報を表す。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        // 基本制限情報を保持する。
        public JobObjectBasicLimitInformation BasicLimitInformation;
        // 入出力統計領域を保持する。
        public IoCounters IoInformation;
        // プロセスメモリ制限を保持する。
        public UIntPtr ProcessMemoryLimit;
        // Job全体のメモリ制限を保持する。
        public UIntPtr JobMemoryLimit;
        // プロセスメモリ使用量の最大値を保持する。
        public UIntPtr PeakProcessMemoryUsed;
        // Job全体のメモリ使用量の最大値を保持する。
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>新しいWindows Jobを作成する。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    /// <summary>Windows Jobへ拡張制限を設定する。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    /// <summary>指定プロセスをWindows Jobへ所属させる。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
