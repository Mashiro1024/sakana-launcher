using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class ModrinthModpackService(HttpClient httpClient, DownloadConcurrencyOptions? downloadConcurrencyOptions = null) : IModrinthModpackService
{
    private const string ManifestPath = "modrinth.index.json";
    private const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const long MaximumOverrideFileBytes = 1024L * 1024 * 1024;
    private const long MaximumOverridesBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumContentBytes = 20L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 30000;
    private const int MaximumManifestFiles = 20000;
    private static readonly string[] AllowedDownloadDomains = ["modrinth.com", "github.com", "githubusercontent.com", "gitlab.com"];
    private static readonly HashSet<string> PersonalRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old", "usercache.json", "usernamecache.json", "launcher_profiles.json"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ParallelArtifactDownloader _downloader = new(httpClient, downloadConcurrencyOptions);

    public async Task<ModrinthModpackDescription> InspectAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadPackAsync(archivePath, cancellationToken);
        return Describe(manifest);
    }

    public async Task<ModrinthModpackUpdatePreview> PreviewUpdateAsync(
        InstanceProfile profile,
        string dataDirectory,
        string newArchivePath,
        string newVersionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ModrinthPackPath) || string.IsNullOrWhiteSpace(profile.ModrinthProjectId))
        {
            throw new InvalidOperationException("Only a Modrinth-linked .mrpack instance can check for pack updates.");
        }

        var oldArchivePath = ResolveDataPath(dataDirectory, profile.ModrinthPackPath);
        EnsureNoReparsePoint(oldArchivePath);
        EnsureNoReparsePoint(Path.GetFullPath(newArchivePath));
        EnsureNoReparsePoint(Path.Combine(Path.GetFullPath(dataDirectory), "instances"));
        var gameDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "instances", InstanceDirectoryName.ForProfile(profile), "game");
        EnsureNoReparsePoint(gameDirectory);
        var oldManifest = await ReadPackAsync(oldArchivePath, cancellationToken);
        var newManifest = await ReadPackAsync(newArchivePath, cancellationToken);
        var oldDescription = Describe(oldManifest);
        var newDescription = Describe(newManifest);
        if (!string.Equals(oldDescription.MinecraftVersion, profile.VersionId, StringComparison.Ordinal) ||
            oldDescription.LoaderType != profile.LoaderType ||
            !string.Equals(oldDescription.LoaderVersion, profile.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The saved source pack no longer matches this instance's Minecraft and loader versions.");
        }

        var oldFiles = await ReadPackFileHashesAsync(oldArchivePath, oldManifest, cancellationToken);
        var newFiles = await ReadPackFileHashesAsync(newArchivePath, newManifest, cancellationToken);
        var previouslySelectedOptional = DeserializeSelectedOptionalFiles(profile.ModrinthSelectedOptionalFilesJson).ToHashSet(StringComparer.Ordinal);
        var selectedOptional = previouslySelectedOptional
            .Intersect(newDescription.OptionalFiles.Select(file => file.Path), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var filesAppliedInPlace = newManifest.Files
            .Where(file => GetClientEnvironment(file) == PackEnvironment.Required ||
                (GetClientEnvironment(file) == PackEnvironment.Optional && selectedOptional.Contains(file.Path)))
            .Select(file => file.Path)
            .Concat(GetOverridePaths(newArchivePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = oldFiles.Keys.Concat(newFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ModrinthPackFileChange>();
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            oldFiles.TryGetValue(relativePath, out var oldFile);
            newFiles.TryGetValue(relativePath, out var newFile);
            var kind = (oldFile, newFile) switch
            {
                (null, not null) => ModrinthPackChangeKind.Added,
                (not null, null) => ModrinthPackChangeKind.Removed,
                (not null, not null) when !string.Equals(oldFile.Sha1, newFile.Sha1, StringComparison.OrdinalIgnoreCase) => ModrinthPackChangeKind.Updated,
                _ => (ModrinthPackChangeKind?)null
            };

            var localPath = GetLocalPackFilePath(gameDirectory, relativePath);
            var hasLocalFile = localPath is not null && File.Exists(localPath);
            var hasLocalChanges = false;
            if (hasLocalFile)
            {
                EnsureNoReparsePoint(localPath!);
                var localHashes = await ComputeFileHashesAsync(localPath!, cancellationToken);
                hasLocalChanges = oldFile is null || !string.Equals(localHashes.Sha1, oldFile.Sha1, StringComparison.OrdinalIgnoreCase);
            }
            else if (Directory.Exists(ResolveDataPathForGame(gameDirectory, relativePath)))
            {
                hasLocalFile = true;
                hasLocalChanges = true;
            }

            if (kind is not null || hasLocalChanges)
            {
                var changeKind = kind ?? ModrinthPackChangeKind.Unchanged;
                var isPersonalData = hasLocalFile && IsPersonalDataPath(relativePath);
                var requiresConflictChoice = filesAppliedInPlace.Contains(relativePath) &&
                    localPath is not null &&
                    (hasLocalChanges || isPersonalData) &&
                    changeKind is ModrinthPackChangeKind.Added or ModrinthPackChangeKind.Updated;
                changes.Add(new ModrinthPackFileChange(relativePath, changeKind, hasLocalChanges, hasLocalFile, requiresConflictChoice, isPersonalData));
            }
        }

        return new ModrinthModpackUpdatePreview(
            profile.ModrinthPackVersionNumber ?? oldManifest.VersionId,
            string.IsNullOrWhiteSpace(newVersionNumber) ? newManifest.VersionId : newVersionNumber,
            oldDescription.MinecraftVersion,
            newDescription.MinecraftVersion,
            oldDescription.LoaderType,
            newDescription.LoaderType,
            oldDescription.LoaderVersion,
            newDescription.LoaderVersion,
            changes);
    }

    public async Task<ModrinthModpackUpdateResult> ApplyUpdateAsync(
        InstanceProfile profile,
        string dataDirectory,
        string newArchivePath,
        IReadOnlyCollection<string> replaceConflictPaths,
        Func<IReadOnlyCollection<string>, CancellationToken, Task> persistUpdatedProfileAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(replaceConflictPaths);
        ArgumentNullException.ThrowIfNull(persistUpdatedProfileAsync);
        if (string.IsNullOrWhiteSpace(profile.ModrinthPackPath) || string.IsNullOrWhiteSpace(profile.ModrinthProjectId))
        {
            throw new InvalidOperationException("Only a Modrinth-linked .mrpack instance can be updated in place.");
        }

        var oldArchivePath = ResolveDataPath(dataDirectory, profile.ModrinthPackPath);
        EnsureNoReparsePoint(oldArchivePath);
        EnsureNoReparsePoint(Path.GetFullPath(newArchivePath));
        var oldManifest = await ReadPackAsync(oldArchivePath, cancellationToken);
        var newManifest = await ReadPackAsync(newArchivePath, cancellationToken);
        var oldDescription = Describe(oldManifest);
        var newDescription = Describe(newManifest);
        if (!string.Equals(oldDescription.MinecraftVersion, profile.VersionId, StringComparison.Ordinal) ||
            oldDescription.LoaderType != profile.LoaderType ||
            !string.Equals(oldDescription.LoaderVersion, profile.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The saved source pack no longer matches this instance's Minecraft and loader versions.");
        }

        if (!string.Equals(newDescription.MinecraftVersion, profile.VersionId, StringComparison.Ordinal) ||
            newDescription.LoaderType != profile.LoaderType ||
            !string.Equals(newDescription.LoaderVersion, profile.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This release changes Minecraft or the mod loader. Create an updated copy to keep the existing instance safe.");
        }

        var instanceDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "instances", InstanceDirectoryName.ForProfile(profile));
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        EnsureNoReparsePoint(Path.Combine(Path.GetFullPath(dataDirectory), "instances"));
        EnsureNoReparsePoint(instanceDirectory);
        EnsureNoReparsePoint(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException("The installed instance game directory could not be found.");
        }

        var oldFiles = await ReadPackFileHashesAsync(oldArchivePath, oldManifest, cancellationToken);
        var newFiles = await ReadPackFileHashesAsync(newArchivePath, newManifest, cancellationToken);
        var selectedConflictPaths = replaceConflictPaths
            .Select(ValidateRelativePackPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldOptional = DeserializeSelectedOptionalFiles(profile.ModrinthSelectedOptionalFilesJson)
            .ToHashSet(StringComparer.Ordinal);
        var availableOptional = newDescription.OptionalFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var selectedOptional = oldOptional.Where(availableOptional.Contains).ToArray();
        var selectedOptionalSet = selectedOptional.ToHashSet(StringComparer.Ordinal);
        var selectedClientFiles = newManifest.Files
            .Where(file => GetClientEnvironment(file) == PackEnvironment.Required ||
                (GetClientEnvironment(file) == PackEnvironment.Optional && selectedOptionalSet.Contains(file.Path)))
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var overridePaths = GetOverridePaths(newArchivePath);
        var stagedPaths = selectedClientFiles.Keys.Concat(overridePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updateRoot = Path.Combine(Path.GetFullPath(dataDirectory), "downloads", $"modpack-update-{Guid.NewGuid():N}");
        var downloadsRoot = Path.GetDirectoryName(updateRoot)!;
        var stagedRoot = Path.Combine(updateRoot, "staged");
        var backupRoot = Path.Combine(updateRoot, "backups");
        var changes = new List<FileUpdateJournalEntry>();
        var installedFiles = 0;
        var updatedFiles = 0;
        var preservedLocalFiles = 0;
        var retainedRemovedFiles = oldFiles.Keys.Count(path => !newFiles.ContainsKey(path));

        var preserveUpdateRoot = false;
        try
        {
            EnsureNoReparsePoint(downloadsRoot);
            Directory.CreateDirectory(downloadsRoot);
            EnsureNoReparsePoint(downloadsRoot);
            Directory.CreateDirectory(stagedRoot);
            Directory.CreateDirectory(backupRoot);
            var remoteArtifacts = selectedClientFiles.Values.Select(file => new InstallArtifact(
                file.Path,
                file.Downloads[0],
                file.Hashes.Sha1,
                file.FileSize,
                Sha512: file.Hashes.Sha512,
                FallbackUrls: file.Downloads.Skip(1).ToArray(),
                AllowedHosts: AllowedDownloadDomains)).ToArray();
            if (remoteArtifacts.Length > 0)
            {
                await _downloader.DownloadAllAsync(profile.Id, stagedRoot, remoteArtifacts, null, cancellationToken, forceRevalidation: true);
            }

            await ExtractOverridesToDirectoryAsync(newArchivePath, stagedRoot, cancellationToken);
            foreach (var relativePath in overridePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = ResolveDataPathForGame(stagedRoot, relativePath);
                var stagedHash = (await ComputeFileHashesAsync(stagedPath, cancellationToken)).Sha1;
                if (!newFiles.TryGetValue(relativePath, out var expectedFile) ||
                    !string.Equals(stagedHash, expectedFile.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The staged override failed its SHA-1 check: {relativePath}");
                }
            }

            var orderedPaths = stagedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var relativePath in orderedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = ResolveDataPathForGame(stagedRoot, relativePath);
                if (!File.Exists(stagedPath))
                {
                    throw new InvalidDataException($"The new modpack did not stage an expected file: {relativePath}");
                }

                var currentPath = GetLocalPackFilePath(gameDirectory, relativePath);
                var canonicalPath = ResolveDataPathForGame(gameDirectory, relativePath);
                if (currentPath is null && Directory.Exists(canonicalPath))
                {
                    // Do not replace a directory with a pack file, even if it collides with a newly added path.
                    preservedLocalFiles++;
                    continue;
                }

                var targetPath = currentPath ?? canonicalPath;
                EnsureSafeGameTarget(gameDirectory, targetPath);
                var hasCurrentFile = File.Exists(targetPath);
                var localHash = hasCurrentFile
                    ? (await ComputeFileHashesAsync(targetPath, cancellationToken)).Sha1
                    : null;
                var oldFileExists = oldFiles.TryGetValue(relativePath, out var oldFile);
                var expectedNewHash = newFiles.TryGetValue(relativePath, out var newFile) ? newFile.Sha1 : null;
                var changedByUser = hasCurrentFile && (!oldFileExists ||
                    !string.Equals(localHash, oldFile!.Sha1, StringComparison.OrdinalIgnoreCase) ||
                    IsPersonalDataPath(relativePath));

                if (hasCurrentFile && string.Equals(localHash, expectedNewHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (changedByUser && !selectedConflictPaths.Contains(relativePath))
                {
                    preservedLocalFiles++;
                    continue;
                }

                var directoryName = Path.GetDirectoryName(targetPath)!;
                EnsureSafeGameTarget(gameDirectory, directoryName, isDirectory: true);
                Directory.CreateDirectory(directoryName);
                var backupPath = Path.Combine(backupRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (hasCurrentFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    EnsureSafeGameTarget(gameDirectory, targetPath);
                    var latestHash = (await ComputeFileHashesAsync(targetPath, cancellationToken)).Sha1;
                    if (!string.Equals(latestHash, localHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"The local file changed while the update was being prepared: {relativePath}");
                    }

                    await CopyFileForTransactionAsync(targetPath, backupPath, cancellationToken);
                    var backupHash = (await ComputeFileHashesAsync(backupPath, cancellationToken)).Sha1;
                    if (!string.Equals(backupHash, localHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"The update backup did not match the original file: {relativePath}");
                    }
                }

                var journalEntry = new FileUpdateJournalEntry(targetPath, backupPath, HadOriginal: hasCurrentFile);
                changes.Add(journalEntry);
                EnsureSafeGameTarget(gameDirectory, targetPath);
                var replacementPath = $"{targetPath}.{Guid.NewGuid():N}.update";
                try
                {
                    File.Copy(stagedPath, replacementPath, overwrite: false);
                    File.Move(replacementPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(replacementPath)) File.Delete(replacementPath);
                }

                if (hasCurrentFile) updatedFiles++;
                else installedFiles++;
            }

            await persistUpdatedProfileAsync(selectedOptional, cancellationToken);
            return new ModrinthModpackUpdateResult(installedFiles, updatedFiles, preservedLocalFiles, retainedRemovedFiles);
        }
        catch (Exception updateException)
        {
            try
            {
                await RollBackFileChangesAsync(gameDirectory, changes);
            }
            catch (Exception rollbackException)
            {
                preserveUpdateRoot = true;
                throw new IOException(
                    $"The Modrinth update failed and Sakana could not restore every file. Recovery data is preserved at {updateRoot}.",
                    new AggregateException(updateException, rollbackException));
            }

            throw;
        }
        finally
        {
            try
            {
                if (!preserveUpdateRoot && Directory.Exists(updateRoot))
                {
                    EnsureNoReparsePoint(updateRoot);
                    Directory.Delete(updateRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // The update is already committed or rolled back. A leftover transaction directory is safe to inspect and remove later.
            }
            catch (UnauthorizedAccessException)
            {
                // The update is already committed or rolled back. A leftover transaction directory is safe to inspect and remove later.
            }
        }
    }

    public async Task InstallAsync(
        InstanceProfile profile,
        string dataDirectory,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.ModrinthPackPath))
        {
            return;
        }

        var sourcePath = ResolveDataPath(dataDirectory, profile.ModrinthPackPath);
        var manifest = await ReadPackAsync(sourcePath, cancellationToken);
        var description = Describe(manifest);
        if (!string.Equals(description.MinecraftVersion, profile.VersionId, StringComparison.Ordinal) ||
            description.LoaderType != profile.LoaderType ||
            !string.Equals(description.LoaderVersion, profile.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The imported modpack no longer matches this instance's Minecraft and loader versions.");
        }

        var selectedOptionalFiles = DeserializeSelectedOptionalFiles(profile.ModrinthSelectedOptionalFilesJson);
        var availableOptionalFiles = description.OptionalFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (selectedOptionalFiles.Any(path => !availableOptionalFiles.Contains(path)))
        {
            throw new InvalidDataException("The saved optional-file selection no longer matches the imported modpack.");
        }

        var instanceDirectory = Path.Combine(dataDirectory, "instances", InstanceDirectoryName.ForProfile(profile));
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        Directory.CreateDirectory(gameDirectory);
        await ApplyOverridesAsync(profile.Id, sourcePath, gameDirectory, progress, cancellationToken);

        var clientFiles = manifest.Files
            .Where(file => GetClientEnvironment(file) == PackEnvironment.Required ||
                (GetClientEnvironment(file) == PackEnvironment.Optional && selectedOptionalFiles.Contains(file.Path, StringComparer.Ordinal)))
            .Select(file => new InstallArtifact(
                file.Path,
                file.Downloads[0],
                file.Hashes.Sha1,
                file.FileSize,
                Sha512: file.Hashes.Sha512,
                FallbackUrls: file.Downloads.Skip(1).ToArray(),
                AllowedHosts: AllowedDownloadDomains))
            .ToArray();

        if (clientFiles.Length > 0)
        {
            await _downloader.DownloadAllAsync(profile.Id, gameDirectory, clientFiles, progress, cancellationToken);
        }
    }

    public async Task<ModrinthModpackExportResult> ExportAsync(
        InstanceProfile profile,
        string dataDirectory,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install this instance before exporting it as a modpack.");
        }

        var instanceDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "instances", InstanceDirectoryName.ForProfile(profile));
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        EnsureNoReparsePoint(Path.Combine(Path.GetFullPath(dataDirectory), "instances"));
        EnsureNoReparsePoint(instanceDirectory);
        EnsureNoReparsePoint(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException("The instance game directory could not be found.");
        }

        var outputPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(outputPath), ".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a destination with the .mrpack extension.", nameof(destinationPath));
        }

        EnsureNoReparsePoint(outputPath);
        var indexFiles = new Dictionary<string, PackFile>(StringComparer.OrdinalIgnoreCase);
        var omittedModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackingIndex = await ReadExportTrackingIndexAsync(dataDirectory, profile, cancellationToken);
        var importedManifest = await ReadImportedManifestAsync(dataDirectory, profile, cancellationToken);
        if (importedManifest is not null)
        {
            var selectedOptional = DeserializeSelectedOptionalFiles(profile.ModrinthSelectedOptionalFilesJson).ToHashSet(StringComparer.Ordinal);
            foreach (var remoteFile in importedManifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var environment = GetClientEnvironment(remoteFile);
                if (environment == PackEnvironment.Unsupported ||
                    (environment == PackEnvironment.Optional && !selectedOptional.Contains(remoteFile.Path)))
                {
                    continue;
                }

                var localPath = GetLocalPackFilePath(gameDirectory, remoteFile.Path);
                if (localPath is null)
                {
                    continue;
                }

                EnsureNoReparsePoint(localPath);
                var hashes = await ComputeFileHashesAsync(localPath, cancellationToken);
                if (!string.Equals(hashes.Sha1, remoteFile.Hashes.Sha1, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(hashes.Sha512, remoteFile.Hashes.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var clientEnvironment = localPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? nameof(PackEnvironment.Optional).ToLowerInvariant()
                    : environment.ToString().ToLowerInvariant();
                indexFiles[remoteFile.Path] = CopyPackFile(remoteFile, hashes, clientEnvironment);
            }
        }

        foreach (var tracked in trackingIndex.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = $"mods/{tracked.FileName}";
            var localPath = GetLocalPackFilePath(gameDirectory, relativePath);
            if (localPath is null)
            {
                continue;
            }

            EnsureNoReparsePoint(localPath);
            var hashes = await ComputeFileHashesAsync(localPath, cancellationToken);
            if ((tracked.Sha1 is not null && !string.Equals(hashes.Sha1, tracked.Sha1, StringComparison.OrdinalIgnoreCase)) ||
                (tracked.Sha512 is not null && !string.Equals(hashes.Sha512, tracked.Sha512, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var downloadUrl = tracked.Url;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = await GetTrackedModDownloadUrlAsync(tracked, cancellationToken);
            }

            ValidateDownloadUrl(downloadUrl, relativePath);
            var clientEnvironment = localPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? nameof(PackEnvironment.Optional).ToLowerInvariant()
                : tracked.ClientEnvironment;
            indexFiles[relativePath] = CreatePackFile(relativePath, hashes, downloadUrl, clientEnvironment.ToLowerInvariant(), tracked.ServerEnvironment.ToLowerInvariant());
        }

        var localModNames = await Task.Run(() => EnumerateLocalModFileNames(gameDirectory, cancellationToken), cancellationToken);
        var includedModNames = indexFiles.Keys
            .Where(IsModJarPath)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var localModName in localModNames)
        {
            if (!includedModNames.Contains(localModName)) omittedModNames.Add(localModName);
        }

        var manifest = CreateExportManifest(profile, indexFiles.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList());
        ValidateManifest(manifest);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The exported modpack manifest exceeds the 8 MB limit.");
        }

        var manifestPaths = indexFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = await Task.Run(
            () => CollectOverrideFiles(gameDirectory, outputPath, manifestPaths, cancellationToken),
            cancellationToken);
        var overridesBytes = overrides.Sum(item => item.Length);
        if (overrides.Count + indexFiles.Count + 1 > MaximumArchiveEntries || overridesBytes > MaximumOverridesBytes)
        {
            throw new InvalidDataException("This instance has too many override files or more than 4 GB of overrides to export safely.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        EnsureNoReparsePoint(outputDirectory);
        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true);
                var manifestEntry = archive.CreateEntry(ManifestPath, CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await manifestStream.WriteAsync(manifestBytes, cancellationToken);
                }

                foreach (var item in overrides)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry($"overrides/{item.RelativePath}", CompressionLevel.Optimal);
                    await using var source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
                }
            }

            if (new FileInfo(temporaryPath).Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("The exported .mrpack exceeds the 4 GB archive limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoint(outputPath);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new ModrinthModpackExportResult(indexFiles.Count, omittedModNames.Count, overrides.Count, overridesBytes);
    }

    private async Task<PackManifest?> ReadImportedManifestAsync(
        string dataDirectory,
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ModrinthPackPath)) return null;
        var path = ResolveDataPath(dataDirectory, profile.ModrinthPackPath);
        EnsureNoReparsePoint(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The source .mrpack for this instance is missing, so its original Modrinth downloads cannot be preserved in the export.", path);
        }
        var manifest = await ReadPackAsync(path, cancellationToken);
        var description = Describe(manifest);
        if (!string.Equals(description.MinecraftVersion, profile.VersionId, StringComparison.Ordinal) ||
            description.LoaderType != profile.LoaderType ||
            !string.Equals(description.LoaderVersion, profile.LoaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The imported modpack no longer matches this instance's Minecraft and loader versions.");
        }

        return manifest;
    }

    private static PackManifest CreateExportManifest(InstanceProfile profile, List<PackFile> files)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft"] = profile.VersionId
        };
        if (profile.LoaderType != MinecraftLoaderType.Vanilla)
        {
            var loaderKey = profile.LoaderType switch
            {
                MinecraftLoaderType.Fabric => "fabric-loader",
                MinecraftLoaderType.Quilt => "quilt-loader",
                MinecraftLoaderType.Forge => "forge",
                MinecraftLoaderType.NeoForge => "neoforge",
                _ => throw new NotSupportedException($"Sakana cannot export the {profile.LoaderType} loader.")
            };
            if (string.IsNullOrWhiteSpace(profile.LoaderVersion))
            {
                throw new InvalidDataException("The instance does not have a mod loader version to export.");
            }

            dependencies[loaderKey] = profile.LoaderVersion;
        }

        return new PackManifest
        {
            FormatVersion = 1,
            Game = "minecraft",
            VersionId = "1.0.0",
            Name = profile.Name,
            Summary = "Exported with Sakana Launcher",
            Dependencies = dependencies,
            Files = files
        };
    }

    private async Task<ExportTrackingIndex> ReadExportTrackingIndexAsync(
        string dataDirectory,
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        var instancesRoot = Path.Combine(Path.GetFullPath(dataDirectory), "instances");
        var instanceRoot = Path.Combine(instancesRoot, InstanceDirectoryName.ForProfile(profile));
        var metadataDirectory = Path.Combine(instanceRoot, ".sakana");
        var path = Path.Combine(metadataDirectory, "modrinth-mods.json");
        EnsureNoReparsePoint(instancesRoot);
        EnsureNoReparsePoint(instanceRoot);
        EnsureNoReparsePoint(metadataDirectory);
        EnsureNoReparsePoint(path);
        if (!File.Exists(path)) return ExportTrackingIndex.Empty;
        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Sakana's Modrinth mod tracking file exceeds the 8 MB export limit.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var index = JsonSerializer.Deserialize<ExportTrackingIndex>(json, JsonOptions)
            ?? throw new InvalidDataException("Sakana's Modrinth mod tracking file is invalid.");
        if (index.SchemaVersion != 1 || index.Mods is null || index.Mods.Count > MaximumManifestFiles)
        {
            throw new InvalidDataException("Sakana's Modrinth mod tracking file uses an unsupported format.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in index.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.FileName) ||
                !string.Equals(Path.GetFileName(mod.FileName), mod.FileName, StringComparison.Ordinal) ||
                !mod.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                mod.FileName.Any(char.IsControl) ||
                !seenNames.Add(mod.FileName) ||
                !IsValidModrinthId(mod.ProjectId) || !IsValidModrinthId(mod.VersionId) ||
                string.IsNullOrWhiteSpace(mod.VersionNumber) || mod.VersionNumber.Length > 100 ||
                (mod.Sha1 is null && mod.Sha512 is null) ||
                (mod.Sha1 is not null && !IsValidHash(mod.Sha1, 40)) ||
                (mod.Sha512 is not null && !IsValidHash(mod.Sha512, 128)) ||
                !IsValidEnvironment(mod.ClientEnvironment) || !IsValidEnvironment(mod.ServerEnvironment))
            {
                throw new InvalidDataException("Sakana's Modrinth mod tracking file contains invalid entries.");
            }
        }

        return index;
    }

    private async Task<string> GetTrackedModDownloadUrlAsync(ExportTrackedMod mod, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(mod.VersionId)}");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<ExportRemoteVersion>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty version while exporting this modpack.");
        var matchingFile = version.Files.FirstOrDefault(file =>
            string.Equals(file.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase) &&
            (mod.Sha512 is null || string.Equals(file.Hashes.Sha512, mod.Sha512, StringComparison.OrdinalIgnoreCase)) &&
            (mod.Sha1 is null || string.Equals(file.Hashes.Sha1, mod.Sha1, StringComparison.OrdinalIgnoreCase)));
        if (matchingFile is null)
        {
            throw new InvalidDataException($"Modrinth no longer lists the tracked file {mod.FileName} for version {mod.VersionNumber}.");
        }

        ValidateDownloadUrl(matchingFile.Url, $"mods/{mod.FileName}");
        return matchingFile.Url;
    }

    private static async Task<FileHashes> ComputeFileHashesAsync(string path, CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            length += read;
            sha1.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);
        }

        return new FileHashes(Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha512.GetHashAndReset()), length);
    }

    private static PackFile CopyPackFile(PackFile source, FileHashes hashes, string clientEnvironment) => new()
    {
        Path = ValidateRelativePackPath(source.Path),
        Hashes = new PackFileHashes { Sha1 = hashes.Sha1, Sha512 = hashes.Sha512 },
        Env = new PackFileEnvironment { Client = clientEnvironment, Server = source.Env?.Server },
        Downloads = source.Downloads.ToList(),
        FileSize = hashes.Length
    };

    private static PackFile CreatePackFile(string relativePath, FileHashes hashes, string url, string clientEnvironment, string serverEnvironment) => new()
    {
        Path = ValidateRelativePackPath(relativePath),
        Hashes = new PackFileHashes { Sha1 = hashes.Sha1, Sha512 = hashes.Sha512 },
        Env = new PackFileEnvironment { Client = clientEnvironment, Server = serverEnvironment },
        Downloads = [url],
        FileSize = hashes.Length
    };

    private static string? GetLocalPackFilePath(string gameDirectory, string relativePath)
    {
        var path = ResolveDataPathForGame(gameDirectory, ValidateRelativePackPath(relativePath));
        if (File.Exists(path)) return path;
        if (IsModJarPath(relativePath) && File.Exists(path + ".disabled")) return path + ".disabled";
        return null;
    }

    private static List<ExportOverride> CollectOverrideFiles(
        string gameDirectory,
        string outputPath,
        IReadOnlySet<string> manifestPaths,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(gameDirectory);
        var results = new List<ExportOverride>();
        long totalBytes = 0;
        var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mods", "assets", "libraries", "versions", "natives", "logs", "crash-reports", "screenshots", "saves", ".fabric", ".mixin.out"
        };
        var excludedRootFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old", "usercache.json", "usernamecache.json", "launcher_profiles.json", "modrinth.index.json"
        };

        Visit(gameDirectory, string.Empty, depth: 0);
        return results;

        void Visit(string currentDirectory, string relativeDirectory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 64) throw new InvalidDataException("The instance contains a directory tree deeper than the export safety limit.");
            foreach (var sourcePath in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoReparsePoint(sourcePath);
                var name = Path.GetFileName(sourcePath);
                var relativePath = string.IsNullOrEmpty(relativeDirectory) ? name : $"{relativeDirectory}/{name}";
                var attributes = File.GetAttributes(sourcePath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (string.IsNullOrEmpty(relativeDirectory) && excludedDirectories.Contains(name)) continue;
                    Visit(sourcePath, relativePath, depth + 1);
                    continue;
                }

                if (string.Equals(Path.GetFullPath(sourcePath), outputPath, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(relativeDirectory) && excludedRootFiles.Contains(name)) ||
                    relativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relativePath = ValidateRelativePackPath(relativePath);
                if (manifestPaths.Contains(relativePath)) continue;
                var length = new FileInfo(sourcePath).Length;
                if (length > MaximumOverrideFileBytes)
                {
                    throw new InvalidDataException($"The instance override {relativePath} exceeds the 1 GB per-file export limit.");
                }

                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumOverridesBytes || results.Count >= MaximumArchiveEntries)
                {
                    throw new InvalidDataException("The instance overrides exceed Sakana's 4 GB or file-count export limit.");
                }

                results.Add(new ExportOverride(sourcePath, relativePath, length));
            }
        }
    }

    private static IReadOnlySet<string> EnumerateLocalModFileNames(string gameDirectory, CancellationToken cancellationToken)
    {
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        EnsureNoReparsePoint(modsDirectory);
        if (!Directory.Exists(modsDirectory)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoint(path);
            var name = Path.GetFileName(path);
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) names.Add(name);
            else if (name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) names.Add(name[..^".disabled".Length]);
        }

        return names;
    }

    private static bool IsModJarPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersonalDataPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("saves/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("screenshots/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase) ||
               PersonalRootFiles.Contains(normalized);
    }

    private static bool IsValidModrinthId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsValidHash(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    private static bool IsValidEnvironment(string? value) =>
        string.Equals(value, "required", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "optional", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "unsupported", StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Sakana refused to export content redirected outside the instance folder.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static async Task<PackManifest> ReadPackAsync(string archivePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException("The selected modpack file could not be found.", fullArchivePath);
        }

        if (!string.Equals(Path.GetExtension(fullArchivePath), ".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Select a Modrinth .mrpack archive.");
        }

        if (new FileInfo(fullArchivePath).Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("The modpack archive exceeds the 4 GB import limit.");
        }

        await using var archiveFile = new FileStream(fullArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException($"The modpack contains too many archive entries (maximum {MaximumArchiveEntries}).");
        }

        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName, ManifestPath, StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The archive must contain exactly one modrinth.index.json file at its root, no larger than 8 MB.");
        }

        var manifestBytes = await ReadEntryBoundedAsync(manifestEntries[0], MaximumManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("The modrinth.index.json file is empty or invalid.");
        ValidateManifest(manifest);
        ValidateOverrides(archive);
        return manifest;
    }

    private static async Task<Dictionary<string, PackFileSnapshot>> ReadPackFileHashesAsync(
        string archivePath,
        PackManifest manifest,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, PackFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (GetClientEnvironment(file) == PackEnvironment.Unsupported)
            {
                continue;
            }

            files[file.Path] = new PackFileSnapshot(file.Hashes.Sha1);
        }

        await using var archiveFile = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = ValidateRelativePackPath(entry.FullName["overrides/".Length..]);
            var hash = await ComputeEntrySha1Async(entry, relativePath, cancellationToken);
            if (!files.TryAdd(relativePath, new PackFileSnapshot(hash)))
            {
                throw new InvalidDataException($"The modrinth.index.json and overrides both contain {relativePath}.");
            }
        }

        return files;
    }

    private static IReadOnlyCollection<string> GetOverridePaths(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .Select(entry => ValidateRelativePackPath(entry.FullName["overrides/".Length..]))
            .ToArray();
    }

    private static async Task ExtractOverridesToDirectoryAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .ToArray();
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ValidateRelativePackPath(entry.FullName["overrides/".Length..]);
            if (entry.Length > MaximumOverrideFileBytes || (totalBytes = checked(totalBytes + entry.Length)) > MaximumOverridesBytes)
            {
                throw new InvalidDataException($"The modpack override exceeds Sakana's extraction limits: {relativePath}");
            }

            var destinationPath = ResolveDataPathForGame(destinationRoot, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(destinationDirectory);
            var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using var source = entry.Open();
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[64 * 1024];
                    long entryBytes = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        entryBytes += read;
                        if (entryBytes > entry.Length || entryBytes > MaximumOverrideFileBytes)
                        {
                            throw new InvalidDataException($"The modpack override expanded beyond its declared size: {relativePath}");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }

                    if (entryBytes != entry.Length)
                    {
                        throw new InvalidDataException($"The modpack override size did not match its archive entry: {relativePath}");
                    }
                }

                File.Move(temporaryPath, destinationPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureSafeGameTarget(string gameDirectory, string targetPath, bool isDirectory = false)
    {
        var root = Path.GetFullPath(gameDirectory);
        var fullTarget = Path.GetFullPath(targetPath);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(root, fullTarget, StringComparison.OrdinalIgnoreCase) && !fullTarget.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update attempted to write outside the instance game directory.");
        }

        EnsureNoReparsePoint(root);
        var relativePath = string.Equals(root, fullTarget, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetRelativePath(root, fullTarget);
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            EnsureNoReparsePoint(current);
            var isLast = index == segments.Length - 1;
            if (!isLast && File.Exists(current))
            {
                throw new IOException($"A file blocks the update path {relativePath}.");
            }

            if (isLast && isDirectory && File.Exists(current))
            {
                throw new IOException($"A file blocks the update directory {relativePath}.");
            }
        }
    }

    private static async Task CopyFileForTransactionAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 64 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task RollBackFileChangesAsync(string gameDirectory, IReadOnlyList<FileUpdateJournalEntry> changes)
    {
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            var change = changes[index];
            EnsureSafeGameTarget(gameDirectory, change.TargetPath);
            if (change.HadOriginal)
            {
                if (!File.Exists(change.BackupPath))
                {
                    throw new IOException($"The update backup for {Path.GetFileName(change.TargetPath)} is missing.");
                }

                await CopyFileForTransactionAsync(change.BackupPath, change.TargetPath, CancellationToken.None);
            }
            else if (File.Exists(change.TargetPath))
            {
                EnsureNoReparsePoint(change.TargetPath);
                File.Delete(change.TargetPath);
            }
        }
    }

    private static async Task<string> ComputeEntrySha1Async(ZipArchiveEntry entry, string relativePath, CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var source = entry.Open();
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > entry.Length || totalBytes > MaximumOverrideFileBytes)
            {
                throw new InvalidDataException($"The pack override expanded beyond its declared size: {relativePath}");
            }

            sha1.AppendData(buffer, 0, read);
        }

        if (totalBytes != entry.Length)
        {
            throw new InvalidDataException($"The pack override size did not match its archive entry: {relativePath}");
        }

        return Convert.ToHexString(sha1.GetHashAndReset());
    }

    private static void ValidateManifest(PackManifest manifest)
    {
        if (manifest.Dependencies is null || manifest.Files is null)
        {
            throw new InvalidDataException("The modrinth.index.json file must include dependency and file lists.");
        }

        if (manifest.FormatVersion != 1)
        {
            throw new NotSupportedException($"Sakana supports Modrinth modpack format version 1; this pack uses version {manifest.FormatVersion}.");
        }

        if (!string.Equals(manifest.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Sakana cannot import modpacks for '{manifest.Game}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(manifest.VersionId) || manifest.VersionId.Length > 100)
        {
            throw new InvalidDataException("The modpack name or version ID is missing or too long.");
        }

        if (manifest.Files.Count > MaximumManifestFiles)
        {
            throw new InvalidDataException($"The modpack lists too many files (maximum {MaximumManifestFiles}).");
        }

        if (manifest.Dependencies is null ||
            !manifest.Dependencies.TryGetValue("minecraft", out var minecraftVersion) ||
            string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new InvalidDataException("The modpack does not declare its Minecraft version.");
        }

        if (minecraftVersion.Length > 80 || minecraftVersion.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("The modpack contains an invalid Minecraft version ID.");
        }

        var unsupportedDependencies = manifest.Dependencies.Keys
            .Where(key => key is not "minecraft" and not "fabric-loader" and not "forge" and not "neoforge" and not "quilt-loader")
            .ToArray();
        if (unsupportedDependencies.Length > 0)
        {
            throw new NotSupportedException($"Sakana does not support the modpack dependency '{unsupportedDependencies[0]}' yet.");
        }

        var loaderDependencies = manifest.Dependencies
            .Where(pair => pair.Key != "minecraft")
            .ToArray();
        if (loaderDependencies.Length > 1)
        {
            throw new InvalidDataException("The modpack declares more than one mod loader.");
        }

        if (loaderDependencies.Length == 1 && string.IsNullOrWhiteSpace(loaderDependencies[0].Value))
        {
            throw new InvalidDataException("The modpack's mod loader version is missing.");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalDownloadBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Hashes is null || file.Downloads is null)
            {
                throw new InvalidDataException("The modrinth.index.json file contains an incomplete file entry.");
            }

            file.Path = ValidateRelativePackPath(file.Path);
            if (!seenPaths.Add(file.Path))
            {
                throw new InvalidDataException($"The modpack lists the file path more than once: {file.Path}");
            }

            if (file.FileSize is null or < 0 || file.FileSize > MaximumContentBytes)
            {
                throw new InvalidDataException($"The modpack contains an invalid file size for {file.Path}.");
            }

            totalDownloadBytes = checked(totalDownloadBytes + file.FileSize.Value);
            if (totalDownloadBytes > MaximumContentBytes)
            {
                throw new InvalidDataException("The modpack's declared files exceed the 20 GB import limit.");
            }

            ValidateHash(file.Hashes.Sha1, 40, "SHA-1", file.Path);
            ValidateHash(file.Hashes.Sha512, 128, "SHA-512", file.Path);
            if (!Enum.TryParse<PackEnvironment>(file.Env?.Client ?? nameof(PackEnvironment.Required), ignoreCase: true, out var clientEnvironment))
            {
                throw new InvalidDataException($"The modpack contains an invalid client environment for {file.Path}.");
            }

            if (clientEnvironment != PackEnvironment.Unsupported)
            {
                if (file.Downloads.Count == 0)
                {
                    throw new InvalidDataException($"The modpack does not provide a download URL for {file.Path}.");
                }

                if (file.Downloads.Count > 20)
                {
                    throw new InvalidDataException($"The modpack provides too many download mirrors for {file.Path}.");
                }

                foreach (var url in file.Downloads)
                {
                    ValidateDownloadUrl(url, file.Path);
                }
            }
        }
    }

    private static void ValidateOverrides(ZipArchive archive)
    {
        long totalExpandedBytes = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.FullName["overrides/".Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue;
            }

            relative = ValidateRelativePackPath(relative);
            if (!seenPaths.Add(relative))
            {
                throw new InvalidDataException($"The modpack contains the same override path more than once: {relative}");
            }

            if (entry.Length > MaximumOverrideFileBytes)
            {
                throw new InvalidDataException($"An override file exceeds the 1 GB extraction limit: {relative}");
            }

            totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
            if (totalExpandedBytes > MaximumOverridesBytes)
            {
                throw new InvalidDataException("The modpack's overrides exceed the 4 GB extraction limit.");
            }
        }
    }

    private static ModrinthModpackDescription Describe(PackManifest manifest)
    {
        var loader = manifest.Dependencies.FirstOrDefault(pair => pair.Key != "minecraft");
        var loaderType = loader.Key switch
        {
            null or "" => MinecraftLoaderType.Vanilla,
            "fabric-loader" => MinecraftLoaderType.Fabric,
            "forge" => MinecraftLoaderType.Forge,
            "neoforge" => MinecraftLoaderType.NeoForge,
            "quilt-loader" => MinecraftLoaderType.Quilt,
            _ => throw new NotSupportedException($"Sakana does not support the modpack loader '{loader.Key}' yet.")
        };

        var optionalFiles = new List<ModrinthOptionalFile>();
        var requiredClientFiles = 0;
        foreach (var file in manifest.Files)
        {
            var environment = GetClientEnvironment(file);
            if (environment == PackEnvironment.Required)
            {
                requiredClientFiles++;
            }
            else if (environment == PackEnvironment.Optional)
            {
                optionalFiles.Add(new ModrinthOptionalFile(file.Path, file.FileSize ?? 0));
            }
        }

        return new ModrinthModpackDescription(
            manifest.Name,
            manifest.VersionId,
            manifest.Summary,
            manifest.Dependencies["minecraft"],
            loaderType,
            loaderType is MinecraftLoaderType.Vanilla ? null : loader.Value,
            requiredClientFiles,
            optionalFiles);
    }

    private static PackEnvironment GetClientEnvironment(PackFile file)
    {
        if (!Enum.TryParse<PackEnvironment>(file.Env?.Client ?? nameof(PackEnvironment.Required), ignoreCase: true, out var environment))
        {
            throw new InvalidDataException($"The modpack contains an invalid client environment for {file.Path}.");
        }

        return environment;
    }

    private static string ValidateRelativePackPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 1024)
        {
            throw new InvalidDataException("The modpack contains an empty or excessively long file path.");
        }

        var path = rawPath.Replace('\\', '/');
        if (path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains(':') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"The modpack contains an absolute or drive-prefixed path: {rawPath}");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(char.IsControl))
            {
                throw new InvalidDataException($"The modpack contains an unsafe file path: {rawPath}");
            }

            var reservedName = segment.Split('.')[0];
            if (reservedName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                reservedName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                reservedName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                reservedName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(reservedName, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException($"The modpack contains a reserved Windows file name: {rawPath}");
            }
        }

        return string.Join('/', segments);
    }

    private static void ValidateHash(string? hash, int expectedLength, string algorithm, string path)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != expectedLength || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"The modpack contains an invalid {algorithm} hash for {path}.");
        }
    }

    private static void ValidateDownloadUrl(string url, string path)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !IsAllowedHost(uri.Host, AllowedDownloadDomains))
        {
            throw new InvalidDataException($"The modpack contains an unsupported download URL for {path}. Sakana accepts HTTPS downloads from Modrinth, GitHub, and GitLab hosts.");
        }
    }

    private static bool IsAllowedHost(string host, IReadOnlyList<string> allowedDomains) =>
        allowedDomains.Any(domain => string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));

    private static string ResolveDataPath(string dataDirectory, string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath) || normalizedRelativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "."))
        {
            throw new InvalidDataException("The saved modpack path is invalid.");
        }

        var root = Path.GetFullPath(dataDirectory);
        var fullPath = Path.GetFullPath(normalizedRelativePath, root);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The saved modpack path points outside Sakana's data directory.");
        }

        return fullPath;
    }

    private static string ResolveDataPathForGame(string gameDirectory, string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(gameDirectory);
        var fullPath = Path.GetFullPath(normalizedRelativePath, root);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The modpack attempted to write outside the instance game directory.");
        }

        return fullPath;
    }

    private static async Task<byte[]> ReadEntryBoundedAsync(ZipArchiveEntry entry, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, maximumBytes));
        var buffer = new byte[32 * 1024];
        long totalRead = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
            if (totalRead > maximumBytes)
            {
                throw new InvalidDataException("The modpack manifest exceeds the 8 MB limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return output.ToArray();
    }

    private static string[] DeserializeSelectedOptionalFiles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved optional-file selection is invalid.", exception);
        }
    }

    private static async Task ApplyOverridesAsync(
        Guid instanceId,
        string archivePath,
        string gameDirectory,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var overrideEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) &&
                !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .ToArray();
        long totalBytes = overrideEntries.Sum(entry => entry.Length);
        long extractedBytes = 0;
        var completed = 0;
        long actuallyWrittenBytes = 0;

        foreach (var entry in overrideEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ValidateRelativePackPath(entry.FullName["overrides/".Length..]);
            var destination = ResolveDataPathForGame(gameDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                var temporaryPath = $"{destination}.{Guid.NewGuid():N}.part";
                try
                {
                    await using var source = entry.Open();
                    await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[64 * 1024];
                        long entryBytes = 0;
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, cancellationToken);
                            if (read == 0)
                            {
                                break;
                            }

                            entryBytes += read;
                            actuallyWrittenBytes += read;
                            if (entryBytes > entry.Length || entryBytes > MaximumOverrideFileBytes || actuallyWrittenBytes > MaximumOverridesBytes)
                            {
                                throw new InvalidDataException($"The modpack override expanded beyond its declared size: {relativePath}");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        }

                        if (entryBytes != entry.Length)
                        {
                            throw new InvalidDataException($"The modpack override size did not match its archive entry: {relativePath}");
                        }
                    }

                    if (new FileInfo(temporaryPath).Length != entry.Length)
                    {
                        throw new InvalidDataException($"The modpack override failed its size check: {relativePath}");
                    }

                    File.Move(temporaryPath, destination, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            extractedBytes += entry.Length;
            completed++;
            progress?.Report(new InstallationProgress(
                instanceId,
                InstallationStage.Extracting,
                extractedBytes,
                totalBytes,
                completed,
                overrideEntries.Length,
                Path.GetFileName(relativePath)));
        }
    }

    private enum PackEnvironment
    {
        Required,
        Optional,
        Unsupported
    }

    private sealed record FileHashes(string Sha1, string Sha512, long Length);

    private sealed record ExportOverride(string FullPath, string RelativePath, long Length);

    private sealed class ExportTrackingIndex
    {
        public ExportTrackingIndex() { }
        public int SchemaVersion { get; init; }
        public List<ExportTrackedMod> Mods { get; init; } = [];
        public static ExportTrackingIndex Empty => new() { SchemaVersion = 1 };
    }

    private sealed class ExportTrackedMod
    {
        public ExportTrackedMod() { }
        public string FileName { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public string VersionId { get; init; } = string.Empty;
        public string VersionNumber { get; init; } = string.Empty;
        public DateTimeOffset PublishedAt { get; init; }
        public string? Sha1 { get; init; }
        public string? Sha512 { get; init; }
        public string? Url { get; init; }
        public string ClientEnvironment { get; init; } = "required";
        public string ServerEnvironment { get; init; } = "required";
    }

    private sealed class ExportRemoteVersion
    {
        public ExportRemoteVersion() { }
        [JsonPropertyName("files")]
        public List<ExportRemoteFile> Files { get; init; } = [];
    }

    private sealed class ExportRemoteFile
    {
        public ExportRemoteFile() { }
        [JsonPropertyName("filename")]
        public string FileName { get; init; } = string.Empty;
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
        [JsonPropertyName("hashes")]
        public ExportRemoteHashes Hashes { get; init; } = new();
    }

    private sealed class ExportRemoteHashes
    {
        public ExportRemoteHashes() { }
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }
        [JsonPropertyName("sha512")]
        public string? Sha512 { get; init; }
    }

    private sealed record PackFileSnapshot(string Sha1);

    private sealed record FileUpdateJournalEntry(string TargetPath, string BackupPath, bool HadOriginal);

    private sealed class PackManifest
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; init; }

        [JsonPropertyName("game")]
        public string Game { get; init; } = string.Empty;

        [JsonPropertyName("versionId")]
        public string VersionId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("files")]
        public List<PackFile> Files { get; init; } = [];
    }

    private sealed class PackFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("hashes")]
        public PackFileHashes Hashes { get; init; } = new();

        [JsonPropertyName("env")]
        public PackFileEnvironment? Env { get; init; }

        [JsonPropertyName("downloads")]
        public List<string> Downloads { get; init; } = [];

        [JsonPropertyName("fileSize")]
        public long? FileSize { get; init; }
    }

    private sealed class PackFileHashes
    {
        [JsonPropertyName("sha1")]
        public string Sha1 { get; init; } = string.Empty;

        [JsonPropertyName("sha512")]
        public string Sha512 { get; init; } = string.Empty;
    }

    private sealed class PackFileEnvironment
    {
        [JsonPropertyName("client")]
        public string? Client { get; init; }

        [JsonPropertyName("server")]
        public string? Server { get; init; }
    }
}
