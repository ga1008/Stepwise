using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Stepwise.UI;

public sealed class CaptureResult : IDisposable
{
    public Bitmap Image { get; }
    /// <summary>Selection in virtual-desktop physical pixels; coordinates may be negative.</summary>
    public Rectangle Bounds { get; }

    internal CaptureResult(Bitmap image, Rectangle bounds)
    {
        Image = image;
        Bounds = bounds;
    }

    public void Dispose() => Image.Dispose();
}

/// <summary>A single frozen virtual desktop. The caller hides its own windows before opening it.</summary>
public sealed class CaptureOverlay : Form
{
    private readonly Bitmap _desktop;
    private readonly Rectangle _desktopBounds;
    private readonly TaskCompletionSource<CaptureResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Font _hintFont = new("Microsoft YaHei UI", 10f);
    private readonly Pen _outline = new(Color.FromArgb(111, 229, 195), 2f);
    private readonly Brush _shade = new SolidBrush(Color.FromArgb(135, 8, 13, 22));
    private Point _anchor;
    private Rectangle _selection;
    private bool _dragging;
    private bool _finished;

    private CaptureOverlay(Bitmap desktop, Rectangle desktopBounds)
    {
        _desktop = desktop;
        _desktopBounds = desktopBounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = desktopBounds;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        AccessibleName = "截图选区";
        Text = "步录 · 选择区域";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public static async Task<CaptureResult?> SelectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = NativeMethods.VirtualScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("无法读取桌面尺寸。");

        NativeMethods.DwmFlush();
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        CaptureOverlay? overlay = null;
        try
        {
            using (var graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            overlay = new CaptureOverlay(image, bounds);
            overlay.Show();
            // Selection is an explicit user action and needs keyboard input for Escape.
            overlay.Activate();
            using var cancellation = cancellationToken.Register(() =>
            {
                if (overlay.IsDisposed || !overlay.IsHandleCreated) return;
                try
                {
                    if (overlay.InvokeRequired) overlay.BeginInvoke((Action)(() => overlay.Finish(null)));
                    else overlay.Finish(null);
                }
                catch (InvalidOperationException) { /* The overlay completed while cancellation was dispatched. */ }
            });
            return await overlay._completion.Task;
        }
        catch
        {
            if (overlay is not null) overlay.Dispose();
            else image.Dispose();
            throw;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_desktop, 0, 0);
        e.Graphics.FillRectangle(_shade, ClientRectangle);
        if (_dragging && _selection.Width > 0 && _selection.Height > 0)
        {
            e.Graphics.DrawImage(_desktop, _selection, _selection, GraphicsUnit.Pixel);
            e.Graphics.DrawRectangle(_outline, _selection.X, _selection.Y,
                Math.Max(0, _selection.Width - 1), Math.Max(0, _selection.Height - 1));
            DrawHint(e.Graphics, $"{_selection.Width} × {_selection.Height}",
                new Point(_selection.Left, Math.Max(4, _selection.Top - 36)));
        }
        else
        {
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            DrawHint(e.Graphics, "拖动框选 · 单击全屏 · Esc 取消",
                new Point(screen.Left - _desktopBounds.Left + (screen.Width - 302) / 2,
                    screen.Top - _desktopBounds.Top + 24));
        }
    }

    private void DrawHint(Graphics graphics, string text, Point origin)
    {
        var measured = TextRenderer.MeasureText(text, _hintFont);
        var size = new Size(measured.Width + 24, measured.Height + 16);
        var box = new Rectangle(Math.Clamp(origin.X, 0, Math.Max(0, Width - size.Width)),
            Math.Clamp(origin.Y, 0, Math.Max(0, Height - size.Height)), size.Width, size.Height);
        using var fill = new SolidBrush(Color.FromArgb(239, 19, 28, 42));
        using var path = RoundedRectangle(box, 8);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(fill, path);
        TextRenderer.DrawText(graphics, text, _hintFont, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { Finish(null); return; }
        if (e.Button != MouseButtons.Left) return;
        _anchor = ClampPoint(e.Location);
        _selection = new Rectangle(_anchor, Size.Empty);
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var point = ClampPoint(e.Location);
        _selection = Rectangle.FromLTRB(Math.Min(_anchor.X, point.X), Math.Min(_anchor.Y, point.Y),
            Math.Max(_anchor.X, point.X), Math.Max(_anchor.Y, point.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging || _finished) return;
        var point = ClampPoint(e.Location);
        _selection = Rectangle.FromLTRB(Math.Min(_anchor.X, point.X), Math.Min(_anchor.Y, point.Y),
            Math.Max(_anchor.X, point.X), Math.Max(_anchor.Y, point.Y));
        _dragging = false;
        Capture = false;
        // A click (including harmless hand jitter) selects the complete virtual desktop.
        var selected = _selection.Width < 5 && _selection.Height < 5 ? ClientRectangle : _selection;
        if (selected.Width < 2 || selected.Height < 2) { Invalidate(); return; }
        selected.Intersect(new Rectangle(Point.Empty, _desktop.Size));
        Bitmap? crop = null;
        try
        {
            crop = _desktop.Clone(selected, PixelFormat.Format32bppPArgb);
            var absolute = new Rectangle(selected.X + _desktopBounds.X, selected.Y + _desktopBounds.Y,
                selected.Width, selected.Height);
            Finish(new CaptureResult(crop, absolute));
        }
        catch (Exception exception)
        {
            crop?.Dispose();
            _completion.TrySetException(exception);
            _finished = true;
            Close();
        }
    }

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, _desktop.Width), Math.Clamp(point.Y, 0, _desktop.Height));

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Finish(null); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Finish(CaptureResult? result)
    {
        if (_finished) { result?.Dispose(); return; }
        _finished = true;
        Hide();
        Close();
        NativeMethods.DwmFlush();
        _completion.TrySetResult(result);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (!_finished) { _finished = true; _completion.TrySetResult(null); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _desktop.Dispose();
            _hintFont.Dispose();
            _outline.Dispose();
            _shade.Dispose();
        }
        base.Dispose(disposing);
    }
}
