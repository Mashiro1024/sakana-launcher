namespace Sakana.Domain;

public enum ModrinthContentKind
{
    ResourcePack,
    ShaderPack,
    DataPack
}

public sealed record ModrinthModProject(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    long Downloads,
    string? IconUrl,
    IReadOnlyList<string> Categories);

public sealed record ModrinthProjectSearchPage(
    IReadOnlyList<ModrinthModProject> Projects,
    int Offset,
    int Limit,
    int TotalHits);

public sealed record ModrinthModRelease(
    string VersionId,
    string VersionNumber,
    string Name,
    string VersionType,
    DateTimeOffset PublishedAt,
    long Downloads,
    string FileName,
    long SizeBytes,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<string> GameVersions);

public sealed record ModrinthContentInstallResult(
    int InstalledCount,
    int SkippedDuplicateCount,
    string FileName);

public sealed record ModrinthModInstallResult(
    int InstalledCount,
    int SkippedDuplicateCount,
    IReadOnlyList<string> InstalledFileNames);

public sealed record ModrinthModUpdate(
    string FileName,
    string CurrentVersionNumber,
    ModrinthModRelease AvailableRelease);
