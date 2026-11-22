using Sakana.Domain;

namespace Sakana.Application;

public interface IForgeNeoForgeLoaderCatalog
{
    Task<IReadOnlyList<MinecraftLoaderVersion>> GetVersionsAsync(
        MinecraftLoaderType loaderType,
        string minecraftVersionId,
        CancellationToken cancellationToken = default);
}
