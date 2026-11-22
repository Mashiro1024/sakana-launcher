using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class MojangMetadataClient : IMinecraftVersionCatalog
{
    public const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private MojangVersionManifest? _manifest;
    private DateTimeOffset _manifestFetchedAt;
    private bool _manifestRefreshInProgress;

    public MojangMetadataClient(HttpClient httpClient, string dataDirectory)
    {
        _httpClient = httpClient;
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "minecraft");
        Directory.CreateDirectory(Path.Combine(_cacheDirectory, "versions"));
    }

    public async Task<IReadOnlyList<MinecraftGameVersion>> GetReleaseVersionsAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        return manifest.Versions
            .Where(version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
            .Select(version => new MinecraftGameVersion(
                version.Id,
                version.Type,
                DateTimeOffset.Parse(version.ReleaseTime, CultureInfo.InvariantCulture),
                string.Equals(version.Id, manifest.Latest.Release, StringComparison.Ordinal)))
            .ToArray();
    }

    internal async Task<(MojangVersionMetadata Metadata, byte[] JsonBytes)> GetVersionMetadataAsync(string versionId, CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        var entry = manifest.Versions.FirstOrDefault(version => string.Equals(version.Id, versionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Minecraft release {versionId} is not present in the official version manifest.");

        var cachePath = Path.Combine(_cacheDirectory, "versions", $"{GetSafeSegment(versionId)}.json");
        if (File.Exists(cachePath))
        {
            var cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            if (HasExpectedSha1(cachedBytes, entry.Sha1))
            {
                return (DeserializeMetadata(cachedBytes, versionId, entry.Type), cachedBytes);
            }

            File.Delete(cachePath);
        }

        var bytes = await _httpClient.GetByteArrayAsync(entry.Url, cancellationToken);
        if (!HasExpectedSha1(bytes, entry.Sha1))
        {
            throw new InvalidDataException($"The official metadata for Minecraft {versionId} failed its SHA-1 check.");
        }

        await WriteAtomicallyAsync(cachePath, bytes, cancellationToken);
        return (DeserializeMetadata(bytes, versionId, entry.Type), bytes);
    }

    internal static MojangAssetIndex DeserializeAssetIndex(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<MojangAssetIndex>(bytes, JsonOptions)
        ?? throw new InvalidDataException("The Minecraft asset index was empty or invalid.");

    internal static MojangVersionMetadata DeserializeLoaderProfile(
        ReadOnlySpan<byte> bytes,
        MinecraftLoaderType loaderType,
        string expectedLoaderVersion,
        string expectedMinecraftVersion)
    {
        var metadata = JsonSerializer.Deserialize<MojangVersionMetadata>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"{loaderType}'s launcher profile was empty or invalid.");
        var expectedPrefix = $"{loaderType.ToString().ToLowerInvariant()}-loader-{expectedLoaderVersion}-";
        if (!string.Equals(metadata.InheritsFrom, expectedMinecraftVersion, StringComparison.Ordinal) ||
            !metadata.Id.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{loaderType}'s launcher profile did not match the selected game and loader versions.");
        }

        return metadata;
    }

    internal static MojangVersionMetadata DeserializeForgeFamilyProfile(
        ReadOnlySpan<byte> bytes,
        MinecraftLoaderType loaderType,
        string expectedLoaderVersion,
        string expectedMinecraftVersion)
    {
        if (loaderType is not (MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge))
        {
            throw new ArgumentOutOfRangeException(nameof(loaderType));
        }

        var metadata = JsonSerializer.Deserialize<MojangVersionMetadata>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"{loaderType}'s generated launcher profile was empty or invalid.");
        var loaderMarker = loaderType == MinecraftLoaderType.Forge ? "forge" : "neoforge";
        if (string.IsNullOrWhiteSpace(metadata.Id) ||
            metadata.Id.Length > 160 ||
            metadata.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '+') ||
            !string.Equals(metadata.InheritsFrom, expectedMinecraftVersion, StringComparison.Ordinal) ||
            !metadata.Id.Contains(loaderMarker, StringComparison.OrdinalIgnoreCase) ||
            !metadata.Id.EndsWith($"-{expectedLoaderVersion}", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(metadata.MainClass))
        {
            throw new InvalidDataException($"The generated {loaderType} profile did not match Minecraft {expectedMinecraftVersion} / {expectedLoaderVersion}.");
        }

        return metadata;
    }

    private async Task<MojangVersionManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        if (_manifest is not null && DateTimeOffset.UtcNow - _manifestFetchedAt < TimeSpan.FromMinutes(30))
        {
            return _manifest;
        }

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifest is not null && DateTimeOffset.UtcNow - _manifestFetchedAt < TimeSpan.FromMinutes(30))
            {
                return _manifest;
            }

            var cachePath = Path.Combine(_cacheDirectory, "version_manifest_v2.json");
            if (_manifest is null && File.Exists(cachePath))
            {
                try
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                    _manifest = JsonSerializer.Deserialize<MojangVersionManifest>(cachedBytes, JsonOptions)
                        ?? throw new InvalidDataException("The cached Minecraft version manifest was empty.");
                    _manifestFetchedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    _manifest = null;
                }
            }

            if (_manifest is not null)
            {
                if (DateTimeOffset.UtcNow - _manifestFetchedAt >= TimeSpan.FromMinutes(30))
                {
                    StartManifestRefresh(cachePath);
                }

                return _manifest;
            }

            var bytes = await _httpClient.GetByteArrayAsync(VersionManifestUrl, cancellationToken);
            var fetched = JsonSerializer.Deserialize<MojangVersionManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The official Minecraft version manifest was empty.");
            await WriteAtomicallyAsync(cachePath, bytes, cancellationToken);
            _manifest = fetched;
            _manifestFetchedAt = DateTimeOffset.UtcNow;
            return fetched;
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private void StartManifestRefresh(string cachePath)
    {
        if (_manifestRefreshInProgress)
        {
            return;
        }

        _manifestRefreshInProgress = true;
        _ = RefreshManifestInBackgroundAsync(cachePath);
    }

    private async Task RefreshManifestInBackgroundAsync(string cachePath)
    {
        try
        {
            await _manifestGate.WaitAsync();
            try
            {
                if (_manifest is not null && DateTimeOffset.UtcNow - _manifestFetchedAt < TimeSpan.FromMinutes(30))
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var bytes = await _httpClient.GetByteArrayAsync(VersionManifestUrl, timeout.Token);
                var refreshed = JsonSerializer.Deserialize<MojangVersionManifest>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("The official Minecraft version manifest was empty.");
                await WriteAtomicallyAsync(cachePath, bytes, timeout.Token);
                _manifest = refreshed;
                _manifestFetchedAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                _manifestGate.Release();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or OperationCanceledException)
        {
            // A stale manifest is still usable; a future request can retry the background refresh.
        }
        finally
        {
            _manifestRefreshInProgress = false;
        }
    }

    private static MojangVersionMetadata DeserializeMetadata(byte[] bytes, string expectedVersionId, string versionType)
    {
        var metadata = JsonSerializer.Deserialize<MojangVersionMetadata>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"The Minecraft metadata for {expectedVersionId} was empty.");
        if (!string.Equals(metadata.Id, expectedVersionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The Minecraft metadata ID did not match {expectedVersionId}.");
        }

        metadata.VersionType = versionType;
        return metadata;
    }

    private static bool HasExpectedSha1(ReadOnlySpan<byte> bytes, string? expectedSha1)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1))
        {
            return true;
        }

        var actual = Convert.ToHexString(SHA1.HashData(bytes));
        return actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetSafeSegment(string value)
    {
        if (value.Length == 0 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("The official manifest contained an invalid version ID.");
        }

        return value;
    }
}
