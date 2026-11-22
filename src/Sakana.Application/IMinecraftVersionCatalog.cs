using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftVersionCatalog
{
    Task<IReadOnlyList<MinecraftGameVersion>> GetReleaseVersionsAsync(CancellationToken cancellationToken = default);
}
