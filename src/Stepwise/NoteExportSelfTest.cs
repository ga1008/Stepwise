using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stepwise.Core;

namespace Stepwise;

/// <summary>Runs the actual export service, including WebView2, on a nonactivating STA message pump.</summary>
internal sealed class NoteExportSelfTest : Form
{
    private readonly string outputRoot;
    private readonly string runRoot;
    private readonly List<CheckResult> results = [];
    private readonly Dictionary<string, ExportResult> exports = [];
    private readonly Stopwatch total = Stopwatch.StartNew();
    private string? projectRoot, longProjectRoot;
    private bool completed;
    private sealed record CheckResult(string Name, bool Passed, long Milliseconds, string? Error = null, object? Evidence = null);
    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000080; return value; } }

    public NoteExportSelfTest(string directory)
    {
        outputRoot = Path.GetFullPath(directory); Directory.CreateDirectory(outputRoot);
        runRoot = Path.Combine(outputRoot, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(runRoot);
        Text = "步录 · 文档导出验收"; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None; SetBounds(-30000, -30000, 320, 120);
        Shown += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { results.Add(new CheckResult("Test setup and runner", false, 0, ex.ToString())); }
            finally
            {
                completed = true; total.Stop(); WriteReport(); Environment.ExitCode = results.All(item => item.Passed) ? 0 : 1; Close();
            }
        };
    }

    private async Task RunAsync()
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "An STA UI pump is required.");
        var store = ProjectStore.Create(runRoot, "中文分享验收：图片、动图与录像"); projectRoot = store.Root;
        var png = store.ResolvePath("media/images/步骤截图.png");
        var gif = store.ResolvePath("media/gifs/动态过程.gif");
        var mp4 = store.ResolvePath("media/videos/录制过程.mp4");
        DrawImage(png);
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=size=480x270:rate=8", "-t", "1.25", "-c:v", "libx264", "-pix_fmt", "yuv420p", mp4);
        await FfmpegAsync("-i", mp4, "-vf", "fps=6,scale=320:-1", gif);
        await store.AddStepAsync(new StepRecord { Title = "选择项目并核对保存位置", Description = "完整保留中文说明与特殊字符：<标签> & \"引号\"。\n截图上的标注也应进入导出文件。", Kind = "image", MediaPath = "media/images/步骤截图.png", Width = 640, Height = 360, IsDraft = false });
        await store.AddStepAsync(new StepRecord { Title = "查看动态操作过程", Description = "HTML 保留动画；Word、PDF 与长图保留第一帧。", Kind = "gif", MediaPath = "media/gifs/动态过程.gif", Width = 320, Height = 180, DurationSeconds = 1.25, IsDraft = false });
        await store.AddStepAsync(new StepRecord { Title = "播放录像并准备分享", Description = "视频在 HTML 中可播放，静态文档使用首帧。", Kind = "video", MediaPath = "media/videos/录制过程.mp4", Width = 480, Height = 270, DurationSeconds = 1.25, Muted = true, IsDraft = true });
        var snapshot = await store.SnapshotAsync();
        var originals = new[] { png, gif, mp4, Path.Combine(store.Root, "project.json"), Path.Combine(store.Root, "document.html") }
            .ToDictionary(path => path, HashFile, StringComparer.OrdinalIgnoreCase);

        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            await CheckAsync($"Chinese mixed-media project exports as {format}", async () =>
            {
                var progress = new List<int>();
                var result = await NoteExportService.ExportAsync(store, snapshot, format, new InlineProgress(update => progress.Add(update.Percent)), CancellationToken.None);
                Assert(result.Files.Count == 1, "Small document should produce one file.");
                ValidateFile(result.Files.Single(), format);
                Assert(progress.Count > 0 && progress[^1] == 100, "Success should end at 100%.");
                Assert(progress.All(value => value is >= 0 and <= 100), "Progress must stay within bounds.");
                AssertNoPending(store); exports[format.ToString()] = result;
                return new { result.Directory, result.Files, length = new FileInfo(result.Files[0]).Length, progress };
            });
        }

        await CheckAsync("Exporting again allocates a new directory and preserves earlier output", async () =>
        {
            var previous = exports[ExportFormat.Html.ToString()]; var checksum = HashFile(previous.Files.Single());
            var result = await NoteExportService.ExportAsync(store, snapshot, ExportFormat.Html, null, CancellationToken.None);
            Assert(!string.Equals(previous.Directory, result.Directory, StringComparison.OrdinalIgnoreCase), "A repeated export reused a directory.");
            Assert(checksum == HashFile(previous.Files.Single()), "Earlier export changed."); AssertNoPending(store);
            return new { previous = previous.Directory, current = result.Directory };
        });

        var empty = ProjectStore.Create(runRoot, "空项目也能分享");
        foreach (var format in Enum.GetValues<ExportFormat>())
            await CheckAsync($"Empty project exports a valid {format} document", async () =>
            {
                var result = await NoteExportService.ExportAsync(empty, await empty.SnapshotAsync(), format, null, CancellationToken.None);
                Assert(result.Files.Count == 1, "Empty project should produce one file."); ValidateFile(result.Files[0], format); AssertNoPending(empty);
                return new { result.Directory, result.Files };
            });

        foreach (var format in Enum.GetValues<ExportFormat>())
            await CheckAsync($"Pre-cancelled {format} export publishes no files", async () =>
            {
                var before = CompletedDirectories(store);
                using var source = new CancellationTokenSource(); source.Cancel();
                await ExpectCancellationAsync(() => NoteExportService.ExportAsync(store, snapshot, format, null, source.Token));
                AssertUnchangedDirectories(store, before); AssertNoPending(store); return null;
            });

        await CheckAsync("Cancelling while FFmpeg prepares frames cleans the entire staging job", async () =>
        {
            var before = CompletedDirectories(store); using var source = new CancellationTokenSource(); var triggered = false;
            var progress = new InlineProgress(update => { if (!triggered && update.Message.StartsWith("准备素材")) { triggered = true; source.CancelAfter(25); } });
            await ExpectCancellationAsync(() => NoteExportService.ExportAsync(store, snapshot, ExportFormat.Word, progress, source.Token));
            Assert(triggered, "The preparation stage was not reached."); AssertUnchangedDirectories(store, before); AssertNoPending(store); return null;
        });

        await CheckAsync("Missing media fails without a partially published document", async () =>
        {
            var before = CompletedDirectories(store); var broken = await store.SnapshotAsync(); broken.Steps[1].MediaPath = "media/images/不存在的图片.png";
            var failed = false;
            try { await NoteExportService.ExportAsync(store, broken, ExportFormat.Html, null, CancellationToken.None); }
            catch (FileNotFoundException) { failed = true; }
            Assert(failed, "Missing media should fail explicitly."); AssertUnchangedDirectories(store, before); AssertNoPending(store); return null;
        });

        var longStore = ProjectStore.Create(runRoot, "长说明自动分片验收"); longProjectRoot = longStore.Root;
        var longPng = longStore.ResolvePath("media/images/完整截图.png"); File.Copy(png, longPng);
        var paragraph = string.Concat(Enumerable.Repeat("连续长说明用于检查跨图阅读，图片分片不得遗漏文字或产生重叠。", 950));
        await longStore.AddStepAsync(new StepRecord { Title = "一段远超单张图片高度的说明", Description = paragraph, Kind = "image", MediaPath = "media/images/完整截图.png", Width = 640, Height = 360, IsDraft = false });
        await longStore.AddStepAsync(new StepRecord { Title = "末尾步骤：完成标记 END OF DOCUMENT", Description = "此步骤应完整出现在最后一张长图中。", Kind = "image", MediaPath = "media/images/完整截图.png", Width = 640, Height = 360, IsDraft = false });
        var longSnapshot = await longStore.SnapshotAsync();

        await CheckAsync("Very long paragraph produces sequential PNGs with bounded dimensions", async () =>
        {
            var result = await NoteExportService.ExportAsync(longStore, longSnapshot, ExportFormat.LongImage, null, CancellationToken.None);
            Assert(result.Files.Count >= 2, "Long document was not split.");
            var dimensions = result.Files.Select((path, index) =>
            {
                using var image = Image.FromFile(path);
                Assert(image.Width == 1440 && image.Height is > 0 and <= 12000, "PNG dimensions exceeded the configured bounds.");
                Assert(Path.GetFileNameWithoutExtension(path).EndsWith($"-{index + 1:D3}"), "PNG files are not numbered consecutively.");
                return new { path, image.Width, image.Height, bytes = new FileInfo(path).Length };
            }).ToArray();
            AssertNoPending(longStore); exports["LongParagraph"] = result;
            return new { result.Directory, dimensions };
        });

        await CheckAsync("Cancelling after the first PNG removes every incomplete image", async () =>
        {
            var before = CompletedDirectories(longStore); using var source = new CancellationTokenSource(); var reachedSecond = false; var sawPartialPng = false;
            var progress = new InlineProgress(update =>
            {
                if (!update.Message.Contains("绘制长图 2")) return;
                reachedSecond = true;
                sawPartialPng = PendingDirectories(longStore).Any(dir => Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).Any(path => Path.GetDirectoryName(path)!.EndsWith("output")));
                source.Cancel();
            });
            await ExpectCancellationAsync(() => NoteExportService.ExportAsync(longStore, longSnapshot, ExportFormat.LongImage, progress, source.Token));
            Assert(reachedSecond && sawPartialPng, "Cancellation did not cover a partially written multi-image export.");
            AssertUnchangedDirectories(longStore, before); AssertNoPending(longStore); await Task.Delay(400); AssertNoPending(longStore);
            return new { reachedSecond, sawPartialPng };
        });

        await CheckAsync("Cancelling during PDF printing stops publication and releases staging files", async () =>
        {
            var before = CompletedDirectories(longStore); using var source = new CancellationTokenSource(); var printing = false;
            var progress = new InlineProgress(update => { if (update.Message.Contains("排版 PDF")) { printing = true; source.CancelAfter(20); } });
            await ExpectCancellationAsync(() => NoteExportService.ExportAsync(longStore, longSnapshot, ExportFormat.Pdf, progress, source.Token));
            Assert(printing, "PDF printing stage was not reached."); AssertUnchangedDirectories(longStore, before); AssertNoPending(longStore);
            await Task.Delay(1200); AssertUnchangedDirectories(longStore, before); AssertNoPending(longStore);
            return new { printing, checkedAgainAfterMilliseconds = 1200 };
        });

        await CheckAsync("Source media, project JSON and working document remain byte-for-byte unchanged", () =>
        {
            foreach (var (path, hash) in originals) Assert(File.Exists(path) && hash == HashFile(path), "Source changed: " + path);
            return Task.FromResult<object?>(originals.Select(item => new { path = item.Key, sha256 = item.Value }).ToArray());
        });
    }

    private async Task CheckAsync(string name, Func<Task<object?>> run)
    {
        var timer = Stopwatch.StartNew();
        try { var evidence = await run(); results.Add(new CheckResult(name, true, timer.ElapsedMilliseconds, Evidence: evidence)); }
        catch (Exception ex) { results.Add(new CheckResult(name, false, timer.ElapsedMilliseconds, ex.ToString())); }
        WriteReport();
    }

    private void WriteReport()
    {
        var report = new { completed, passed = completed && results.All(item => item.Passed), total = results.Count, elapsedMilliseconds = total.ElapsedMilliseconds,
            runRoot, projectRoot, longProjectRoot, exports, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(outputRoot, "note-export-test-result.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(runRoot, "result.json"), json, new UTF8Encoding(false));
    }

    private static void ValidateFile(string path, ExportFormat format)
    {
        Assert(new FileInfo(path).Length > 100, "Output is unexpectedly empty.");
        switch (format)
        {
            case ExportFormat.Word:
                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert(zip.GetEntry("word/document.xml") != null && zip.GetEntry("[Content_Types].xml") != null, "Invalid DOCX package.");
                    using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open()); var xml = reader.ReadToEnd();
                    Assert(xml.Contains("操作") || xml.Contains("笔记") || xml.Contains("空项目"), "Word document text is missing.");
                }
                break;
            case ExportFormat.Html:
                var html = File.ReadAllText(path);
                Assert(html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) && html.Contains("asset-map"), "Invalid standalone HTML.");
                Assert(!html.Contains("https://project.stepwise.local/"), "Export depends on the private project media host.");
                break;
            case ExportFormat.LongImage:
                using (var image = Image.FromFile(path)) Assert(image.Width == 1440 && image.Height > 100 && image.Height <= 12000, "Invalid PNG dimensions.");
                break;
            case ExportFormat.Pdf:
                using (var stream = File.OpenRead(path)) { var header = new byte[5]; stream.ReadExactly(header); Assert(Encoding.ASCII.GetString(header) == "%PDF-", "Invalid PDF header."); }
                break;
        }
    }

    private static async Task ExpectCancellationAsync(Func<Task<ExportResult>> run)
    {
        var cancelled = false; try { await run(); } catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "The export did not observe cancellation.");
    }
    private static string[] PendingDirectories(ProjectStore store)
    {
        var root = Path.Combine(store.Root, "exports");
        return Directory.Exists(root) ? Directory.GetDirectories(root, ".pending-*") : [];
    }
    private static string[] CompletedDirectories(ProjectStore store)
    {
        var root = Path.Combine(store.Root, "exports");
        return Directory.Exists(root) ? Directory.GetDirectories(root).Where(path => !Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal)).Order().ToArray() : [];
    }
    private static void AssertNoPending(ProjectStore store) => Assert(PendingDirectories(store).Length == 0, "Staging directories remain after completion/cancellation.");
    private static void AssertUnchangedDirectories(ProjectStore store, string[] before) => Assert(before.SequenceEqual(CompletedDirectories(store)), "A failed/cancelled export published a directory.");
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static void DrawImage(string path)
    {
        using var bitmap = new Bitmap(640, 360); using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(248, 250, 244));
        using var teal = new SolidBrush(Color.FromArgb(40, 123, 100)); using var ink = new SolidBrush(Color.FromArgb(35, 66, 54));
        graphics.FillRectangle(teal, 0, 0, 640, 64);
        using var title = new Font("Microsoft YaHei UI", 22, FontStyle.Bold); using var body = new Font("Microsoft YaHei UI", 14);
        graphics.DrawString("截图与标注 · 导出验收", title, Brushes.White, 23, 13);
        graphics.DrawString("步骤 01   选择项目文件夹", title, ink, 28, 106);
        graphics.DrawString("中文路径、图片比例和颜色应该完整保留。", body, ink, 28, 174);
        using var marker = new Pen(Color.FromArgb(231, 94, 74), 6); graphics.DrawRectangle(marker, 25, 237, 558, 71);
        graphics.DrawString("重要设置：导出后直接分享", body, teal, 43, 259);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static async Task FfmpegAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments)) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start bundled FFmpeg.");
        var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } await process.WaitForExitAsync(); throw; }
        await stdout; var text = await error;
        Assert(process.ExitCode == 0, "Test media preparation failed: " + text);
    }
}
