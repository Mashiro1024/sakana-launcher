using System.IO.Compression;
using System.Text.Json;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class LocalFabricModService(string dataDirectory) : ILocalModService
{
    private const string DisabledSuffix = ".disabled";
    private const long MaximumFabricMetadataBytes = 1024 * 1024;

    public Task<IReadOnlyList<MinecraftModFile>> ListAsync(string instanceDirectoryName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsDirectory = GetModsDirectory(instanceDirectoryName);
        Directory.CreateDirectory(modsDirectory);

        var files = new Dictionary<string, MinecraftModFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diskName = Path.GetFileName(path);
            var isEnabled = diskName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            var fileName = isEnabled
                ? diskName
                : diskName.EndsWith($".jar{DisabledSuffix}", StringComparison.OrdinalIgnoreCase)
                    ? diskName[..^DisabledSuffix.Length]
                    : null;
            if (fileName is null || !TryNormalizeFileName(fileName, out var normalizedName))
            {
                continue;
            }

            var modFile = ReadModFile(path, normalizedName, isEnabled);
            if (!files.TryGetValue(normalizedName, out var current) || (!current.IsEnabled && modFile.IsEnabled))
            {
                files[normalizedName] = modFile;
            }
        }

        IReadOnlyList<MinecraftModFile> result = files.Values
            .OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(file => file.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task<LocalModImportResult> ImportAsync(
        string instanceDirectoryName,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            return new LocalModImportResult(0, 0);
        }

        var sources = sourcePaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("A selected mod file could not be found.", fullPath);
                }

                var fileName = Path.GetFileName(fullPath);
                if (!TryNormalizeFileName(fileName, out var normalizedName))
                {
                    throw new InvalidDataException($"Select a .jar mod file: {fileName}");
                }

                return (Path: fullPath, FileName: normalizedName);
            })
            .ToArray();

        var modsDirectory = GetModsDirectory(instanceDirectoryName);
        Directory.CreateDirectory(modsDirectory);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diskPath in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var diskName = Path.GetFileName(diskPath);
            if (diskName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                knownNames.Add(diskName);
            }
            else if (diskName.EndsWith($".jar{DisabledSuffix}", StringComparison.OrdinalIgnoreCase))
            {
                knownNames.Add(diskName[..^DisabledSuffix.Length]);
            }
        }

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

            var destination = Path.Combine(modsDirectory, source.FileName);
            var temporaryPath = Path.Combine(modsDirectory, $".{Guid.NewGuid():N}.part");
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

        return new LocalModImportResult(importedCount, skippedCount);
    }

    public Task SetEnabledAsync(
        string instanceDirectoryName,
        string fileName,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = NormalizeFileName(fileName);
        var modsDirectory = GetModsDirectory(instanceDirectoryName);
        Directory.CreateDirectory(modsDirectory);
        var enabledPath = Path.Combine(modsDirectory, normalizedName);
        var disabledPath = enabledPath + DisabledSuffix;

        if (isEnabled)
        {
            if (File.Exists(enabledPath))
            {
                return Task.CompletedTask;
            }

            if (!File.Exists(disabledPath))
            {
                throw new FileNotFoundException("The mod file no longer exists in this instance.", disabledPath);
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
                throw new FileNotFoundException("The mod file no longer exists in this instance.", enabledPath);
            }

            File.Move(enabledPath, disabledPath, overwrite: false);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string instanceDirectoryName, string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = NormalizeFileName(fileName);
        var modsDirectory = GetModsDirectory(instanceDirectoryName);
        var enabledPath = Path.Combine(modsDirectory, normalizedName);
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
            throw new FileNotFoundException("The mod file no longer exists in this instance.", enabledPath);
        }

        return Task.CompletedTask;
    }

    private string GetModsDirectory(string instanceDirectoryName) =>
        Path.Combine(dataDirectory, "instances", InstanceDirectoryName.Validate(instanceDirectoryName), "game", "mods");

    private static MinecraftModFile ReadModFile(string path, string fileName, bool isEnabled)
    {
        var displayName = Path.GetFileNameWithoutExtension(fileName);
        string? modId = null;
        string? version = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var metadataEntry = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");
            if (metadataEntry is not null && metadataEntry.Length <= MaximumFabricMetadataBytes)
            {
                using var stream = metadataEntry.Open();
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    modId = ReadString(root, "id");
                    version = ReadString(root, "version");
                    displayName = ReadString(root, "name") ?? displayName;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // Keep unreadable archives visible by their filename so users can remove them.
        }

        return new MinecraftModFile(fileName, displayName, modId, version, new FileInfo(path).Length, isEnabled);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeFileName(string fileName) => TryNormalizeFileName(fileName, out var normalizedName)
        ? normalizedName
        : throw new InvalidDataException("The mod filename is invalid. Select a .jar file in the instance's mods folder.");

    private static bool TryNormalizeFileName(string? fileName, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            fileName.Any(char.IsControl) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        normalizedName = fileName;
        return true;
    }
}
