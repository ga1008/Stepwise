using System.Runtime.InteropServices;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>A UI-thread-owned set of system hotkeys. Existing registrations stay held until a change commits.</summary>
internal sealed class HotkeyRegistry(IntPtr window) : IDisposable
{
    internal static readonly string[] Actions = ["screenshot", "record", "preview", "showToolbar", "settings"];
    internal static readonly IReadOnlyDictionary<string, string> ActionLabels = new Dictionary<string, string>
    {
        ["screenshot"] = "立即截图", ["record"] = "立即录制 / 停止", ["preview"] = "预览文档",
        ["showToolbar"] = "显示操作栏", ["settings"] = "设置"
    };
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<HotkeyChord, Registration> _held = [];
    private readonly HashSet<int> _reservedIds = [];
    private readonly Dictionary<int, Registration> _unreleased = [];
    private Dictionary<string, Registration> _bindings = [];
    private int _nextId = 1000;
    private bool _disposed;
    private sealed record Registration(int Id, HotkeyChord Chord);

    internal static Dictionary<string, string> Values(HotkeyPreferences value) => new()
    {
        ["screenshot"] = value.Screenshot, ["record"] = value.Record, ["preview"] = value.Preview,
        ["showToolbar"] = value.ShowToolbar, ["settings"] = value.Settings
    };

    internal string Shortcut(string action, HotkeyPreferences configured) => _bindings.TryGetValue(action, out var registered)
        ? registered.Chord.Display : string.IsNullOrWhiteSpace(Values(configured)[action]) ? "未设置" : "未注册";

    internal bool TryResolve(int id, out string action)
    {
        action = _bindings.FirstOrDefault(pair => pair.Value.Id == id).Key ?? "";
        return action.Length != 0;
    }

    internal static IReadOnlyDictionary<string, string> ValidateSyntax(HotkeyPreferences preferences)
        => Parse(preferences).Errors;

    /// <summary>Hold every new candidate together while checking; never drop our current keys to probe.</summary>
    internal IReadOnlyDictionary<string, string> Validate(HotkeyPreferences preferences)
    {
        AssertThread();
        var (desired, errors) = Parse(preferences);
        var reservations = new List<Registration>();
        try
        {
            foreach (var (action, chord) in desired)
            {
                if (errors.ContainsKey(action) || _held.ContainsKey(chord)) continue;
                var registration = Reserve(chord, out var error);
                if (registration == null) errors[action] = error;
                else reservations.Add(registration);
            }
        }
        finally { ReleaseCandidates(reservations); }
        return errors;
    }

    /// <summary>Best-effort startup: each unavailable binding is reported, with no silent substitute.</summary>
    internal IReadOnlyDictionary<string, string> Start(HotkeyPreferences preferences)
    {
        AssertThread();
        if (_held.Count != 0) throw new InvalidOperationException("快捷键已启动。");
        var (desired, errors) = Parse(preferences);
        foreach (var (action, chord) in desired)
        {
            if (errors.ContainsKey(action)) continue;
            var registration = Reserve(chord, out var error);
            if (registration == null) errors[action] = error;
            else { _held.Add(chord, registration); _bindings.Add(action, registration); }
        }
        return errors;
    }

    /// <summary>Reserve, persist, then release obsolete keys. A failed save leaves all old bindings intact.</summary>
    internal void Apply(HotkeyPreferences preferences, Action persist)
    {
        AssertThread();
        var (desired, errors) = Parse(preferences);
        if (errors.Count != 0) throw new HotkeyConfigurationException(errors);
        var acquired = new List<Registration>();
        var next = new Dictionary<string, Registration>();
        try
        {
            foreach (var (action, chord) in desired)
            {
                if (_held.TryGetValue(chord, out var existing)) { next.Add(action, existing); continue; }
                var reservation = Reserve(chord, out var error);
                if (reservation == null) { errors[action] = error; continue; }
                acquired.Add(reservation);
                next.Add(action, reservation);
            }
            if (errors.Count != 0) throw new HotkeyConfigurationException(errors);
            persist();
        }
        catch (Exception failure)
        {
            try { ReleaseCandidates(acquired); }
            catch (Exception rollbackFailure)
            { throw new InvalidOperationException("设置未保存，且部分候选快捷键未能释放。原快捷键仍保留，请关闭步录后重试。", new AggregateException(failure, rollbackFailure)); }
            throw;
        }
        // No callback or reentrant message pumping occurs during this commit. Swapped actions reuse
        // their registration IDs, so their chords never become available for another application.
        _bindings = next;
        foreach (var registration in acquired) _held.Add(registration.Chord, registration);
        var retained = next.Values.Select(item => item.Chord).ToHashSet();
        foreach (var obsolete in _held.Values.Where(item => !retained.Contains(item.Chord)).ToArray())
        {
            Release(obsolete);
            _held.Remove(obsolete.Chord);
        }
    }

