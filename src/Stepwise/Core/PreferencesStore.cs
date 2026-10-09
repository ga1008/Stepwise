using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stepwise.Core;

public sealed class AnnotationPreferences
{
    public string Tool { get; set; } = "rect";
    public string Color { get; set; } = "#f04d4d";
    public int LineWidth { get; set; } = 5;
    public int FontSize { get; set; } = 28;
    public string ZoomMode { get; set; } = "fit";
    public double ZoomScale { get; set; } = 1;
}

public sealed class ToolbarPlacement
{
    public string ScreenDevice { get; set; } = "";
    public double XFraction { get; set; }
    public double YFraction { get; set; }
    public string Dock { get; set; } = "None";
}

public sealed class HotkeyPreferences
{
    public string Screenshot { get; set; } = "Ctrl+Alt+S";
    public string Record { get; set; } = "Ctrl+Alt+R";
    public string Preview { get; set; } = "Ctrl+Alt+P";
    public string ShowToolbar { get; set; } = "Ctrl+Alt+B";
    public string Settings { get; set; } = "Ctrl+Alt+O";
}

/// <summary>Configured defaults and learned choices; individual capture delays, bounds and project contents are excluded.</summary>
public sealed class UserPreferences
{
    public AnnotationPreferences Annotation { get; set; } = new();
    public bool RememberChoices { get; set; } = true;
    public int ScreenshotDelay { get; set; }
    public int RecordingDelay { get; set; }
    public int Mp4FramesPerSecond { get; set; } = 20;
    public int Mp4MaxWidth { get; set; }
    public int GifFramesPerSecond { get; set; } = 12;
    public int GifMaxWidth { get; set; } = 1440;
    public HotkeyPreferences Hotkeys { get; set; } = new();
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; } = true;
    public string ExportFormat { get; set; } = "mp4";
    public string NoteExportFormat { get; set; } = "word";
    public bool ExportMuted { get; set; }
    public string ProjectParentDirectory { get; set; } = "";
    public ToolbarPlacement? Toolbar { get; set; }
}

