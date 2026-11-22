using Sakana.Domain;

namespace Sakana.Application;

public interface IModrinthModpackService
{
    Task<ModrinthModpackDescription> InspectAsync(string archivePath, CancellationToken cancellationToken = default);

    Task<ModrinthModpackUpdatePreview> PreviewUpdateAsync(
        InstanceProfile profile,
        string dataDirectory,
        string newArchivePath,
        string newVersionNumber,
        CancellationToken cancellationToken = default);

    Task<ModrinthModpackUpdateResult> ApplyUpdateAsync(
        InstanceProfile profile,
        string dataDirectory,
        string newArchivePath,
        IReadOnlyCollection<string> replaceConflictPaths,
        Func<IReadOnlyCollection<string>, CancellationToken, Task> persistUpdatedProfileAsync,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        InstanceProfile profile,
        string dataDirectory,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModrinthModpackExportResult> ExportAsync(
        InstanceProfile profile,
        string dataDirectory,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
