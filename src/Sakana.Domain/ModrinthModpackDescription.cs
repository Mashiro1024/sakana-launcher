namespace Sakana.Domain;

public sealed record ModrinthModpackDescription(
    string Name,
    string VersionId,
    string? Summary,
    string MinecraftVersion,
    MinecraftLoaderType LoaderType,
    string? LoaderVersion,
    int RequiredClientFiles,
    IReadOnlyList<ModrinthOptionalFile> OptionalFiles);

public sealed record ModrinthModpackExportResult(
    int ModrinthFilesIncluded,
    int LocalModFilesOmitted,
    int OverrideFilesIncluded,
    long OverrideBytesIncluded);

public enum ModrinthPackChangeKind
{
    Added,
    Updated,
    Removed,
    Unchanged
}

public sealed record ModrinthPackFileChange(
    string Path,
    ModrinthPackChangeKind Kind,
    bool HasLocalChanges,
    bool HasLocalFile,
    bool RequiresConflictChoice,
    bool IsPersonalData);

public sealed record ModrinthModpackUpdatePreview(
    string CurrentVersion,
    string NewVersion,
    string CurrentMinecraftVersion,
    string NewMinecraftVersion,
    MinecraftLoaderType CurrentLoader,
    MinecraftLoaderType NewLoader,
    string? CurrentLoaderVersion,
    string? NewLoaderVersion,
    IReadOnlyList<ModrinthPackFileChange> Changes)
{
    public int AddedCount => Changes.Count(change => change.Kind == ModrinthPackChangeKind.Added);
    public int UpdatedCount => Changes.Count(change => change.Kind == ModrinthPackChangeKind.Updated);
    public int RemovedCount => Changes.Count(change => change.Kind == ModrinthPackChangeKind.Removed);
    public int LocallyChangedCount => Changes.Count(change => change.HasLocalChanges);
    public int LocalFileCollisionCount => Changes.Count(change => change.HasLocalFile && change.Kind == ModrinthPackChangeKind.Added);
    public int ConflictCount => Changes.Count(change => change.RequiresConflictChoice);
    public bool SupportsInPlaceUpdate =>
        string.Equals(CurrentMinecraftVersion, NewMinecraftVersion, StringComparison.Ordinal) &&
        CurrentLoader == NewLoader &&
        string.Equals(CurrentLoaderVersion, NewLoaderVersion, StringComparison.Ordinal);
}

public sealed record ModrinthModpackUpdateResult(
    int InstalledFiles,
    int UpdatedFiles,
    int PreservedLocalFiles,
    int RetainedRemovedFiles);