/// <summary>Small, per-user settings. Each mutation rereads under a cross-process lock so independent windows can patch their own fields.</summary>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Lazy<PreferencesStore> DefaultStore = new(() => new PreferencesStore(
        Environment.GetEnvironmentVariable("STEPWISE_SETTINGS_FILE") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stepwise", "settings.json")));
    public static PreferencesStore Default => DefaultStore.Value;
    public string FilePath { get; }
    private readonly string _mutexName;
    private const int MaximumFileBytes = 256 * 1024;

    public PreferencesStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("设置文件路径不能为空。", nameof(path));
        FilePath = Path.GetFullPath(path);
        var identity = OperatingSystem.IsWindows() ? FilePath.ToUpperInvariant() : FilePath;
        _mutexName = "Local\\Stepwise.Preferences." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>Returns a fresh object graph; reading does not repair or create files.</summary>
    // Atomic replacement gives readers either the whole previous file or the whole next file.
    // Startup therefore never waits for another process' settings mutex.
    public UserPreferences Read() => Load(out _);

    /// <summary>Patch only the fields changed by the caller. Persistence errors propagate to the UI.</summary>
    public void Update(Action<UserPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        WithLock(() =>
        {
            var preferences = Load(out var primaryValid);
            update(preferences);
            Normalize(preferences);
            AtomicWrite(JsonSerializer.Serialize(preferences, JsonOptions), primaryValid);
            return true;
        });
    }

    /// <summary>Learn an operation choice only when enabled, checking under the same cross-process lock as the write.</summary>
    public void Remember(Action<UserPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        WithLock(() =>
        {
            var preferences = Load(out var primaryValid);
            if (!preferences.RememberChoices) return false;
            update(preferences);
            Normalize(preferences);
            AtomicWrite(JsonSerializer.Serialize(preferences, JsonOptions), primaryValid);
            return true;
        });
    }

    /// <summary>Explicit settings save always persists; preserve the separately managed toolbar placement.</summary>
    public void ApplyConfiguration(UserPreferences configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var snapshot = JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(configuration, JsonOptions), JsonOptions)!;
        Normalize(snapshot);
        Update(current =>
        {
            current.Annotation = snapshot.Annotation;
            current.RememberChoices = snapshot.RememberChoices;
            current.ScreenshotDelay = snapshot.ScreenshotDelay;
            current.RecordingDelay = snapshot.RecordingDelay;
            current.Mp4FramesPerSecond = snapshot.Mp4FramesPerSecond;
            current.Mp4MaxWidth = snapshot.Mp4MaxWidth;
            current.GifFramesPerSecond = snapshot.GifFramesPerSecond;
            current.GifMaxWidth = snapshot.GifMaxWidth;
            current.Hotkeys = snapshot.Hotkeys;
            current.SystemAudio = snapshot.SystemAudio;
            current.Microphone = snapshot.Microphone;
            current.ExportFormat = snapshot.ExportFormat;
            current.ExportMuted = snapshot.ExportMuted;
            current.ProjectParentDirectory = snapshot.ProjectParentDirectory;
        });
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("设置正由另一个步录窗口保存，请稍后重试。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private UserPreferences Load(out bool primaryValid)
    {
        primaryValid = TryRead(FilePath, out var preferences);
        if (primaryValid || TryRead(FilePath + ".bak", out preferences)) return preferences!;
        return new UserPreferences();
    }

    private static bool TryRead(string path, out UserPreferences? preferences)
    {
        preferences = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) return false;
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var result = new UserPreferences();
            // Parse individual fields rather than letting one incorrectly typed legacy value discard all other choices.
            if (TryProperty(root, "annotation", out var annotation) && annotation.ValueKind == JsonValueKind.Object)
            {
                ReadString(annotation, "tool", x => result.Annotation.Tool = x);
                ReadString(annotation, "color", x => result.Annotation.Color = x);
                ReadInt(annotation, "lineWidth", x => result.Annotation.LineWidth = x);
                ReadInt(annotation, "fontSize", x => result.Annotation.FontSize = x);
                ReadString(annotation, "zoomMode", x => result.Annotation.ZoomMode = x);
                ReadDouble(annotation, "zoomScale", x => result.Annotation.ZoomScale = x);
            }
            ReadBool(root, "systemAudio", x => result.SystemAudio = x);
            ReadBool(root, "rememberChoices", x => result.RememberChoices = x);
            ReadInt(root, "screenshotDelay", x => result.ScreenshotDelay = x);
            ReadInt(root, "recordingDelay", x => result.RecordingDelay = x);
            ReadInt(root, "mp4FramesPerSecond", x => result.Mp4FramesPerSecond = x);
            ReadInt(root, "mp4MaxWidth", x => result.Mp4MaxWidth = x);
            ReadInt(root, "gifFramesPerSecond", x => result.GifFramesPerSecond = x);
            ReadInt(root, "gifMaxWidth", x => result.GifMaxWidth = x);
            if (TryProperty(root, "hotkeys", out var hotkeys) && hotkeys.ValueKind == JsonValueKind.Object)
            {
                ReadString(hotkeys, "screenshot", x => result.Hotkeys.Screenshot = x);
                ReadString(hotkeys, "record", x => result.Hotkeys.Record = x);
                ReadString(hotkeys, "preview", x => result.Hotkeys.Preview = x);
                ReadString(hotkeys, "showToolbar", x => result.Hotkeys.ShowToolbar = x);
                ReadString(hotkeys, "settings", x => result.Hotkeys.Settings = x);
            }
            ReadBool(root, "microphone", x => result.Microphone = x);
            ReadString(root, "exportFormat", x => result.ExportFormat = x);
            ReadString(root, "noteExportFormat", x => result.NoteExportFormat = x);
            ReadBool(root, "exportMuted", x => result.ExportMuted = x);
            ReadString(root, "projectParentDirectory", x => result.ProjectParentDirectory = x);
            if (TryProperty(root, "toolbar", out var toolbar) && toolbar.ValueKind == JsonValueKind.Object)
            {
                result.Toolbar = new ToolbarPlacement();
                ReadString(toolbar, "screenDevice", x => result.Toolbar.ScreenDevice = x);
                ReadDouble(toolbar, "xFraction", x => result.Toolbar.XFraction = x);
                ReadDouble(toolbar, "yFraction", x => result.Toolbar.YFraction = x);
                ReadString(toolbar, "dock", x => result.Toolbar.Dock = x);
            }
            Normalize(result);
            preferences = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    private static void Normalize(UserPreferences preferences)
    {
        preferences.Annotation ??= new AnnotationPreferences();
        preferences.Hotkeys ??= new HotkeyPreferences();
        preferences.Hotkeys.Screenshot = NormalizeShortcut(preferences.Hotkeys.Screenshot);
        preferences.Hotkeys.Record = NormalizeShortcut(preferences.Hotkeys.Record);
        preferences.Hotkeys.Preview = NormalizeShortcut(preferences.Hotkeys.Preview);
        preferences.Hotkeys.ShowToolbar = NormalizeShortcut(preferences.Hotkeys.ShowToolbar);
        preferences.Hotkeys.Settings = NormalizeShortcut(preferences.Hotkeys.Settings);
        if (preferences.ScreenshotDelay is not (0 or 3 or 5 or 10)) preferences.ScreenshotDelay = 0;
        if (preferences.RecordingDelay is not (0 or 3 or 5 or 10)) preferences.RecordingDelay = 0;
        if (preferences.Mp4FramesPerSecond is < 1 or > 60) preferences.Mp4FramesPerSecond = 20;
        if (preferences.GifFramesPerSecond is < 1 or > 30) preferences.GifFramesPerSecond = 12;
        if (preferences.Mp4MaxWidth != 0 && preferences.Mp4MaxWidth is < 160 or > 7680) preferences.Mp4MaxWidth = 0;
        if (preferences.GifMaxWidth != 0 && preferences.GifMaxWidth is < 160 or > 7680) preferences.GifMaxWidth = 1440;
        var annotation = preferences.Annotation;
        if (annotation.Tool is not ("select" or "rect" or "ellipse" or "pen" or "arrow" or "text")) annotation.Tool = "rect";
        annotation.Color = IsColor(annotation.Color) ? annotation.Color.ToLowerInvariant() : "#f04d4d";
        if (annotation.LineWidth is not (2 or 3 or 5 or 8 or 12 or 18)) annotation.LineWidth = 5;
        if (annotation.FontSize is not (16 or 20 or 24 or 28 or 36 or 48 or 64 or 96)) annotation.FontSize = 28;
        if (annotation.ZoomMode is not ("fit" or "custom")) annotation.ZoomMode = "fit";
        if (!double.IsFinite(annotation.ZoomScale) || annotation.ZoomScale < 0.01 || annotation.ZoomScale > 8) annotation.ZoomScale = 1;
        if (preferences.ExportFormat is not ("mp4" or "gif")) preferences.ExportFormat = "mp4";
        if (preferences.NoteExportFormat is not ("word" or "html" or "longimage" or "pdf")) preferences.NoteExportFormat = "word";
        preferences.ProjectParentDirectory = NormalizeDirectory(preferences.ProjectParentDirectory);
        if (preferences.Toolbar is { } toolbar)
        {
            toolbar.ScreenDevice = (toolbar.ScreenDevice ?? "").Trim();
            if (toolbar.ScreenDevice.Length > 256) toolbar.ScreenDevice = "";
            if (!double.IsFinite(toolbar.XFraction) || toolbar.XFraction is < 0 or > 1) toolbar.XFraction = 0;
            if (!double.IsFinite(toolbar.YFraction) || toolbar.YFraction is < 0 or > 1) toolbar.YFraction = 0;
            if (toolbar.Dock is not ("None" or "Left" or "Right" or "Top" or "Bottom")) toolbar.Dock = "None";
        }
    }

    private static string NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    // Do not silently replace an invalid/conflicting shortcut with a different action key.
    // The toolbar reports syntax and OS registration errors; an empty string disables the action.
    private static string NormalizeShortcut(string? value) => value?.Trim() is { Length: <= 96 } shortcut ? shortcut : "";

    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
    private static void ReadString(JsonElement element, string name, Action<string> set)
    { if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text) set(text); }
    private static void ReadBool(JsonElement element, string name, Action<bool> set)
    { if (TryProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) set(value.GetBoolean()); }
    private static void ReadInt(JsonElement element, string name, Action<int> set)
    { if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) set(number); }
    private static void ReadDouble(JsonElement element, string name, Action<double> set)
    { if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) set(number); }

    private void AtomicWrite(string content, bool primaryValid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            { writer.Write(content); writer.Flush(); stream.Flush(true); }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, primaryValid ? FilePath + ".bak" : null, true);
                    else File.Move(temporary, FilePath);
                    break;
                }
                catch (IOException ex) when (attempt < 4 && File.Exists(temporary) && (ex.HResult & 0xffff) is 32 or 33 or 1175)
                {
                    // Antivirus/sync readers can briefly deny replacement (1175 is
                    // ERROR_UNABLE_TO_REMOVE_REPLACED). Retry only noncommitting errors;
                    // a persistent lock is still reported, never silently discarded.
                    Thread.Sleep(25 << attempt);
                }
            }
        }
        finally
        {
            // The old backup is deliberately retained when the primary was corrupt.
            // Cleanup must never hide the original write exception.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
