using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>Edits an isolated draft. The owner applies hotkeys and disk changes as one transaction.</summary>
public sealed class SettingsForm : Form
{
    private const string PageUrl = "https://app.stepwise.local/settings.html";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(244, 245, 239) };
    private readonly string root;
    private readonly Func<HotkeyPreferences, IReadOnlyDictionary<string, string>> validateHotkeys;
    private readonly Action<UserPreferences> apply;
    private bool ready, dirty, saving, accepted;

    public SettingsForm(string projectRoot, Func<HotkeyPreferences, IReadOnlyDictionary<string, string>> validateHotkeys, Action<UserPreferences> apply)
    {
        root = projectRoot; this.validateHotkeys = validateHotkeys; this.apply = apply;
        Text = "步录 · 设置"; Icon = Program.AppIcon; TopMost = true;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen; BackColor = web.DefaultBackgroundColor;
        Controls.Add(web);
        Load += (_, _) =>
        {
            var work = Screen.FromControl(this).WorkingArea; var scale = DeviceDpi / 96d;
            ClientSize = new Size(Math.Min((int)(980 * scale), work.Width - 48), Math.Min((int)(760 * scale), work.Height - 100));
            MinimumSize = new Size(Math.Min((int)(660 * scale), work.Width - 24), Math.Min((int)(480 * scale), work.Height - 48));
            CenterToScreen();
        };
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (saving) { e.Cancel = true; return; }
            if (!accepted && dirty && MessageBox.Show(this, "放弃尚未保存的设置？", "步录", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                e.Cancel = true;
        };
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
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (e.Uri != PageUrl) e.Cancel = true; };
            core.WebMessageReceived += async (_, e) =>
            {
                if (e.Source != PageUrl || IsDisposed) return;
                try
                {
                    using var document = JsonDocument.Parse(e.WebMessageAsJson);
                    await HandleMessageAsync(document.RootElement);
                }
                catch (Exception ex)
                {
                    saving = false;
                    await SendAsync(new { type = "error", message = ex.Message });
                }
            };
            core.ProcessFailed += (_, _) =>
            {
                if (IsDisposed) return;
                MessageBox.Show(this, "设置页面意外关闭。已保存的配置不受影响，请重新打开设置。", "步录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                dirty = false; saving = false; Close();
            };
            web.Source = new Uri(PageUrl);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            MessageBox.Show(this, "无法打开设置，请检查 WebView2 Runtime 后重试。\n\n" + ex.Message, "步录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private async Task HandleMessageAsync(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "ready":
                ready = true;
                await SendAsync(new { type = "init", preferences = PreferencesStore.Default.Read(), defaults = new UserPreferences() });
                break;
            case "dirty": dirty = message.GetProperty("value").GetBoolean(); break;
            case "cancel": if (!saving) Close(); break;
            case "browseDirectory":
                if (saving) return;
                var current = message.TryGetProperty("path", out var path) ? path.GetString() : null;
                using (var picker = new FolderBrowserDialog { Description = "选择新项目的默认位置", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(current) ? current! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
                    if (picker.ShowDialog(this) == DialogResult.OK) await SendAsync(new { type = "directory", path = picker.SelectedPath });
                break;
            case "validateHotkeys":
                var hotkeys = message.GetProperty("hotkeys").Deserialize<HotkeyPreferences>(JsonOptions) ?? throw new InvalidDataException("快捷键配置为空。");
                await SendAsync(new { type = "hotkeyValidation", requestId = message.GetProperty("requestId").GetInt32(), errors = validateHotkeys(hotkeys) });
                break;
            case "save":
                if (saving) return;
                saving = true;
                var preferences = message.GetProperty("preferences").Deserialize<UserPreferences>(JsonOptions) ?? throw new InvalidDataException("设置为空。");
                ValidateDraft(preferences);
                var errors = validateHotkeys(preferences.Hotkeys);
                if (errors.Count > 0)
                {
                    saving = false;
                    await SendAsync(new { type = "saveRejected", errors, message = "快捷键存在冲突，请调整后保存。" });
                    return;
                }
                apply(preferences);
                accepted = true; dirty = false; saving = false; Close();
                break;
        }
    }

    private static void ValidateDraft(UserPreferences preferences)
    {
        if (preferences.Annotation == null || preferences.Hotkeys == null) throw new InvalidDataException("设置内容不完整，请恢复默认后重试。");
        var annotation = preferences.Annotation;
        if (!new[] { "select", "rect", "ellipse", "pen", "arrow", "text" }.Contains(annotation.Tool)) throw new InvalidDataException("请选择有效的标注工具。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(annotation.Color ?? "", "^#[0-9a-fA-F]{6}$")) throw new InvalidDataException("请选择有效颜色。");
        if (!new[] { 2, 3, 5, 8, 12, 18 }.Contains(annotation.LineWidth) || !new[] { 16, 20, 24, 28, 36, 48, 64, 96 }.Contains(annotation.FontSize)) throw new InvalidDataException("请选择有效的线条或字体大小。");
        if (annotation.ZoomMode is not ("fit" or "custom") || !double.IsFinite(annotation.ZoomScale) || annotation.ZoomScale < .01 || annotation.ZoomScale > 8) throw new InvalidDataException("缩放比例应为 1%–800%。");
        if (!new[] { 0, 3, 5, 10 }.Contains(preferences.ScreenshotDelay) || !new[] { 0, 3, 5, 10 }.Contains(preferences.RecordingDelay)) throw new InvalidDataException("请选择有效的等待时间。");
        if (preferences.ExportFormat is not ("mp4" or "gif")) throw new InvalidDataException("请选择 MP4 或 GIF。");
        if (preferences.Mp4FramesPerSecond is < 1 or > 60 || preferences.GifFramesPerSecond is < 1 or > 30) throw new InvalidDataException("MP4 帧率应为 1–60，GIF 帧率应为 1–30。");
        foreach (var width in new[] { preferences.Mp4MaxWidth, preferences.GifMaxWidth })
            if (width != 0 && width is < 160 or > 7680) throw new InvalidDataException("最大宽度应为 160–7680 像素，或 0（原始大小）。");
        if (!string.IsNullOrWhiteSpace(preferences.ProjectParentDirectory))
        {
            if (!Path.IsPathFullyQualified(preferences.ProjectParentDirectory) || !Directory.Exists(preferences.ProjectParentDirectory)) throw new InvalidDataException("默认项目位置不存在，请先创建该文件夹或重新选择。");
            preferences.ProjectParentDirectory = Path.GetFullPath(preferences.ProjectParentDirectory);
        }
    }

    private async Task SendAsync(object message)
    {
        if (!ready || IsDisposed || web.CoreWebView2 == null) return;
        try { await web.ExecuteScriptAsync("window.stepwiseSettings.receive(" + JsonSerializer.Serialize(message, JsonOptions) + ")"); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException ex) { System.Diagnostics.Debug.WriteLine("Settings webview: " + ex.Message); }
        catch (System.Runtime.InteropServices.COMException ex) { System.Diagnostics.Debug.WriteLine("Settings webview: " + ex.Message); }
    }

    /// <summary>Windows can consume a registered key before the focused web input sees it.</summary>
    public Task CaptureRegisteredHotkeyAsync(string chord) => SendAsync(new { type = "capturedHotkey", chord });

    protected override void Dispose(bool disposing) { if (disposing) web.Dispose(); base.Dispose(disposing); }
}
