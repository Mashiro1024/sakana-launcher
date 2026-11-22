using Sakana.Domain;

namespace Sakana.Application;

public interface IJavaRuntimeCatalog
{
    Task<IReadOnlyList<JavaInstallation>> DetectInstalledJavaAsync(CancellationToken cancellationToken = default);
}
