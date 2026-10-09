using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Stepwise.UI;

namespace Stepwise.Core;

/// <summary>One immutable project snapshot per job; only a fully completed output directory is published.</summary>
public static class NoteExportService
{
    public static long MediaBytes(ProjectStore store, ProjectData snapshot) => snapshot.Steps
        .Select(step => store.ResolvePath(step.MediaPath)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Sum(path => new FileInfo(path).Length);

    // Invoked on the UI thread: WebView2 rendering must retain its owning STA context.
    public static async Task<ExportResult> ExportAsync(ProjectStore store, ProjectData project, ExportFormat format,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var exports = store.ResolvePath("exports/.boundary");
        var parent = Path.GetDirectoryName(exports)!;
        Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N");
        var job = store.ResolvePath($"exports/.pending-{id}");
        var output = Path.Combine(job, "output");
        var work = Path.Combine(job, "work");
        var label = SafeName(project.Title);
        var target = store.ResolvePath($"exports/{label}-{DateTime.Now:yyyyMMdd-HHmmss}-{format}-{id[..6]}");
        try
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(work);
            var snapshot = await PrepareAsync(store, project, work, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var mappedProgress = new ProgressAdapter(progress);
            if (format == ExportFormat.Word)
                await Task.Run(() => WordNoteExporter.ExportAsync(snapshot, Path.Combine(output, label + ".docx"), mappedProgress, cancellationToken), cancellationToken);
            else if (format == ExportFormat.Html)
                await Task.Run(() => StandaloneHtmlExporter.ExportAsync(snapshot, Path.Combine(output, label + ".html"), mappedProgress, cancellationToken), cancellationToken);
            else
                await NoteRenderForm.RenderAsync(snapshot, work, output, label, format == ExportFormat.Pdf, mappedProgress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.GetFiles(output).OrderBy(Path.GetFileName).ToArray();
            if (files.Length == 0 || files.Any(path => new FileInfo(path).Length == 0)) throw new IOException("导出结果为空，请重试。");
            // A cancelled/failed export never becomes a completed export and never replaces a prior one.
            Directory.Move(output, target);
            var result = files.Select(path => Path.Combine(target, Path.GetFileName(path))).ToArray();
            progress?.Report(new ExportProgress(100, "导出完成"));
            return new ExportResult(target, result, format == ExportFormat.LongImage && result.Length > 1
                ? $"内容较长，已按顺序保存为 {result.Length} 张长图。" : "已保存到项目的 exports 文件夹。");
        }
        finally
        {
            // Only our checked, uniquely allocated job directory is eligible for recursive cleanup.
            try
            {
                if (Directory.Exists(job) && Path.GetFullPath(job).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && new DirectoryInfo(job).LinkTarget == null) Directory.Delete(job, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine("Export staging cleanup: " + ex.Message); }
        }
    }

    internal static async Task<ExportSnapshot> PrepareAsync(ProjectStore store, ProjectData project, string work,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        var steps = new List<ExportStep>();
        var posters = new Dictionary<string, (string Path, Size Size)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < project.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = project.Steps[i];
            progress?.Report(new ExportProgress((int)(i * 25d / Math.Max(1, project.Steps.Count)), $"准备素材 {i + 1} / {project.Steps.Count}"));
            var source = store.ResolvePath(step.MediaPath);
            if (!File.Exists(source) || new FileInfo(source).Length == 0) throw new FileNotFoundException($"步骤 {step.Number} 的素材不存在，无法完整导出。", source);
            if (!posters.TryGetValue(source, out var poster))
            {
                var destination = Path.Combine(work, $"poster-{i:D5}.png");
                await MakePosterAsync(source, destination, cancellationToken);
                using var image = Image.FromFile(destination);
                poster = (destination, image.Size); posters.Add(source, poster);
            }
            steps.Add(new ExportStep(step, source, poster.Path, poster.Size.Width, poster.Size.Height));
        }
        return new ExportSnapshot(project, steps);
    }

    private static async Task MakePosterAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("缺少内置媒体转换器。", ffmpeg);
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source,
            "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=w='min(1600,iw)':h='min(2400,ih)':force_original_aspect_ratio=decrease:flags=lanczos", "-an", "-update", "1", destination }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("无法启动首帧转换。");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(2), cancellationToken); }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        await stdout;
        var error = await errors;
        if (process.ExitCode != 0 || !File.Exists(destination)) throw new IOException("读取媒体首帧失败：" + error.Trim());
    }

    private static string SafeName(string title)
    {
        var name = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 48) name = name[..48].TrimEnd();
        if (string.IsNullOrEmpty(name)) name = "操作笔记";
        // Prefix avoids reserved Windows device names while preserving a readable project title.
        return "笔记-" + name;
    }

    private sealed class ProgressAdapter(IProgress<ExportProgress>? outer) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => outer?.Report(new ExportProgress(25 + Math.Clamp(value.Percent, 0, 100) * 74 / 100, value.Message));
    }
}
