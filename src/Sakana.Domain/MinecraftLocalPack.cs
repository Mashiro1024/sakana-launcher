namespace Sakana.Domain;

public enum MinecraftLocalPackKind
{
    ResourcePack,
    ShaderPack,
    DataPack
}

public sealed record MinecraftLocalPack(
    string FileName,
    string DisplayName,
    string? Details,
    long SizeBytes,
    bool IsEnabled);

public sealed record LocalPackImportResult(int ImportedCount, int SkippedDuplicateCount);
