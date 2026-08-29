using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TaskManager.Windows;

/// <summary>F13グローバルホットキーを受け取る非表示Windowsウィンドウを管理する。</summary>
public sealed class VoiceInputHotkeyHost : NativeWindow, IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const int HotkeyIdentifier = 0x0F13;
    private const uint NoRepeatModifier = 0x4000;
    private const uint F13VirtualKey = 0x7C;

    // F13押下時に実行する処理と登録状態を保持する。
    private readonly Action hotkeyAction;
    private bool hotkeyRegistered;
    private bool disposed;

    /// <summary>非表示ウィンドウを作りF13をシステム全体へ登録する。</summary>
    public VoiceInputHotkeyHost(Action voiceInputAction)
    {
        // トレイと同じメッセージループでWM_HOTKEYを受け取る。
        hotkeyAction = voiceInputAction;
        CreateHandle(new CreateParams { Caption = "TaskManagerVoiceInputHotkey" });
        hotkeyRegistered = RegisterHotKey(Handle, HotkeyIdentifier, NoRepeatModifier, F13VirtualKey);
        if (!hotkeyRegistered)
        {
            int errorCode = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(errorCode, "F13を音声入力ホットキーとして登録できませんでした。");
        }
    }

    /// <summary>F13のWindowsメッセージを音声入力開始処理へ渡す。</summary>
    protected override void WndProc(ref Message message)
    {
        // 登録した識別子だけを処理し、その他はWinForms既定処理へ渡す。
        if (message.Msg == HotkeyMessage && message.WParam.ToInt32() == HotkeyIdentifier)
        {
            hotkeyAction();
            return;
        }
        base.WndProc(ref message);
    }

    /// <summary>グローバルホットキーと非表示ウィンドウを解放する。</summary>
    public void Dispose()
    {
        // 終了後にF13がWindowsへ残らないよう登録を明示解除する。
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyIdentifier);
            hotkeyRegistered = false;
        }
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    /// <summary>指定ウィンドウへグローバルホットキーを登録する。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int identifier, uint modifiers, uint virtualKey);

    /// <summary>指定ウィンドウのグローバルホットキーを解除する。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int identifier);
}
