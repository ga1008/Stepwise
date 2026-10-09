using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Stepwise.Core;

namespace Stepwise;

/// <summary>Real OOXML package checks; visual pagination is verified separately in Word/LibreOffice.</summary>
internal static class WordExportSelfTest
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static int Run(string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var checks = new List<string>();
        var runRoot = Path.Combine(outputRoot, "word-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(runRoot);
        var samplePath = Path.Combine(runRoot, "word-export-sample.docx");
        try
        {
            var landscape = Path.Combine(runRoot, "预览_横屏.png");
            var portrait = Path.Combine(runRoot, "预览_竖屏.png");
            DrawPoster(landscape, 1200, 680, false); DrawPoster(portrait, 560, 1400, true);
            var project = new ProjectData
            {
                Title = "从桌面操作到清晰笔记：项目配置与分享记录",
                CreatedAt = new DateTimeOffset(2026, 10, 9, 9, 30, 0, TimeSpan.FromHours(8)),
                UpdatedAt = new DateTimeOffset(2026, 10, 9, 10, 15, 0, TimeSpan.FromHours(8))
            };
            var explanation = "选择项目文件夹后，输入本次操作的名称。所有截图、录制和导出文件均保存在项目目录中。\r\n\r\n核对中文路径：笔记 / 示例。保留符号 <、>、&、\"，以及表情 ✅ 🙂。\n确认后开始记录。";
            var longExplanation = string.Join("\n", Enumerable.Range(1, 10).Select(i =>
                $"{i:D2}　检查设置与预览。记录关键的点击位置、输入内容和实际结果，让读者能够在自己的环境中复现。长说明可以自然跨页，标题、正文和截图保持清楚的层次，打印时也能完整阅读。"));
            project.Steps =
            [
                new StepRecord { Number = 1, Title = "新建项目并检查保存位置", Description = explanation, Kind = "image", IsDraft = false },
                new StepRecord { Number = 2, Title = "检查默认配置、截图范围与导出选项，确保较长的步骤标题能够自然换行并保持编号清晰", Description = longExplanation, Kind = "gif", DurationSeconds = 6, IsDraft = false },
                new StepRecord { Number = 3, Title = "核对竖屏截图", Description = "竖屏图片按比例缩放，不裁切关键内容。", Kind = "image", IsDraft = false },
                new StepRecord { Number = 4, Title = "预览录制结果并分享", Description = "Word 文档展示视频的第一帧。需要播放完整过程时，可导出单页 HTML。", Kind = "video", DurationSeconds = 75, IsDraft = true }
            ];
            var steps = project.Steps.Select((step, i) => new ExportStep(step,
                i == 3 ? Path.Combine(runRoot, "recording.mp4") : i == 1 ? Path.Combine(runRoot, "animation.gif") : landscape,
                i == 2 ? portrait : landscape, i == 2 ? 560 : 1200, i == 2 ? 1400 : 680)).ToArray();
            var snapshot = new ExportSnapshot(project, steps);
            var progress = new InlineProgress();
            WordNoteExporter.ExportAsync(snapshot, samplePath, progress, CancellationToken.None).GetAwaiter().GetResult();
            using (var package = ZipFile.OpenRead(samplePath))
            {
                Check(package.Entries.Count == 14, "OOXML 核心部件与四张内嵌图片完整", checks);
                foreach (var part in package.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.Ordinal) || x.FullName.EndsWith(".rels", StringComparison.Ordinal)))
                { using var stream = part.Open(); XDocument.Load(stream); }
                Check(true, "所有 XML 部件编码与语法合法", checks);
                var doc = ReadXml(package, "word/document.xml");
                var allText = string.Concat(doc.Descendants(W + "t").Select(x => x.Value));
                Check(allText.Contains(project.Title) && project.Steps.All(s => allText.Contains(s.Title))
                    && allText.Contains("<、>、&、\"") && allText.Contains("✅") && allText.Contains("🙂"), "标题、中文、符号及补充平面字符无损写入", checks);
                var expectedBreaks = project.Steps.Sum(s => s.Description.Replace("\r\n", "\n").Replace('\r', '\n').Count(c => c == '\n'));
                Check(doc.Descendants(W + "br").Count() == expectedBreaks, "说明换行和空行保留", checks);
                Check(allText.Contains("GIF 动图 · 仅保留第一帧") && allText.Contains("屏幕录制 · 仅保留第一帧")
                    && allText.Contains("1:15") && allText.Contains("待补充"), "动态媒体首帧、时长与草稿状态说明准确", checks);
                var relationships = ReadXml(package, "word/_rels/document.xml.rels").Root!.Elements(PR + "Relationship").ToArray();
                Check(relationships.All(rel => rel.Attribute("TargetMode") == null)
                    && relationships.All(rel => package.GetEntry("word/" + rel.Attribute("Target")!.Value) != null), "所有文档引用均为包内有效资源", checks);
                var imageRels = relationships.Where(x => x.Attribute("Type")!.Value.EndsWith("/image", StringComparison.Ordinal)).ToArray();
                var imageRefs = doc.Descendants(A + "blip").Select(x => x.Attribute(R + "embed")!.Value).ToArray();
                Check(imageRels.Length == steps.Length && imageRefs.Distinct().Count() == steps.Length
                    && imageRefs.All(id => imageRels.Any(r => r.Attribute("Id")!.Value == id)), "每一步的图片引用独立且可解析", checks);
                for (var i = 0; i < steps.Length; i++)
                {
                    using var original = File.OpenRead(steps[i].PosterPath);
                    using var embedded = package.GetEntry($"word/media/step-{i + 1}.png")!.Open();
                    if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(embedded))) throw new InvalidDataException("嵌入图片内容改变。");
                }
                Check(true, "图片流式嵌入且字节完整", checks);
                var extents = doc.Descendants(WP + "extent").ToArray();
                Check(extents.Length == steps.Length && extents.Select((element, i) =>
                {
                    var cx = (long)element.Attribute("cx")!; var cy = (long)element.Attribute("cy")!;
                    return cx > 0 && cy > 0 && cx <= 6.5 * 914400 && cy <= 3.25 * 914400
                        && Math.Abs((double)cx / cy - (double)steps[i].PosterWidth / steps[i].PosterHeight) < .00001;
                }).All(x => x), "横屏与竖屏图片保持比例且不超出页面", checks);
                var section = doc.Descendants(W + "sectPr").Single();
                Check((string?)section.Element(W + "pgSz")?.Attribute(W + "w") == "12240"
                    && (string?)section.Element(W + "pgSz")?.Attribute(W + "h") == "15840"
                    && (string?)section.Element(W + "pgMar")?.Attribute(W + "left") == "1440", "Letter 页面与一英寸页边距明确", checks);
                var styles = ReadXml(package, "word/styles.xml");
                var body = styles.Descendants(W + "style").Single(x => (string?)x.Attribute(W + "styleId") == "StepBody");
                var heading = styles.Descendants(W + "style").Single(x => (string?)x.Attribute(W + "styleId") == "StepHeading");
                Check((string?)body.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val") == "22"
                    && (string?)body.Element(W + "pPr")?.Element(W + "keepLines")?.Attribute(W + "val") == "0"
                    && heading.Element(W + "pPr")?.Element(W + "keepNext") != null, "11pt 正文允许跨页且步骤标题避免孤行", checks);
                var footer = ReadXml(package, "word/footer1.xml");
                Check(footer.Descendants(W + "fldSimple").Select(x => (string?)x.Attribute(W + "instr")).SequenceEqual(new[] { "PAGE", "NUMPAGES" })
                    && section.Element(W + "headerReference") != null && section.Element(W + "footerReference") != null, "页眉、页脚和可更新页码已连接", checks);
            }
            Check(progress.Values.Count == 4 && progress.Values.Select(x => x.Percent).SequenceEqual(progress.Values.Select(x => x.Percent).Order()), "导出进度持续递增", checks);
            var empty = Path.Combine(runRoot, "empty.docx");
            WordNoteExporter.ExportAsync(new ExportSnapshot(new ProjectData { Title = "空笔记" }, []), empty, null, CancellationToken.None).GetAwaiter().GetResult();
            using (var zip = ZipFile.OpenRead(empty)) Check(ReadXml(zip, "word/document.xml").Root!.Value.Contains("尚未添加步骤"), "空项目生成有效文档", checks);
            var cancelledPath = Path.Combine(runRoot, "cancelled.docx"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Expect<OperationCanceledException>(() => WordNoteExporter.ExportAsync(snapshot, cancelledPath, null, cancelled.Token).GetAwaiter().GetResult());
            Check(!File.Exists(cancelledPath), "预取消不产生文件", checks);
            var invalid = new ExportSnapshot(project, [steps[0] with { PosterWidth = 0 }]);
            var invalidPath = Path.Combine(runRoot, "invalid.docx");
            Expect<InvalidDataException>(() => WordNoteExporter.ExportAsync(invalid, invalidPath, null, CancellationToken.None).GetAwaiter().GetResult());
            Check(!File.Exists(invalidPath), "非法图片尺寸在写入前拒绝", checks);
            invalid = new ExportSnapshot(project, [steps[0] with { PosterWidth = 1199 }]);
            Expect<InvalidDataException>(() => WordNoteExporter.ExportAsync(invalid, invalidPath, null, CancellationToken.None).GetAwaiter().GetResult());
            Check(!File.Exists(invalidPath), "PNG 真实尺寸与元数据必须相符", checks);
            var before = SHA256.HashData(File.ReadAllBytes(samplePath));
            Expect<IOException>(() => WordNoteExporter.ExportAsync(snapshot, samplePath, null, CancellationToken.None).GetAwaiter().GetResult());
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(samplePath))), "已有目标文件不会被导出器覆盖", checks);
            File.WriteAllText(Path.Combine(outputRoot, "word-export-test-result.json"), JsonSerializer.Serialize(
                new { passed = true, checks, samplePath, runRoot, visualReviewRequired = true }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputRoot, "word-export-test-result.json"), JsonSerializer.Serialize(
                new { passed = false, checks, samplePath, runRoot, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static void DrawPoster(string path, int width, int height, bool portrait)
    {
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(240, 245, 241));
        using var ink = new SolidBrush(Color.FromArgb(36, 55, 51));
        using var muted = new SolidBrush(Color.FromArgb(106, 127, 115));
        using var accent = new SolidBrush(Color.FromArgb(34, 122, 105));
        using var white = new SolidBrush(Color.White);
        using var title = new Font("Microsoft YaHei", portrait ? 30 : 38, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Microsoft YaHei", portrait ? 20 : 24, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillRectangle(white, 30, 30, width - 60, height - 60);
        graphics.FillRectangle(accent, 30, 30, width - 60, 10);
        graphics.DrawString("Stepwise / 步录", title, ink, 64, 80);
        graphics.DrawString(portrait ? "竖屏预览 · 完整画面" : "每一步操作，都值得清楚记录。", body, muted, 64, 145);
        for (var i = 0; i < (portrait ? 6 : 3); i++)
        {
            var y = 230 + i * (portrait ? 160 : 115);
            graphics.FillEllipse(accent, 65, y, 42, 42);
            graphics.DrawString((i + 1).ToString(), body, white, new RectangleF(65, y, 42, 42), numberFormat);
            graphics.DrawString(new[] { "选择项目目录", "截图并添加说明", "核对导出结果" }[i % 3], body, ink, 130, y + 4);
            graphics.DrawString("保存关键步骤与实际结果", body, muted, 130, y + 48);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    { using var stream = zip.GetEntry(path)!.Open(); return XDocument.Load(stream); }
    private static void Check(bool value, string name, List<string> checks)
    { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"预期异常 {typeof(T).Name} 未抛出。"); }
    private sealed class InlineProgress : IProgress<ExportProgress>
    {
        public List<ExportProgress> Values { get; } = [];
        public void Report(ExportProgress value) => Values.Add(value);
    }
}
