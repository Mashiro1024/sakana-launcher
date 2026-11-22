using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class ForgeNeoForgeMetaClient(HttpClient httpClient) : IForgeNeoForgeLoaderCatalog
{
    private const string ForgeMetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string NeoForgeMetadataUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _forgeVersions;
    private IReadOnlyList<string>? _neoForgeVersions;
    private DateTimeOffset _forgeFetchedAt;
    private DateTimeOffset _neoForgeFetchedAt;

    public async Task<IReadOnlyList<MinecraftLoaderVersion>> GetVersionsAsync(
        MinecraftLoaderType loaderType,
        string minecraftVersionId,
        CancellationToken cancellationToken = default)
    {
        ValidateGameVersion(minecraftVersionId);
        if (loaderType is not (MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge))
        {
            throw new ArgumentOutOfRangeException(nameof(loaderType), "Forge metadata is only available for Forge and NeoForge.");
        }

        var allVersions = await GetAllVersionsAsync(loaderType, cancellationToken);
        var prefix = loaderType == MinecraftLoaderType.Forge
            ? $"{minecraftVersionId}-"
            : GetNeoForgePrefix(minecraftVersionId);
        return allVersions
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(version => loaderType == MinecraftLoaderType.Forge ? version[prefix.Length..] : version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetNumericVersion)
            .Select(version => new MinecraftLoaderVersion(version, IsStable(version)))
            .OrderByDescending(version => version.IsStable)
            .ThenByDescending(version => GetNumericVersion(version.Version))
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetAllVersionsAsync(
        MinecraftLoaderType loaderType,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var isForge = loaderType == MinecraftLoaderType.Forge;
            var cached = isForge ? _forgeVersions : _neoForgeVersions;
            var fetchedAt = isForge ? _forgeFetchedAt : _neoForgeFetchedAt;
            if (cached is not null && DateTimeOffset.UtcNow - fetchedAt < CacheDuration)
            {
                return cached;
            }

            var url = isForge ? ForgeMetadataUrl : NeoForgeMetadataUrl;
            await using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var versions = document.Descendants("version")
                .Select(element => element.Value.Trim())
                .Where(version => version.Length is > 0 and <= 120 && IsSafeVersion(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (versions.Length == 0)
            {
                throw new InvalidDataException($"The official {loaderType} Maven metadata did not contain any loader versions.");
            }

            if (isForge)
            {
                _forgeVersions = versions;
                _forgeFetchedAt = DateTimeOffset.UtcNow;
                return _forgeVersions;
            }

            _neoForgeVersions = versions;
            _neoForgeFetchedAt = DateTimeOffset.UtcNow;
            return _neoForgeVersions;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetNeoForgePrefix(string minecraftVersionId)
    {
        var components = minecraftVersionId.Split('.');
        if (components.Length < 3 || !int.TryParse(components[0], out var gameMajor) || !int.TryParse(components[1], out var gameMinor))
        {
            return "unsupported-version-";
        }

        // NeoForge kept the legacy Minecraft-prefixed scheme for the 1.20.1 branch,
        // then moved to <Minecraft major><minor>.<patch>.<NeoForge build>.
        if (gameMajor == 1 && gameMinor == 20 && components[2] == "1")
        {
            return "1.20.1-";
        }

        return $"{gameMajor}{gameMinor}.{components[2]}.";
    }

    private static string GetNumericVersion(string version)
    {
        var numbers = Regex.Matches(version, "[0-9]+", RegexOptions.CultureInvariant)
            .Select(match => long.TryParse(match.Value, out var value) ? value : 0)
            .ToArray();
        return string.Join('.', numbers.Select(number => number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static bool IsStable(string version) =>
        !Regex.IsMatch(version, "(?:^|[-+.])(alpha|beta|rc|pre|snapshot|preview)(?:[-+.0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsSafeVersion(string version) =>
        version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    private static void ValidateGameVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > 80 || !version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            throw new ArgumentException("The Minecraft version contains unsupported characters.", nameof(version));
        }
    }
}
