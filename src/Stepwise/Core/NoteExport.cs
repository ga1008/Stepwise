namespace Stepwise.Core;

public enum ExportFormat { Word, Html, LongImage, Pdf }
public sealed record ExportProgress(int Percent, string Message);
public sealed record ExportResult(string Directory, IReadOnlyList<string> Files, string Summary);
public sealed record ExportStep(StepRecord Step, string MediaPath, string PosterPath, int PosterWidth, int PosterHeight);
public sealed record ExportSnapshot(ProjectData Project, IReadOnlyList<ExportStep> Steps);
