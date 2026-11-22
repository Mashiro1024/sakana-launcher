using Sakana.Domain;

namespace Sakana.Application;

public interface IModrinthModCatalog
{
    Task<ModrinthProjectSearchPage> SearchModsAsync(
        InstanceProfile profile,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleVersionsAsync(
        InstanceProfile profile,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<ModrinthModInstallResult> InstallVersionAsync(
        InstanceProfile profile,
        string versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthModUpdate>> CheckInstalledModUpdatesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<ModrinthModInstallResult> UpdateInstalledModAsync(
        InstanceProfile profile,
        string fileName,
        string versionId,
        CancellationToken cancellationToken = default);

    Task<ModrinthProjectSearchPage> SearchContentAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleContentVersionsAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string projectId,
        CancellationToken cancellationToken = default);

    Task<ModrinthContentInstallResult> InstallContentVersionAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string versionId,
        string? worldName,
        CancellationToken cancellationToken = default);

    Task<ModrinthProjectSearchPage> SearchModpacksAsync(
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModrinthModRelease>> GetSupportedModpackVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<string> DownloadModpackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default);
}
