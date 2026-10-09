using System.Text.Json;
using Stepwise.Core;
using Stepwise.UI;

namespace Stepwise;

/// <summary>Isolated Win32 integration checks, using invisible message-only windows and no input injection.</summary>
internal static class HotkeySelfTest
{
    public static int Run(string outputRoot)
    {
        var checks = new List<string>();
        Directory.CreateDirectory(outputRoot);
        try
        {
            Check(HotkeyChord.TryParse(" alt + control + s ", out var parsed, out _) && parsed.Display == "Ctrl + Alt + S", "解析大小写、空白与修饰键别名", checks);
            Check(HotkeyChord.TryParse("Ctrl+Alt+0", out var digit, out _) && digit.Key == (uint)Keys.D0, "数字快捷键使用实际数字虚拟键", checks);
            Check(!HotkeyChord.TryParse("Shift+A", out _, out _) && !HotkeyChord.TryParse("Ctrl+Ctrl+A", out _, out _) && !HotkeyChord.TryParse("Ctrl+Alt", out _, out _) && !HotkeyChord.TryParse("A", out _, out _) && !HotkeyChord.TryParse("Ctrl++S", out _, out _), "拒绝无修饰、重复修饰、缺键与非法格式", checks);
            Check(!HotkeyChord.TryParse("Ctrl+Alt+F12", out _, out var reserved) && reserved.Contains("保留") && !HotkeyChord.TryParse("Ctrl+Alt+Delete", out _, out _), "系统保留组合提示", checks);
            var duplicate = Empty(); duplicate.Screenshot = "Ctrl+Alt+S"; duplicate.Record = "alt+control+s";
            var duplicateErrors = HotkeyRegistry.ValidateSyntax(duplicate);
            Check(duplicateErrors.Count == 2 && duplicateErrors.ContainsKey("screenshot") && duplicateErrors.ContainsKey("record"), "拟配置互相重复按字段报告", checks);
            Check(HotkeyRegistry.ValidateSyntax(Empty()).Count == 0, "空快捷键可全部禁用", checks);

            using var owner = new TestWindow();
            using var competitor = new TestWindow();
            using var registry = new HotkeyRegistry(owner.Handle);
            using var other = new HotkeyRegistry(competitor.Handle);
            var available = Enumerable.Range(13, 12).Select(number => $"Ctrl+Alt+Shift+F{number}")
                .Where(key => { var preference = Empty(); preference.Screenshot = key; return registry.Validate(preference).Count == 0; }).Take(8).ToArray();
            if (available.Length < 8) throw new InvalidOperationException("测试所需的 8 组 F13–F24 组合已被其他软件占用，请在空闲会话重试。");
            var initial = new HotkeyPreferences { Screenshot = available[0], Record = available[1], Preview = available[2], ShowToolbar = available[3], Settings = available[4] };
            Check(registry.Start(initial).Count == 0, "实际向 Windows 注册五个动作", checks);
            Check(registry.Validate(initial).Count == 0, "自动检测忽略自身当前注册", checks);
            Check(other.Validate(initial).Count == 5, "另一窗口探测真实系统级占用", checks);
            var occupied = Empty(); occupied.Screenshot = available[5];
            Check(other.Start(occupied).Count == 0, "模拟外部程序预占候选组合", checks);
            using (var partialWindow = new TestWindow())
            using (var partialRegistry = new HotkeyRegistry(partialWindow.Handle))
            {
                var partial = Empty(); partial.Screenshot = available[5]; partial.Record = available[6];
                var startupErrors = partialRegistry.Start(partial);
                Check(startupErrors.Count == 1 && startupErrors.ContainsKey("screenshot") && partialRegistry.Shortcut("screenshot", partial) == "未注册" && partialRegistry.Shortcut("record", partial).Contains("F"), "启动冲突逐项提示并保留可用动作，不静默替换按键", checks);
            }

            var conflict = Clone(initial); conflict.Screenshot = available[6]; conflict.Settings = available[5];
            var persisted = false; var conflictFailed = false;
            try { registry.Apply(conflict, () => persisted = true); }
            catch (HotkeyConfigurationException error) { conflictFailed = error.Errors.ContainsKey("settings"); }
            var newlyFree = Empty(); newlyFree.Screenshot = available[6];
            Check(conflictFailed && !persisted && other.Validate(initial).Count == 5 && registry.Validate(newlyFree).Count == 0, "提交时新冲突阻止保存并释放已申请候选，原组合仍生效", checks);

            var pending = Clone(initial); pending.Screenshot = available[6];
            var saveFailed = false;
            try { registry.Apply(pending, () => throw new IOException("模拟配置写入失败")); }
            catch (IOException) { saveFailed = true; }
            Check(saveFailed && other.Validate(initial).Count == 5 && registry.Validate(newlyFree).Count == 0 && registry.Shortcut("screenshot", initial).EndsWith("F" + available[0].Split('F')[1]), "持久化失败保留旧快捷键并释放新快捷键", checks);

            var swapped = Clone(initial); (swapped.Screenshot, swapped.Record) = (swapped.Record, swapped.Screenshot);
            var heldDuringSave = false;
            registry.Apply(swapped, () => { heldDuringSave = other.Validate(initial).Count == 5; persisted = true; });
            HotkeyChord.TryParse(swapped.Screenshot, out var expected, out _);
            Check(persisted && heldDuringSave && registry.Shortcut("screenshot", swapped) == expected.Display && registry.Validate(swapped).Count == 0, "交换两个动作不中断注册且保存时持续保留全部组合", checks);

            var next = Clone(swapped); next.Screenshot = available[7];
            registry.Apply(next, () => { });
            var removed = Empty(); removed.Screenshot = swapped.Screenshot;
            Check(other.Validate(removed).Count == 0 && other.Validate(next).Count == 5, "成功替换后旧组合释放、新组合真实占用", checks);

            var wrongThreadRejected = Task.Run(() =>
            {
                try { registry.Validate(next); return false; } catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Check(wrongThreadRejected, "拒绝跨线程 Win32 注册", checks);
            registry.Apply(Empty(), () => { });
            Check(other.Validate(next).Count == 0 && registry.Shortcut("screenshot", Empty()) == "未设置", "禁用全部快捷键立即释放全部组合", checks);
            registry.Apply(initial, () => { });
            registry.Dispose();
            Check(other.Validate(initial).Count == 0, "窗口生命周期结束释放注册", checks);
            other.Dispose();
            using var finalProbe = new HotkeyRegistry(owner.Handle);
            Check(finalProbe.Validate(occupied).Count == 0 && !finalProbe.HasReleaseErrors, "测试外部占用亦已清理", checks);

            File.WriteAllText(Path.Combine(outputRoot, "hotkey-test-result.json"), JsonSerializer.Serialize(new { success = true, count = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(outputRoot, "hotkey-test-result.json"), JsonSerializer.Serialize(new { success = false, count = checks.Count, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static HotkeyPreferences Empty() => new() { Screenshot = "", Record = "", Preview = "", ShowToolbar = "", Settings = "" };
    private static HotkeyPreferences Clone(HotkeyPreferences value) => new() { Screenshot = value.Screenshot, Record = value.Record, Preview = value.Preview, ShowToolbar = value.ShowToolbar, Settings = value.Settings };
    private static void Check(bool passed, string name, List<string> checks)
    {
        if (!passed) throw new InvalidOperationException(name);
        checks.Add(name);
    }

    private sealed class TestWindow : NativeWindow, IDisposable
    {
        internal TestWindow() => CreateHandle(new CreateParams { Caption = "Stepwise hotkey test", Parent = new IntPtr(-3) });
        public void Dispose() => DestroyHandle();
    }
}
