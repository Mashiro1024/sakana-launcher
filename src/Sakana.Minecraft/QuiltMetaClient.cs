using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class QuiltMetaClient(HttpClient httpClient) : IQuiltLoaderCatalog
{
    private const string ApiRoot = "https://meta.quiltmc.org/v3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<MinecraftLoaderVersion>> GetVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = Uri.EscapeDataString(ValidateSegment(minecraftVersionId, "Minecraft version"));
        var bytes = await httpClient.GetByteArrayAsync($"{ApiRoot}/versions/loader/{gameVersion}", cancellationToken);
        var versions = JsonSerializer.Deserialize<List<QuiltLoaderVersionResponse>>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Quilt's version service returned an empty loader list.");

        return versions
            .Where(item => !string.IsNullOrWhiteSpace(item.Loader.Version))
            .Select(item => new MinecraftLoaderVersion(
                item.Loader.Version,
                !item.Loader.Version.Contains('-', StringComparison.Ordinal)))
            .DistinctBy(item => item.Version, StringComparer.Ordinal)
            .OrderByDescending(item => item.IsStable)
            .ThenByDescending(item => GetNumericVersion(item.Version))
            .ToArray();
    }

    public async Task<byte[]> GetProfileJsonAsync(
        string minecraftVersionId,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = Uri.EscapeDataString(ValidateSegment(minecraftVersionId, "Minecraft version"));
        var safeLoaderVersion = Uri.EscapeDataString(ValidateSegment(loaderVersion, "Quilt loader version"));
        return await httpClient.GetByteArrayAsync(
            $"{ApiRoot}/versions/loader/{gameVersion}/{safeLoaderVersion}/profile/json",
            cancellationToken);
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

    private static Version GetNumericVersion(string version)
    {
        var coreVersion = version.Split(['-', '+'], 2)[0];
        return Version.TryParse(coreVersion, out var parsed) ? parsed : new Version(0, 0);
    }

    private sealed class QuiltLoaderVersionResponse
    {
        [JsonPropertyName("loader")]
        public QuiltLoaderDetails Loader { get; init; } = new();
    }

    private sealed class QuiltLoaderDetails
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;
    }
}
