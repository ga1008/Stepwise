using System.Drawing.Drawing2D;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>A nonactivating toolbar with separate delayed-capture menus and per-monitor edge docking.</summary>
public sealed class ToolbarForm : Form
{
    public event EventHandler<int>? ScreenshotRequested;
    public event EventHandler<int>? RecordRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? PreviewRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<string>? SuspendedHotkeyPressed;
    public event EventHandler? ExitRequested;

    public bool SystemAudio { get; private set; } = true;
    public bool Microphone { get; private set; } = true;
    public bool CaptureExclusionEnabled { get; private set; }
    public bool InspectionMode => _inspectionMode;
    public IReadOnlyList<string> HotkeyErrors => _hotkeyErrors;
    public string ScreenshotShortcut { get; private set; } = "未注册";
    public string RecordShortcut { get; private set; } = "未注册";
    public string PreviewShortcut { get; private set; } = "未注册";
    public string SettingsShortcut { get; private set; } = "未注册";
    public string ShowToolbarShortcut { get; private set; } = "未注册";
    public bool SuspendHotkeyActions
    {
        get => _suspendHotkeyActions;
        set
        {
            if (_suspendHotkeyActions && !value)
            {
                _hotkeyResumeTick = Environment.TickCount;
                _hotkeyResumedAt = Environment.TickCount64;
            }
            _suspendHotkeyActions = value;
        }
    }

