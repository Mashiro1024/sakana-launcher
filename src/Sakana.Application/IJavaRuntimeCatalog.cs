using Sakana.Domain;

namespace Sakana.Application;

public interface IJavaRuntimeCatalog
{
    Task<IReadOnlyList<JavaInstallation>> DetectInstalledJavaAsync(CancellationToken cancellationToken = default);
    Task<JavaInstallation> InstallManagedRuntimeAsync(
        int majorVersion,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
