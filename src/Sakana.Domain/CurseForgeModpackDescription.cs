namespace Sakana.Domain;

public sealed record CurseForgeModpackFile(long ProjectId, long FileId, bool Required);

public sealed record CurseForgeManualDownload(
    long ProjectId,
    long FileId,
    string FileName,
    string ProjectName,
    string OfficialPageUrl);

public sealed record CurseForgeModpackDescription(
    string Name,
    string Version,
    string Author,
    string MinecraftVersion,
    MinecraftLoaderType LoaderType,
    string? LoaderVersion,
    IReadOnlyList<CurseForgeModpackFile> Files,
    int OverrideFiles);
