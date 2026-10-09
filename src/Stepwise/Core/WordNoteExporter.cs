using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Stepwise.Core;

/// <summary>Produces editable WordprocessingML without requiring Microsoft Office or WPS.</summary>
public static class WordNoteExporter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const long EmuPerInch = 914400;
    // Keep a screenshot and its caption together, while leaving room for normal step prose.
    private const double MaxImageWidth = 6.5;
    private const double MaxImageHeight = 3.25;

    public static Task ExportAsync(ExportSnapshot snapshot, string destination,
        IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(() => WriteAsync(snapshot, destination, progress, ct), ct);

    private static async Task WriteAsync(ExportSnapshot snapshot, string destination,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ct.ThrowIfCancellationRequested();
        foreach (var step in snapshot.Steps)
        {
            ct.ThrowIfCancellationRequested();
            await ValidatePosterAsync(step, ct).ConfigureAwait(false);
        }

        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        WriteXml(package, "[Content_Types].xml", x => WriteContentTypes(x));
        WriteXml(package, "_rels/.rels", x => WritePackageRelationships(x));
        WriteXml(package, "docProps/core.xml", x => WriteCoreProperties(x, snapshot.Project));
        WriteXml(package, "docProps/app.xml", x => WriteApplicationProperties(x));
        WriteXml(package, "word/styles.xml", WriteStyles);
        WriteXml(package, "word/settings.xml", WriteSettings);
        WriteXml(package, "word/header1.xml", WriteHeader);
        WriteXml(package, "word/footer1.xml", WriteFooter);
        WriteXml(package, "word/_rels/document.xml.rels", x => WriteDocumentRelationships(x, snapshot.Steps.Count));
        WriteXml(package, "word/document.xml", x => WriteDocument(x, snapshot, ct));

        for (var i = 0; i < snapshot.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = package.CreateEntry($"word/media/step-{i + 1}.png", CompressionLevel.NoCompression);
            await using var input = new FileStream(snapshot.Steps[i].PosterPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var media = entry.Open();
            await input.CopyToAsync(media, 65536, ct).ConfigureAwait(false);
            progress?.Report(new ExportProgress(15 + (i + 1) * 80 / Math.Max(1, snapshot.Steps.Count),
                $"整理 Word · {i + 1} / {snapshot.Steps.Count}"));
        }
        ct.ThrowIfCancellationRequested();
    }

    private static async Task ValidatePosterAsync(ExportStep step, CancellationToken ct)
    {
        if (step.PosterWidth <= 0 || step.PosterHeight <= 0)
            throw new InvalidDataException($"步骤 {step.Step.Number} 的图片尺寸无效。");
        await using var input = new FileStream(step.PosterPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        var header = new byte[24];
        try { await input.ReadExactlyAsync(header, ct).ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("步骤预览图不完整。", ex); }
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header.AsSpan(0, 8).SequenceEqual(signature)
            || !header.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) != step.PosterWidth
            || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) != step.PosterHeight)
            throw new InvalidDataException($"步骤 {step.Step.Number} 的 PNG 预览图或尺寸无效。");
    }

    private static void WriteXml(ZipArchive package, string path, Action<XmlWriter> write)
    {
        using var stream = package.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var x = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false,
            CheckCharacters = true, OmitXmlDeclaration = false
        });
        x.WriteStartDocument();
        write(x);
        x.WriteEndDocument();
    }

    private static void WriteContentTypes(XmlWriter x)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        x.WriteStartElement("Types", ns);
        Default("rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default("xml", "application/xml"); Default("png", "image/png");
        Override("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        Override("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
        Override("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml");
        Override("/word/header1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml");
        Override("/word/footer1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml");
        Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        x.WriteEndElement();
        void Default(string ext, string type)
        { x.WriteStartElement("Default", ns); x.WriteAttributeString("Extension", ext); x.WriteAttributeString("ContentType", type); x.WriteEndElement(); }
        void Override(string path, string type)
        { x.WriteStartElement("Override", ns); x.WriteAttributeString("PartName", path); x.WriteAttributeString("ContentType", type); x.WriteEndElement(); }
    }

    private static void WritePackageRelationships(XmlWriter x)
    {
        x.WriteStartElement("Relationships", PR);
        Relationship(x, "rDocument", R + "/officeDocument", "word/document.xml");
        Relationship(x, "rCore", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Relationship(x, "rApp", R + "/extended-properties", "docProps/app.xml");
        x.WriteEndElement();
    }

    private static void WriteDocumentRelationships(XmlWriter x, int count)
    {
        x.WriteStartElement("Relationships", PR);
        Relationship(x, "rStyles", R + "/styles", "styles.xml");
        Relationship(x, "rSettings", R + "/settings", "settings.xml");
        Relationship(x, "rHeader", R + "/header", "header1.xml");
        Relationship(x, "rFooter", R + "/footer", "footer1.xml");
        for (var i = 0; i < count; i++) Relationship(x, $"rImage{i + 1}", R + "/image", $"media/step-{i + 1}.png");
        x.WriteEndElement();
    }

    private static void Relationship(XmlWriter x, string id, string type, string target)
    {
        x.WriteStartElement("Relationship", PR);
        x.WriteAttributeString("Id", id); x.WriteAttributeString("Type", type); x.WriteAttributeString("Target", target);
        x.WriteEndElement();
    }

    private static void WriteCoreProperties(XmlWriter x, ProjectData project)
    {
        const string cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string terms = "http://purl.org/dc/terms/";
        x.WriteStartElement("cp", "coreProperties", cp);
        x.WriteElementString("dc", "title", dc, CleanText(project.Title));
        x.WriteElementString("dc", "creator", dc, "步录 Stepwise");
        x.WriteElementString("cp", "lastModifiedBy", cp, "步录 Stepwise");
        x.WriteElementString("dc", "description", dc, "操作步骤笔记；动态图像与视频以首帧展示。");
        Date("created", project.CreatedAt); Date("modified", project.UpdatedAt);
        x.WriteEndElement();
        void Date(string name, DateTimeOffset value)
        {
            x.WriteStartElement("dcterms", name, terms);
            x.WriteAttributeString("xmlns", "dcterms", null, terms);
            x.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "dcterms:W3CDTF");
            x.WriteString(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)); x.WriteEndElement();
        }
    }

    private static void WriteApplicationProperties(XmlWriter x)
    {
        const string ns = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        x.WriteStartElement("Properties", ns); x.WriteElementString("Application", ns, "Stepwise");
        x.WriteElementString("AppVersion", ns, "1.0"); x.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter x)
    {
        Start(x, "styles"); Start(x, "docDefaults"); Start(x, "rPrDefault"); Start(x, "rPr");
        Fonts(x); Val(x, "color", "243733"); Val(x, "sz", "22"); Val(x, "szCs", "22");
        Start(x, "lang"); Attr(x, "val", "zh-CN"); Attr(x, "eastAsia", "zh-CN"); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement(); Start(x, "pPrDefault"); Start(x, "pPr");
        Val(x, "widowControl", "1"); Spacing(x, 0, 140, 340, "atLeast"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        Style("Normal", "Normal", 22, "243733", false, 0, 140, 340);
        Style("Title", "Title", 44, "000000", true, 120, 200, 600, keepNext: true);
        Style("Subtitle", "Subtitle", 19, "000000", false, 0, 260, 280, keepNext: true);
        Style("StepHeading", "Heading 1", 32, "000000", true, 280, 120, 440, keepNext: true, outline: true);
        Style("StepBody", "Body Text", 22, "38483F", false, 0, 180, 340);
        Style("Caption", "Caption", 18, "708177", false, 90, 240, 270);
        Style("Small", "Small", 17, "708177", false, 0, 100, 260);
        x.WriteEndElement();

        void Style(string id, string name, int size, string color, bool bold, int before, int after, int line,
            bool keepNext = false, bool outline = false)
        {
            Start(x, "style"); Attr(x, "type", "paragraph"); Attr(x, "styleId", id);
            if (id == "Normal") Attr(x, "default", "1");
            Val(x, "name", name); if (id != "Normal") Val(x, "basedOn", "Normal");
            Val(x, "next", "Normal"); Start(x, "qFormat"); x.WriteEndElement();
            Start(x, "pPr"); if (keepNext) Val(x, "keepNext", "1");
            // Do not keep an arbitrarily long explanation on one page.
            Val(x, "keepLines", "0"); Val(x, "widowControl", "1");
            Spacing(x, before, after, line, "atLeast"); if (outline) Val(x, "outlineLvl", "0"); x.WriteEndElement();
            Start(x, "rPr"); Fonts(x); if (bold) { Val(x, "b", "1"); Val(x, "bCs", "1"); }
            Val(x, "color", color); Val(x, "sz", size.ToString(CultureInfo.InvariantCulture));
            Val(x, "szCs", size.ToString(CultureInfo.InvariantCulture)); x.WriteEndElement(); x.WriteEndElement();
        }
    }

    private static void WriteSettings(XmlWriter x)
    {
        Start(x, "settings"); Val(x, "defaultTabStop", "420"); Val(x, "updateFields", "true");
        Start(x, "compat"); Start(x, "compatSetting"); Attr(x, "name", "compatibilityMode");
        Attr(x, "uri", "http://schemas.microsoft.com/office/word"); Attr(x, "val", "15");
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }

    private static void WriteHeader(XmlWriter x)
    {
        Start(x, "hdr"); Start(x, "p"); Start(x, "pPr"); Val(x, "pStyle", "Small");
        Start(x, "pBdr"); Border(x, "bottom", "E2E8E2", "4", "7"); x.WriteEndElement(); x.WriteEndElement();
        Run(x, "步录  /  STEPWISE", "000000", true); x.WriteEndElement(); x.WriteEndElement();
    }

    private static void WriteFooter(XmlWriter x)
    {
        Start(x, "ftr"); Start(x, "p"); Start(x, "pPr"); Val(x, "pStyle", "Small");
        Start(x, "pBdr"); Border(x, "top", "E2E8E2", "4", "8"); x.WriteEndElement();
        Start(x, "tabs"); Start(x, "tab"); Attr(x, "val", "right"); Attr(x, "pos", "9360");
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        Run(x, "操作笔记"); Start(x, "r"); Start(x, "tab"); x.WriteEndElement(); x.WriteEndElement();
        Run(x, "第 "); Field(x, "PAGE"); Run(x, " 页 / 共 "); Field(x, "NUMPAGES"); Run(x, " 页");
        x.WriteEndElement(); x.WriteEndElement();
    }

    private static void WriteDocument(XmlWriter x, ExportSnapshot snapshot, CancellationToken ct)
    {
        Start(x, "document"); x.WriteAttributeString("xmlns", "r", null, R);
        x.WriteAttributeString("xmlns", "wp", null, WP); x.WriteAttributeString("xmlns", "a", null, A);
        x.WriteAttributeString("xmlns", "pic", null, PIC); Start(x, "body");
        Paragraph(x, "Small", "操作记录 / PROCESS NOTES", "227A69");
        Paragraph(x, "Title", snapshot.Project.Title);
        Paragraph(x, "Subtitle", $"{snapshot.Steps.Count} 个步骤  ·  {snapshot.Project.CreatedAt.ToLocalTime():yyyy 年 M 月 d 日}");
        if (snapshot.Steps.Count == 0) Paragraph(x, "StepBody", "尚未添加步骤。");
        for (var i = 0; i < snapshot.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = snapshot.Steps[i]; var step = entry.Step;
            Start(x, "p"); Start(x, "pPr"); Val(x, "pStyle", "StepHeading"); x.WriteEndElement();
            Run(x, $"{step.Number:D2}  ", "227A69");
            Run(x, string.IsNullOrWhiteSpace(step.Title) ? $"步骤 {step.Number}" : step.Title);
            x.WriteEndElement();
            if (!string.IsNullOrWhiteSpace(step.Description))
            {
                // A short explanation belongs with its screenshot; long prose must still paginate freely.
                var compact = step.Description.Length <= 260
                    && step.Description.Replace("\r\n", "\n").Replace('\r', '\n').Count(c => c == '\n') <= 4;
                Paragraph(x, "StepBody", step.Description, keepWithNext: compact);
            }
            Picture(x, entry, i + 1);
            var label = step.Kind switch { "video" => "屏幕录制 · 仅保留第一帧", "gif" => "GIF 动图 · 仅保留第一帧", _ => "屏幕截图" };
            if (step.DurationSeconds > 0 && double.IsFinite(step.DurationSeconds))
                label += "  ·  " + Duration(step.DurationSeconds);
            if (step.IsDraft) label += "  ·  待补充";
            Paragraph(x, "Caption", label);
        }
        Start(x, "sectPr");
        Start(x, "headerReference"); Attr(x, "type", "default"); x.WriteAttributeString("r", "id", R, "rHeader"); x.WriteEndElement();
        Start(x, "footerReference"); Attr(x, "type", "default"); x.WriteAttributeString("r", "id", R, "rFooter"); x.WriteEndElement();
        Start(x, "pgSz"); Attr(x, "w", "12240"); Attr(x, "h", "15840"); x.WriteEndElement();
        Start(x, "pgMar"); Attr(x, "top", "1440"); Attr(x, "right", "1440"); Attr(x, "bottom", "1440");
        Attr(x, "left", "1440"); Attr(x, "header", "720"); Attr(x, "footer", "720"); Attr(x, "gutter", "0"); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }

    private static void Picture(XmlWriter x, ExportStep step, int id)
    {
        // At most natural size at 96 dpi; screenshots retain their aspect ratio, including portrait captures.
        var scale = Math.Min(1d / 96, Math.Min(MaxImageWidth / step.PosterWidth, MaxImageHeight / step.PosterHeight));
        var cx = Math.Max(1, (long)Math.Round(step.PosterWidth * scale * EmuPerInch));
        var cy = Math.Max(1, (long)Math.Round(step.PosterHeight * scale * EmuPerInch));
        var width = cx.ToString(CultureInfo.InvariantCulture); var height = cy.ToString(CultureInfo.InvariantCulture);
        Start(x, "p"); Start(x, "pPr"); Val(x, "keepNext", "1");
        Spacing(x, 40, 0, 240); Val(x, "jc", "center"); x.WriteEndElement(); Start(x, "r"); Start(x, "drawing");
        x.WriteStartElement("wp", "inline", WP);
        foreach (var side in new[] { "distT", "distB", "distL", "distR" }) x.WriteAttributeString(side, "0");
        x.WriteStartElement("wp", "extent", WP); x.WriteAttributeString("cx", width); x.WriteAttributeString("cy", height); x.WriteEndElement();
        x.WriteStartElement("wp", "effectExtent", WP);
        foreach (var side in new[] { "l", "t", "r", "b" }) x.WriteAttributeString(side, "0"); x.WriteEndElement();
        x.WriteStartElement("wp", "docPr", WP); x.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("name", $"步骤 {step.Step.Number} 预览图");
        x.WriteAttributeString("descr", CleanText(step.Step.Title)); x.WriteEndElement();
        x.WriteStartElement("wp", "cNvGraphicFramePr", WP);
        x.WriteStartElement("a", "graphicFrameLocks", A); x.WriteAttributeString("noChangeAspect", "1"); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("a", "graphic", A); x.WriteStartElement("a", "graphicData", A); x.WriteAttributeString("uri", PIC);
        x.WriteStartElement("pic", "pic", PIC); x.WriteStartElement("pic", "nvPicPr", PIC);
        x.WriteStartElement("pic", "cNvPr", PIC); x.WriteAttributeString("id", "0"); x.WriteAttributeString("name", $"step-{id}.png"); x.WriteEndElement();
        x.WriteStartElement("pic", "cNvPicPr", PIC); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("pic", "blipFill", PIC); x.WriteStartElement("a", "blip", A);
        x.WriteAttributeString("r", "embed", R, $"rImage{id}"); x.WriteEndElement();
        x.WriteStartElement("a", "stretch", A); x.WriteStartElement("a", "fillRect", A); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("pic", "spPr", PIC); x.WriteStartElement("a", "xfrm", A);
        x.WriteStartElement("a", "off", A); x.WriteAttributeString("x", "0"); x.WriteAttributeString("y", "0"); x.WriteEndElement();
        x.WriteStartElement("a", "ext", A); x.WriteAttributeString("cx", width); x.WriteAttributeString("cy", height); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("a", "prstGeom", A); x.WriteAttributeString("prst", "rect");
        x.WriteStartElement("a", "avLst", A); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }

    private static void Paragraph(XmlWriter x, string style, string text, string? color = null, bool keepWithNext = false)
    {
        Start(x, "p"); Start(x, "pPr"); Val(x, "pStyle", style);
        if (keepWithNext) { Val(x, "keepNext", "1"); Val(x, "keepLines", "1"); }
        x.WriteEndElement();
        Run(x, text, color); x.WriteEndElement();
    }

    private static void Run(XmlWriter x, string text, string? color = null, bool bold = false)
    {
        Start(x, "r");
        if (color != null || bold)
        {
            Start(x, "rPr"); if (bold) Val(x, "b", "1"); if (color != null) Val(x, "color", color); x.WriteEndElement();
        }
        var lines = CleanText(text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) { Start(x, "br"); x.WriteEndElement(); }
            var pieces = lines[i].Split('\t');
            for (var j = 0; j < pieces.Length; j++)
            {
                if (j > 0) { Start(x, "tab"); x.WriteEndElement(); }
                Start(x, "t"); x.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                x.WriteString(pieces[j]); x.WriteEndElement();
            }
        }
        x.WriteEndElement();
    }

    private static void Field(XmlWriter x, string instruction)
    {
        Start(x, "fldSimple"); Attr(x, "instr", instruction); Run(x, "1"); x.WriteEndElement();
    }

    private static void Fonts(XmlWriter x)
    {
        Start(x, "rFonts"); Attr(x, "ascii", "Calibri"); Attr(x, "hAnsi", "Calibri");
        Attr(x, "eastAsia", "Microsoft YaHei"); Attr(x, "cs", "Calibri"); x.WriteEndElement();
    }

    private static void Spacing(XmlWriter x, int before, int after, int line, string lineRule = "auto")
    {
        Start(x, "spacing"); Attr(x, "before", before.ToString(CultureInfo.InvariantCulture));
        Attr(x, "after", after.ToString(CultureInfo.InvariantCulture)); Attr(x, "line", line.ToString(CultureInfo.InvariantCulture));
        Attr(x, "lineRule", lineRule); x.WriteEndElement();
    }

    private static void Border(XmlWriter x, string edge, string color, string size, string space)
    {
        Start(x, edge); Attr(x, "val", "single"); Attr(x, "sz", size); Attr(x, "space", space);
        Attr(x, "color", color); x.WriteEndElement();
    }

    private static string Duration(double seconds)
    {
        var whole = (long)Math.Min(seconds, int.MaxValue);
        return whole >= 3600 ? $"{whole / 3600}:{whole % 3600 / 60:D2}:{whole % 60:D2}" : $"{whole / 60}:{whole % 60:D2}";
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var output = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c)) output.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            { output.Append(c); output.Append(value[++i]); }
            else output.Append('\uFFFD');
        }
        return output.ToString();
    }

    private static void Start(XmlWriter x, string name) => x.WriteStartElement("w", name, W);
    private static void Attr(XmlWriter x, string name, string value) => x.WriteAttributeString("w", name, W, value);
    private static void Val(XmlWriter x, string name, string value)
    { Start(x, name); Attr(x, "val", value); x.WriteEndElement(); }
}
