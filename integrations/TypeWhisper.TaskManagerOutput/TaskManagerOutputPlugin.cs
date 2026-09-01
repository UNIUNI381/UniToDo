using System.Net.Http;
using System.Net.Http.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Clipboard = System.Windows.Forms.Clipboard;

namespace TypeWhisper.Plugin.TaskManagerOutput;

/// <summary>校正済み音声入力をTask Managerの確認画面へ渡すアクション拡張。</summary>
public sealed class TaskManagerOutputPlugin : ITypeWhisperPlugin, IActionPlugin
{
    // Task ManagerのループバックAPIと通知用ホスト機能を保持する。
    private readonly HttpClient httpClient = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:48120"),
        Timeout = TimeSpan.FromSeconds(3)
    };
    private IPluginHostServices? hostServices;

    /// <summary>プラグインを識別する既存互換IDを取得する。</summary>
    public string PluginId => "com.local.codex-thread-output";

    /// <summary>プラグインの表示名を取得する。</summary>
    public string PluginName => "UniToDo Review Output";

    /// <summary>プラグインのバージョンを取得する。</summary>
    public string PluginVersion => "2.0.0";

    /// <summary>出力アクションを識別するIDを取得する。</summary>
    public string ActionId => "codex-thread-output";

    /// <summary>出力アクションの表示名を取得する。</summary>
    public string ActionName => "UniToDoで確認";

    /// <summary>出力アクションのアイコンを取得する。</summary>
    public string ActionIcon => "message-square";

    /// <summary>TypeWhisperホストとの接続を開始する。</summary>
    public Task ActivateAsync(IPluginHostServices services)
    {
        // ログ出力へ利用するホスト機能を保持する。
        hostServices = services;
        return Task.CompletedTask;
    }

    /// <summary>TypeWhisperホストとの接続を終了する。</summary>
    public Task DeactivateAsync()
    {
        // 再有効化時に古いホスト参照を残さない。
        hostServices = null;
        return Task.CompletedTask;
    }

    /// <summary>プラグインが保持するHTTP資源を解放する。</summary>
    public void Dispose()
    {
        // TypeWhisper終了時に接続資源とホスト参照を解放する。
        httpClient.Dispose();
        hostServices = null;
    }

    /// <summary>追加設定を必要としない空の設定画面を返す。</summary>
    public System.Windows.Controls.UserControl CreateSettingsView()
    {
        // 接続先はTask Managerの固定ループバックAPIなので入力欄を設けない。
        return new System.Windows.Controls.UserControl();
    }

    /// <summary>校正済み本文をTask Managerの確認待ちへ追加する。</summary>
    public async Task<ActionResult> ExecuteAsync(
        string text,
        ActionContext context,
        CancellationToken cancellationToken)
    {
        // 空本文はAPIへ送らず、TypeWhisper上で入力エラーとして扱う。
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ActionResult(false, "確認する本文がありません。", null, null, 3);
        }

        try
        {
            // ローカル更新ヘッダーとJSON本文を付けて確認APIへ渡す。
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/codex/reviews")
            {
                Content = JsonContent.Create(new { text })
            };
            request.Headers.Add("X-TaskManager-Request", "local");
            request.Headers.Add("X-TaskManager-Source", "TypeWhisper");
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            hostServices?.Log(PluginLogLevel.Info, "校正済み音声入力をUniToDoの確認待ちへ追加しました。");
            return new ActionResult(true, "UniToDoで確認してください。", null, ActionIcon, 2);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ActionResult(false, "UniToDoへの転送を中止しました。", null, null, 3);
        }
        catch (Exception transferError)
        {
            // API障害時は本文をクリップボードへ退避して音声入力を失わない。
            try
            {
                SetClipboardText(text);
                hostServices?.Log(PluginLogLevel.Error, $"UniToDo転送に失敗し、本文をクリップボードへ退避しました: {transferError.Message}");
                return new ActionResult(false, "UniToDoへ接続できないため、本文をクリップボードへコピーしました。", null, null, 4);
            }
            catch (Exception clipboardError)
            {
                hostServices?.Log(PluginLogLevel.Error, $"UniToDo転送とクリップボード退避に失敗しました: {clipboardError.Message}");
                return new ActionResult(false, "UniToDoへの転送とクリップボード退避に失敗しました。", null, null, 4);
            }
        }
    }

    /// <summary>指定本文をSTAスレッドでWindowsクリップボードへ保存する。</summary>
    private static void SetClipboardText(string text)
    {
        // TypeWhisperの非同期継続スレッドに依存せずクリップボードを操作する。
        Exception? clipboardException = null;
        Thread clipboardThread = new(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception setClipboardError)
            {
                clipboardException = setClipboardError;
            }
        });
        clipboardThread.SetApartmentState(ApartmentState.STA);
        clipboardThread.Start();
        clipboardThread.Join();
        if (clipboardException is not null)
        {
            throw new InvalidOperationException("本文をクリップボードへ保存できませんでした。", clipboardException);
        }
    }
}
