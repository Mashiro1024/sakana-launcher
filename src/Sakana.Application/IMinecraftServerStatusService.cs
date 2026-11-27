using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftServerStatusService
{
    Task<MinecraftServerStatus> PingAsync(string address, CancellationToken cancellationToken = default);
}
