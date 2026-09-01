using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Services;

namespace TaskManager.Windows;

/// <summary>Windowsトレイアイコンとバルーン通知を管理する。</summary>
public sealed class TaskManagerTray(
    IServiceProvider serviceProvider,
    CodexReviewService codexReviewService,
    CodexSubmissionQueue codexSubmissionQueue,
    VoiceInputCoordinator voiceInputCoordinator,
    TaskManagerPaths taskManagerPaths,
    IHostApplicationLifetime applicationLifetime,
    ILogger<TaskManagerTray> logger) : IUserNotificationService
{
    // トレイスレッド、音声確認、送信キュー、音声起動、通知、終了制御を保持する。
    private readonly IServiceProvider services = serviceProvider;
    private readonly CodexReviewService reviews = codexReviewService;
    private readonly CodexSubmissionQueue submissions = codexSubmissionQueue;
    private readonly VoiceInputCoordinator voiceInput = voiceInputCoordinator;
    private readonly TaskManagerPaths paths = taskManagerPaths;
    private readonly IHostApplicationLifetime lifetime = applicationLifetime;
    private readonly ILogger<TaskManagerTray> applicationLogger = logger;
    private readonly ConcurrentQueue<(string Title, string Message)> notificationQueue = new();
    private readonly ManualResetEventSlim trayReady = new(false);
    private Thread? trayThread;
    private TrayApplicationContext? trayContext;

    /// <summary>音声確認画面を表示可能な対話セッションであるかを返す。</summary>
    public bool CanShowCodexReview => Environment.UserInteractive && Volatile.Read(ref trayContext) is not null;

    /// <summary>対話セッションでトレイアイコンを開始する。</summary>
    public void Start()
    {
        // CLI実行や非対話環境ではトレイを作成しない。
        if (!Environment.UserInteractive || trayThread is not null)
        {
            return;
        }
        trayThread = new Thread(RunTrayLoop)
        {
            IsBackground = true,
            Name = "TaskManagerTray"
        };
        trayThread.SetApartmentState(ApartmentState.STA);
        reviews.ReviewAvailable += RequestCodexReview;
        voiceInput.StatusChanged += RequestVoiceInputStatus;
        trayThread.Start();
        trayReady.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Windows通知として表示する内容をキューへ追加する。</summary>
    public void Notify(string title, string message)
    {
        // トレイスレッドから安全に表示できるようキューへ渡す。
        notificationQueue.Enqueue((title, message));
    }

    /// <summary>Codex CLIの応答をF13と同じ中央下の状態画面へ表示する。</summary>
    public void ShowCodexResult(string message, bool isError)
    {
        // CLIワーカーから受けた一時的な応答をWinFormsスレッドへ渡す。
        RequestVoiceInputStatus(new VoiceInputStatus(
            message,
            isError ? VoiceInputStatusKind.Error : VoiceInputStatusKind.Success,
            isError ? "Codex CLIエラー" : "Codex応答",
            true));
    }

    /// <summary>Codex CLIから応答が返るまで中央下の状態画面を継続表示する。</summary>
    public void ShowCodexProcessing()
    {
        // Windows通知を使わず、F13と同じ非アクティブ画面を処理中表示へ更新する。
        RequestVoiceInputStatus(new VoiceInputStatus(
            "Codexからの応答を待っています…",
            VoiceInputStatusKind.Progress));
    }

    /// <summary>トレイアイコンのメッセージループを実行する。</summary>
    private void RunTrayLoop()
    {
        // WinFormsのメッセージループを専用STAスレッドで維持する。
        try
        {
            ApplicationConfiguration.Initialize();
            using TrayApplicationContext applicationContext = new(
                notificationQueue,
                OpenDashboard,
                SynchronizeCalendarAsync,
                lifetime.StopApplication,
                ShowNextCodexReviewAsync,
                BeginVoiceInput,
                message => WriteVoiceReviewLog(message));
            Volatile.Write(ref trayContext, applicationContext);
            trayReady.Set();
            Application.Run(applicationContext);
        }
        catch (Exception trayError)
        {
            applicationLogger.LogError(trayError, "Tray application failed.");
        }
        finally
        {
            Volatile.Write(ref trayContext, null);
            trayReady.Set();
            reviews.ReviewAvailable -= RequestCodexReview;
            voiceInput.StatusChanged -= RequestVoiceInputStatus;
        }
    }

    /// <summary>F13押下を即時表示し、音声入力の準備処理をバックグラウンドで開始する。</summary>
    private void BeginVoiceInput()
    {
        // 作業中画面のフォーカスを維持したまま、押下への反応を先に表示する。
        TrayApplicationContext? applicationContext = Volatile.Read(ref trayContext);
        applicationContext?.CaptureVoiceInputWorkingArea();
        applicationContext?.ShowVoiceInputStatus(new VoiceInputStatus(
            "音声入力を準備しています…",
            VoiceInputStatusKind.Progress));
        _ = Task.Run(() => voiceInput.StartAsync(lifetime.ApplicationStopping));
    }

    /// <summary>音声入力の準備状況をトレイUIスレッドへ渡す。</summary>
    private void RequestVoiceInputStatus(VoiceInputStatus status)
    {
        // 起動処理のワーカースレッドからWinFormsを直接操作しない。
        Volatile.Read(ref trayContext)?.RequestVoiceInputStatus(status);
    }

    /// <summary>次の音声確認画面の表示をトレイスレッドへ要求する。</summary>
    private void RequestCodexReview()
    {
        // APIや送信ワーカーのスレッドからWinForms操作を直接行わない。
        WriteVoiceReviewLog("確認待ち通知を受信しました。");
        Volatile.Read(ref trayContext)?.RequestCodexReview();
    }

    /// <summary>確認待ちの先頭を設定済み送信先とともに表示する。</summary>
    private async Task ShowNextCodexReviewAsync()
    {
        // 表示中画面がある場合は閉じた後の再要求に任せる。
        WriteVoiceReviewLog("確認画面の表示処理を開始しました。");
        TrayApplicationContext? applicationContext = Volatile.Read(ref trayContext);
        if (applicationContext is null || applicationContext.HasCodexReviewWindow)
        {
            WriteVoiceReviewLog(applicationContext is null
                ? "トレイ画面が未初期化のため表示を保留しました。"
                : "既存の確認画面があるため表示を保留しました。");
            return;
        }
        if (!reviews.TryActivateNext(out CodexReviewItem? reviewItem, out int waitingCount)
            || reviewItem is null)
        {
            WriteVoiceReviewLog("表示可能な確認待ちはありませんでした。");
            return;
        }
        try
        {
            WriteVoiceReviewLog($"確認待ちを表示中へ移しました。待機件数={waitingCount}");
            TaskRepository repository = services.GetRequiredService<TaskRepository>();
            TaskManagerSettings settings = await repository.GetSettingsAsync().ConfigureAwait(false);
            string threadIdentifier = CodexThreadIdentifier.Normalize(settings.CodexThreadIdentifier);
            WriteVoiceReviewLog("送信先設定を読み込み、UIスレッドへ表示を要求します。");
            await applicationContext.ShowCodexReviewAsync(
                reviewItem,
                threadIdentifier,
                waitingCount,
                (item, text) => SendCodexReviewAsync(item, text, threadIdentifier),
                DiscardCodexReview);
            WriteVoiceReviewLog("確認画面を閉じました。次の確認待ちを検索します。");
            RequestCodexReview();
        }
        catch (Exception reviewError)
        {
            reviews.ReturnActiveToFront(reviewItem.Identifier);
            applicationLogger.LogError(reviewError, "Codex review window could not be prepared.");
            WriteVoiceReviewLog("確認画面を表示できませんでした。", reviewError);
            RequestVoiceInputStatus(new VoiceInputStatus(
                reviewError.Message,
                VoiceInputStatusKind.Error,
                "音声入力の確認エラー",
                true));
        }
    }

    /// <summary>本文を含まない音声確認画面の遷移を診断ログへ記録する。</summary>
    private void WriteVoiceReviewLog(string message, Exception? exception = null)
    {
        // 障害調査用メタデータだけを記録し、音声本文と送信先は保存しない。
        try
        {
            paths.EnsureDirectories();
            string logPath = Path.Combine(paths.LogDirectory, $"voice-review-{DateTimeOffset.Now:yyyy-MM-dd}.log");
            string logText = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            if (exception is not null)
            {
                logText += exception + Environment.NewLine;
            }
            File.AppendAllText(logPath, logText, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 診断ログの失敗で確認画面の表示処理を止めない。
        }
    }

    /// <summary>編集済み本文をCodex CLI送信キューへ追加する。</summary>
    private Task<string?> SendCodexReviewAsync(
        CodexReviewItem reviewItem,
        string text,
        string threadIdentifier)
    {
        // 受付時点で本文と送信先を確定し、確認画面をすぐ閉じられるようにする。
        try
        {
            CodexReviewService.ValidateText(text);
            submissions.Enqueue(new CodexSubmission
            {
                ReviewIdentifier = reviewItem.Identifier,
                ThreadIdentifier = threadIdentifier,
                Text = text,
                CreatedAt = reviewItem.CreatedAt
            });
            if (!reviews.CompleteActive(reviewItem.Identifier))
            {
                return Task.FromResult<string?>("確認状態が変更されたため送信できませんでした。");
            }
            return Task.FromResult<string?>(null);
        }
        catch (Exception sendError)
        {
            return Task.FromResult<string?>(sendError.Message);
        }
    }

    /// <summary>現在の確認項目を明示的に破棄する。</summary>
    private void DiscardCodexReview(CodexReviewItem reviewItem)
    {
        // 閉じる、Esc、破棄ボタンを同じ完了処理へ集約する。
        reviews.CompleteActive(reviewItem.Identifier);
    }

    /// <summary>既定ブラウザでダッシュボードを開く。</summary>
    private static void OpenDashboard()
    {
        // シェル実行で既定ブラウザへlocalhostを渡す。
        Process.Start(new ProcessStartInfo(TaskManagerPaths.GetLocalAddress()) { UseShellExecute = true });
    }

    /// <summary>トレイメニューからカレンダー同期を実行する。</summary>
    private async Task SynchronizeCalendarAsync()
    {
        // スコープを作成して同期サービスを安全に呼び出す。
        try
        {
            using IServiceScope serviceScope = services.CreateScope();
            GoogleCalendarService calendarService = serviceScope.ServiceProvider.GetRequiredService<GoogleCalendarService>();
            RecommendationService recommendationService = serviceScope.ServiceProvider.GetRequiredService<RecommendationService>();
            int eventCount = await calendarService.SynchronizeAsync(false);
            await recommendationService.RefreshAsync();
            Notify("カレンダー同期", $"{eventCount}件の予定を更新しました。");
        }
        catch (Exception synchronizationError)
        {
            Notify("カレンダー同期エラー", synchronizationError.Message);
        }
    }

    /// <summary>トレイのWinFormsコンテキストを実装する。</summary>
    private sealed class TrayApplicationContext : ApplicationContext
    {
        // アイコン、通知キュー、UIディスパッチャー、処理デリゲート、確認画面、音声状態画面を保持する。
        private readonly NotifyIcon notificationIcon;
        // Web画面と同じ意匠で生成したトレイ用アイコンを保持する。
        private readonly Icon taskManagerIcon;
        private readonly System.Windows.Forms.Timer notificationTimer;
        private readonly ConcurrentQueue<(string Title, string Message)> queue;
        private readonly Control dispatcher;
        private readonly Action openAction;
        private readonly Func<Task> synchronizeAction;
        private readonly Action exitAction;
        private readonly Func<Task> showCodexReviewAction;
        private readonly Action<string> voiceReviewLogAction;
        private readonly VoiceInputHotkeyHost? voiceInputHotkeyHost;
        private readonly int userInterfaceThreadIdentifier;
        // F13押下時に利用者が操作していたディスプレイの作業領域を保持する。
        private Rectangle voiceInputWorkingArea;
        private bool voiceInputWorkingAreaCaptured;
        private CodexReviewForm? codexReviewForm;
        private VoiceInputStatusForm? voiceInputStatusForm;

        /// <summary>音声入力の確認画面が表示中であるかを返す。</summary>
        public bool HasCodexReviewWindow => codexReviewForm is not null;

        /// <summary>トレイアイコンとメニューを初期化する。</summary>
        public TrayApplicationContext(
            ConcurrentQueue<(string Title, string Message)> notificationQueue,
            Action openDashboardAction,
            Func<Task> synchronizeCalendarAction,
            Action stopApplicationAction,
            Func<Task> showNextCodexReviewAction,
            Action startVoiceInputAction,
            Action<string> writeVoiceReviewLogAction)
        {
            // メニュー操作と通知ポーリングを設定する。
            userInterfaceThreadIdentifier = Environment.CurrentManagedThreadId;
            voiceInputWorkingArea = ResolveForegroundWorkingArea();
            queue = notificationQueue;
            openAction = openDashboardAction;
            synchronizeAction = synchronizeCalendarAction;
            exitAction = stopApplicationAction;
            showCodexReviewAction = showNextCodexReviewAction;
            voiceReviewLogAction = writeVoiceReviewLogAction;
            dispatcher = new Control();
            dispatcher.CreateControl();
            ContextMenuStrip contextMenu = new();
            contextMenu.Items.Add("ダッシュボードを開く", null, HandleOpenClick);
            contextMenu.Items.Add("音声入力の確認を開く", null, HandleCodexReviewClick);
            contextMenu.Items.Add("カレンダーを同期", null, HandleSynchronizeClick);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("終了", null, HandleExitClick);
            taskManagerIcon = CreateTaskManagerIcon();
            notificationIcon = new NotifyIcon
            {
                Icon = taskManagerIcon,
                Text = TaskConstants.ApplicationDisplayName,
                Visible = true,
                ContextMenuStrip = contextMenu
            };
            notificationIcon.DoubleClick += HandleOpenClick;
            notificationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            notificationTimer.Tick += HandleNotificationTimer;
            notificationTimer.Start();

            // TypeWhisper内部のF14とは分離し、利用者向けF13をTask Managerで受け取る。
            try
            {
                voiceInputHotkeyHost = new VoiceInputHotkeyHost(startVoiceInputAction);
            }
            catch (Exception hotkeyError)
            {
                ShowVoiceInputStatus(new VoiceInputStatus(
                    hotkeyError.Message,
                    VoiceInputStatusKind.Error,
                    "音声入力ホットキーエラー",
                    true));
            }
        }

        /// <summary>トレイ資源を解放する。</summary>
        protected override void Dispose(bool disposing)
        {
            // アイコンが終了後も残らないよう明示的に非表示へする。
            if (disposing)
            {
                codexReviewForm?.Dispose();
                voiceInputStatusForm?.Dispose();
                voiceInputHotkeyHost?.Dispose();
                dispatcher.Dispose();
                notificationTimer.Stop();
                notificationTimer.Dispose();
                notificationIcon.Visible = false;
                notificationIcon.Dispose();
                taskManagerIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Web画面と同じ緑のチェックマークをWindowsアイコンとして生成する。</summary>
        private static Icon CreateTaskManagerIcon()
        {
            // 32pxの透過画像へ緑の角丸背景と白いチェックをアンチエイリアス付きで描画する。
            const int iconSize = 32;
            const float backgroundInset = 1f;
            const float cornerDiameter = 18f;
            using Bitmap iconBitmap = new(iconSize, iconSize);
            using Graphics iconGraphics = Graphics.FromImage(iconBitmap);
            iconGraphics.Clear(Color.Transparent);
            iconGraphics.SmoothingMode = SmoothingMode.AntiAlias;

            RectangleF backgroundBounds = new(
                backgroundInset,
                backgroundInset,
                iconSize - (backgroundInset * 2f),
                iconSize - (backgroundInset * 2f));
            using GraphicsPath backgroundPath = new();
            backgroundPath.AddArc(backgroundBounds.Left, backgroundBounds.Top, cornerDiameter, cornerDiameter, 180f, 90f);
            backgroundPath.AddArc(backgroundBounds.Right - cornerDiameter, backgroundBounds.Top, cornerDiameter, cornerDiameter, 270f, 90f);
            backgroundPath.AddArc(backgroundBounds.Right - cornerDiameter, backgroundBounds.Bottom - cornerDiameter, cornerDiameter, cornerDiameter, 0f, 90f);
            backgroundPath.AddArc(backgroundBounds.Left, backgroundBounds.Bottom - cornerDiameter, cornerDiameter, cornerDiameter, 90f, 90f);
            backgroundPath.CloseFigure();
            using SolidBrush backgroundBrush = new(Color.FromArgb(28, 107, 71));
            iconGraphics.FillPath(backgroundBrush, backgroundPath);

            // SVGの64px座標を半分へ縮小した比率で白いチェックを配置する。
            using Pen checkPen = new(Color.White, 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            PointF[] checkPoints =
            [
                new PointF(9f, 16.25f),
                new PointF(13.5f, 20.5f),
                new PointF(23f, 10.5f)
            ];
            iconGraphics.DrawLines(checkPen, checkPoints);

            // 一時HICONを複製して.NET側が所有するIconへ変換し、ネイティブ資源を解放する。
            IntPtr temporaryIconHandle = iconBitmap.GetHicon();
            try
            {
                using Icon temporaryIcon = Icon.FromHandle(temporaryIconHandle);
                return (Icon)temporaryIcon.Clone();
            }
            finally
            {
                _ = DestroyIcon(temporaryIconHandle);
            }
        }

        /// <summary>一時生成したWindowsアイコンハンドルを解放する。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr iconHandle);

        /// <summary>現在の前面ウィンドウを取得する。</summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>ダッシュボードメニューのクリックを処理する。</summary>
        private void HandleOpenClick(object? sender, EventArgs eventArguments)
        {
            // 既定ブラウザで画面を開く。
            openAction();
        }

        /// <summary>トレイメニューから保留中の音声確認画面を開く。</summary>
        private async void HandleCodexReviewClick(object? sender, EventArgs eventArguments)
        {
            // 表示中なら前面化し、未表示なら次の確認項目を要求する。
            if (codexReviewForm is not null)
            {
                codexReviewForm.Activate();
                codexReviewForm.BringToFront();
                return;
            }
            await showCodexReviewAction();
        }

        /// <summary>カレンダー同期メニューのクリックを処理する。</summary>
        private async void HandleSynchronizeClick(object? sender, EventArgs eventArguments)
        {
            // 非同期同期をUIスレッドを止めずに実行する。
            await synchronizeAction();
        }

        /// <summary>終了メニューのクリックを処理する。</summary>
        private void HandleExitClick(object? sender, EventArgs eventArguments)
        {
            // Webホストを停止してメッセージループを終了する。
            exitAction();
            ExitThread();
        }

        /// <summary>キューにあるPC通知を順に表示する。</summary>
        private void HandleNotificationTimer(object? sender, EventArgs eventArguments)
        {
            // 連続通知で最後の通知が失われないよう1回につき1件だけ表示する。
            if (queue.TryDequeue(out (string Title, string Message) notification))
            {
                notificationIcon.BalloonTipTitle = notification.Title;
                notificationIcon.BalloonTipText = notification.Message;
                notificationIcon.BalloonTipIcon = ToolTipIcon.Info;
                notificationIcon.ShowBalloonTip(10000);
            }
        }

        /// <summary>音声確認画面の表示処理をWinFormsスレッドへ切り替える。</summary>
        public void RequestCodexReview()
        {
            // APIスレッドからの通知をコントロールのメッセージキューへ渡す。
            if (dispatcher.IsDisposed)
            {
                return;
            }
            dispatcher.BeginInvoke(new Action(async () => await showCodexReviewAction()));
        }

        /// <summary>音声入力の準備状況をWinFormsスレッドへ切り替えて表示する。</summary>
        public void RequestVoiceInputStatus(VoiceInputStatus status)
        {
            // ワーカーから届いた状態をトレイのメッセージキューへ渡す。
            if (dispatcher.IsDisposed)
            {
                return;
            }
            dispatcher.BeginInvoke(new Action(() => ShowVoiceInputStatus(status)));
        }

        /// <summary>非アクティブな小型画面へ音声入力の現在状態を表示する。</summary>
        public void ShowVoiceInputStatus(VoiceInputStatus status)
        {
            // 状態画面を一つだけ再利用し、成功・失敗で閉じた後は次回作り直す。
            if (voiceInputStatusForm is null || voiceInputStatusForm.IsDisposed)
            {
                voiceInputStatusForm = new VoiceInputStatusForm();
                voiceInputStatusForm.FormClosed += HandleVoiceInputStatusClosed;
                voiceInputStatusForm.UpdateStatus(status);
                voiceInputStatusForm.Show();
                return;
            }
            voiceInputStatusForm.UpdateStatus(status);
        }

        /// <summary>F13押下時の前面ウィンドウがあるディスプレイ作業領域を記憶する。</summary>
        public void CaptureVoiceInputWorkingArea()
        {
            // TypeWhisper処理後も同じ物理画面へ確認フォームを戻せるよう先に画面を確定する。
            voiceInputWorkingArea = ResolveForegroundWorkingArea();
            voiceInputWorkingAreaCaptured = true;
        }

        /// <summary>指定本文の確認ダイアログをWinFormsスレッドで閉じるまで表示する。</summary>
        public Task ShowCodexReviewAsync(
            CodexReviewItem reviewItem,
            string threadIdentifier,
            int waitingCount,
            Func<CodexReviewItem, string, Task<string?>> sendAction,
            Action<CodexReviewItem> discardAction)
        {
            // SQLite読込後の継続スレッドにかかわらずモーダル表示をメッセージループへ戻す。
            return InvokeOnUserInterfaceThreadAsync(() => ShowCodexReview(
                reviewItem,
                threadIdentifier,
                waitingCount,
                sendAction,
                discardAction));
        }

        /// <summary>指定本文を編集する音声確認画面をモーダル表示する。</summary>
        private void ShowCodexReview(
            CodexReviewItem reviewItem,
            string threadIdentifier,
            int waitingCount,
            Func<CodexReviewItem, string, Task<string?>> sendAction,
            Action<CodexReviewItem> discardAction)
        {
            // 既存画面がある場合は新しい画面を重ねず、閉じた後に次を表示する。
            if (codexReviewForm is not null)
            {
                return;
            }
            // API復元を含め、確認画面と同じデスクトップに所有元となる進捗画面を必ず用意する。
            EnsureVoiceInputStatusForReview();
            // F13を経由しない復元時は所有元の画面を使い、直前の別ディスプレイへ飛ばさない。
            Rectangle reviewWorkingArea = voiceInputWorkingAreaCaptured
                ? voiceInputWorkingArea
                : ResolveVoiceInputStatusWorkingArea();
            voiceInputWorkingAreaCaptured = false;
            using CodexReviewForm reviewForm = new(
                reviewItem,
                threadIdentifier,
                waitingCount,
                reviewWorkingArea,
                sendAction,
                discardAction);
            codexReviewForm = reviewForm;
            reviewForm.Shown += HandleCodexReviewShown;
            try
            {
                // 既に見えている進捗画面を所有元にして同じデスクトップの前面へ確実に表示する。
                voiceReviewLogAction(
                    $"確認ダイアログを表示します。OwnerVisible={voiceInputStatusForm?.Visible == true}, "
                    + $"Bounds={reviewForm.Bounds}, WorkingArea={reviewWorkingArea}");
                if (voiceInputStatusForm is not null
                    && !voiceInputStatusForm.IsDisposed
                    && voiceInputStatusForm.Visible)
                {
                    reviewForm.ShowDialog(voiceInputStatusForm);
                }
                else
                {
                    reviewForm.ShowDialog();
                }
            }
            finally
            {
                // ダイアログ終了時に参照と進捗表示を必ず解放し、次の確認項目を表示可能に戻す。
                voiceReviewLogAction("確認ダイアログのモーダル表示が終了しました。");
                reviewForm.Shown -= HandleCodexReviewShown;
                codexReviewForm = null;
                CloseVoiceInputStatus();
            }
        }

        /// <summary>確認ダイアログの所有元となる音声状態画面を表示する。</summary>
        private void EnsureVoiceInputStatusForReview()
        {
            // F13を経由しない復元要求でも、利用者に見える同一デスクトップ上へ所有元を作る。
            if (voiceInputStatusForm is null
                || voiceInputStatusForm.IsDisposed
                || !voiceInputStatusForm.Visible)
            {
                ShowVoiceInputStatus(new VoiceInputStatus(
                    "校正内容の確認画面を開いています…",
                    VoiceInputStatusKind.Progress));
            }
        }

        /// <summary>表示中の音声状態画面があるディスプレイ作業領域を返す。</summary>
        private Rectangle ResolveVoiceInputStatusWorkingArea()
        {
            // 所有元が表示済みなら同じ画面を優先し、取得不能時だけ前面画面へ戻る。
            return voiceInputStatusForm is not null
                && !voiceInputStatusForm.IsDisposed
                && voiceInputStatusForm.Visible
                ? Screen.FromControl(voiceInputStatusForm).WorkingArea
                : ResolveForegroundWorkingArea();
        }

        /// <summary>現在の前面ウィンドウまたはマウスがあるディスプレイ作業領域を解決する。</summary>
        private static Rectangle ResolveForegroundWorkingArea()
        {
            // 前面ウィンドウを優先し、取得不能時だけ利用者のマウスがある画面へフォールバックする。
            IntPtr foregroundWindowHandle = GetForegroundWindow();
            return foregroundWindowHandle == IntPtr.Zero
                ? Screen.FromPoint(Cursor.Position).WorkingArea
                : Screen.FromHandle(foregroundWindowHandle).WorkingArea;
        }

        /// <summary>確認画面のShownイベントと表示状態を診断ログへ記録する。</summary>
        private void HandleCodexReviewShown(object? sender, EventArgs eventArguments)
        {
            // 本文を含めず、ネイティブ画面が表示状態へ遷移したことだけを記録する。
            if (codexReviewForm is not null)
            {
                voiceReviewLogAction(
                    $"確認画面のShownイベントを受信しました。Visible={codexReviewForm.Visible}, "
                    + $"HandleCreated={codexReviewForm.IsHandleCreated}, Bounds={codexReviewForm.Bounds}");
            }
        }

        /// <summary>指定処理をWinFormsスレッドへ切り替えて完了まで待機する。</summary>
        private Task InvokeOnUserInterfaceThreadAsync(Action action)
        {
            // 既にUIスレッド上なら再ディスパッチせず、その場で実行する。
            if (Environment.CurrentManagedThreadId == userInterfaceThreadIdentifier)
            {
                action();
                return Task.CompletedTask;
            }
            if (dispatcher.IsDisposed)
            {
                return Task.FromException(new ObjectDisposedException(nameof(TrayApplicationContext)));
            }

            // 非同期DB読込後のスレッドからフォームを作らず、表示結果を呼出し元へ返す。
            TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        action();
                        completionSource.SetResult();
                    }
                    catch (Exception displayError)
                    {
                        completionSource.SetException(displayError);
                    }
                }));
            }
            catch (Exception dispatchError)
            {
                completionSource.SetException(dispatchError);
            }
            return completionSource.Task;
        }

        /// <summary>音声入力の状態画面を閉じ、次回表示可能な状態へ戻す。</summary>
        private void CloseVoiceInputStatus()
        {
            // モーダル画面の所有元を表示中は維持し、モーダル終了後にだけ閉じる。
            if (voiceInputStatusForm is not null && !voiceInputStatusForm.IsDisposed)
            {
                voiceInputStatusForm.Close();
            }
        }

        /// <summary>音声入力の状態画面が閉じた後に参照を解放する。</summary>
        private void HandleVoiceInputStatusClosed(object? sender, FormClosedEventArgs eventArguments)
        {
            // 次回F13で新しい非アクティブ画面を作れるよう現在の画面を破棄する。
            if (voiceInputStatusForm is null)
            {
                return;
            }
            voiceInputStatusForm.FormClosed -= HandleVoiceInputStatusClosed;
            voiceInputStatusForm.Dispose();
            voiceInputStatusForm = null;
        }
    }
}
