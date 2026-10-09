using System.Text.Json.Serialization;

namespace Stepwise.Core;

public sealed class ProjectData
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("title")] public string Title { get; set; } = "操作记录";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("steps")] public List<StepRecord> Steps { get; set; } = [];
    [JsonPropertyName("deletedSteps")] public List<StepRecord> DeletedSteps { get; set; } = [];
}

public sealed class StepRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "image";
    [JsonPropertyName("mediaPath")] public string MediaPath { get; set; } = "";
    [JsonPropertyName("originalPath")] public string? OriginalPath { get; set; }
    [JsonPropertyName("annotationPath")] public string? AnnotationPath { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("muted")] public bool Muted { get; set; }
    [JsonPropertyName("isDraft")] public bool IsDraft { get; set; } = true;
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("deletedAt")] public DateTimeOffset? DeletedAt { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}
