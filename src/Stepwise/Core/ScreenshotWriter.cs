using System.Drawing.Imaging;
using System.Text.Json;

namespace Stepwise.Core;

/// <summary>Commits a confirmed screenshot and its editable evidence as one new step.</summary>
public static class ScreenshotWriter
{
    public static async Task<StepRecord> SaveAsync(ProjectStore store, Bitmap original, Bitmap annotated, string layersJson, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("截图标识无效。", nameof(id));
        if (original.Size != annotated.Size) throw new InvalidDataException("标注不能改变原始截图尺寸。");
        using var parsed = JsonDocument.Parse(layersJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("笔迹数据无效。");
        if (store.Data.Steps.Concat(store.Data.DeletedSteps).Any(s => s.Id == id)) throw new InvalidOperationException("此截图已经保存。");
        var step = new StepRecord
        {
            Id = id, Title = $"步骤 {store.Data.Steps.Count + 1}", Kind = "image", IsDraft = true,
            MediaPath = $"media/images/{id}.png", OriginalPath = $"media/images/{id}-original.png",
            AnnotationPath = $"media/images/{id}.annotations.json", Width = annotated.Width, Height = annotated.Height
        };
        var destinations = new[] { store.ResolvePath(step.MediaPath), store.ResolvePath(step.OriginalPath), store.ResolvePath(step.AnnotationPath) };
        // A retry must never overwrite an unrelated existing file, even if imported data reuse an id.
        if (destinations.Any(File.Exists)) throw new IOException("同名截图文件已存在，请保留原件并重新截图。");
        var temporary = destinations.Select(path => path + ".pending-" + Guid.NewGuid().ToString("N")).ToArray();
        var created = new List<string>();
        var committed = false;
        try
        {
            await Task.Run(() =>
            {
                annotated.Save(temporary[0], ImageFormat.Png);
                original.Save(temporary[1], ImageFormat.Png);
                File.WriteAllText(temporary[2], layersJson, new System.Text.UTF8Encoding(false));
                for (var i = 0; i < destinations.Length; i++) { File.Move(temporary[i], destinations[i], false); created.Add(destinations[i]); }
            }).ConfigureAwait(false);
            await store.AddStepAsync(step).ConfigureAwait(false);
            committed = true;
            return store.Data.Steps.First(s => s.Id == id);
        }
        finally
        {
            // Preserve a successfully committed step even if an observer threw after the commit.
            committed |= store.Data.Steps.Any(s => s.Id == id && s.MediaPath == step.MediaPath);
            foreach (var path in temporary.Concat(committed ? [] : created))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { try { File.AppendAllText(Path.Combine(store.Root, "logs", "app.log"), $"{DateTimeOffset.Now:O} 截图临时文件清理失败：{path} {ex.Message}\n"); } catch { } }
            }
        }
    }
}