    private HotkeyRegistry? _hotkeys;
    private HotkeyPreferences _configuredHotkeys;
    private int _screenshotDelay;
    private int _recordingDelay;
    private bool _applyingSettings;
    private bool _suspendHotkeyActions;
    private int? _hotkeyResumeTick;
    private long _hotkeyResumedAt;
    private readonly List<string> _hotkeyErrors = [];
    private readonly bool _inspectionMode;
    private readonly ToolbarPlacement? _savedPlacement;
    private readonly Font _uiFont = new("Microsoft YaHei UI", 9f);
    private readonly ToolTip _tips = new() { InitialDelay = 450, ReshowDelay = 120, AutoPopDelay = 5000 };
    private readonly System.Windows.Forms.Timer _edgeTimer = new() { Interval = 120 };
    private readonly ContextMenuStrip _screenshotMenu;
    private readonly ContextMenuStrip _recordMenu;
    private readonly ContextMenuStrip _audioMenu;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _trayScreenshot;
    private readonly ToolStripMenuItem _trayRecord;
    private readonly ToolStripMenuItem _trayStop;
    private readonly ToolStripMenuItem _trayPreview;
    private readonly ToolStripMenuItem _trayShow;
    private readonly ToolStripMenuItem _traySettings;
    private readonly ToolStripMenuItem _systemAudioItem;
    private readonly ToolStripMenuItem _microphoneItem;
    private readonly ToolbarButton _grip;
    private readonly ToolbarButton _screenshot;
    private readonly ToolbarButton _screenshotArrow;
    private readonly ToolbarButton _record;
    private readonly ToolbarButton _recordArrow;
    private readonly ToolbarButton _audio;
    private readonly ToolbarButton _preview;
    private readonly ToolbarButton _settings;
    private readonly ToolbarButton _exit;
    private readonly Label _status;
    private DockEdge _dock;
    private Rectangle _expandedBounds;
    private bool _collapsed;
    private bool _autoHideSuspended;
    private bool _dragging;
    private bool _busy;
    private bool _recording;
    private bool _positionRestored;
    private Point _dragCursor;
    private Point _dragOrigin;
    private DateTime _stayExpandedUntil = DateTime.UtcNow.AddSeconds(2);
    private enum DockEdge { None, Left, Top, Right, Bottom }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (_inspectionMode) parameters.ExStyle &= ~0x00000080;
            else parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: do not occupy Alt+Tab.
            return parameters;
        }
    }

    public ToolbarForm(bool inspectionMode = false)
    {
        _inspectionMode = inspectionMode;
        var preferences = PreferencesStore.Default.Read();
        SystemAudio = preferences.SystemAudio;
        Microphone = preferences.Microphone;
        _configuredHotkeys = CopyHotkeys(preferences.Hotkeys);
        _screenshotDelay = preferences.ScreenshotDelay;
        _recordingDelay = preferences.RecordingDelay;
        _savedPlacement = preferences.Toolbar;
        Text = "步录 · 操作栏";
        AccessibleName = "步录操作栏";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = inspectionMode;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(22, 31, 46);
        ForeColor = Color.FromArgb(233, 239, 247);
        Font = _uiFont;

        _grip = AddButton(ButtonGlyph.Grip, "拖动操作栏", "拖动 · 贴边自动隐藏");
        _grip.Cursor = Cursors.SizeAll;
        _screenshot = AddButton(ButtonGlyph.Camera, "截图", "截图");
        _screenshotArrow = AddButton(ButtonGlyph.Arrow, "延迟截图", "延迟截图");
        _record = AddButton(ButtonGlyph.Record, "录制", "录制 / 停止");
        _recordArrow = AddButton(ButtonGlyph.Arrow, "延迟录制", "延迟录制");
        _audio = AddButton(ButtonGlyph.Audio, "音频选项", "系统声音 · 麦克风");
        _preview = AddButton(ButtonGlyph.Document, "预览文档", "预览文档");
        _settings = AddButton(ButtonGlyph.Settings, "设置", "设置");
        _exit = AddButton(ButtonGlyph.Close, "退出", "退出步录");
        _status = new Label { Text = "就绪", AutoEllipsis = true, ForeColor = Color.FromArgb(151, 170, 193),
            BackColor = BackColor, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "当前状态" };
        Controls.Add(_status);

        _screenshotMenu = BuildDelayMenu(seconds => RequestScreenshot(seconds));
        _recordMenu = BuildDelayMenu(seconds => RequestRecord(seconds));
        _audioMenu = CreateMenu();
        _audioMenu.ShowCheckMargin = true;
        _systemAudioItem = new ToolStripMenuItem("系统声音") { Checked = SystemAudio, CheckOnClick = true };
        _microphoneItem = new ToolStripMenuItem("麦克风") { Checked = Microphone, CheckOnClick = true };
        // Attach only after restoring checks: opening a toolbar must not overwrite preferences.
        _systemAudioItem.CheckedChanged += (_, _) =>
        {
            SystemAudio = _systemAudioItem.Checked;
            UpdateAudioButton();
            if (!_applyingSettings) SavePreference(value => value.SystemAudio = SystemAudio);
        };
        _microphoneItem.CheckedChanged += (_, _) =>
        {
            Microphone = _microphoneItem.Checked;
            UpdateAudioButton();
            if (!_applyingSettings) SavePreference(value => value.Microphone = Microphone);
        };
        _audioMenu.Items.AddRange([_systemAudioItem, _microphoneItem]);
        UpdateAudioButton();

        _trayMenu = CreateMenu();
        _trayShow = new ToolStripMenuItem("显示操作栏", null, (_, _) => RestoreBar());
        _trayMenu.Items.Add(_trayShow);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayScreenshot = new ToolStripMenuItem("截图", null, (_, _) => RequestAfterTrayDismiss(() => RequestScreenshot(_screenshotDelay)));
        _trayRecord = new ToolStripMenuItem("录制", null, (_, _) => RequestAfterTrayDismiss(() => RequestRecord(_recordingDelay)));
        _trayStop = new ToolStripMenuItem("停止录制", null, (_, _) => RequestAfterTrayDismiss(() => RequestRecord(0))) { Enabled = false };
        _trayPreview = new ToolStripMenuItem("预览文档", null, (_, _) => RequestAfterTrayDismiss(RequestPreview));
        _traySettings = new ToolStripMenuItem("设置", null, (_, _) => RequestAfterTrayDismiss(RequestSettings));
        _trayMenu.Items.AddRange([_trayScreenshot, _trayRecord, _trayStop, _trayPreview, _traySettings]);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => RequestAfterTrayDismiss(() => ExitRequested?.Invoke(this, EventArgs.Empty)));
        _trayIcon = new NotifyIcon { Text = "步录 · 双击显示操作栏", Icon = SystemIcons.Application,
            ContextMenuStrip = _trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => RestoreBar();

        _screenshot.Click += (_, _) => RequestScreenshot(_screenshotDelay);
        _screenshotArrow.Click += (_, _) => ShowMenu(_screenshotMenu, _screenshotArrow);
        _record.Click += (_, _) => RequestRecord(_recordingDelay);
        _recordArrow.Click += (_, _) => ShowMenu(_recordMenu, _recordArrow);
        _audio.Click += (_, _) => ShowMenu(_audioMenu, _audio);
        _preview.Click += (_, _) => RequestPreview();
        _settings.Click += (_, _) => RequestSettings();
        _exit.Click += (_, _) => { CloseMenus(); ExitRequested?.Invoke(this, EventArgs.Empty); };

        _grip.MouseDown += BeginDrag;
        _grip.MouseMove += Drag;
        _grip.MouseUp += EndDrag;
        _status.MouseDown += BeginDrag;
        _status.MouseMove += Drag;
        _status.MouseUp += EndDrag;
        MouseDown += BeginDrag;
        MouseMove += Drag;
        MouseUp += EndDrag;
        MouseEnter += (_, _) => { if (_collapsed && !_autoHideSuspended) Expand(); };
        _edgeTimer.Tick += (_, _) => CheckAutoHide();
        ArrangeButtons();
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(work.Right - Width - 24, work.Top + 24);
        _expandedBounds = Bounds;
    }

    private ToolbarButton AddButton(ButtonGlyph glyph, string accessibleName, string tip)
    {
        var button = new ToolbarButton(glyph) { AccessibleName = accessibleName, AccessibleRole = AccessibleRole.PushButton,
            BackColor = BackColor, ForeColor = ForeColor, Cursor = Cursors.Hand, TabStop = false };
        Controls.Add(button);
        _tips.SetToolTip(button, tip);
        return button;
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Color.FromArgb(28, 39, 56), ForeColor = ForeColor,
            ShowImageMargin = false, Font = Font, Renderer = new DarkMenuRenderer() };
        menu.Opened += (_, _) => _stayExpandedUntil = DateTime.UtcNow.AddSeconds(1);
        menu.Closed += (_, _) => _stayExpandedUntil = DateTime.UtcNow.AddMilliseconds(900);
        return menu;
    }

    private ContextMenuStrip BuildDelayMenu(Action<int> action)
    {
        var menu = CreateMenu();
        foreach (var seconds in new[] { 0, 3, 5, 10 })
        {
            var value = seconds;
            var item = new ToolStripMenuItem(value == 0 ? "0" : $"{value} s") { AccessibleName = $"{value} 秒后开始" };
            item.Click += (_, _) =>
            {
                menu.Close();
                // Run after the menu has completely dismissed so it cannot enter a capture.
                if (!IsDisposed) BeginInvoke((Action)(() => action(value)));
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ShowMenu(ContextMenuStrip menu, Control anchor)
    {
        CloseMenus();
        _stayExpandedUntil = DateTime.UtcNow.AddSeconds(1);
        menu.Show(anchor, new Point(0, anchor.Height + 4));
    }

    public void CloseMenus()
    {
        _screenshotMenu.Close();
        _recordMenu.Close();
        _audioMenu.Close();
        _trayMenu.Close();
    }

    private void RequestAfterTrayDismiss(Action action)
    {
        _trayMenu.Close();
        if (!IsDisposed) BeginInvoke(action);
    }

    /// <summary>The caller retains ownership of the icon.</summary>
    public void SetTrayIcon(Icon icon) => _trayIcon.Icon = icon;

    /// <summary>Freeze edge behavior before hiding for capture; RestoreBar resumes it.</summary>
    public void SuspendAutoHide(bool suspended)
    {
        _autoHideSuspended = suspended;
        if (suspended) { CloseMenus(); Expand(); }
        else _stayExpandedUntil = DateTime.UtcNow.AddSeconds(1);
    }

    public void RestoreBar()
    {
        if (IsDisposed) return;
        _autoHideSuspended = false;
        Expand();
        ClampExpandedToScreen();
        Show();
        _stayExpandedUntil = DateTime.UtcNow.AddSeconds(1);
    }

    public void SetRecording(bool value)
    {
        _recording = value;
        _record.Glyph = value ? ButtonGlyph.Stop : ButtonGlyph.Record;
        _record.AccessibleName = value ? "停止录制" : "录制";
        _record.Accent = value;
        _record.Invalidate();
        Invalidate();
        UpdateShortcutLabels();
        ApplyEnabledState();
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        UpdateStatusDescription();
        var trayStatus = $"步录 · {text} · 双击显示操作栏";
        _trayIcon.Text = trayStatus.Length <= 63 ? trayStatus : trayStatus[..63];
    }

    public void SetBusy(bool busy) { _busy = busy; ApplyEnabledState(); }

    private void ApplyEnabledState()
    {
        _screenshot.Enabled = _screenshotArrow.Enabled = !_busy && !_recording;
        _record.Enabled = !_busy || _recording;
        _recordArrow.Enabled = _audio.Enabled = _preview.Enabled = _settings.Enabled = !_busy && !_recording;
        _trayScreenshot.Enabled = _trayPreview.Enabled = _traySettings.Enabled = !_busy && !_recording;
        _trayRecord.Enabled = !_busy && !_recording;
        _trayStop.Enabled = _recording;
    }

    private void UpdateAudioButton()
    {
        _audio.Muted = !SystemAudio && !Microphone;
        _audio.Invalidate();
        _tips.SetToolTip(_audio, $"系统声音：{(SystemAudio ? "开" : "关")}  ·  麦克风：{(Microphone ? "开" : "关")}");
    }

    private void SavePreference(Action<UserPreferences> update)
    {
        try { PreferencesStore.Default.Remember(update); }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Toolbar preference save failed: {error}");
            SetStatus("设置未保存");
        }
    }

    private void RequestScreenshot(int seconds)
    {
        if (_busy || _recording) return;
        CloseMenus();
        ScreenshotRequested?.Invoke(this, seconds);
    }

    private void RequestRecord(int seconds)
    {
        CloseMenus();
        if (_recording) { StopRequested?.Invoke(this, EventArgs.Empty); return; }
        if (!_busy) RecordRequested?.Invoke(this, seconds);
    }

    private void RequestPreview()
    {
        if (_busy || _recording) return;
        CloseMenus();
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestSettings()
    {
        if (_busy || _recording) return;
        CloseMenus();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.StyleFloatingWindow(Handle);
        var affinityApplied = NativeMethods.SetWindowDisplayAffinity(Handle,
            _inspectionMode ? 0u : NativeMethods.WdaExcludeFromCapture);
        CaptureExclusionEnabled = !_inspectionMode && affinityApplied;
        _hotkeys = new HotkeyRegistry(Handle);
        SetHotkeyErrors(_hotkeys.Start(_configuredHotkeys));
        RefreshShortcutLabels();
    }

    public IReadOnlyDictionary<string, string> ValidateHotkeys(HotkeyPreferences preferences)
    {
        if (InvokeRequired) throw new InvalidOperationException("请在操作栏的界面线程检查快捷键。");
        _ = Handle;
        return _hotkeys!.Validate(preferences);
    }

    /// <summary>Commit configuration only after all requested shortcuts are reserved successfully.</summary>
    public void ApplySettings(UserPreferences preferences, Action persist)
    {
        if (InvokeRequired) throw new InvalidOperationException("请在操作栏的界面线程保存设置。");
        _ = Handle;
        _hotkeys!.Apply(preferences.Hotkeys, persist);
        _configuredHotkeys = CopyHotkeys(preferences.Hotkeys);
        _screenshotDelay = preferences.ScreenshotDelay;
        _recordingDelay = preferences.RecordingDelay;
        _applyingSettings = true;
        try
        {
            SystemAudio = preferences.SystemAudio;
            Microphone = preferences.Microphone;
            _systemAudioItem.Checked = SystemAudio;
            _microphoneItem.Checked = Microphone;
        }
        finally { _applyingSettings = false; }
        _hotkeyErrors.Clear();
        UpdateAudioButton();
        RefreshShortcutLabels();
        if (_hotkeys.HasReleaseErrors)
        {
            _hotkeyErrors.Add("旧快捷键未能完全释放，请关闭步录后重新打开。");
            SetStatus("设置已保存 · 请重启以释放旧快捷键");
        }
        else SetStatus("设置已保存");
    }

    private static HotkeyPreferences CopyHotkeys(HotkeyPreferences value) => new()
    {
        Screenshot = value.Screenshot, Record = value.Record, Preview = value.Preview,
        ShowToolbar = value.ShowToolbar, Settings = value.Settings
    };

    private void SetHotkeyErrors(IReadOnlyDictionary<string, string> errors)
    {
        _hotkeyErrors.Clear();
        foreach (var (action, error) in errors) _hotkeyErrors.Add($"{HotkeyRegistry.ActionLabels[action]}：{error}");
    }

    private void RefreshShortcutLabels()
    {
        ScreenshotShortcut = _hotkeys?.Shortcut("screenshot", _configuredHotkeys) ?? "未注册";
        RecordShortcut = _hotkeys?.Shortcut("record", _configuredHotkeys) ?? "未注册";
        PreviewShortcut = _hotkeys?.Shortcut("preview", _configuredHotkeys) ?? "未注册";
        SettingsShortcut = _hotkeys?.Shortcut("settings", _configuredHotkeys) ?? "未注册";
        ShowToolbarShortcut = _hotkeys?.Shortcut("showToolbar", _configuredHotkeys) ?? "未注册";
        UpdateShortcutLabels();
    }

    private void UpdateShortcutLabels()
    {
        SetShortcutTip(_screenshot, _screenshotDelay == 0 ? "截图" : $"截图 · {_screenshotDelay} s", ScreenshotShortcut, true);
        SetShortcutTip(_record, _recording ? "停止录制" : _recordingDelay == 0 ? "录制" : $"录制 · {_recordingDelay} s", RecordShortcut, !_recording);
        SetShortcutTip(_preview, "预览文档", PreviewShortcut);
        SetShortcutTip(_settings, "设置", SettingsShortcut);
        _trayScreenshot.ShortcutKeyDisplayString = ScreenshotShortcut;
        _trayRecord.ShortcutKeyDisplayString = RecordShortcut;
        _trayStop.ShortcutKeyDisplayString = RecordShortcut;
        _trayPreview.ShortcutKeyDisplayString = PreviewShortcut;
        _traySettings.ShortcutKeyDisplayString = SettingsShortcut;
        _trayShow.ShortcutKeyDisplayString = ShowToolbarShortcut;
        _trayScreenshot.Text = _screenshotDelay == 0 ? "截图" : $"截图 · {_screenshotDelay} s";
        _trayRecord.Text = _recordingDelay == 0 ? "录制" : $"录制 · {_recordingDelay} s";
        UpdateStatusDescription();
    }

    private void SetShortcutTip(Control control, string action, string shortcut, bool immediate = false)
    {
        var text = shortcut is "未注册" or "未设置" ? $"{action} · 快捷键{shortcut}"
            : $"{action}  ·  {(immediate ? "立即：" : "")}{shortcut}";
        _tips.SetToolTip(control, text);
        control.AccessibleDescription = text;
    }

    private void UpdateStatusDescription()
    {
        var description = $"{_status.Text}\n立即截图：{ScreenshotShortcut}\n立即录制 / 停止：{RecordShortcut}\n预览：{PreviewShortcut}\n设置：{SettingsShortcut}\n显示操作栏：{ShowToolbarShortcut}";
        if (_hotkeyErrors.Count != 0) description += "\n" + string.Join("\n", _hotkeyErrors);
        _tips.SetToolTip(_status, description);
        _status.AccessibleDescription = description;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        ScreenshotShortcut = RecordShortcut = PreviewShortcut = SettingsShortcut = ShowToolbarShortcut = "未注册";
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ArrangeButtons();
        if (!_positionRestored)
        {
            RestorePlacement();
            _positionRestored = true;
        }
        ClampExpandedToScreen();
        _edgeTimer.Start();
        if (_hotkeyErrors.Count != 0)
        {
            SetStatus("快捷键占用 · 在设置中修改");
            _trayIcon.ShowBalloonTip(5000, "步录 · 快捷键未启用", string.Join("\n", _hotkeyErrors), ToolTipIcon.Warning);
        }
    }

    private void RestorePlacement()
    {
        if (_savedPlacement is null) return;
        var screen = Screen.AllScreens.FirstOrDefault(item => string.Equals(item.DeviceName,
            _savedPlacement.ScreenDevice, StringComparison.OrdinalIgnoreCase)) ?? Screen.PrimaryScreen ?? Screen.FromControl(this);
        // Moving to another monitor can change DeviceDpi. Arrange again there before resolving
        // fractions against the final expanded size; never restore using a collapsed handle.
        Location = PlacementBounds(_savedPlacement, screen.WorkingArea, Size).Location;
        ArrangeButtons();
        Bounds = PlacementBounds(_savedPlacement, screen.WorkingArea, Size);
        _dock = Enum.TryParse<DockEdge>(_savedPlacement.Dock, out var edge) && Enum.IsDefined(edge) ? edge : DockEdge.None;
        _expandedBounds = Bounds;
    }

    internal static Rectangle PlacementBounds(ToolbarPlacement placement, Rectangle area, Size size)
    {
        var travelX = Math.Max(0, area.Width - size.Width);
        var travelY = Math.Max(0, area.Height - size.Height);
        var xFraction = double.IsFinite(placement.XFraction) ? Math.Clamp(placement.XFraction, 0, 1) : 0;
        var yFraction = double.IsFinite(placement.YFraction) ? Math.Clamp(placement.YFraction, 0, 1) : 0;
        var x = area.Left + (int)Math.Round(travelX * xFraction);
        var y = area.Top + (int)Math.Round(travelY * yFraction);
        switch (placement.Dock)
        {
            case nameof(DockEdge.Left): x = area.Left; break;
            case nameof(DockEdge.Right): x = area.Left + travelX; break;
            case nameof(DockEdge.Top): y = area.Top; break;
            case nameof(DockEdge.Bottom): y = area.Top + travelY; break;
        }
        return new Rectangle(x, y, size.Width, size.Height);
    }

    internal static ToolbarPlacement PlacementFromBounds(Rectangle bounds, Rectangle area, string screenDevice, string dock)
    {
        var travelX = Math.Max(0, area.Width - bounds.Width);
        var travelY = Math.Max(0, area.Height - bounds.Height);
        return new ToolbarPlacement
        {
            ScreenDevice = screenDevice,
            XFraction = travelX == 0 ? 0 : Math.Clamp((bounds.Left - area.Left) / (double)travelX, 0, 1),
            YFraction = travelY == 0 ? 0 : Math.Clamp((bounds.Top - area.Top) / (double)travelY, 0, 1),
            Dock = dock
        };
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_collapsed) { ArrangeButtons(); _expandedBounds = Bounds; }
    }

    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    private void ArrangeButtons()
    {
        if (_collapsed) return;
        ClientSize = new Size(Px(384), Px(76));
        _grip.Bounds = new Rectangle(Px(10), Px(13), Px(20), Px(34));
        _screenshot.Bounds = new Rectangle(Px(36), Px(9), Px(44), Px(42));
        _screenshotArrow.Bounds = new Rectangle(Px(80), Px(9), Px(20), Px(42));
        _record.Bounds = new Rectangle(Px(108), Px(9), Px(44), Px(42));
        _recordArrow.Bounds = new Rectangle(Px(152), Px(9), Px(20), Px(42));
        _audio.Bounds = new Rectangle(Px(180), Px(9), Px(40), Px(42));
        _preview.Bounds = new Rectangle(Px(226), Px(9), Px(40), Px(42));
        _settings.Bounds = new Rectangle(Px(270), Px(9), Px(40), Px(42));
        _exit.Bounds = new Rectangle(Px(326), Px(9), Px(40), Px(42));
        _status.Bounds = new Rectangle(Px(38), Px(51), Px(328), Px(18));
        UpdateRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = ButtonDrawing.RoundRect(new RectangleF(0, 0, Width, Height), _collapsed ? Px(3) : Px(15));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_collapsed)
        {
            using var accent = new SolidBrush(_recording ? Color.FromArgb(255, 105, 121) : Color.FromArgb(102, 225, 191));
            var horizontal = _dock is DockEdge.Top or DockEdge.Bottom;
            var line = horizontal ? new RectangleF(Width / 2f - Px(24), 1, Px(48), Height - 2)
                : new RectangleF(1, Height / 2f - Px(16), Width - 2, Px(32));
            using var path = ButtonDrawing.RoundRect(line, Px(2));
            e.Graphics.FillPath(accent, path);
            return;
        }
        using var border = new Pen(Color.FromArgb(51, 70, 94));
        using var outline = ButtonDrawing.RoundRect(new RectangleF(.5f, .5f, Width - 1, Height - 1), Px(15));
        e.Graphics.DrawPath(border, outline);
        using var separator = new Pen(Color.FromArgb(46, 61, 80));
        foreach (var x in new[] { 104, 176, 318 }) e.Graphics.DrawLine(separator, Px(x), Px(20), Px(x), Px(40));
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _collapsed) return;
        _dragging = true;
        _dock = DockEdge.None;
        _dragCursor = Cursor.Position;
        _dragOrigin = Location;
        if (sender is Control source) source.Capture = true;
    }

    private void Drag(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pointer = Cursor.Position;
        Location = new Point(_dragOrigin.X + pointer.X - _dragCursor.X, _dragOrigin.Y + pointer.Y - _dragCursor.Y);
        _expandedBounds = Bounds;
    }

    private void EndDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        if (sender is Control source) source.Capture = false;
        SnapToEdge();
        var screen = Screen.FromRectangle(_expandedBounds);
        var placement = PlacementFromBounds(_expandedBounds, screen.WorkingArea, screen.DeviceName, _dock.ToString());
        SavePreference(value => value.Toolbar = placement);
        _stayExpandedUntil = DateTime.UtcNow.AddMilliseconds(900);
    }

    private void ClampExpandedToScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        _expandedBounds = Bounds;
    }

    private void SnapToEdge()
    {
        ClampExpandedToScreen();
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var candidates = new[] { (DockEdge.Left, Math.Abs(Left - area.Left)),
            (DockEdge.Top, Math.Abs(Top - area.Top)), (DockEdge.Right, Math.Abs(Right - area.Right)),
            (DockEdge.Bottom, Math.Abs(Bottom - area.Bottom)) };
        var closest = candidates.MinBy(item => item.Item2);
        _dock = closest.Item2 <= Px(20) ? closest.Item1 : DockEdge.None;
        if (_dock == DockEdge.Left) Left = area.Left;
        else if (_dock == DockEdge.Right) Left = area.Right - Width;
        else if (_dock == DockEdge.Top) Top = area.Top;
        else if (_dock == DockEdge.Bottom) Top = area.Bottom - Height;
        _expandedBounds = Bounds;
    }

    private void CheckAutoHide()
    {
        if (_autoHideSuspended || !Visible || _dragging || _dock == DockEdge.None ||
            _screenshotMenu.Visible || _recordMenu.Visible || _audioMenu.Visible || _trayMenu.Visible) return;
        if (Bounds.Contains(Cursor.Position))
        {
            if (_collapsed) Expand();
            _stayExpandedUntil = DateTime.UtcNow.AddMilliseconds(650);
        }
        else if (!_collapsed && DateTime.UtcNow > _stayExpandedUntil) Collapse();
    }

    private void Collapse()
    {
        if (_collapsed || _dock == DockEdge.None) return;
        _expandedBounds = Bounds;
        var area = Screen.FromRectangle(_expandedBounds).WorkingArea;
        var strip = Math.Max(6, Px(7));
        _collapsed = true;
        foreach (Control control in Controls) control.Visible = false;
        // Shrink to the visible handle instead of sliding into an adjacent monitor.
        Bounds = _dock switch
        {
            DockEdge.Left => new Rectangle(area.Left, Top, strip, Height),
            DockEdge.Right => new Rectangle(area.Right - strip, Top, strip, Height),
            DockEdge.Top => new Rectangle(Left, area.Top, Width, strip),
            _ => new Rectangle(Left, area.Bottom - strip, Width, strip)
        };
        UpdateRegion();
    }

    private void Expand()
    {
        if (!_collapsed) return;
        _collapsed = false;
        Bounds = _expandedBounds;
        ArrangeButtons();
        foreach (Control control in Controls) control.Visible = true;
        _stayExpandedUntil = DateTime.UtcNow.AddMilliseconds(900);
        UpdateRegion();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmMouseActivate) { message.Result = (IntPtr)NativeMethods.MaNoActivate; return; }
        if (message.Msg == NativeMethods.WmHotkey)
        {
            if (_hotkeys?.TryResolve(message.WParam.ToInt32(), out var action) == true)
            {
                if (SuspendHotkeyActions)
                {
                    // Windows can consume a registered chord before WebView receives keydown.
                    // Forward the chord only to the settings editor, without invoking its action.
                    SuspendedHotkeyPressed?.Invoke(this, _hotkeys.Shortcut(action, _configuredHotkeys));
                    return;
                }
                // Ignore messages posted while the modal editor was open but delivered after close.
                if (_hotkeyResumeTick is int resumed && Environment.TickCount64 - _hotkeyResumedAt < 5000 &&
                    unchecked(NativeMethods.GetMessageTime() - resumed) <= 0) return;
                _hotkeyResumeTick = null;
                if (action == "screenshot") RequestScreenshot(0);
                else if (action == "record") RequestRecord(0);
                else if (action == "preview") RequestPreview();
                else if (action == "showToolbar") { if (!_autoHideSuspended) RestoreBar(); }
                else if (action == "settings") RequestSettings();
            }
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _edgeTimer.Stop();
            _edgeTimer.Dispose();
            _tips.Dispose();
            _screenshotMenu.Dispose();
            _recordMenu.Dispose();
            _audioMenu.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _uiFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum ButtonGlyph { Grip, Camera, Record, Stop, Arrow, Audio, Document, Settings, Close }

internal sealed class ToolbarButton(ButtonGlyph glyph) : Control
{
    internal ButtonGlyph Glyph { get; set; } = glyph;
    internal bool Accent { get; set; }
    internal bool Muted { get; set; }
    private bool _hovered;
    private bool _pressed;

    protected override void OnCreateControl()
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        base.OnCreateControl();
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        if ((_hovered && Enabled) || Accent)
        {
            using var fill = new SolidBrush(Accent ? Color.FromArgb(88, 40, 55) : _pressed
                ? Color.FromArgb(65, 87, 110) : Color.FromArgb(42, 58, 79));
            using var path = ButtonDrawing.RoundRect(new RectangleF(1, 1, Width - 2, Height - 2), 10 * scale);
            graphics.FillPath(fill, path);
        }
        graphics.TranslateTransform(Width / 2f, Height / 2f);
        graphics.ScaleTransform(scale, scale);
        var color = !Enabled ? Color.FromArgb(77, 94, 116) : Accent || Glyph == ButtonGlyph.Record
            ? Color.FromArgb(255, 115, 136) : ForeColor;
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (Glyph)
        {
            case ButtonGlyph.Grip:
                for (var x = -3; x <= 3; x += 6)
                    for (var y = -6; y <= 6; y += 6) graphics.FillEllipse(brush, x - 1, y - 1, 2, 2);
                break;
            case ButtonGlyph.Camera:
                using (var camera = new GraphicsPath())
                {
                    camera.AddLines([new PointF(-10, -6), new PointF(-5, -6), new PointF(-3, -9), new PointF(3, -9),
                        new PointF(5, -6), new PointF(10, -6), new PointF(10, 8), new PointF(-10, 8), new PointF(-10, -6)]);
                    graphics.DrawPath(pen, camera);
                }
                graphics.DrawEllipse(pen, -4, -4, 8, 8);
                break;
            case ButtonGlyph.Record:
                graphics.DrawEllipse(pen, -10, -10, 20, 20);
                graphics.FillEllipse(brush, -5, -5, 10, 10);
                break;
            case ButtonGlyph.Stop:
                using (var stop = ButtonDrawing.RoundRect(new RectangleF(-7, -7, 14, 14), 3)) graphics.FillPath(brush, stop);
                break;
            case ButtonGlyph.Arrow:
                graphics.DrawLines(pen, [new PointF(-3, -1), new PointF(0, 2), new PointF(3, -1)]);
                break;
            case ButtonGlyph.Audio:
                graphics.DrawPolygon(pen, [new PointF(-9, -3), new PointF(-5, -3), new PointF(0, -8),
                    new PointF(0, 8), new PointF(-5, 3), new PointF(-9, 3)]);
                if (Muted)
                {
                    graphics.DrawLine(pen, 4, -4, 10, 4);
                    graphics.DrawLine(pen, 4, 4, 10, -4);
                }
                else { graphics.DrawArc(pen, -2, -6, 12, 12, -60, 120); graphics.DrawArc(pen, -5, -10, 20, 20, -48, 96); }
                break;
            case ButtonGlyph.Document:
                using (var document = ButtonDrawing.RoundRect(new RectangleF(-8, -10, 16, 20), 2)) graphics.DrawPath(pen, document);
                graphics.DrawLine(pen, -4, -4, 4, -4);
                graphics.DrawLine(pen, -4, 0, 4, 0);
                graphics.DrawLine(pen, -4, 4, 1, 4);
                break;
            case ButtonGlyph.Close:
                graphics.DrawLine(pen, -6, -6, 6, 6);
                graphics.DrawLine(pen, -6, 6, 6, -6);
                break;
            case ButtonGlyph.Settings:
                var teeth = new List<PointF>();
                for (var i = 0; i < 32; i++)
                {
                    var angle = MathF.PI * 2 * i / 32 - MathF.PI / 8;
                    var radius = i % 4 is 1 or 2 ? 10 : 7.8f;
                    teeth.Add(new PointF(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
                }
                graphics.DrawPolygon(pen, teeth.ToArray());
                graphics.DrawEllipse(pen, -3.2f, -3.2f, 6.4f, 6.4f);
                break;
        }
    }
}

internal static class ButtonDrawing
{
    internal static GraphicsPath RoundRect(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(rectangle.Width, rectangle.Height), radius * 2);
        if (diameter <= 0) return path;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        using var brush = new SolidBrush(Color.FromArgb(56, 77, 102));
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(70, 91, 116));
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
}
