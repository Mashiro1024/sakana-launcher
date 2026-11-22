using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class FabricMetaClient(HttpClient httpClient) : IFabricLoaderCatalog
{
    private const string ApiRoot = "https://meta.fabricmc.net/v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<MinecraftLoaderVersion>> GetVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = Uri.EscapeDataString(ValidateSegment(minecraftVersionId, "Minecraft version"));
        var url = $"{ApiRoot}/versions/loader/{gameVersion}";
        var bytes = await httpClient.GetByteArrayAsync(url, cancellationToken);
        var versions = JsonSerializer.Deserialize<List<FabricLoaderVersionResponse>>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Fabric's version service returned an empty loader list.");

        return versions
            .Where(item => !string.IsNullOrWhiteSpace(item.Loader.Version))
            .Select(item => new MinecraftLoaderVersion(item.Loader.Version, item.Loader.Stable))
            .DistinctBy(item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<byte[]> GetProfileJsonAsync(
        string minecraftVersionId,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = Uri.EscapeDataString(ValidateSegment(minecraftVersionId, "Minecraft version"));
        var safeLoaderVersion = Uri.EscapeDataString(ValidateSegment(loaderVersion, "Fabric loader version"));
        var url = $"{ApiRoot}/versions/loader/{gameVersion}/{safeLoaderVersion}/profile/json";
        return await httpClient.GetByteArrayAsync(url, cancellationToken);
    }

    private static string ValidateSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '+'))
        {
            throw new ArgumentException($"The {label} contains unsupported characters.", label);
        }

        return value;
    }

    private sealed class FabricLoaderVersionResponse
    {
        [JsonPropertyName("loader")]
        public FabricLoaderDetails Loader { get; init; } = new();
    }

    private sealed class FabricLoaderDetails
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("stable")]
        public bool Stable { get; init; }
    }
}
