using Sakana.Domain;

namespace Sakana.Application;

public interface ILocalModService
{
    Task<IReadOnlyList<MinecraftModFile>> ListAsync(string instanceDirectoryName, CancellationToken cancellationToken = default);

    Task<LocalModImportResult> ImportAsync(
        string instanceDirectoryName,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        string instanceDirectoryName,
        string fileName,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string instanceDirectoryName, string fileName, CancellationToken cancellationToken = default);
}
