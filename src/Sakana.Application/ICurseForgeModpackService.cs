using Sakana.Domain;

namespace Sakana.Application;

public interface ICurseForgeModpackService
{
    Task<CurseForgeModpackDescription> InspectAsync(string archivePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CurseForgeManualDownload>> InstallAsync(
        InstanceProfile profile,
        string dataDirectory,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
