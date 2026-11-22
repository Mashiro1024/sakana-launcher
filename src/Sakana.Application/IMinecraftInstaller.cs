using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftInstaller
{
    Task InstallAsync(
        InstanceProfile profile,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRevalidation = false);
}
