using System.Diagnostics;
using System.Drawing.Drawing2D;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>A cancellable export session. The caller owns snapshotting and atomic publication.</summary>
public sealed class ExportForm : Form
{
    private static readonly Color Ink = Color.FromArgb(35, 66, 54);
    private static readonly Color Muted = Color.FromArgb(112, 134, 119);
    private static readonly Color Accent = Color.FromArgb(40, 123, 100);
    private static readonly Color Paper = Color.FromArgb(248, 250, 246);
    private readonly Func<ExportFormat, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> export;
    private readonly Action<ExportFormat> rememberChoice;
    private readonly int stepCount;
    private readonly long mediaBytes;
    private readonly Dictionary<ExportFormat, FormatCard> cards = [];
    private readonly Label detail = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.TopLeft };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Ink, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label resultNote = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
    private readonly Label preferenceNote = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(157, 103, 65), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressTrack progress = new() { Dock = DockStyle.Fill };
    private readonly Button start = CreateButton("开始导出", true);
    private readonly Button cancel = CreateButton("关闭", false);
    private readonly Button openFile = CreateButton("打开文件", false);
    private readonly Button openDirectory = CreateButton("打开文件夹", false);
    private readonly ToolTip hints = new() { AutoPopDelay = 15000 };
    private readonly List<Font> fonts = [];
    private ExportFormat selectedFormat;
    private ExportResult? lastResult;
    private CancellationTokenSource? cancellation;
    private bool running, closeWhenFinished;
    private int progressValue;

    public ExportForm(int stepCount, long mediaBytes, ExportFormat initialFormat,
        Func<ExportFormat, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> export,
        Action<ExportFormat> rememberChoice)
    {
        this.stepCount = Math.Max(0, stepCount); this.mediaBytes = Math.Max(0, mediaBytes);
        this.export = export; this.rememberChoice = rememberChoice;
        selectedFormat = Enum.IsDefined(initialFormat) ? initialFormat : ExportFormat.Word;
        Text = "步录 · 导出文档"; Icon = Program.AppIcon; StartPosition = FormStartPosition.CenterParent;
        BackColor = Paper; ForeColor = Ink; Font = MakeFont(9);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(792, 666); MinimumSize = new Size(640, 620);
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Paper };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 666, ColumnCount = 1, RowCount = 9, Padding = new Padding(30, 24, 30, 22),
            BackColor = Paper
        };
        foreach (var height in new[] { 20f, 64f, 32f, 196f, 76f, 30f, 8f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        viewport.Controls.Add(layout); Controls.Add(viewport);
        viewport.Resize += (_, _) => layout.Height = Math.Max((int)(630 * DeviceDpi / 96d), viewport.ClientSize.Height);
        var eyebrow = new Label { Text = "SHARE YOUR PROCESS", Dock = DockStyle.Fill, ForeColor = Accent, Font = MakeFont(8, FontStyle.Bold) };
        var title = new Label { Text = "导出文档", Dock = DockStyle.Fill, ForeColor = Ink, Font = MakeFont(20, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var meta = new Label { Text = $"{this.stepCount} 个步骤  ·  素材 {FormatBytes(this.mediaBytes)}", Dock = DockStyle.Fill, ForeColor = Muted, Font = MakeFont(9) };
        layout.Controls.Add(eyebrow, 0, 0); layout.Controls.Add(title, 0, 1); layout.Controls.Add(meta, 0, 2);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddCard(grid, ExportFormat.Word, "W", "Word 文档", ".docx  ·  可继续编辑", 0, 0);
        AddCard(grid, ExportFormat.Html, "</>", "单文件 HTML", ".html  ·  动图、视频可播放", 1, 0);
        AddCard(grid, ExportFormat.LongImage, "PNG", "长图", ".png  ·  直接发送与查看", 0, 1);
        AddCard(grid, ExportFormat.Pdf, "PDF", "PDF 文档", ".pdf  ·  固定排版与打印", 1, 1);
        layout.Controls.Add(grid, 0, 3);
        detail.Margin = new Padding(1, 11, 1, 0); detail.Font = MakeFont(9); layout.Controls.Add(detail, 0, 4);
        status.Font = MakeFont(10, FontStyle.Bold); status.Margin = new Padding(0, 0, 0, 3); layout.Controls.Add(status, 0, 5);
        progress.Margin = new Padding(0, 1, 0, 1); layout.Controls.Add(progress, 0, 6);
        var notes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        notes.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); notes.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        resultNote.Font = MakeFont(8.5f); preferenceNote.Font = MakeFont(8);
        notes.Controls.Add(resultNote, 0, 0); notes.Controls.Add(preferenceNote, 0, 1); layout.Controls.Add(notes, 0, 7);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        footer.Controls.Add(openFile, 0, 0); footer.Controls.Add(openDirectory, 1, 0); footer.Controls.Add(cancel, 3, 0); footer.Controls.Add(start, 4, 0);
        layout.Controls.Add(footer, 0, 8);
        openFile.Visible = openDirectory.Visible = false;
        start.Click += async (_, _) => await StartExportAsync();
        cancel.Click += (_, _) => { if (running) RequestCancellation(); else Close(); };
        openFile.Click += (_, _) => OpenResult(false); openDirectory.Click += (_, _) => OpenResult(true);
        AcceptButton = start; CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true; closeWhenFinished = true; RequestCancellation();
        };
        Load += (_, _) =>
        {
            var work = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width - 24), Math.Min(MinimumSize.Height, work.Height - 32));
            Size = new Size(Math.Min(Width, work.Width - 32), Math.Min(Height, work.Height - 48));
            CenterToScreen();
        };
        SelectFormat(selectedFormat, false);
    }

    private Font MakeFont(float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); fonts.Add(font); return font;
    }

    private void AddCard(TableLayoutPanel grid, ExportFormat format, string symbol, string title, string caption, int column, int row)
    {
        var card = new FormatCard(symbol, title, caption)
        {
            Dock = DockStyle.Fill, Margin = new Padding(column == 0 ? 0 : 7, 0, column == 0 ? 7 : 0, 12),
            AccessibleName = title, AccessibleDescription = caption, TabIndex = cards.Count,
            BackColor = Paper, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Cursor = Cursors.Hand
        };
        card.FlatAppearance.BorderSize = 0;
        card.Click += (_, _) => SelectFormat(format, true);
        cards.Add(format, card); grid.Controls.Add(card, column, row);
    }

    private void SelectFormat(ExportFormat format, bool remember)
    {
        if (running) return;
        if(selectedFormat!=format)
        {
            lastResult=null;openFile.Visible=openDirectory.Visible=false;
            progress.Value=0;status.ForeColor=Ink;
        }
        selectedFormat = format;
        foreach (var item in cards) { item.Value.Selected = item.Key == format; item.Value.Invalidate(); }
        var estimated = EstimateHtmlBytes(mediaBytes);
        detail.Text = format switch
        {
            ExportFormat.Html => $"图片、动图、视频全部嵌入一个 HTML，离线打开即可播放。预计约 {FormatBytes(estimated)}。\n" + (mediaBytes >= 75L * 1024 * 1024 ? "视频较大，文件载入和分享可能较慢；导出时逐段写入以控制内存。" : "素材按需显示，视频不会自动播放；无需附带素材文件夹。"),
            ExportFormat.LongImage => "按文档顺序生成清晰长图；内容过长时自动分为多张，保存在同一文件夹。\n动图和视频使用第一帧，图片保留标注。",
            ExportFormat.Pdf => "分页文档、统一页边距，适合打印、归档与跨设备分享。\n动图和视频使用第一帧，正文可搜索。",
            _ => "标准 Word 文档，标题与说明可继续编辑，图片嵌入文件。\n动图和视频使用第一帧，适合补充说明和正式交付。"
        };
        if (lastResult == null)
        {
            status.Text = "准备导出";
            resultNote.Text = "保存至项目的 exports 文件夹，每次生成独立副本。";
        }
        start.Text = lastResult == null ? "开始导出" : "再次导出";
        if (remember)
        {
            try { rememberChoice(format); preferenceNote.Text = ""; }
            catch (Exception ex) { preferenceNote.Text = "导出格式偏好未保存，不影响本次导出。"; hints.SetToolTip(preferenceNote, ex.Message); }
        }
    }

    private async Task StartExportAsync()
    {
        if (running) return;
        running = true; closeWhenFinished = false; progressValue = 0; progress.Value = 0;
        cancellation = new CancellationTokenSource();
        var session = cancellation;
        var requestedFormat = selectedFormat;
        lastResult = null; openFile.Visible = openDirectory.Visible = false;
        foreach (var card in cards.Values) card.Enabled = false;
        start.Enabled = false; start.Text = "正在导出…"; cancel.Text = "取消导出"; cancel.Enabled = true;
        status.ForeColor = Ink; status.Text = "正在整理文档…";
        resultNote.Text = "完成后可直接打开文件。关闭此窗口会先取消正在进行的导出。";
        try
        {
            var reporter = new Progress<ExportProgress>(update =>
            {
                if (IsDisposed || !running || !ReferenceEquals(cancellation, session) || session.IsCancellationRequested) return;
                progressValue = Math.Max(progressValue, Math.Clamp(update.Percent, 0, 100));
                progress.Value = progressValue; status.Text = $"{progressValue}%  {update.Message}";
            });
            // Some exporters use WebView2 on this UI thread; the delegate controls its own workers.
            lastResult = await export(requestedFormat, reporter, session.Token);
            if (IsDisposed) return;
            progress.Value = 100; status.Text = "导出完成"; status.ForeColor = Accent;
            resultNote.Text = lastResult.Summary + "\n" + lastResult.Directory;
            hints.SetToolTip(resultNote, resultNote.Text);
            openDirectory.Visible = true; openFile.Visible = lastResult.Files.Count == 1;
            if (lastResult.Files.Count > 1) openDirectory.Text = $"查看 {lastResult.Files.Count} 个文件";
            else openDirectory.Text = "打开文件夹";
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            status.Text = "已取消"; progress.Value = 0;
            resultNote.Text = "未发布不完整的文件，可选择其他格式后重新导出。";
        }
        catch (Exception ex)
        {
            status.Text = "导出未完成"; status.ForeColor = Color.FromArgb(163, 76, 50);
            resultNote.Text = ex.Message; hints.SetToolTip(resultNote, ex.Message);
        }
        finally
        {
            running = false; cancellation = null; session.Dispose();
            if (!IsDisposed)
            {
                foreach (var card in cards.Values) card.Enabled = true;
                start.Enabled = true; start.Text = lastResult == null ? "重新导出" : "再次导出";
                cancel.Text = "关闭"; cancel.Enabled = true;
                if (closeWhenFinished) Close();
            }
        }
    }

    private void RequestCancellation()
    {
        if (!running || cancellation == null || cancellation.IsCancellationRequested) return;
        cancel.Enabled = false; status.Text = "正在取消，请稍候…";
        cancellation.Cancel();
    }

    private void OpenResult(bool directory)
    {
        if (lastResult == null) return;
        try
        {
            var path = directory || lastResult.Files.Count != 1 ? lastResult.Directory : lastResult.Files[0];
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("文件已被移动或删除。请在项目的 exports 文件夹中查找。", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开导出文件", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    internal static long EstimateHtmlBytes(long bytes) => bytes > (long.MaxValue - 256 * 1024) / 4 * 3 ? long.MaxValue : ((Math.Max(0, bytes) + 2) / 3) * 4 + 256 * 1024;
    internal static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GB" : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.FromArgb(237, 243, 238), ForeColor = primary ? Color.White : Ink,
            Margin = new Padding(0, 4, 8, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Color.FromArgb(219, 230, 221);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(30, 104, 84) : Color.FromArgb(225, 236, 227);
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cancellation?.Cancel(); hints.Dispose();
            foreach (var font in fonts) font.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class FormatCard(string symbol, string title, string caption) : Button
    {
        public bool Selected { get; set; }
        private bool hovered;
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Paper); g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            using var outline = Rounded(new RectangleF(scale, scale, Width - 2 * scale, Height - 2 * scale), 13 * scale);
            using var fill = new SolidBrush(Selected ? Color.FromArgb(232, 243, 235) : hovered && Enabled ? Color.FromArgb(242, 246, 240) : Color.FromArgb(253, 254, 251));
            using var border = new Pen(Selected ? Accent : Color.FromArgb(219, 229, 216), Selected ? 1.7f * scale : scale);
            g.FillPath(fill, outline); g.DrawPath(border, outline);
            var icon = new Rectangle((int)(18 * scale), (Height - (int)(43 * scale)) / 2, (int)(43 * scale), (int)(43 * scale));
            using var iconPath = Rounded(icon, 10 * scale); using var iconBrush = new SolidBrush(Selected ? Accent : Color.FromArgb(228, 237, 223));
            g.FillPath(iconBrush, iconPath);
            using var symbolFont = new Font("Segoe UI", symbol.Length == 1 ? 20 : 9.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, symbol, symbolFont, icon, Selected ? Color.White : Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var titleFont = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
            using var captionFont = new Font("Microsoft YaHei UI", 8.5f);
            var left = (int)(77 * scale); var top = (Height - (int)(46 * scale)) / 2;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(left, top, Width - left - (int)(25 * scale), (int)(25 * scale)), Enabled ? Ink : Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, caption, captionFont, new Rectangle(left, top + (int)(29 * scale), Width - left - (int)(17 * scale), (int)(20 * scale)), Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Selected)
            {
                using var check = new Pen(Accent, 1.8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var x = Width - 21 * scale; var y = 17 * scale;
                g.DrawLines(check, [new PointF(x - 4 * scale, y), new PointF(x - scale, y + 3 * scale), new PointF(x + 5 * scale, y - 4 * scale)]);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -(int)(6 * scale), -(int)(6 * scale)), Accent, Paper);
        }
    }

    private sealed class ProgressTrack : Control
    {
        private int value;
        public int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = Rounded(new RectangleF(0, 0, Width, Height), Height / 2f);
            using var background = new SolidBrush(Color.FromArgb(227, 235, 222)); e.Graphics.FillPath(background, track);
            if (value <= 0) return;
            using var bar = Rounded(new RectangleF(0, 0, Math.Max(Height, Width * value / 100f), Height), Height / 2f);
            using var foreground = new SolidBrush(Accent); e.Graphics.FillPath(foreground, bar);
        }
    }

    private static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
}
