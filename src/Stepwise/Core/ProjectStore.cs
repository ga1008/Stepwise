using System.Text;
using System.Text.Json;

namespace Stepwise.Core;

/// <summary>The project directory is the unit of portability. All media paths are relative to it.</summary>
public sealed class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly string[] Folders = ["media/images", "media/videos", "media/gifs", "recordings", "drafts", "logs", ".cache"];
    public string Root { get; }
    public ProjectData Data { get; private set; }
    public event EventHandler? Changed;

    private ProjectStore(string root, ProjectData data)
    {
        Root = Path.GetFullPath(root);
        Data = data;
        foreach (var folder in Folders) Directory.CreateDirectory(Path.Combine(Root, folder));
    }

    public static ProjectStore Create(string parentDirectory, string title)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory)) throw new ArgumentException("请选择项目目录。");
        var parent = Path.GetFullPath(parentDirectory);
        Directory.CreateDirectory(parent);
        var label = string.IsNullOrWhiteSpace(title) ? "操作记录" : title.Trim();
        var safe = new string(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length > 40) safe = safe[..40];
        if (string.IsNullOrEmpty(safe)) safe = "操作记录";
        var root = Path.Combine(parent, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
        var store = new ProjectStore(root, new ProjectData { Title = label });
        store.SaveAsync().GetAwaiter().GetResult();
        return store;
    }

    public static ProjectStore Open(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("项目目录不存在。");
        var primary = Path.Combine(root, "project.json");
        Exception? firstError = null;
        foreach (var candidate in new[] { primary, primary + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var data = JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(candidate, Encoding.UTF8), JsonOptions)
                    ?? throw new InvalidDataException("项目文件为空。");
                if (data.SchemaVersion != 1) throw new InvalidDataException("此项目由其他版本创建，暂不能打开。");
                var store = new ProjectStore(root, data);
                store.ValidateAndNormalize();
                if (candidate != primary && File.Exists(primary))
                    File.Copy(primary, Path.Combine(root, "logs", $"project-recovery-{DateTime.Now:yyyyMMddHHmmssfff}.json"), false);
                // A crash between the JSON and HTML writes is healed from the authoritative JSON.
                if (candidate != primary) File.Copy(candidate, primary, true);
                AtomicWrite(Path.Combine(root, "document.html"), DocumentExporter.Render(data));
                return store;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            {
                firstError ??= ex;
            }
        }
        throw new InvalidDataException("无法读取项目及其备份，请保留目录并检查 logs 文件夹。", firstError);
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await PersistAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ProjectData> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Clone(Data); }
        finally { _gate.Release(); }
    }

    public Task AddStepAsync(StepRecord step) => MutateAsync(() =>
    {
        if (string.IsNullOrWhiteSpace(step.Id)) step.Id = Guid.NewGuid().ToString("N");
        if (Data.Steps.Concat(Data.DeletedSteps).Any(s => s.Id == step.Id)) throw new InvalidOperationException("此步骤已保存。");
        ValidateStep(step);
        step.DeletedAt = null;
        Data.Steps.Add(Clone(step));
    });

    public Task UpdateStepAsync(StepRecord step) => MutateAsync(() =>
    {
        var index = Data.Steps.FindIndex(s => s.Id == step.Id);
        if (index < 0) throw new KeyNotFoundException("步骤不存在，可能已被删除。");
        ValidateStep(step);
        step.DeletedAt = null;
        Data.Steps[index] = Clone(step);
    });

    public Task DeleteStepAsync(string id) => MutateAsync(() =>
    {
        var step = Data.Steps.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("步骤不存在。");
        Data.Steps.Remove(step);
        step.DeletedAt = DateTimeOffset.Now;
        Data.DeletedSteps.Add(step);
    });

    public Task MoveStepAsync(string id, int direction) => MutateAsync(() =>
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var index = Data.Steps.FindIndex(s => s.Id == id);
        if (index < 0) throw new KeyNotFoundException("步骤不存在。");
        var target = index + direction;
        if (target < 0 || target >= Data.Steps.Count) return;
        (Data.Steps[index], Data.Steps[target]) = (Data.Steps[target], Data.Steps[index]);
    });

    /// <summary>Resolve a project-local file; absolute paths, URI schemes and traversal are rejected.</summary>
    public string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.IndexOf('\0') >= 0)
            throw new InvalidDataException("素材路径必须位于项目目录内。");
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(s => s is ".." or "."))
            throw new InvalidDataException("素材路径不能包含上级目录。");
        var full = Path.GetFullPath(Path.Combine(Root, normalized));
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材路径越出项目目录。");
        // Do not follow an imported junction/symlink out of the project boundary.
        var current = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (current != null && current.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && current.LinkTarget != null) throw new InvalidDataException("项目素材目录不能使用符号链接。");
            current = current.Parent;
        }
        if (File.Exists(full) && new FileInfo(full).LinkTarget != null) throw new InvalidDataException("项目素材不能使用符号链接。");
        return full;
    }

    private async Task MutateAsync(Action mutation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var previous = Clone(Data);
        try { mutation(); await PersistAsync().ConfigureAwait(false); }
        catch { Data = previous; throw; }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task PersistAsync()
    {
        ValidateAndNormalize();
        Data.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(Data, JsonOptions);
        var html = DocumentExporter.Render(Data);
        return Task.Run(() =>
        {
            var manifest = Path.Combine(Root, "project.json");
            var document = Path.Combine(Root, "document.html");
            try
            {
                // Commit the authoritative JSON last. An interrupted write is healed on Open.
                AtomicWrite(document, html);
                AtomicWrite(manifest, json);
            }
            catch
            {
                // A failed manifest write must not leave the visible document ahead of the saved project.
                try
                {
                    if (File.Exists(manifest))
                    {
                        var committed = JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(manifest, Encoding.UTF8), JsonOptions);
                        if (committed != null) AtomicWrite(document, DocumentExporter.Render(committed));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* Open regenerates HTML from the last committed JSON. */ }
                throw;
            }
        });
    }

    private void ValidateAndNormalize()
    {
        if (Data.Steps == null || Data.DeletedSteps == null || Data.Steps.Any(s => s is null) || Data.DeletedSteps.Any(s => s is null)) throw new InvalidDataException("项目步骤列表无效。");
        Data.Title = string.IsNullOrWhiteSpace(Data.Title) ? "操作记录" : Data.Title.Trim();
        if (Data.Title.Length > 200) throw new InvalidDataException("项目名称不能超过 200 字。");
        if (Data.Steps.Concat(Data.DeletedSteps).Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Data.Steps.Count + Data.DeletedSteps.Count)
            throw new InvalidDataException("项目包含重复步骤编号。");
        for (var i = 0; i < Data.Steps.Count; i++) { ValidateStep(Data.Steps[i]); Data.Steps[i].Number = i + 1; }
        foreach (var step in Data.DeletedSteps) ValidateStep(step);
    }

    private void ValidateStep(StepRecord step)
    {
        if (string.IsNullOrWhiteSpace(step.Id) || step.Id.Length > 100) throw new InvalidDataException("步骤标识无效。");
        step.Title = (step.Title ?? "").Trim();
        step.Description ??= "";
        if (step.Title.Length > 200 || step.Description.Length > 100000) throw new InvalidDataException("步骤文字过长。");
        if (step.Kind is not ("image" or "video" or "gif")) throw new InvalidDataException("不支持的素材类型。");
        ResolvePath(step.MediaPath);
        step.MediaPath = step.MediaPath.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(step.OriginalPath)) { ResolvePath(step.OriginalPath); step.OriginalPath = step.OriginalPath.Replace('\\', '/'); }
        if (!string.IsNullOrWhiteSpace(step.AnnotationPath)) { ResolvePath(step.AnnotationPath); step.AnnotationPath = step.AnnotationPath.Replace('\\', '/'); }
        if (!double.IsFinite(step.DurationSeconds) || step.DurationSeconds < 0) step.DurationSeconds = 0;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            { writer.Write(content); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
