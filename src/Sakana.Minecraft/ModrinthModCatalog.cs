using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class ModrinthModCatalog(
    HttpClient httpClient,
    string dataDirectory,
    ILocalModService localModService,
    ILocalPackService localPackService) : IModrinthModCatalog
{
    private const string ApiRoot = "https://api.modrinth.com/v2";
    private const long MaximumDependencySetBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] AllowedDownloadDomains = ["modrinth.com"];
    private static readonly string[] ClientEnvironments =
    [
        "client_and_server",
        "client_only",
        "client_only_server_optional",
        "singleplayer_only",
        "client_or_server",
        "client_or_server_prefers_both"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ParallelArtifactDownloader _downloader = new(httpClient, maxConcurrency: 4);

    public async Task<ModrinthProjectSearchPage> SearchModsAsync(
        InstanceProfile profile,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ValidateFabricProfile(profile);
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length > 100)
        {
            throw new ArgumentException("Search text must be 100 characters or fewer.", nameof(query));
        }

        var facets = new[]
        {
            new[] { "project_type:mod" },
            new[] { $"versions:{profile.VersionId}" },
            new[] { $"categories:{GetLoaderSlug(profile.LoaderType)}" },
            ClientEnvironments.Select(environment => $"environment:{environment}").ToArray()
        };
        var facetsJson = Uri.EscapeDataString(JsonSerializer.Serialize(facets));
        var queryPart = $"query={Uri.EscapeDataString(normalizedQuery)}&";
        var uri = BuildSearchUri(queryPart, facetsJson, sortOrder, offset);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty search response.");

        return ToSearchPage(result);
    }

    public async Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleVersionsAsync(
        InstanceProfile profile,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateFabricProfile(profile);
        var normalizedProjectId = ValidateIdentifier(projectId, nameof(projectId));
        var versions = await GetProjectVersionsAsync(normalizedProjectId, profile, GetLoaderSlug(profile.LoaderType), cancellationToken);
        return versions
            .Where(version => IsCompatible(version, profile) && TryGetPrimaryJar(version, out _))
            .OrderBy(version => VersionTypeOrder(version.VersionType))
            .ThenByDescending(version => version.PublishedAt)
            .Select(version =>
            {
                _ = TryGetPrimaryJar(version, out var file);
                return new ModrinthModRelease(
                    version.Id,
                    version.VersionNumber,
                    version.Name,
                    version.VersionType,
                    version.PublishedAt,
                    version.Downloads,
                    file.FileName,
                    file.Size,
                    version.Loaders,
                    version.GameVersions);
            })
            .ToArray();
    }

    public async Task<ModrinthProjectSearchPage> SearchContentAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ValidateContentProfile(profile);
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length > 100)
        {
            throw new ArgumentException("Search text must be 100 characters or fewer.", nameof(query));
        }

        var facets = kind == ModrinthContentKind.DataPack
            ? new[]
            {
                new[] { "all_project_types:datapack" },
                new[] { $"versions:{profile.VersionId}" }
            }
            : new[]
            {
                new[] { $"project_type:{GetProjectType(kind)}" },
                new[] { $"versions:{profile.VersionId}" }
            };
        var facetsJson = Uri.EscapeDataString(JsonSerializer.Serialize(facets));
        var queryPart = $"query={Uri.EscapeDataString(normalizedQuery)}&";
        var uri = BuildSearchUri(queryPart, facetsJson, sortOrder, offset);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty search response.");

        return ToSearchPage(result);
    }

    public async Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleContentVersionsAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateContentProfile(profile);
        var normalizedProjectId = ValidateIdentifier(projectId, nameof(projectId));
        var versions = await GetProjectVersionsAsync(normalizedProjectId, profile, loaderFilter: null, cancellationToken: cancellationToken);
        return versions
            .Where(version => IsListed(version) && version.GameVersions.Contains(profile.VersionId, StringComparer.Ordinal) &&
                (kind switch
                {
                    ModrinthContentKind.ResourcePack => version.Loaders.Contains("minecraft", StringComparer.OrdinalIgnoreCase),
                    ModrinthContentKind.DataPack => version.Loaders.Contains("datapack", StringComparer.OrdinalIgnoreCase),
                    _ => true
                }) &&
                TryGetPrimaryZip(version, out _))
            .OrderBy(version => VersionTypeOrder(version.VersionType))
            .ThenByDescending(version => version.PublishedAt)
            .Select(version =>
            {
                _ = TryGetPrimaryZip(version, out var file);
                return new ModrinthModRelease(
                    version.Id,
                    version.VersionNumber,
                    version.Name,
                    version.VersionType,
                    version.PublishedAt,
                    version.Downloads,
                    file.FileName,
                    file.Size,
                    version.Loaders,
                    version.GameVersions);
            })
            .ToArray();
    }

    public async Task<ModrinthProjectSearchPage> SearchModpacksAsync(
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length > 100)
        {
            throw new ArgumentException("Search text must be 100 characters or fewer.", nameof(query));
        }

        var facetsJson = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { new[] { "project_type:modpack" } }));
        var queryPart = $"query={Uri.EscapeDataString(normalizedQuery)}&";
        var uri = BuildSearchUri(queryPart, facetsJson, sortOrder, offset);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty search response.");
        return ToSearchPage(result);
    }

    public async Task<IReadOnlyList<ModrinthModRelease>> GetSupportedModpackVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = ValidateIdentifier(projectId, nameof(projectId));
        var projectType = await GetProjectTypeAsync(normalizedProjectId, cancellationToken);
        if (!string.Equals(projectType, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected Modrinth project is not a modpack.");
        }

        var uri = new Uri($"{ApiRoot}/project/{Uri.EscapeDataString(normalizedProjectId)}/version?include_changelog=false");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var versions = await response.Content.ReadFromJsonAsync<List<CatalogVersion>>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty version list.");
        return versions
            .Where(version => IsSupportedModpackVersion(version) && TryGetPrimaryModpack(version, out _))
            .OrderBy(version => VersionTypeOrder(version.VersionType))
            .ThenByDescending(version => version.PublishedAt)
            .Take(40)
            .Select(version =>
            {
                _ = TryGetPrimaryModpack(version, out var file);
                return new ModrinthModRelease(
                    version.Id,
                    version.VersionNumber,
                    version.Name,
                    version.VersionType,
                    version.PublishedAt,
                    version.Downloads,
                    file.FileName,
                    file.Size,
                    version.Loaders,
                    version.GameVersions);
            })
            .ToArray();
    }

    public async Task<string> DownloadModpackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedVersionId = ValidateIdentifier(versionId, nameof(versionId));
        var version = await GetVersionAsync(normalizedVersionId, cancellationToken);
        if (!IsSupportedModpackVersion(version))
        {
            throw new InvalidOperationException("This Modrinth version uses a loader that Sakana does not support yet. Supported modpack loaders are Vanilla, Fabric, and Quilt.");
        }

        var projectType = await GetProjectTypeAsync(version.ProjectId, cancellationToken);
        if (!string.Equals(projectType, "modpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected Modrinth project is not a modpack.");
        }

        if (!TryGetPrimaryModpack(version, out var file))
        {
            throw new InvalidDataException($"Modrinth version {version.VersionNumber} has no downloadable .mrpack file.");
        }

        ValidateDownloadFile(file, ".mrpack", 4L * 1024 * 1024 * 1024);
        var stagingDirectory = Path.Combine(dataDirectory, "downloads", "modrinth-pack-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            await _downloader.DownloadAllAsync(
                Guid.Empty,
                stagingDirectory,
                [new InstallArtifact(file.FileName, file.Url, Sha1: file.Hashes.Sha1, Size: file.Size, Sha512: file.Hashes.Sha512, AllowedHosts: AllowedDownloadDomains)],
                progress: null,
                cancellationToken);
            return Path.Combine(stagingDirectory, file.FileName);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    public async Task<ModrinthContentInstallResult> InstallContentVersionAsync(
        InstanceProfile profile,
        ModrinthContentKind kind,
        string versionId,
        string? worldName,
        CancellationToken cancellationToken = default)
    {
        ValidateContentProfile(profile);
        var normalizedVersionId = ValidateIdentifier(versionId, nameof(versionId));
        var version = await GetVersionAsync(normalizedVersionId, cancellationToken);
        if (!IsListed(version) || !version.GameVersions.Contains(profile.VersionId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"This pack version is not listed for Minecraft {profile.VersionId}.");
        }

        var projectType = kind == ModrinthContentKind.DataPack
            ? null
            : await GetProjectTypeAsync(version.ProjectId, cancellationToken);
        var isSupportedContentVersion = kind switch
        {
            ModrinthContentKind.DataPack => version.Loaders.Contains("datapack", StringComparer.OrdinalIgnoreCase),
            _ => string.Equals(projectType, GetProjectType(kind), StringComparison.OrdinalIgnoreCase)
        };
        if (!isSupportedContentVersion)
        {
            throw new InvalidDataException("The selected Modrinth version is not the requested content type.");
        }

        if (kind == ModrinthContentKind.ResourcePack && !version.Loaders.Contains("minecraft", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Modrinth did not mark this version as a Minecraft resource pack.");
        }

        if (!TryGetPrimaryZip(version, out var file))
        {
            throw new InvalidDataException($"Modrinth version {version.VersionNumber} has no downloadable ZIP file.");
        }

        ValidateDownloadFile(file, ".zip");
        var stagingDirectory = Path.Combine(dataDirectory, "downloads", "modrinth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            await _downloader.DownloadAllAsync(
                profile.Id,
                stagingDirectory,
                [new InstallArtifact(file.FileName, file.Url, Sha1: file.Hashes.Sha1, Size: file.Size, Sha512: file.Hashes.Sha512, AllowedHosts: AllowedDownloadDomains)],
                progress: null,
                cancellationToken);
            var import = await localPackService.ImportAsync(
                InstanceDirectoryName.ForProfile(profile),
                ToLocalPackKind(kind),
                [Path.Combine(stagingDirectory, file.FileName)],
                worldName,
                cancellationToken);
            return new ModrinthContentInstallResult(import.ImportedCount, import.SkippedDuplicateCount, file.FileName);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public async Task<ModrinthModInstallResult> InstallVersionAsync(
        InstanceProfile profile,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ValidateFabricProfile(profile);
        var normalizedVersionId = ValidateIdentifier(versionId, nameof(versionId));
        var installPlan = new List<CatalogVersion>();
        var visitedVersions = new HashSet<string>(StringComparer.Ordinal);
        var visitingProjects = new HashSet<string>(StringComparer.Ordinal);
        await ResolveRequiredDependenciesAsync(
            normalizedVersionId,
            profile,
            visitedVersions,
            visitingProjects,
            installPlan,
            depth: 0,
            cancellationToken);
        ValidateNoSelectedIncompatibilities(installPlan);
        var plannedFiles = BuildInstallPlanFiles(installPlan);
        var stagedFiles = plannedFiles.Values.ToDictionary(item => item.FileName, item => item.Artifact, StringComparer.OrdinalIgnoreCase);

        var stagingDirectory = Path.Combine(dataDirectory, "downloads", "modrinth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            await _downloader.DownloadAllAsync(
                profile.Id,
                stagingDirectory,
                stagedFiles.Values.ToArray(),
                progress: null,
                cancellationToken);

            var existingNames = (await localModService.ListAsync(InstanceDirectoryName.ForProfile(profile), cancellationToken))
                .Select(mod => mod.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var importedFileNames = stagedFiles.Keys
                .Where(fileName => !existingNames.Contains(fileName))
                .ToArray();
            var installed = await localModService.ImportAsync(
                InstanceDirectoryName.ForProfile(profile),
                stagedFiles.Keys.Select(fileName => Path.Combine(stagingDirectory, fileName)).ToArray(),
                cancellationToken);
            if (importedFileNames.Length > 0)
            {
                var index = await ReadInstalledModIndexAsync(profile, cancellationToken);
                var importedEntries = importedFileNames
                    .Select(fileName => plannedFiles[fileName].ToIndexEntry())
                    .ToArray();
                try
                {
                    await SaveInstalledModIndexAsync(
                        profile,
                        index with { Mods = index.Mods.Where(item => !importedFileNames.Contains(item.FileName, StringComparer.OrdinalIgnoreCase)).Concat(importedEntries).ToList() },
                        cancellationToken);
                }
                catch
                {
                    foreach (var fileName in importedFileNames)
                    {
                        try { await localModService.RemoveAsync(InstanceDirectoryName.ForProfile(profile), fileName, CancellationToken.None); }
                        catch (FileNotFoundException) { }
                    }

                    throw;
                }
            }

            return new ModrinthModInstallResult(
                installed.ImportedCount,
                installed.SkippedDuplicateCount,
                importedFileNames);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public async Task<IReadOnlyList<ModrinthModUpdate>> CheckInstalledModUpdatesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateFabricProfile(profile);
        var index = await ReadInstalledModIndexAsync(profile, cancellationToken);
        if (index.Mods.Count == 0)
        {
            return [];
        }

        var localMods = (await localModService.ListAsync(InstanceDirectoryName.ForProfile(profile), cancellationToken))
            .ToDictionary(mod => mod.FileName, StringComparer.OrdinalIgnoreCase);
        var updates = new List<ModrinthModUpdate>();
        using var concurrencyGate = new SemaphoreSlim(3, 3);
        var updateGroups = index.Mods.GroupBy(mod => mod.ProjectId, StringComparer.Ordinal).Select(async group =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                var validEntries = new List<TrackedModInstall>();
                foreach (var tracked in group)
                {
                    if (localMods.ContainsKey(tracked.FileName) && await IsTrackedFileUnmodifiedAsync(profile, tracked, cancellationToken))
                    {
                        validEntries.Add(tracked);
                    }
                }

                if (validEntries.Count == 0)
                {
                    return;
                }

                var releases = await GetCompatibleVersionsAsync(profile, group.Key, cancellationToken);
                foreach (var tracked in validEntries)
                {
                    var release = releases
                        .Where(candidate => candidate.PublishedAt > tracked.PublishedAt &&
                                            !string.Equals(candidate.VersionId, tracked.VersionId, StringComparison.Ordinal))
                        .OrderByDescending(candidate => candidate.PublishedAt)
                        .FirstOrDefault();
                    if (release is not null)
                    {
                        lock (updates)
                        {
                            updates.Add(new ModrinthModUpdate(tracked.FileName, tracked.VersionNumber, release));
                        }
                    }
                }
            }
            finally
            {
                concurrencyGate.Release();
            }
        });
        await Task.WhenAll(updateGroups);
        return updates.OrderBy(update => update.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<ModrinthModInstallResult> UpdateInstalledModAsync(
        InstanceProfile profile,
        string fileName,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ValidateFabricProfile(profile);
        var normalizedFileName = NormalizeTrackedModFileName(fileName);
        var index = await ReadInstalledModIndexAsync(profile, cancellationToken);
        var installedEntry = index.Mods.FirstOrDefault(mod => string.Equals(mod.FileName, normalizedFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("此模组不是由 Sakana 通过 Modrinth 安装的，或其来源记录已失效。");
        if (!await IsTrackedFileUnmodifiedAsync(profile, installedEntry, cancellationToken))
        {
            throw new InvalidOperationException("模组文件已被移动或修改。为避免覆盖你的更改，请先重新从 Modrinth 安装此模组。");
        }

        var targetRelease = (await GetCompatibleVersionsAsync(profile, installedEntry.ProjectId, cancellationToken))
            .FirstOrDefault(release => string.Equals(release.VersionId, ValidateIdentifier(versionId, nameof(versionId)), StringComparison.Ordinal))
            ?? throw new InvalidOperationException("所选 Modrinth 版本已不可用，重新检查更新后再试。");
        if (targetRelease.PublishedAt <= installedEntry.PublishedAt)
        {
            throw new InvalidOperationException("所选版本并不比当前安装版本更新。");
        }

        var installPlan = new List<CatalogVersion>();
        await ResolveRequiredDependenciesAsync(
            targetRelease.VersionId,
            profile,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            installPlan,
            depth: 0,
            cancellationToken);
        ValidateNoSelectedIncompatibilities(installPlan);
        var plannedFiles = BuildInstallPlanFiles(installPlan);
        var affectedProjectIds = installPlan.Select(version => version.ProjectId).ToHashSet(StringComparer.Ordinal);
        var replacingEntries = index.Mods.Where(mod => affectedProjectIds.Contains(mod.ProjectId)).ToArray();
        var instanceDirectoryName = InstanceDirectoryName.ForProfile(profile);
        var modsDirectory = GetModsDirectory(profile);
        Directory.CreateDirectory(modsDirectory);

        var existingMods = (await localModService.ListAsync(instanceDirectoryName, cancellationToken))
            .ToDictionary(mod => mod.FileName, StringComparer.OrdinalIgnoreCase);
        var replacementPaths = new Dictionary<string, (string Path, bool IsEnabled)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tracked in replacingEntries)
        {
            if (!existingMods.TryGetValue(tracked.FileName, out var existingMod))
            {
                continue;
            }

            if (!await IsTrackedFileUnmodifiedAsync(profile, tracked, cancellationToken))
            {
                throw new InvalidOperationException($"模组文件 {tracked.FileName} 已被修改。为避免丢失内容，Sakana 没有覆盖它。");
            }

            replacementPaths[tracked.FileName] = (GetExistingModPath(profile, tracked.FileName, existingMod.IsEnabled), existingMod.IsEnabled);
        }

        var reusedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var planned in plannedFiles.Values)
        {
            if (!existingMods.TryGetValue(planned.FileName, out var existingMod) || replacementPaths.ContainsKey(planned.FileName))
            {
                continue;
            }

            var currentPath = GetExistingModPath(profile, planned.FileName, existingMod.IsEnabled);
            if (!await FileMatchesArtifactAsync(currentPath, planned.Artifact, cancellationToken))
            {
                throw new IOException($"实例中已有不同内容的同名模组 {planned.FileName}。请先在模组管理中移除它再更新。");
            }

            reusedFileNames.Add(planned.FileName);
        }

        var stagingDirectory = Path.Combine(dataDirectory, "downloads", "modrinth", Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(stagingDirectory, "backup");
        Directory.CreateDirectory(stagingDirectory);
        var movedBackups = new List<(string BackupPath, string OriginalPath)>();
        var installedPaths = new List<string>();
        try
        {
            await _downloader.DownloadAllAsync(
                profile.Id,
                stagingDirectory,
                plannedFiles.Values.Select(item => item.Artifact).ToArray(),
                progress: null,
                cancellationToken);

            Directory.CreateDirectory(backupDirectory);
            foreach (var oldFile in replacementPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backupPath = Path.Combine(backupDirectory, $"{Guid.NewGuid():N}.jar");
                File.Move(oldFile.Value.Path, backupPath);
                movedBackups.Add((backupPath, oldFile.Value.Path));
            }

            foreach (var planned in plannedFiles.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isEnabled = FindPreviousEnabledState(planned, replacingEntries, replacementPaths, existingMods);
                var targetPath = Path.Combine(modsDirectory, planned.FileName);
                if (!reusedFileNames.Contains(planned.FileName))
                {
                    File.Move(Path.Combine(stagingDirectory, planned.FileName), targetPath, overwrite: false);
                    installedPaths.Add(targetPath);
                }

                if (!isEnabled)
                {
                    var disabledPath = targetPath + ".disabled";
                    if (!reusedFileNames.Contains(planned.FileName))
                    {
                        File.Move(targetPath, disabledPath, overwrite: false);
                        installedPaths[^1] = disabledPath;
                    }
                }
            }

            var updatedEntries = plannedFiles.Values.Select(item => item.ToIndexEntry()).ToArray();
            await SaveInstalledModIndexAsync(
                profile,
                index with
                {
                    Mods = index.Mods
                        .Where(mod => !affectedProjectIds.Contains(mod.ProjectId) && !plannedFiles.ContainsKey(mod.FileName))
                        .Concat(updatedEntries)
                        .ToList()
                },
                cancellationToken);

            return new ModrinthModInstallResult(
                plannedFiles.Count - reusedFileNames.Count,
                reusedFileNames.Count,
                plannedFiles.Keys.ToArray());
        }
        catch
        {
            foreach (var installedPath in installedPaths)
            {
                if (File.Exists(installedPath)) File.Delete(installedPath);
            }

            foreach (var (backupPath, originalPath) in movedBackups.AsEnumerable().Reverse())
            {
                if (File.Exists(backupPath)) File.Move(backupPath, originalPath, overwrite: true);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static Dictionary<string, PlannedModFile> BuildInstallPlanFiles(IReadOnlyCollection<CatalogVersion> installPlan)
    {
        var files = new Dictionary<string, PlannedModFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in installPlan)
        {
            if (!TryGetPrimaryJar(version, out var file))
            {
                throw new InvalidDataException($"Modrinth version {version.VersionNumber} has no downloadable JAR file.");
            }

            ValidateDownloadFile(file, ".jar");
            var artifact = new InstallArtifact(
                file.FileName,
                file.Url,
                Sha1: file.Hashes.Sha1,
                Size: file.Size,
                Sha512: file.Hashes.Sha512,
                AllowedHosts: AllowedDownloadDomains);
            var plannedFile = new PlannedModFile(version, file, artifact);
            if (files.TryGetValue(file.FileName, out var existing))
            {
                if (!string.Equals(existing.Artifact.Sha1, artifact.Sha1, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.Artifact.Sha512, artifact.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Required mods contain different files with the same name: {file.FileName}.");
                }

                continue;
            }

            files.Add(file.FileName, plannedFile);
        }

        if (files.Values.Sum(item => item.Artifact.Size ?? 0) > MaximumDependencySetBytes)
        {
            throw new InvalidDataException("The selected mod and its required dependencies exceed Sakana's 2 GB safety limit.");
        }

        return files;
    }

    private async Task<InstalledModIndex> ReadInstalledModIndexAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var path = GetInstalledModIndexPath(profile);
        EnsureNoReparsePoint(path);
        if (!File.Exists(path))
        {
            return InstalledModIndex.Empty;
        }

        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Sakana's Modrinth mod tracking file is larger than 4 MB.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var index = JsonSerializer.Deserialize<InstalledModIndex>(json, JsonOptions)
            ?? throw new InvalidDataException("Sakana's Modrinth mod tracking file is invalid.");
        if (index.SchemaVersion != 1 || index.Mods is null || index.Mods.Count > 10000)
        {
            throw new InvalidDataException("Sakana's Modrinth mod tracking file has an unsupported format.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in index.Mods)
        {
            if (!string.Equals(mod.FileName, NormalizeTrackedModFileName(mod.FileName), StringComparison.Ordinal) ||
                !seenNames.Add(mod.FileName) ||
                ValidateIdentifier(mod.ProjectId, "project ID") != mod.ProjectId ||
                ValidateIdentifier(mod.VersionId, "version ID") != mod.VersionId ||
                string.IsNullOrWhiteSpace(mod.VersionNumber) || mod.VersionNumber.Length > 100 ||
                (mod.Sha1 is null && mod.Sha512 is null) ||
                (mod.Sha1 is not null && !HasValidHash(mod.Sha1, 40)) ||
                (mod.Sha512 is not null && !HasValidHash(mod.Sha512, 128)) ||
                !IsValidModEnvironment(mod.ClientEnvironment) || !IsValidModEnvironment(mod.ServerEnvironment))
            {
                throw new InvalidDataException("Sakana's Modrinth mod tracking file contains invalid or duplicate entries.");
            }
        }

        return index;
    }

    private async Task SaveInstalledModIndexAsync(
        InstanceProfile profile,
        InstalledModIndex index,
        CancellationToken cancellationToken)
    {
        var path = GetInstalledModIndexPath(profile);
        var directory = Path.GetDirectoryName(path)!;
        EnsureNoReparsePoint(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoint(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            EnsureNoReparsePoint(path);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetInstalledModIndexPath(InstanceProfile profile)
    {
        var instancesDirectory = Path.Combine(dataDirectory, "instances");
        EnsureNoReparsePoint(instancesDirectory);
        var instanceDirectory = Path.Combine(instancesDirectory, InstanceDirectoryName.ForProfile(profile));
        EnsureNoReparsePoint(instanceDirectory);
        var metadataDirectory = Path.Combine(instanceDirectory, ".sakana");
        EnsureNoReparsePoint(metadataDirectory);
        return Path.Combine(metadataDirectory, "modrinth-mods.json");
    }

    private string GetExistingModPath(InstanceProfile profile, string fileName, bool isEnabled)
    {
        return Path.Combine(GetModsDirectory(profile), isEnabled ? fileName : fileName + ".disabled");
    }

    private string GetModsDirectory(InstanceProfile profile)
    {
        var instancesDirectory = Path.Combine(dataDirectory, "instances");
        EnsureNoReparsePoint(instancesDirectory);
        var instanceDirectory = Path.Combine(instancesDirectory, InstanceDirectoryName.ForProfile(profile));
        EnsureNoReparsePoint(instanceDirectory);
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        EnsureNoReparsePoint(gameDirectory);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        EnsureNoReparsePoint(modsDirectory);
        return modsDirectory;
    }

    private async Task<bool> IsTrackedFileUnmodifiedAsync(
        InstanceProfile profile,
        TrackedModInstall tracked,
        CancellationToken cancellationToken)
    {
        var enabledPath = GetExistingModPath(profile, tracked.FileName, isEnabled: true);
        var path = File.Exists(enabledPath) ? enabledPath : enabledPath + ".disabled";
        EnsureNoReparsePoint(path);
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (tracked.Sha512 is not null)
        {
            var hash = await SHA512.HashDataAsync(stream, cancellationToken);
            return string.Equals(Convert.ToHexString(hash), tracked.Sha512, StringComparison.OrdinalIgnoreCase);
        }

        var sha1 = await SHA1.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(sha1), tracked.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> FileMatchesArtifactAsync(
        string path,
        InstallArtifact artifact,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (artifact.Sha512 is not null)
        {
            var hash = await SHA512.HashDataAsync(stream, cancellationToken);
            return string.Equals(Convert.ToHexString(hash), artifact.Sha512, StringComparison.OrdinalIgnoreCase);
        }

        var sha1 = await SHA1.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(sha1), artifact.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FindPreviousEnabledState(
        PlannedModFile planned,
        IReadOnlyCollection<TrackedModInstall> replacingEntries,
        IReadOnlyDictionary<string, (string Path, bool IsEnabled)> replacementPaths,
        IReadOnlyDictionary<string, MinecraftModFile> existingMods)
    {
        var previous = replacingEntries.FirstOrDefault(item => string.Equals(item.ProjectId, planned.Version.ProjectId, StringComparison.Ordinal));
        if (previous is null || !replacementPaths.ContainsKey(previous.FileName) || !existingMods.TryGetValue(previous.FileName, out var mod))
        {
            return true;
        }

        return mod.IsEnabled;
    }

    private static void EnsureNoReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Sakana refused to use a redirected mod or tracking path.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string NormalizeTrackedModFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length > 240 || fileName.Any(char.IsControl) ||
            Path.GetInvalidFileNameChars().Any(fileName.Contains))
        {
            throw new InvalidDataException("The tracked Modrinth mod filename is invalid.");
        }

        return fileName;
    }

    private async Task ResolveRequiredDependenciesAsync(
        string versionId,
        InstanceProfile profile,
        HashSet<string> visitedVersions,
        HashSet<string> visitingProjects,
        List<CatalogVersion> installPlan,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 16 || visitedVersions.Count >= 64)
        {
            throw new InvalidDataException("This mod's required dependency chain is too large to install safely.");
        }

        if (!visitedVersions.Add(versionId))
        {
            return;
        }

        var version = await GetVersionAsync(versionId, cancellationToken);
        if (!IsCompatible(version, profile))
        {
            throw new InvalidDataException($"Modrinth version {version.VersionNumber} is not compatible with Minecraft {profile.VersionId} and {profile.LoaderType}.");
        }

        if (!visitingProjects.Add(version.ProjectId))
        {
            throw new InvalidDataException("Modrinth returned a circular required dependency between mods.");
        }

        foreach (var dependency in version.Dependencies.Where(item =>
                     string.Equals(item.DependencyType, "required", StringComparison.OrdinalIgnoreCase)))
        {
            var requiredVersionId = dependency.VersionId;
            if (string.IsNullOrWhiteSpace(requiredVersionId))
            {
                if (string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    throw new InvalidDataException($"A required dependency for {version.Name} has no project or version ID.");
                }

                var dependencyVersions = await GetProjectVersionsAsync(dependency.ProjectId, profile, GetLoaderSlug(profile.LoaderType), cancellationToken);
                var compatibleDependency = dependencyVersions
                    .Where(candidate => IsCompatible(candidate, profile) && TryGetPrimaryJar(candidate, out _))
                    .OrderBy(candidate => VersionTypeOrder(candidate.VersionType))
                    .ThenByDescending(candidate => candidate.PublishedAt)
                    .FirstOrDefault();
                requiredVersionId = compatibleDependency?.Id
                    ?? throw new InvalidDataException($"No compatible release was found for required dependency {dependency.ProjectId}.");
            }

            await ResolveRequiredDependenciesAsync(
                ValidateIdentifier(requiredVersionId, "dependency version ID"),
                profile,
                visitedVersions,
                visitingProjects,
                installPlan,
                depth + 1,
                cancellationToken);
        }

        visitingProjects.Remove(version.ProjectId);
        installPlan.Add(version);
    }

    private static void ValidateNoSelectedIncompatibilities(IReadOnlyCollection<CatalogVersion> versions)
    {
        var projectIds = versions.Select(version => version.ProjectId).ToHashSet(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var conflict = version.Dependencies.FirstOrDefault(dependency =>
                string.Equals(dependency.DependencyType, "incompatible", StringComparison.OrdinalIgnoreCase) &&
                dependency.ProjectId is not null && projectIds.Contains(dependency.ProjectId));
            if (conflict is not null)
            {
                throw new InvalidDataException($"The selected mods have incompatible dependency metadata ({version.Name} and {conflict.ProjectId}).");
            }
        }
    }

    private async Task<IReadOnlyList<CatalogVersion>> GetProjectVersionsAsync(
        string projectId,
        InstanceProfile profile,
        string? loaderFilter,
        CancellationToken cancellationToken)
    {
        var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { profile.VersionId }));
        var loaders = loaderFilter is null
            ? string.Empty
            : $"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderFilter }))}&";
        var uri = new Uri($"{ApiRoot}/project/{Uri.EscapeDataString(projectId)}/version?{loaders}game_versions={gameVersions}&include_changelog=false");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CatalogVersion>>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty version list.");
    }

    private async Task<CatalogVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{ApiRoot}/version/{Uri.EscapeDataString(versionId)}");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogVersion>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty mod version.");
    }

    private async Task<string> GetProjectTypeAsync(string projectId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{ApiRoot}/project/{Uri.EscapeDataString(ValidateIdentifier(projectId, "project ID"))}");
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<CatalogProject>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Modrinth returned an empty project.");
        return project.ProjectType;
    }

    private static bool IsCompatible(CatalogVersion version, InstanceProfile profile) =>
        IsListed(version)
            ? version.GameVersions.Contains(profile.VersionId, StringComparer.Ordinal) &&
              version.Loaders.Contains(GetLoaderSlug(profile.LoaderType), StringComparer.OrdinalIgnoreCase) &&
              ClientEnvironments.Contains(version.Environment, StringComparer.OrdinalIgnoreCase)
            : false;

    private static bool IsListed(CatalogVersion version) =>
        version.Status is null || string.Equals(version.Status, "listed", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPrimaryJar(CatalogVersion version, out CatalogFile file)
    {
        file = version.Files.FirstOrDefault(candidate => candidate.Primary &&
                candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            ?? version.Files.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))!;
        return file is not null;
    }

    private static bool TryGetPrimaryZip(CatalogVersion version, out CatalogFile file)
    {
        file = version.Files.FirstOrDefault(candidate => candidate.Primary &&
                candidate.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? version.Files.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))!;
        return file is not null;
    }

    private static void ValidateDownloadFile(CatalogFile file, string expectedExtension, long maximumSizeBytes = 2L * 1024 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(file.FileName) ||
            !string.Equals(Path.GetFileName(file.FileName), file.FileName, StringComparison.Ordinal) ||
            file.FileName.Any(char.IsControl) ||
            Path.GetInvalidFileNameChars().Any(file.FileName.Contains) ||
            !file.FileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Modrinth returned an invalid {expectedExtension.TrimStart('.')} filename.");
        }

        if (file.Size < 0 || file.Size > maximumSizeBytes)
        {
            throw new InvalidDataException($"Modrinth returned an invalid file size for {file.FileName}.");
        }

        if ((!string.IsNullOrWhiteSpace(file.Hashes.Sha1) && !HasValidHash(file.Hashes.Sha1, 40)) ||
            (!string.IsNullOrWhiteSpace(file.Hashes.Sha512) && !HasValidHash(file.Hashes.Sha512, 128)) ||
            (!HasValidHash(file.Hashes.Sha1, 40) && !HasValidHash(file.Hashes.Sha512, 128)))
        {
            throw new InvalidDataException($"Modrinth did not provide a valid SHA-1 or SHA-512 hash for {file.FileName}.");
        }

        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(uri.Host, "modrinth.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Modrinth returned an unsupported download URL for {file.FileName}.");
        }
    }

    private static bool HasValidHash(string? value, int length) =>
        value is { Length: var actualLength } && actualLength == length && value.All(Uri.IsHexDigit);

    private static bool IsValidModEnvironment(string? value) =>
        string.Equals(value, "required", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "optional", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "unsupported", StringComparison.OrdinalIgnoreCase);

    private static int VersionTypeOrder(string versionType) => versionType switch
    {
        "release" => 0,
        "beta" => 1,
        _ => 2
    };

    private static string GetLoaderSlug(MinecraftLoaderType loaderType) => loaderType switch
    {
        MinecraftLoaderType.Fabric => "fabric",
        MinecraftLoaderType.Quilt => "quilt",
        MinecraftLoaderType.Forge => "forge",
        MinecraftLoaderType.NeoForge => "neoforge",
        _ => throw new NotSupportedException("Modrinth mod installation requires a mod loader instance.")
    };

    private static string GetProjectType(ModrinthContentKind kind) => kind switch
    {
        ModrinthContentKind.ResourcePack => "resourcepack",
        ModrinthContentKind.ShaderPack => "shader",
        ModrinthContentKind.DataPack => "datapack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static MinecraftLocalPackKind ToLocalPackKind(ModrinthContentKind kind) => kind switch
    {
        ModrinthContentKind.ResourcePack => MinecraftLocalPackKind.ResourcePack,
        ModrinthContentKind.ShaderPack => MinecraftLocalPackKind.ShaderPack,
        ModrinthContentKind.DataPack => MinecraftLocalPackKind.DataPack,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Uri BuildSearchUri(string queryPart, string facetsJson, string sortOrder, int offset)
    {
        var normalizedSortOrder = sortOrder?.Trim().ToLowerInvariant();
        if (normalizedSortOrder is not ("relevance" or "downloads" or "follows" or "newest" or "updated"))
        {
            throw new ArgumentException("Choose a supported Modrinth sort order.", nameof(sortOrder));
        }

        if (offset is < 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The Modrinth search offset must be between 0 and 10,000.");
        }

        return new Uri($"{ApiRoot}/search?{queryPart}facets={facetsJson}&index={normalizedSortOrder}&offset={offset}&limit=25");
    }

    private static ModrinthProjectSearchPage ToSearchPage(SearchResponse response)
    {
        var projects = response.Hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.ProjectId) && !string.IsNullOrWhiteSpace(hit.Title))
            .Select(ToProject)
            .ToArray();
        return new ModrinthProjectSearchPage(projects, response.Offset, response.Limit, response.TotalHits);
    }

    private static bool IsSupportedModpackVersion(CatalogVersion version) =>
        IsListed(version) &&
        version.Loaders.Any(loader => string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(loader, "quilt", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(loader, "minecraft", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetPrimaryModpack(CatalogVersion version, out CatalogFile file)
    {
        file = version.Files.FirstOrDefault(candidate => candidate.Primary &&
                candidate.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            ?? version.Files.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))!;
        return file is not null;
    }

    private sealed record PlannedModFile(CatalogVersion Version, CatalogFile File, InstallArtifact Artifact)
    {
        public string FileName => File.FileName;

        public TrackedModInstall ToIndexEntry() => new(
            FileName,
            Version.ProjectId,
            Version.Id,
            Version.VersionNumber,
            Version.PublishedAt,
            File.Hashes.Sha1,
            File.Hashes.Sha512,
            File.Url,
            Version.ClientSide,
            Version.ServerSide);
    }

    private sealed record InstalledModIndex
    {
        [JsonConstructor]
        public InstalledModIndex(int schemaVersion, List<TrackedModInstall> mods)
        {
            SchemaVersion = schemaVersion;
            Mods = mods;
        }

        public int SchemaVersion { get; init; }
        public List<TrackedModInstall> Mods { get; init; }
        public static InstalledModIndex Empty => new(1, []);
    }

    private sealed record TrackedModInstall
    {
        [JsonConstructor]
        public TrackedModInstall(
            string fileName,
            string projectId,
            string versionId,
            string versionNumber,
            DateTimeOffset publishedAt,
            string? sha1,
            string? sha512,
            string? url = null,
            string? clientEnvironment = null,
            string? serverEnvironment = null)
        {
            FileName = fileName;
            ProjectId = projectId;
            VersionId = versionId;
            VersionNumber = versionNumber;
            PublishedAt = publishedAt;
            Sha1 = sha1;
            Sha512 = sha512;
            Url = url;
            ClientEnvironment = clientEnvironment ?? "required";
            ServerEnvironment = serverEnvironment ?? "required";
        }

        public string FileName { get; init; }
        public string ProjectId { get; init; }
        public string VersionId { get; init; }
        public string VersionNumber { get; init; }
        public DateTimeOffset PublishedAt { get; init; }
        public string? Sha1 { get; init; }
        public string? Sha512 { get; init; }
        public string? Url { get; init; }
        public string ClientEnvironment { get; init; }
        public string ServerEnvironment { get; init; }
    }

    private static ModrinthModProject ToProject(SearchHit hit) => new(
        hit.ProjectId,
        hit.Slug,
        hit.Title,
        hit.Description,
        hit.Author,
        hit.Downloads,
        hit.IconUrl,
        hit.Categories);

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 80 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException("The Modrinth identifier is invalid.", parameterName);
        }

        return value;
    }

    private static void ValidateFabricProfile(InstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.LoaderType is not (MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge) || profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install a Fabric, Quilt, Forge, or NeoForge instance before browsing or installing Modrinth mods.");
        }
    }

    private static void ValidateContentProfile(InstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install this instance before browsing or installing resource or shader packs.");
        }
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")]
        public List<SearchHit> Hits { get; init; } = [];

        [JsonPropertyName("offset")]
        public int Offset { get; init; }

        [JsonPropertyName("limit")]
        public int Limit { get; init; }

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; init; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; init; } = string.Empty;

        [JsonPropertyName("slug")]
        public string Slug { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; init; } = string.Empty;

        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; init; }

        [JsonPropertyName("categories")]
        public List<string> Categories { get; init; } = [];
    }

    private sealed class CatalogProject
    {
        [JsonPropertyName("project_type")]
        public string ProjectType { get; init; } = string.Empty;
    }

    private sealed class CatalogVersion
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("project_id")]
        public string ProjectId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("version_number")]
        public string VersionNumber { get; init; } = string.Empty;

        [JsonPropertyName("version_type")]
        public string VersionType { get; init; } = "release";

        [JsonPropertyName("date_published")]
        public DateTimeOffset PublishedAt { get; init; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; init; }

        [JsonPropertyName("game_versions")]
        public List<string> GameVersions { get; init; } = [];

        [JsonPropertyName("loaders")]
        public List<string> Loaders { get; init; } = [];

        [JsonPropertyName("environment")]
        public string Environment { get; init; } = "unknown";

        [JsonPropertyName("client_side")]
        public string ClientSide { get; init; } = "required";

        [JsonPropertyName("server_side")]
        public string ServerSide { get; init; } = "required";

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("files")]
        public List<CatalogFile> Files { get; init; } = [];

        [JsonPropertyName("dependencies")]
        public List<CatalogDependency> Dependencies { get; init; } = [];
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("hashes")]
        public CatalogFileHashes Hashes { get; init; } = new();

        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;

        [JsonPropertyName("filename")]
        public string FileName { get; init; } = string.Empty;

        [JsonPropertyName("primary")]
        public bool Primary { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }

    private sealed class CatalogFileHashes
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }

        [JsonPropertyName("sha512")]
        public string? Sha512 { get; init; }
    }

    private sealed class CatalogDependency
    {
        [JsonPropertyName("version_id")]
        public string? VersionId { get; init; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; init; }

        [JsonPropertyName("dependency_type")]
        public string DependencyType { get; init; } = string.Empty;
    }
}
