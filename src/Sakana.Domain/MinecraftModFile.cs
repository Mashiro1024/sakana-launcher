namespace Sakana.Domain;

public sealed record MinecraftModFile(
    string FileName,
    string DisplayName,
    string? ModId,
    string? Version,
    long SizeBytes,
    bool IsEnabled);

public sealed record LocalModImportResult(int ImportedCount, int SkippedDuplicateCount);

public sealed record InstanceCopyProgress(
    string Status,
    long BytesCopied,
    long TotalBytes,
    int FilesCopied,
    int TotalFiles,
    string? CurrentFile = null);
