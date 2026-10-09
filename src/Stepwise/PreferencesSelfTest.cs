using System.Diagnostics;
using System.Text.Json;
using Stepwise.Core;
using Stepwise.UI;

namespace Stepwise;

internal static class PreferencesSelfTest
{
    public static void Run(string[] args)
    {
        var parent = args.SkipWhile(x => x != "--preferences-test").Skip(1).FirstOrDefault()
            ?? throw new ArgumentException("偏好验收必须指定隔离目录。");
        parent = Path.GetFullPath(parent);
        Directory.CreateDirectory(parent);
        var worker = args.SkipWhile(x => x != "--preferences-worker").Skip(1).FirstOrDefault();
        if (worker != null) { RunWorker(parent, worker); return; }
        var checks = new List<string>();
        var run = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        try
        {
            var path = Path.Combine(run, "settings.json");
            var store = new PreferencesStore(path);
            var defaults = store.Read();
            Assert(defaults.Annotation.LineWidth == 5 && defaults.Annotation.FontSize == 28 && defaults.SystemAudio && defaults.Microphone && defaults.Toolbar == null && !File.Exists(path), "缺失配置使用默认且读取不建文件", checks);
            store.Update(p => { p.Annotation.Color = "#4E9CFF"; p.Annotation.Tool = "arrow"; p.Annotation.LineWidth = 12; p.Annotation.FontSize = 48; p.Annotation.ZoomMode = "custom"; p.Annotation.ZoomScale = 1.25; p.SystemAudio = false; p.ExportFormat = "gif"; p.ExportMuted = true; p.ProjectParentDirectory = run; });
            var restarted = new PreferencesStore(path).Read();
            Assert(restarted.Annotation.Color == "#4e9cff" && restarted.Annotation.Tool == "arrow" && restarted.Annotation.LineWidth == 12 && restarted.Annotation.FontSize == 48 && restarted.Annotation.ZoomMode == "custom" && restarted.Annotation.ZoomScale == 1.25 && !restarted.SystemAudio && restarted.Microphone && restarted.ExportFormat == "gif" && restarted.ExportMuted && restarted.ProjectParentDirectory == run, "新实例恢复全部选择", checks);
            restarted.Annotation.Color = "#000000";
            Assert(store.Read().Annotation.Color == "#4e9cff", "读取结果独立且不会暗改持久化设置", checks);
            var second = new PreferencesStore(path);
            second.Update(p => p.Microphone = false);
            store.Update(p => p.Annotation.FontSize = 64);
            var patched = second.Read();
            Assert(!patched.Microphone && patched.Annotation.FontSize == 64 && patched.Annotation.Color == "#4e9cff" && patched.ExportFormat == "gif", "多个实例部分修改不丢其他选择", checks);
            Assert(File.Exists(path + ".bak") && JsonDocument.Parse(File.ReadAllText(path + ".bak")).RootElement.ValueKind == JsonValueKind.Object, "更新生成完整备份", checks);
            var json = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(json))
                Assert(doc.RootElement.TryGetProperty("systemAudio", out _) && doc.RootElement.GetProperty("annotation").TryGetProperty("lineWidth", out _) && doc.RootElement.TryGetProperty("screenshotDelay", out _) && !json.Contains("steps", StringComparison.OrdinalIgnoreCase) && !json.Contains("bounds", StringComparison.OrdinalIgnoreCase), "camelCase默认配置且不保存步骤选区", checks);
            File.WriteAllText(path, "{broken");
            var recovered = store.Read();
            Assert(recovered.Annotation.Color == "#4e9cff" && recovered.Annotation.FontSize == 48 && !recovered.Microphone && File.ReadAllText(path) == "{broken", "损坏主文件从备份恢复且读取不破坏原文件", checks);
            store.Update(p => p.ExportMuted = false);
            File.WriteAllText(path, "broken again");
            Assert(store.Read().Annotation.Color == "#4e9cff", "修复保存不会以损坏主文件覆盖有效备份", checks);
            File.WriteAllText(path + ".bak", "broken backup");
            Assert(store.Read().Annotation.LineWidth == 5 && store.Read().SystemAudio, "主文件和备份均损坏安全使用默认", checks);
            File.WriteAllText(path, """
                {"annotation":{"tool":"bad","color":"#12XY00","lineWidth":999,"fontSize":"64","zoomMode":"bad","zoomScale":1e400},"systemAudio":"false","microphone":false,"exportFormat":"avi","exportMuted":true,"projectParentDirectory":"relative/path","toolbar":{"screenDevice":"screen","xFraction":-10,"yFraction":2,"dock":"Unsafe"}}
                """);
            var invalid = store.Read();
            Assert(invalid.Annotation.Tool == "rect" && invalid.Annotation.Color == "#f04d4d" && invalid.Annotation.LineWidth == 5 && invalid.Annotation.FontSize == 28 && invalid.Annotation.ZoomMode == "fit" && invalid.Annotation.ZoomScale == 1 && invalid.SystemAudio && !invalid.Microphone && invalid.ExportFormat == "mp4" && invalid.ExportMuted && invalid.ProjectParentDirectory == "" && invalid.Toolbar is { XFraction: 0, YFraction: 0, Dock: "None" }, "非法字段逐项归一且保留其他合法设置", checks);
            File.WriteAllText(path, "{\"Annotation\":{\"Color\":\"#ABCDEF\"},\"Microphone\":false}");
            Assert(store.Read().Annotation.Color == "#abcdef" && !store.Read().Microphone && store.Read().Annotation.LineWidth == 5, "旧版大小写与缺少字段兼容", checks);
            store.Update(p => { p.Annotation.ZoomScale = double.NaN; p.Toolbar = new ToolbarPlacement { XFraction = double.PositiveInfinity, YFraction = 0.75, Dock = "Right" }; });
            Assert(store.Read().Annotation.ZoomScale == 1 && store.Read().Toolbar is { XFraction: 0, YFraction: 0.75, Dock: "Right" }, "写入非有限数字归一且合法停靠保存", checks);
            var unchanged = File.ReadAllText(path);
            var rejected = false;
            try { store.Update(p => { p.Microphone = true; throw new InvalidOperationException("test"); }); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && File.ReadAllText(path) == unchanged, "修改回调失败不提交半成品", checks);
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var release = Task.Run(() => { Thread.Sleep(100); reader.Dispose(); });
                store.Update(p => p.Annotation.Color = "#13579b");
                release.GetAwaiter().GetResult();
            }
            Assert(store.Read().Annotation.Color == "#13579b", "短暂读取占用解除后原子保存成功", checks);
            var blocked = Path.Combine(run, "blocked-parent");
            File.WriteAllText(blocked, "This file prevents directory creation.");
            rejected = false;
            try { new PreferencesStore(Path.Combine(blocked, "settings.json")).Update(p => p.Microphone = false); } catch (IOException) { rejected = true; }
            Assert(rejected, "写入失败向调用方报错", checks);
            CheckToolbarPlacement(checks);
            CheckConcurrentProcesses(run, checks);
            CheckConfiguredDefaults(run, checks);
            CheckNoteExportPreference(run, checks);
            File.WriteAllText(Path.Combine(parent, "preferences-test-result.json"), JsonSerializer.Serialize(new { passed = true, checks, isolatedDirectory = run }, ProjectStore.JsonOptions));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(parent, "preferences-test-result.json"), JsonSerializer.Serialize(new { passed = false, checks, isolatedDirectory = run, error = ex.ToString() }, ProjectStore.JsonOptions));
            Environment.ExitCode = 1;
        }
    }

    private static void CheckConfiguredDefaults(string run,List<string> checks)
    {
        var path=Path.Combine(run,"configured-defaults.json");
        var store=new PreferencesStore(path);
        store.Update(p=>p.Toolbar=new ToolbarPlacement { XFraction=.8,Dock="Top" });
        var settings=store.Read();
        settings.RememberChoices=false;settings.ScreenshotDelay=3;settings.RecordingDelay=10;
        settings.Mp4FramesPerSecond=30;settings.Mp4MaxWidth=1920;settings.GifFramesPerSecond=15;settings.GifMaxWidth=960;
        settings.Hotkeys.Screenshot="Ctrl+Shift+F9";settings.Hotkeys.Record="";
        store.Update(p=>p.Toolbar!.XFraction=.3);
        store.ApplyConfiguration(settings);
        var restored=new PreferencesStore(path).Read();
        Assert(!restored.RememberChoices&&restored.ScreenshotDelay==3&&restored.RecordingDelay==10&&restored.Mp4FramesPerSecond==30&&restored.Mp4MaxWidth==1920&&restored.GifFramesPerSecond==15&&restored.GifMaxWidth==960&&restored.Hotkeys.Screenshot=="Ctrl+Shift+F9"&&restored.Hotkeys.Record=="", "默认延时媒体参数与禁用快捷键重启恢复",checks);
        Assert(restored.Toolbar?.XFraction==.3,"保存设置草稿不会覆盖期间更新的操作栏位置",checks);
        var before=File.ReadAllText(path);
        var invoked=false;
        store.Remember(p=>{invoked=true;p.Annotation.Color="#123456";p.ExportMuted=true;p.ProjectParentDirectory=run;});
        Assert(!invoked&&File.ReadAllText(path)==before,"关闭记忆后操作不执行写回且不修改配置文件",checks);
        restored.Annotation.FontSize=64;store.ApplyConfiguration(restored);
        Assert(store.Read().Annotation.FontSize==64&&!store.Read().RememberChoices,"关闭记忆仍可显式修改默认配置",checks);
        store.Update(p=>p.RememberChoices=true);
        store.Remember(p=>{p.Annotation.Color="#abcdef";p.Microphone=false;});
        var learned=store.Read();
        Assert(learned.Annotation.Color=="#abcdef"&&!learned.Microphone&&learned.ScreenshotDelay==3&&learned.RecordingDelay==10,"重新开启记忆只更新操作字段且保留默认延时",checks);
        File.WriteAllText(path,"""{"screenshotDelay":-1,"recordingDelay":300,"mp4FramesPerSecond":1000,"mp4MaxWidth":1,"gifFramesPerSecond":0,"gifMaxWidth":-1,"hotkeys":{"record":"","screenshot":22},"rememberChoices":false,"annotation":{"color":"#123456"}}""");
        var invalid=store.Read();
        Assert(invalid.ScreenshotDelay==0&&invalid.RecordingDelay==0&&invalid.Mp4FramesPerSecond==20&&invalid.Mp4MaxWidth==0&&invalid.GifFramesPerSecond==12&&invalid.GifMaxWidth==1440&&invalid.Hotkeys.Record==""&&invalid.Hotkeys.Screenshot=="Ctrl+Alt+S"&&!invalid.RememberChoices&&invalid.Annotation.Color=="#123456","配置逐字段容错兼容且不丢合法选择",checks);
    }

    private static void CheckNoteExportPreference(string run,List<string> checks)
    {
        var path=Path.Combine(run,"note-export-settings.json");
        var store=new PreferencesStore(path);
        Assert(store.Read().NoteExportFormat=="word","旧版配置默认导出Word",checks);
        var settings=store.Read();
        store.Remember(p=>p.NoteExportFormat="longimage");
        Assert(new PreferencesStore(path).Read().NoteExportFormat=="longimage","文档导出格式跨实例恢复",checks);
        store.ApplyConfiguration(settings);
        Assert(store.Read().NoteExportFormat=="longimage","设置窗口旧草稿不覆盖已记忆导出格式",checks);
        store.Update(p=>p.RememberChoices=false);
        store.Remember(p=>p.NoteExportFormat="html");
        Assert(store.Read().NoteExportFormat=="longimage","禁用记忆不保存临时文档格式",checks);
        store.Update(p=>p.NoteExportFormat="unknown");
        Assert(store.Read().NoteExportFormat=="word","未知文档格式安全回退Word",checks);
    }

    private static void CheckToolbarPlacement(List<string> checks)
    {
        var negativeArea = new Rectangle(-1920, -100, 1920, 1040);
        var original = new Rectangle(-1470, 250, 330, 76);
        var placement = ToolbarForm.PlacementFromBounds(original, negativeArea, "left-screen", "None");
        Assert(ToolbarForm.PlacementBounds(placement, negativeArea, original.Size) == original && placement.ScreenDevice == "left-screen", "负坐标显示器位置往返不偏移", checks);
        var newArea = new Rectangle(0, 0, 1280, 680);
        var changedSize = new Size(495, 114);
        var restored = ToolbarForm.PlacementBounds(placement, newArea, changedSize);
        Assert(newArea.Contains(restored) && Math.Abs((restored.X - newArea.Left) / (double)(newArea.Width - changedSize.Width) - placement.XFraction) < 0.002 && Math.Abs((restored.Y - newArea.Top) / (double)(newArea.Height - changedSize.Height) - placement.YFraction) < 0.002, "分辨率与DPI变化后保持相对位置且无越界", checks);
        placement.Dock = "Right";
        placement.XFraction = double.NaN;
        placement.YFraction = 20;
        var docked = ToolbarForm.PlacementBounds(placement, negativeArea, original.Size);
        Assert(negativeArea.Contains(docked) && docked.Right == negativeArea.Right && docked.Bottom == negativeArea.Bottom, "边缘停靠精确恢复且异常比例钳制在工作区", checks);
    }

    private static void CheckConcurrentProcesses(string parent, List<string> checks)
    {
        var path = Path.Combine(parent, "concurrent", "settings.json");
        var store = new PreferencesStore(path);
        store.Update(p => { p.Annotation.Color = "#2468ac"; p.Toolbar = new ToolbarPlacement { XFraction = 0.1, YFraction = 0.2 }; });
        using var x = StartWorker(parent, "x");
        using var y = StartWorker(parent, "y");
        if (!x.WaitForExit(20000) || !y.WaitForExit(20000)) throw new TimeoutException("跨进程偏好验收超时。");
        var result = store.Read();
        Assert(x.ExitCode == 0 && y.ExitCode == 0 && result.Toolbar is { } toolbar && Math.Abs(toolbar.XFraction - 0.6) < 1e-8 && Math.Abs(toolbar.YFraction - 0.7) < 1e-8 && result.Annotation.Color == "#2468ac", "两个真实进程各50次增量修改互不覆盖", checks);
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "原子写入后无临时文件遗留", checks);
    }

    private static Process StartWorker(string parent, string axis)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位验收程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(PreferencesSelfTest).Assembly.Location);
        foreach (var arg in new[] { "--preferences-test", parent, "--preferences-worker", axis }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动偏好验收子进程。");
    }

    private static void RunWorker(string parent, string axis)
    {
        try
        {
            var store = new PreferencesStore(Path.Combine(parent, "concurrent", "settings.json"));
            for (var i = 0; i < 50; i++)
            {
                store.Update(p => { p.Toolbar ??= new ToolbarPlacement(); if (axis == "x") p.Toolbar.XFraction += 0.01; else p.Toolbar.YFraction += 0.01; });
                Thread.Sleep(2);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(parent, "preferences-worker-" + axis + "-error.txt"), $"HRESULT 0x{ex.HResult:X8}\n{ex}");
            Environment.ExitCode = 1;
        }
    }

    private static void Assert(bool condition, string name, List<string> checks)
    { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
}
