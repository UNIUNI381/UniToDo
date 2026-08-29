using System.Drawing;
using TaskManager.Services;

namespace TaskManager.Windows;

/// <summary>フォーカスを奪わず音声入力の準備状況を即時表示する。</summary>
public sealed class VoiceInputStatusForm : Form
{
    private const int NoActivateExtendedStyle = 0x08000000;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int CompactMinimumWidth = 390;
    private const int CompactMaximumWidth = 900;
    private const int CompactMinimumStatusHeight = 30;
    private const int CompactHorizontalPadding = 36;
    private const int CompactScreenMargin = 24;
    private const int BottomMargin = 120;
    private const int MaximumDetailWidth = 684;
    private const int MaximumDetailHeight = 180;

    // 状態文、Codex応答欄、進捗表示、閉じる操作、自動終了タイマーを保持する。
    private readonly Label statusLabel;
    private readonly TextBox detailTextBox;
    private readonly ProgressBar progressBar;
    private readonly Button closeButton;
    private readonly System.Windows.Forms.Timer closeTimer;

    /// <summary>音声入力の状態表示ウィンドウを初期化する。</summary>
    public VoiceInputStatusForm()
    {
        // 作業中アプリのフォーカスを維持する小型ポップアップを構成する。
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 34, 39);
        ClientSize = new Size(CompactMinimumWidth, 82);
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        statusLabel = new Label
        {
            AutoEllipsis = false,
            ForeColor = Color.White,
            Font = new Font(Control.DefaultFont.FontFamily, 10.5F, FontStyle.Regular),
            Location = new Point(18, 14),
            Size = new Size(354, 30),
            Text = "音声入力を準備しています…",
            TextAlign = ContentAlignment.MiddleLeft
        };
        progressBar = new ProgressBar
        {
            Location = new Point(18, 56),
            MarqueeAnimationSpeed = 24,
            Size = new Size(354, 5),
            Style = ProgressBarStyle.Marquee
        };
        detailTextBox = new TextBox
        {
            BackColor = Color.FromArgb(42, 44, 50),
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.White,
            Font = new Font(Control.DefaultFont.FontFamily, 10F, FontStyle.Regular),
            Location = new Point(18, 50),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.None,
            Size = new Size(684, 202),
            TabStop = false,
            Visible = false,
            WordWrap = true
        };
        closeButton = new Button
        {
            AutoSize = false,
            BackColor = Color.FromArgb(58, 61, 68),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            Size = new Size(88, 30),
            TabStop = false,
            Text = "閉じる",
            UseVisualStyleBackColor = false,
            Visible = false
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(90, 93, 102);
        closeButton.Click += HandleCloseButtonClick;
        Controls.Add(statusLabel);
        Controls.Add(detailTextBox);
        Controls.Add(progressBar);
        Controls.Add(closeButton);

        closeTimer = new System.Windows.Forms.Timer();
        closeTimer.Tick += HandleCloseTimer;
    }

    /// <summary>表示時にマウス位置の画面中央下から少し上へ配置する。</summary>
    protected override void OnShown(EventArgs eventArguments)
    {
        // 複数画面環境では利用者が現在操作している画面へ表示する。
        PositionAtBottomCenter();
        base.OnShown(eventArguments);
    }

    /// <summary>音声入力の状態文と表示時間を更新する。</summary>
    public void UpdateStatus(VoiceInputStatus status)
    {
        // Codex応答は拡大表示し、通常の起動状態はメッセージ量に合わせて調整する。
        closeTimer.Stop();
        if (status.IsDetailed)
        {
            ShowDetailedStatus(status);
            return;
        }
        statusLabel.Text = status.Message;
        statusLabel.ForeColor = status.Kind == VoiceInputStatusKind.Error
            ? Color.FromArgb(255, 180, 180)
            : Color.White;
        detailTextBox.Visible = false;
        closeButton.Visible = false;
        UpdateCompactLayout(status.Message);
        progressBar.Visible = true;
        progressBar.Style = status.Kind == VoiceInputStatusKind.Progress
            ? ProgressBarStyle.Marquee
            : ProgressBarStyle.Blocks;
        progressBar.Value = status.Kind == VoiceInputStatusKind.Error ? 0 : 100;
        if (status.Kind != VoiceInputStatusKind.Progress)
        {
            closeTimer.Interval = status.Kind == VoiceInputStatusKind.Success ? 2200 : 8000;
            closeTimer.Start();
        }
        PositionAtBottomCenter();
    }

