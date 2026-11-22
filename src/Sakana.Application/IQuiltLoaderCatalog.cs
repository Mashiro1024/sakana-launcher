using Sakana.Domain;

namespace Sakana.Application;

public interface IQuiltLoaderCatalog
{
    Task<IReadOnlyList<MinecraftLoaderVersion>> GetVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken = default);

    Task<byte[]> GetProfileJsonAsync(
        string minecraftVersionId,
        string loaderVersion,
        CancellationToken cancellationToken = default);
}
