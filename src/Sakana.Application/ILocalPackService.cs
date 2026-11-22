using Sakana.Domain;

namespace Sakana.Application;

public interface ILocalPackService
{
    Task<IReadOnlyList<string>> ListWorldsAsync(string instanceDirectoryName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MinecraftLocalPack>> ListAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string? worldName,
        CancellationToken cancellationToken = default);

    Task<LocalPackImportResult> ImportAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        IReadOnlyCollection<string> sourcePaths,
        string? worldName,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string fileName,
        bool isEnabled,
        string? worldName,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string fileName,
        string? worldName,
        CancellationToken cancellationToken = default);
}
