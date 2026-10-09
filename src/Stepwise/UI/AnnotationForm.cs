using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>Owns only an in-memory capture until the caller commits a confirmed result.</summary>
public sealed class AnnotationResult(Bitmap image, string layersJson) : IDisposable
{
    public Bitmap Image { get; } = image;
    public string LayersJson { get; } = layersJson;
    public void Dispose() => Image.Dispose();
}

public sealed class AnnotationForm : Form
{
    private const string AppOrigin = "https://app.stepwise.local/";
    private const string ImageUrl = "https://capture.stepwise.local/image.png";
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(26, 32, 40) };
    private readonly string root;
    private readonly byte[] sourcePng;
    private readonly Size sourceSize;
    private readonly Func<AnnotationResult, Task> commit;
    private readonly List<MemoryStream> responseStreams = [];
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool saving, accepted, cancelPending, closingPreferencesFlushed, closingPreferencesFlushing;

    private AnnotationForm(Bitmap source, string projectRoot, Func<AnnotationResult, Task> onConfirm)
    {
        root = projectRoot; sourceSize = source.Size; commit = onConfirm;
        using var encoded = new MemoryStream(); source.Save(encoded, ImageFormat.Png); sourcePng = encoded.ToArray();
        Text = "步录 · 截图标注"; Icon = Program.AppIcon;
        TopMost = true; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None; BackColor = web.DefaultBackgroundColor;
        Controls.Add(web);
        Load += (_, _) =>
        {
            var work = Screen.FromControl(this).WorkingArea;
            var scale = DeviceDpi / 96d;
            ClientSize = new Size(Math.Min((int)(1240 * scale), work.Width - 40), Math.Min((int)(850 * scale), work.Height - 85));
            MinimumSize = new Size(Math.Min((int)(740 * scale), work.Width - 20), Math.Min((int)(500 * scale), work.Height - 40));
            CenterToScreen();
        };
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += async (_, e) =>
        {
            if (saving) { cancelPending = true; e.Cancel = true; return; }
            if (closingPreferencesFlushing) { e.Cancel = true; return; }
            if (closingPreferencesFlushed || web.CoreWebView2 == null) return;
            e.Cancel = true;
            closingPreferencesFlushing = true;
            try
            {
                // Native X / Alt+F4 can precede the color/zoom debounce timer.
                var pending = await web.ExecuteScriptAsync("window.stepwiseAnnotation?.takePendingPreferences?.() ?? null");
                using var parsed = JsonDocument.Parse(pending);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object) SavePreferences(parsed.RootElement);
            }
            catch (Exception ex) { LogPreferenceError(ex); }
            finally
            {
                closingPreferencesFlushing = false; closingPreferencesFlushed = true;
                if (!IsDisposed) BeginInvoke((Action)Close);
            }
        };
        FormClosed += (_, _) => completion.TrySetResult(accepted);
    }

    public static async Task<bool> EditAsync(Bitmap source, string projectRoot, Func<AnnotationResult, Task> onConfirm, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var form = new AnnotationForm(source, projectRoot, onConfirm);
        form.Show(); form.Activate();
        using var cancellation = token.Register(() =>
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            try { form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); })); }
            catch (InvalidOperationException) { }
        });
        return await form.completion.Task;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, ".cache", "webview"));
            if (IsDisposed) return;
            await web.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("app.stepwise.local", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (e.Uri != AppOrigin + "annotation.html") e.Cancel = true; };
            core.AddWebResourceRequestedFilter(ImageUrl, CoreWebView2WebResourceContext.Image);
            core.WebResourceRequested += (_, e) =>
            {
                if (e.Request.Uri != ImageUrl) return;
                var stream = new MemoryStream(sourcePng, false); responseStreams.Add(stream);
                e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: image/png\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://app.stepwise.local\r\n");
            };
            core.WebMessageReceived += async (_, e) =>
            {
                if (e.Source != AppOrigin + "annotation.html") return;
                try
                {
                    using var parsed = JsonDocument.Parse(e.WebMessageAsJson);
                    var message = parsed.RootElement;
                    switch (message.GetProperty("type").GetString())
                    {
                        case "ready":
                            await SendAsync(new { type = "init", imageUrl = ImageUrl, width = sourceSize.Width, height = sourceSize.Height, preferences = PreferencesStore.Default.Read().Annotation });
                            break;
                        case "annotationPreferences":
                            try { SavePreferences(message.GetProperty("preferences")); }
                            catch (Exception ex)
                            {
                                LogPreferenceError(ex);
                                await SendAsync(new { type = "preferencesError", message = "偏好未能保存，下次启动可能恢复上次设置。" });
                            }
                            break;
                        case "cancel": if (!saving) Close(); break;
                        case "confirm":
                            if (saving) return;
                            saving = true;
                            try
                            {
                                using var result = DecodeResult(message);
                                await commit(result);
                                accepted = true; saving = false; Close();
                            }
                            catch (Exception ex)
                            {
                                saving = false;
                                await SendAsync(new { type = "error", message = "保存未完成，标注仍在，请重试：" + ex.Message });
                                if (cancelPending) Close();
                            }
                            break;
                    }
                }
                catch (Exception ex) { await SendAsync(new { type = "error", message = ex.Message }); }
            };
            core.ProcessFailed += (_, _) =>
            {
                if (IsDisposed) return;
                MessageBox.Show(this, "标注页面意外关闭，本次截图尚未保存。请重新截图。", "步录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!saving) Close();
            };
            web.Source = new Uri(AppOrigin + "annotation.html");
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            MessageBox.Show(this, "无法打开截图标注。请检查 WebView2 Runtime 后重试。\n\n" + ex.Message, "步录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private AnnotationResult DecodeResult(JsonElement message)
    {
        var text = message.GetProperty("pngBase64").GetString() ?? throw new InvalidDataException("图片数据为空。");
        const string prefix = "data:image/png;base64,";
        if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
        // The local page is trusted, but malformed messages must not commit a different capture.
        var maximumBytes = Math.Max(8_000_000L, (long)sourceSize.Width * sourceSize.Height * 6 + 1_000_000);
        if (text.Length > maximumBytes * 4 / 3 + 4) throw new InvalidDataException("截图数据超出预期大小。");
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("截图必须为 PNG。");
        using var stream = new MemoryStream(bytes, false);
        using var decoded = new Bitmap(stream);
        if (decoded.Size != sourceSize) throw new InvalidDataException("标注截图的尺寸与原图不一致。");
        var layers = message.GetProperty("layers");
        if (layers.ValueKind != JsonValueKind.Object) throw new InvalidDataException("笔迹数据无效。");
        return new AnnotationResult(decoded.Clone(new Rectangle(Point.Empty, sourceSize), PixelFormat.Format32bppPArgb), layers.GetRawText());
    }

    private async Task SendAsync(object message)
    {
        if (IsDisposed || web.CoreWebView2 == null) return;
        await web.ExecuteScriptAsync("window.stepwiseAnnotation.receive(" + JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + ")");
    }

    private static void SavePreferences(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) return;
        PreferencesStore.Default.Remember(preferences =>
        {
            var annotation = preferences.Annotation;
            if (patch.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String) annotation.Tool = tool.GetString()!;
            if (patch.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String) annotation.Color = color.GetString()!;
            if (patch.TryGetProperty("lineWidth", out var lineWidth) && lineWidth.TryGetInt32(out var width)) annotation.LineWidth = width;
            if (patch.TryGetProperty("fontSize", out var fontSize) && fontSize.TryGetInt32(out var size)) annotation.FontSize = size;
            if (patch.TryGetProperty("zoomMode", out var zoomMode) && zoomMode.ValueKind == JsonValueKind.String) annotation.ZoomMode = zoomMode.GetString()!;
            if (patch.TryGetProperty("zoomScale", out var zoomScale) && zoomScale.TryGetDouble(out var scale)) annotation.ZoomScale = scale;
        });
    }

    private void LogPreferenceError(Exception error)
    {
        try { File.AppendAllText(Path.Combine(root, "logs", "app.log"), $"{DateTimeOffset.Now:O} 偏好保存失败：{error.Message}\n"); }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            web.Dispose();
            foreach (var stream in responseStreams) stream.Dispose();
            responseStreams.Clear();
        }
        base.Dispose(disposing);
    }
}
