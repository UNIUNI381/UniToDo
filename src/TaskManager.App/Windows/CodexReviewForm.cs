#if WINDOWS
using System.Drawing;
using TaskManager.Domain;
using TaskManager.Services;

namespace TaskManager.Windows;

/// <summary>校正済み音声入力を送信前に編集するネイティブ画面を提供する。</summary>
public sealed class CodexReviewForm : Form
{
    // 表示中項目、操作処理、入力欄、状態表示を保持する。
    private readonly CodexReviewItem reviewItem;
    private readonly Func<CodexReviewItem, string, Task<string?>> sendAction;
    private readonly Action<CodexReviewItem> discardAction;
    private readonly TextBox textEditor;
    private readonly Label statusLabel;
    private readonly Button sendButton;
    private readonly Button discardButton;
    private bool operationCompleted;
    private bool operationRunning;

    /// <summary>本文、送信先、待ち件数、操作処理から確認画面を初期化する。</summary>
    public CodexReviewForm(
        CodexReviewItem item,
        string threadIdentifier,
        int waitingCount,
        Rectangle workingArea,
        Func<CodexReviewItem, string, Task<string?>> sendReviewAction,
        Action<CodexReviewItem> discardReviewAction)
    {
        // 画面の基本動作と編集しやすい寸法を設定する。
        reviewItem = item;
        sendAction = sendReviewAction;
        discardAction = discardReviewAction;
        Text = $"{TaskConstants.ApplicationDisplayName} - 音声入力を確認";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(640, 420);
        Size = new Size(760, 520);
        PositionWithinWorkingArea(workingArea);
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        Font = new Font("Yu Gothic UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        // 本文編集、送信先、待ち件数を縦方向へ配置する。
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label headingLabel = new()
        {
            AutoSize = true,
            Text = "校正結果を確認・編集してください",
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        Label targetLabel = new()
        {
            AutoSize = true,
            Text = $"送信先: {threadIdentifier}　待ち: {waitingCount}件",
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 10)
        };
        textEditor = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Vertical,
            Text = item.Text,
            MaxLength = CodexReviewService.MaximumTextLength,
            Font = new Font("Yu Gothic UI", 11F, FontStyle.Regular, GraphicsUnit.Point)
        };
        Label shortcutLabel = new()
        {
            AutoSize = true,
            Text = "Ctrl+Enter: 送信　Enter: 改行　Esc: 破棄",
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0)
        };
        statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 6, 0, 0)
        };
        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        sendButton = new Button
        {
            AutoSize = true,
            Text = "送信",
            Padding = new Padding(16, 6, 16, 6)
        };
        discardButton = new Button
        {
            AutoSize = true,
            Text = "破棄",
            Padding = new Padding(16, 6, 16, 6)
        };
        sendButton.Click += HandleSendClick;
        discardButton.Click += HandleDiscardClick;
        actions.Controls.Add(sendButton);
        actions.Controls.Add(discardButton);
        layout.Controls.Add(headingLabel, 0, 0);
        layout.Controls.Add(targetLabel, 0, 1);
        layout.Controls.Add(textEditor, 0, 2);
        layout.Controls.Add(shortcutLabel, 0, 3);
        layout.Controls.Add(statusLabel, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        Controls.Add(layout);

        Shown += HandleShown;
        FormClosing += HandleFormClosing;
    }

    /// <summary>確認画面を指定ディスプレイの作業領域中央へ完全に収める。</summary>
    private void PositionWithinWorkingArea(Rectangle workingArea)
    {
        // 画面寸法とフォーム寸法の差を半分ずつ配分し、負数は作業領域の端へ丸める。
        int horizontalPosition = workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2);
        int verticalPosition = workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2);
        Location = new Point(horizontalPosition, verticalPosition);
    }

    /// <summary>キーボードショートカットを送信または破棄へ割り当てる。</summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        // Ctrl+Enterだけを送信とし、通常のEnterはテキスト欄の改行へ渡す。
        if (keyData == (Keys.Control | Keys.Enter))
        {
            _ = SendAsync();
            return true;
        }
        if (keyData == Keys.Escape)
        {
            Discard();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>表示直後に最前面化して本文入力欄へフォーカスする。</summary>
    private void HandleShown(object? sender, EventArgs eventArguments)
    {
        // 所有元の進捗画面より前へ出し、利用者の操作対象として明示的にアクティブ化する。
        Activate();
        BringToFront();
        textEditor.Focus();
        textEditor.SelectionStart = textEditor.TextLength;
    }

    /// <summary>送信ボタンのクリックを非同期送信へ渡す。</summary>
    private async void HandleSendClick(object? sender, EventArgs eventArguments)
    {
        // UIスレッドを止めずに送信受付を実行する。
        await SendAsync();
    }

    /// <summary>編集済み本文を送信キューへ追加して成功時に画面を閉じる。</summary>
    private async Task SendAsync()
    {
        // 二重送信を防ぎ、即時検証エラーは画面内へ表示する。
        if (operationRunning || operationCompleted)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(textEditor.Text))
        {
            statusLabel.Text = "送信する本文を入力してください。";
            return;
        }
        operationRunning = true;
        sendButton.Enabled = false;
        discardButton.Enabled = false;
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Text = "送信を受け付けています…";
        try
        {
            string? errorMessage = await sendAction(reviewItem, textEditor.Text);
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = errorMessage;
                return;
            }
            operationCompleted = true;
            Close();
        }
        catch (Exception sendError)
        {
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = sendError.Message;
        }
        finally
        {
            operationRunning = false;
            if (!operationCompleted)
            {
                sendButton.Enabled = true;
                discardButton.Enabled = true;
            }
        }
    }

    /// <summary>破棄ボタンのクリックを現在項目の破棄へ渡す。</summary>
    private void HandleDiscardClick(object? sender, EventArgs eventArguments)
    {
        // 閉じる操作と同じ明示破棄を実行する。
        Discard();
    }

    /// <summary>現在の本文を破棄済みにして画面を閉じる。</summary>
    private void Discard()
    {
        // 送信受付中の破棄と二重完了を防ぐ。
        if (operationRunning || operationCompleted)
        {
            return;
        }
        operationCompleted = true;
        discardAction(reviewItem);
        Close();
    }

    /// <summary>ウィンドウの閉じる操作を明示破棄として処理する。</summary>
    private void HandleFormClosing(object? sender, FormClosingEventArgs eventArguments)
    {
        // Windows終了以外の利用者操作では未送信本文を破棄する。
        if (!operationCompleted && eventArguments.CloseReason == CloseReason.UserClosing)
        {
            if (operationRunning)
            {
                eventArguments.Cancel = true;
                return;
            }
            operationCompleted = true;
            discardAction(reviewItem);
        }
    }
}
#endif