    /// <summary>通常状態の幅と高さをメッセージ全体が読める寸法へ調整する。</summary>
    private void UpdateCompactLayout(string message)
    {
        // まず横幅を広げ、画面内の上限へ達した場合は省略せず複数行へ折り返す。
        Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        int availableScreenWidth = Math.Max(1, workingArea.Width - (CompactScreenMargin * 2));
        int maximumClientWidth = Math.Min(CompactMaximumWidth, availableScreenWidth);
        int minimumClientWidth = Math.Min(CompactMinimumWidth, maximumClientWidth);
        Size singleLineSize = TextRenderer.MeasureText(
            message,
            statusLabel.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int clientWidth = Math.Clamp(
            singleLineSize.Width + CompactHorizontalPadding,
            minimumClientWidth,
            maximumClientWidth);
        int contentWidth = Math.Max(1, clientWidth - CompactHorizontalPadding);
        Size wrappedTextSize = TextRenderer.MeasureText(
            message,
            statusLabel.Font,
            new Size(contentWidth, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        int statusHeight = Math.Max(CompactMinimumStatusHeight, wrappedTextSize.Height);

        // 状態文の下へ進捗バーと余白を積み、内容量に応じたフォーム高を設定する。
        int progressTop = 14 + statusHeight + 12;
        ClientSize = new Size(clientWidth, progressTop + 26);
        statusLabel.Location = new Point(18, 14);
        statusLabel.Size = new Size(contentWidth, statusHeight);
        progressBar.Location = new Point(18, progressTop);
        progressBar.Size = new Size(contentWidth, 5);
    }

    /// <summary>Codex CLIの応答を内容量に合わせた詳細画面へ表示する。</summary>
    private void ShowDetailedStatus(VoiceInputStatus status)
    {
        // 文字量から表示寸法を計算し、収まらない場合だけ縦スクロールを有効にする。
        string displayMessage = string.IsNullOrWhiteSpace(status.Message)
            ? "応答本文はありません。"
            : status.Message;
        string longestLine = displayMessage
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .OrderByDescending(line => line.Length)
            .FirstOrDefault() ?? displayMessage;
        Size longestLineSize = TextRenderer.MeasureText(
            longestLine,
            detailTextBox.Font,
            new Size(MaximumDetailWidth, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int detailWidth = Math.Clamp(longestLineSize.Width + 20, 354, MaximumDetailWidth);
        Size measuredTextSize = TextRenderer.MeasureText(
            displayMessage,
            detailTextBox.Font,
            new Size(detailWidth - 12, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        int requiredDetailHeight = measuredTextSize.Height + 16;
        int detailHeight = Math.Clamp(requiredDetailHeight, 46, MaximumDetailHeight);
        bool requiresScrollBar = requiredDetailHeight > MaximumDetailHeight;
        int clientWidth = detailWidth + 36;
        int buttonTop = 50 + detailHeight + 10;
        ClientSize = new Size(clientWidth, buttonTop + 42);
        statusLabel.Location = new Point(18, 12);
        statusLabel.Size = new Size(detailWidth, 30);
        statusLabel.Text = string.IsNullOrWhiteSpace(status.Heading) ? "Codex応答" : status.Heading;
        statusLabel.ForeColor = status.Kind == VoiceInputStatusKind.Error
            ? Color.FromArgb(255, 180, 180)
            : Color.White;
        detailTextBox.Size = new Size(detailWidth, detailHeight);
        detailTextBox.ScrollBars = requiresScrollBar ? ScrollBars.Vertical : ScrollBars.None;
        detailTextBox.Text = displayMessage;
        detailTextBox.SelectionStart = 0;
        detailTextBox.SelectionLength = 0;
        detailTextBox.Visible = true;
        progressBar.Visible = false;
        closeButton.Location = new Point(clientWidth - closeButton.Width - 18, buttonTop);
        closeButton.Visible = true;
        closeTimer.Interval = 30000;
        closeTimer.Start();
        PositionAtBottomCenter();
    }

    /// <summary>現在の寸法でマウス位置の画面中央下から少し上へ配置する。</summary>
    private void PositionAtBottomCenter()
    {
        // 横方向を中央へ揃え、TypeWhisperの表示と重ならない下端余白を確保する。
        Rectangle workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        int horizontalPosition = workingArea.Left + ((workingArea.Width - Width) / 2);
        int verticalPosition = Math.Max(workingArea.Top, workingArea.Bottom - Height - BottomMargin);
        Location = new Point(horizontalPosition, verticalPosition);
    }

    /// <summary>成功または失敗の表示時間が過ぎたら画面を閉じる。</summary>
    private void HandleCloseTimer(object? sender, EventArgs eventArguments)
    {
        // タイマーを止めてから閉じ、重複イベントを防ぐ。
        closeTimer.Stop();
        Close();
    }

    /// <summary>閉じるボタンで応答画面を直ちに閉じる。</summary>
    private void HandleCloseButtonClick(object? sender, EventArgs eventArguments)
    {
        // 自動終了タイマーを止め、利用者の明示操作を優先する。
        closeTimer.Stop();
        Close();
    }

    /// <summary>状態表示に使うタイマーと画面資源を解放する。</summary>
    protected override void Dispose(bool disposing)
    {
        // WinFormsタイマーが画面破棄後に発火しないよう明示的に解放する。
        if (disposing)
        {
            closeTimer.Stop();
            closeTimer.Dispose();
            statusLabel.Dispose();
            detailTextBox.Dispose();
            progressBar.Dispose();
            closeButton.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>表示時に現在のアプリからキーボードフォーカスを奪わない。</summary>
    protected override bool ShowWithoutActivation
    {
        get
        {
            // 音声入力前に選択していた入力先をそのまま維持する。
            return true;
        }
    }

    /// <summary>非アクティブなツールウィンドウとしてWindowsスタイルを返す。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            // Alt+Tabへ出さず、表示によるアクティブ化も禁止する。
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= NoActivateExtendedStyle | ToolWindowExtendedStyle;
            return parameters;
        }
    }
}
