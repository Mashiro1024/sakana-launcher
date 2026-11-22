using System.IO.Compression;
using System.Text.Json;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class LocalMinecraftPackService(string dataDirectory) : ILocalPackService
{
    private const string DisabledSuffix = ".disabled";
    private const long MaximumPackFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumMetadataBytes = 256 * 1024;
    private const int MaximumZipEntries = 10000;

    public Task<IReadOnlyList<string>> ListWorldsAsync(string instanceDirectoryName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instanceDirectory = Path.Combine(dataDirectory, "instances", InstanceDirectoryName.Validate(instanceDirectoryName), "game");
        var savesDirectory = Path.Combine(instanceDirectory, "saves");
        if (!Directory.Exists(savesDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> worlds = Directory.EnumerateDirectories(savesDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsWorldDirectory(path, cancellationToken))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray()!;
        return Task.FromResult(worlds);
    }

    private static bool IsWorldDirectory(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var name = Path.GetFileName(path);
            return TryNormalizeWorldName(name, out _) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                File.Exists(Path.Combine(path, "level.dat"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<MinecraftLocalPack>> ListAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string? worldName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetPackDirectory(instanceDirectoryName, kind, worldName);
        Directory.CreateDirectory(directory);
        var packs = new Dictionary<string, MinecraftLocalPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diskName = Path.GetFileName(path);
            var isEnabled = diskName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var fileName = isEnabled
                ? diskName
                : diskName.EndsWith($".zip{DisabledSuffix}", StringComparison.OrdinalIgnoreCase)
                    ? diskName[..^DisabledSuffix.Length]
                    : null;
            if (fileName is null || !TryNormalizeFileName(fileName, out var normalizedName))
            {
                continue;
            }

            var pack = ReadPack(path, normalizedName, kind, isEnabled);
            if (!packs.TryGetValue(normalizedName, out var current) || (!current.IsEnabled && pack.IsEnabled))
            {
                packs[normalizedName] = pack;
            }
        }

        IReadOnlyList<MinecraftLocalPack> result = packs.Values
            .OrderBy(pack => pack.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pack => pack.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task<LocalPackImportResult> ImportAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        IReadOnlyCollection<string> sourcePaths,
        string? worldName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            return new LocalPackImportResult(0, 0);
        }

        var sources = sourcePaths.Select(path => ValidateSource(path, kind, cancellationToken)).ToArray();
        var directory = GetPackDirectory(instanceDirectoryName, kind, worldName);
        Directory.CreateDirectory(directory);
        var knownNames = GetKnownNames(directory);
        var importedCount = 0;
        var skippedCount = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!knownNames.Add(source.FileName))
            {
                skippedCount++;
                continue;
            }

            var destination = Path.Combine(directory, source.FileName);
            var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.part");
            try
            {
                await using (var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, destination, overwrite: false);
                importedCount++;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        return new LocalPackImportResult(importedCount, skippedCount);
    }

    public Task SetEnabledAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string fileName,
        bool isEnabled,
        string? worldName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = NormalizeFileName(fileName);
        var directory = GetPackDirectory(instanceDirectoryName, kind, worldName);
        Directory.CreateDirectory(directory);
        var enabledPath = Path.Combine(directory, normalizedName);
        var disabledPath = enabledPath + DisabledSuffix;
        if (isEnabled)
        {
            if (File.Exists(enabledPath))
            {
                return Task.CompletedTask;
            }

            if (!File.Exists(disabledPath))
            {
                throw new FileNotFoundException("The pack file no longer exists in this instance.", disabledPath);
            }

            File.Move(disabledPath, enabledPath, overwrite: false);
        }
        else
        {
            if (File.Exists(disabledPath))
            {
                return Task.CompletedTask;
            }

            if (!File.Exists(enabledPath))
            {
                throw new FileNotFoundException("The pack file no longer exists in this instance.", enabledPath);
            }

            File.Move(enabledPath, disabledPath, overwrite: false);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(
        string instanceDirectoryName,
        MinecraftLocalPackKind kind,
        string fileName,
        string? worldName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = NormalizeFileName(fileName);
        var directory = GetPackDirectory(instanceDirectoryName, kind, worldName);
        var enabledPath = Path.Combine(directory, normalizedName);
        var disabledPath = enabledPath + DisabledSuffix;
        var removed = false;
        if (File.Exists(enabledPath))
        {
            File.Delete(enabledPath);
            removed = true;
        }

        if (File.Exists(disabledPath))
        {
            File.Delete(disabledPath);
            removed = true;
        }

        if (!removed)
        {
            throw new FileNotFoundException("The pack file no longer exists in this instance.", enabledPath);
        }

        return Task.CompletedTask;
    }

    private SourcePack ValidateSource(string sourcePath, MinecraftLocalPackKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("A selected pack file could not be found.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length is <= 0 or > MaximumPackFileBytes)
        {
            throw new InvalidDataException("Pack archives must be larger than 0 bytes and no larger than 2 GB.");
        }

        var fileName = Path.GetFileName(fullPath);
        if (!TryNormalizeFileName(fileName, out var normalizedName))
        {
            throw new InvalidDataException($"Select a .zip pack archive: {fileName}");
        }

        ValidateArchive(fullPath, kind, cancellationToken);
        return new SourcePack(fullPath, normalizedName);
    }

    private static void ValidateArchive(string path, MinecraftLocalPackKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaximumZipEntries)
        {
            throw new InvalidDataException($"The pack archive contains more than {MaximumZipEntries} entries.");
        }

        var isSupported = kind switch
        {
            MinecraftLocalPackKind.ResourcePack => archive.GetEntry("pack.mcmeta") is not null,
            MinecraftLocalPackKind.DataPack => archive.GetEntry("pack.mcmeta") is not null &&
                archive.Entries.Any(entry => entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)),
            MinecraftLocalPackKind.ShaderPack => archive.Entries.Any(entry =>
                entry.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FullName, "shaders", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FullName, "shaders.properties", StringComparison.OrdinalIgnoreCase)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (!isSupported)
        {
            var expected = kind switch
            {
                MinecraftLocalPackKind.ResourcePack => "root pack.mcmeta",
                MinecraftLocalPackKind.DataPack => "root pack.mcmeta and a data/ folder",
                _ => "a shaders/ folder"
            };
            throw new InvalidDataException($"This archive does not look like a {GetDisplayName(kind)}; it must contain {expected}.");
        }
    }

    private static MinecraftLocalPack ReadPack(string path, string fileName, MinecraftLocalPackKind kind, bool isEnabled)
    {
        var displayName = Path.GetFileNameWithoutExtension(fileName);
        string? details = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if ((kind is MinecraftLocalPackKind.ResourcePack or MinecraftLocalPackKind.DataPack) && archive.GetEntry("pack.mcmeta") is { Length: <= MaximumMetadataBytes } metadataEntry)
            {
                using var stream = metadataEntry.Open();
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 24 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pack", out var pack) && pack.ValueKind == JsonValueKind.Object)
                {
                    if (pack.TryGetProperty("description", out var description))
                    {
                        details = GetDescriptionText(description);
                    }

                    if (pack.TryGetProperty("pack_format", out var format) && format.TryGetInt32(out var packFormat))
                    {
                        var formatName = kind == MinecraftLocalPackKind.DataPack ? "数据包格式" : "资源格式";
                        details = string.IsNullOrWhiteSpace(details) ? $"{formatName} {packFormat}" : $"{details} · {formatName} {packFormat}";
                    }
                }
            }
            else if (kind == MinecraftLocalPackKind.ShaderPack)
            {
                details = archive.GetEntry("shaders/gbuffers_terrain.vsh") is not null || archive.Entries.Any(entry => entry.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
                    ? "光影包"
                    : "未读取到光影元数据";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // Keep damaged archives visible so users can remove them.
        }

        return new MinecraftLocalPack(fileName, displayName, details, new FileInfo(path).Length, isEnabled);
    }

    private static string? GetDescriptionText(JsonElement description)
    {
        var text = description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Array => string.Join("", description.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").ValueKind == JsonValueKind.String ? item.GetProperty("text").GetString() : string.Empty)),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Length > 240 ? $"{text[..237]}…" : text;
    }

    private static HashSet<string> GetKnownNames(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(fileName);
            }
            else if (fileName.EndsWith($".zip{DisabledSuffix}", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(fileName[..^DisabledSuffix.Length]);
            }
        }

        return names;
    }

    private string GetPackDirectory(string instanceDirectoryName, MinecraftLocalPackKind kind, string? worldName)
    {
        var gameDirectory = Path.Combine(dataDirectory, "instances", InstanceDirectoryName.Validate(instanceDirectoryName), "game");
        return kind switch
        {
            MinecraftLocalPackKind.ResourcePack => Path.Combine(gameDirectory, "resourcepacks"),
            MinecraftLocalPackKind.ShaderPack => Path.Combine(gameDirectory, "shaderpacks"),
            MinecraftLocalPackKind.DataPack => GetWorldDataPackDirectory(gameDirectory, worldName),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static string GetWorldDataPackDirectory(string gameDirectory, string? worldName)
    {
        if (!TryNormalizeWorldName(worldName, out var normalizedName))
        {
            throw new InvalidOperationException("Choose a world before managing its data packs.");
        }

        var savesDirectory = Path.GetFullPath(Path.Combine(gameDirectory, "saves"));
        var worldDirectory = Path.GetFullPath(Path.Combine(savesDirectory, normalizedName));
        var savesPrefix = savesDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!worldDirectory.StartsWith(savesPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(worldDirectory) ||
            (File.GetAttributes(worldDirectory) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(Path.Combine(worldDirectory, "level.dat")))
        {
            throw new DirectoryNotFoundException("The selected world could not be found in this instance.");
        }

        return Path.Combine(worldDirectory, "datapacks");
    }

    private static bool TryNormalizeWorldName(string? worldName, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(worldName) || worldName is "." or ".." ||
            !string.Equals(Path.GetFileName(worldName), worldName, StringComparison.Ordinal) ||
            worldName.Any(char.IsControl) || Path.GetInvalidFileNameChars().Any(worldName.Contains))
        {
            return false;
        }

        normalizedName = worldName;
        return true;
    }

    private static string GetDisplayName(MinecraftLocalPackKind kind) => kind switch
    {
        MinecraftLocalPackKind.ResourcePack => "资源包",
        MinecraftLocalPackKind.ShaderPack => "光影包",
        MinecraftLocalPackKind.DataPack => "数据包",
        _ => "内容包"
    };

    private static string NormalizeFileName(string fileName) => TryNormalizeFileName(fileName, out var normalizedName)
        ? normalizedName
        : throw new InvalidDataException("The pack filename is invalid.");

    private static bool TryNormalizeFileName(string? fileName, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            fileName.Any(char.IsControl) || Path.GetInvalidFileNameChars().Any(fileName.Contains))
        {
            return false;
        }

        normalizedName = fileName;
        return true;
    }

    private sealed record SourcePack(string Path, string FileName);
}