    private static (Dictionary<string, HotkeyChord> Desired, Dictionary<string, string> Errors) Parse(HotkeyPreferences preferences)
    {
        var desired = new Dictionary<string, HotkeyChord>();
        var errors = new Dictionary<string, string>();
        foreach (var (action, value) in Values(preferences))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!HotkeyChord.TryParse(value, out var chord, out var error)) errors[action] = error;
            else desired.Add(action, chord);
        }
        foreach (var duplicate in desired.GroupBy(item => item.Value).Where(group => group.Count() > 1))
            foreach (var entry in duplicate)
                errors[entry.Key] = $"与{string.Join("、", duplicate.Where(other => other.Key != entry.Key).Select(other => ActionLabels[other.Key]))}重复";
        return (desired, errors);
    }

    private Registration? Reserve(HotkeyChord chord, out string error)
    {
        int id;
        do
        {
            id = _nextId++;
            // Include provisional registrations so wrapping cannot collide during a validation.
            if (_nextId >= 0xBFFF) _nextId = 1000;
        } while (_reservedIds.Contains(id));
        if (NativeMethods.RegisterHotKey(window, id, chord.Modifiers | NativeMethods.ModNoRepeat, chord.Key))
        {
            error = "";
            _reservedIds.Add(id);
            return new Registration(id, chord);
        }
        var code = Marshal.GetLastPInvokeError();
        error = code == 1409 ? "已被其他程序或系统占用" : $"无法注册，可能被系统保留（{code}）";
        return null;
    }

    internal bool HasReleaseErrors => _unreleased.Count != 0;

    private bool Release(Registration registration)
    {
        if (!NativeMethods.UnregisterHotKey(window, registration.Id))
        {
            _unreleased[registration.Id] = registration;
            System.Diagnostics.Debug.WriteLine($"Unable to release hotkey {registration.Chord.Display}: {Marshal.GetLastPInvokeError()}");
            return false;
        }
        _reservedIds.Remove(registration.Id);
        _unreleased.Remove(registration.Id);
        return true;
    }

    private void ReleaseCandidates(IEnumerable<Registration> registrations)
    {
        var failures = registrations.Where(registration => !Release(registration)).ToArray();
        if (failures.Length != 0) throw new InvalidOperationException("快捷键检测结束后未能完全释放，请关闭步录后重试。");
    }

    private void AssertThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("请在操作栏的界面线程配置快捷键。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        AssertThread();
        foreach (var registration in _held.Values.Concat(_unreleased.Values).DistinctBy(item => item.Id).ToArray()) Release(registration);
        _held.Clear(); _bindings.Clear(); _disposed = true;
    }
}

public sealed class HotkeyConfigurationException(IReadOnlyDictionary<string, string> errors)
    : InvalidOperationException(string.Join("；", errors.Select(pair => $"{HotkeyRegistry.ActionLabels[pair.Key]}：{pair.Value}")))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

internal readonly record struct HotkeyChord(uint Modifiers, uint Key)
{
    internal string Display
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & NativeMethods.ModWin) != 0) parts.Add("Win");
            parts.Add(Key is >= 0x30 and <= 0x39 ? ((char)Key).ToString() : ((Keys)Key).ToString() switch
            {
                "Return" => "Enter", "Prior" => "PageUp", "Next" => "PageDown", "Back" => "Backspace", var other => other
            });
            return string.Join(" + ", parts);
        }
    }

    internal static bool TryParse(string? text, out HotkeyChord chord, out string error)
    {
        chord = default;
        error = "请按下 Ctrl / Alt / Win 与字母、数字、功能键或导航键的组合";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        uint modifiers = 0;
        foreach (var token in parts[..^1])
        {
            var modifier = token.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => NativeMethods.ModControl, "ALT" => NativeMethods.ModAlt,
                "SHIFT" => NativeMethods.ModShift, "WIN" or "WINDOWS" or "META" => NativeMethods.ModWin, _ => 0u
            };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        if ((modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModWin)) == 0) return false;
        var keyText = parts[^1].ToUpperInvariant();
        uint key = 0;
        if (keyText.Length == 1 && (keyText[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = keyText[0];
        else if (keyText.StartsWith('F') && int.TryParse(keyText[1..], out var number) && number is >= 1 and <= 24)
            key = (uint)Keys.F1 + (uint)number - 1;
        else key = keyText switch
        {
            "SPACE" => (uint)Keys.Space, "TAB" => (uint)Keys.Tab, "ENTER" or "RETURN" => (uint)Keys.Enter,
            "ESC" or "ESCAPE" => (uint)Keys.Escape, "BACKSPACE" or "BACK" => (uint)Keys.Back,
            "DELETE" or "DEL" => (uint)Keys.Delete, "INSERT" or "INS" => (uint)Keys.Insert,
            "PRINTSCREEN" or "SNAPSHOT" => (uint)Keys.PrintScreen, "PAUSE" => (uint)Keys.Pause,
            "SCROLLLOCK" or "SCROLL" => (uint)Keys.Scroll, "NUMLOCK" => (uint)Keys.NumLock,
            "HOME" => (uint)Keys.Home, "END" => (uint)Keys.End,
            "PAGEUP" or "PRIOR" => (uint)Keys.PageUp, "PAGEDOWN" or "NEXT" => (uint)Keys.PageDown,
            "UP" or "ARROWUP" => (uint)Keys.Up, "DOWN" or "ARROWDOWN" => (uint)Keys.Down,
            "LEFT" or "ARROWLEFT" => (uint)Keys.Left, "RIGHT" or "ARROWRIGHT" => (uint)Keys.Right, _ => 0
        };
        if (key == 0) return false;
        if (key == (uint)Keys.F12) { error = "F12 由 Windows 调试器保留，请使用其他键"; return false; }
        if (key == (uint)Keys.Delete && (modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt)) == (NativeMethods.ModControl | NativeMethods.ModAlt))
        { error = "Ctrl + Alt + Delete 由 Windows 保留"; return false; }
        chord = new HotkeyChord(modifiers, key);
        error = "";
        return true;
    }
}
