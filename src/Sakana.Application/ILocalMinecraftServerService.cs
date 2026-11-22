using Sakana.Domain;

namespace Sakana.Application;

public interface ILocalMinecraftServerService
{
    Task<IReadOnlyList<MinecraftServerBookmark>> ListAsync(string instanceDirectoryName, CancellationToken cancellationToken = default);
    Task<MinecraftServerBookmark> AddAsync(string instanceDirectoryName, string name, string address, CancellationToken cancellationToken = default);
    Task RemoveAsync(string instanceDirectoryName, Guid bookmarkId, CancellationToken cancellationToken = default);
}
