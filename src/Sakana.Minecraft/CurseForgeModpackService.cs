using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class CurseForgeModpackService(
    HttpClient httpClient,
    ICurseForgeApiKeyStore apiKeyStore,
    DownloadConcurrencyOptions? downloadConcurrencyOptions = null) : ICurseForgeModpackService
{
    private const string ApiRoot = "https://api.curseforge.com/v1";
    private const int MaximumArchiveEntries = 30000;
    private const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const long MaximumSingleOverrideBytes = 1024L * 1024 * 1024;
    private const long MaximumOverridesBytes = 4L * 1024 * 1024 * 1024;
    private static readonly string[] AllowedDownloadHosts = ["forgecdn.net"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ParallelArtifactDownloader _downloader = new(httpClient, downloadConcurrencyOptions);

    public async Task<CurseForgeModpackDescription> InspectAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);
        return Describe(manifest, GetOverrideEntries(archivePath).Count);
    }

    public async Task<IReadOnlyList<CurseForgeManualDownload>> InstallAsync(
        InstanceProfile profile,
        string dataDirectory,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.CurseForgePackPath)) return Array.Empty<CurseForgeManualDownload>();

        var dataRoot = Path.GetFullPath(dataDirectory);
        var archivePath = ResolveDataPath(dataRoot, profile.CurseForgePackPath);
        EnsureNoReparsePoints(dataRoot, archivePath);
        var manifest = await ReadManifestAsync(archivePath, cancellationToken);
        var selectedOptionalIds = DeserializeSelectedOptionalFiles(profile.ModrinthSelectedOptionalFilesJson)
            .ToHashSet(StringComparer.Ordinal);
        var selectedFiles = manifest.Files
            .Where(file => file.Required || selectedOptionalIds.Contains(GetFileKey(file)))
            .ToArray();

        string? apiKey = null;
        if (selectedFiles.Length > 0)
        {
            apiKey = await apiKeyStore.ReadAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                ValidateApiKey(apiKey);
            }
        }

        var resolved = selectedFiles.Length == 0
            ? new ResolvedCurseForgeFiles(Array.Empty<CurseForgeApiFile>(), Array.Empty<CurseForgeManualDownload>())
            : string.IsNullOrWhiteSpace(apiKey)
                ? new ResolvedCurseForgeFiles(
                    Array.Empty<CurseForgeApiFile>(),
                    selectedFiles.Select(file => new CurseForgeManualDownload(
                        file.ProjectId,
                        file.FileId,
                        $"项目 {file.ProjectId} · 文件 {file.FileId}",
                        $"CurseForge 项目 {file.ProjectId}",
                        CreateOfficialFilePageUrl(null, file.ProjectId, file.FileId))).ToArray())
                : await ResolveFilesAsync(apiKey, selectedFiles, cancellationToken);
        var resolvedFiles = resolved.DownloadableFiles;
        var overrideEntries = GetOverrideEntries(archivePath);
        var overridePaths = overrideEntries.Select(entry => ValidateRelativePath(entry.Path))
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var downloadsRoot = Path.Combine(dataRoot, "downloads");
        EnsureDirectoryNotReparsePoint(dataRoot, downloadsRoot);
        Directory.CreateDirectory(downloadsRoot);
        var stagingRoot = Path.Combine(downloadsRoot, $"curseforge-stage-{profile.Id:N}-{Guid.NewGuid():N}");
        var instanceDirectory = Path.Combine(dataRoot, "instances", InstanceDirectoryName.ForProfile(profile));
        var gameDirectory = Path.Combine(instanceDirectory, "game");

        try
        {
            Directory.CreateDirectory(stagingRoot);
            EnsureDirectoryNotReparsePoint(downloadsRoot, stagingRoot);
            var artifacts = new List<InstallArtifact>();
            foreach (var file in resolvedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = ValidateFileName(file.FileName);
                var relativePath = $"mods/{fileName}";
                if (overridePaths.Contains(relativePath))
                {
                    throw new InvalidDataException($"CurseForge package entries contain a duplicate path: {relativePath}");
                }

                artifacts.Add(new InstallArtifact(
                    relativePath,
                    ValidateDownloadUrl(file.DownloadUrl, relativePath),
                    file.Sha1,
                    file.Size,
                    AllowedHosts: AllowedDownloadHosts));
            }

            if (artifacts.Count > 0)
            {
                await _downloader.DownloadAllAsync(profile.Id, stagingRoot, artifacts, progress, cancellationToken);
            }

            await ExtractOverridesAsync(archivePath, stagingRoot, overrideEntries, cancellationToken, progress, profile.Id);
            EnsureDirectoryNotReparsePoint(dataRoot, instanceDirectory);
            EnsureDirectoryNotReparsePoint(dataRoot, gameDirectory);
            Directory.CreateDirectory(gameDirectory);
            EnsureDirectoryNotReparsePoint(dataRoot, gameDirectory);
            await CopyStagedFilesAsync(stagingRoot, gameDirectory, cancellationToken);
            return resolved.ManualDownloads;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                await Task.Run(() => DeleteDirectoryTreeWithoutFollowingLinks(stagingRoot), CancellationToken.None);
            }
        }
    }

    private async Task<PackManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("选择从 CurseForge 导出的整合包 ZIP 文件。");
        }

        if (new FileInfo(fullPath).Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("CurseForge 整合包 ZIP 超过 4 GB 导入上限。");
        }

        await using var archiveFile = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException($"整合包包含的条目过多（上限 {MaximumArchiveEntries}）。");
        }

        var manifests = archive.Entries.Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifests.Length != 1 || manifests[0].Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("这不是有效的 CurseForge 整合包：ZIP 根目录必须有一个不超过 8 MB 的 manifest.json。");
        }

        var manifestBytes = await ReadEntryBoundedAsync(manifests[0], MaximumManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("CurseForge manifest.json 为空或格式无效。");
        ValidateManifest(manifest);
        _ = GetOverrideEntries(archive);
        return manifest;
    }

    private static void ValidateManifest(PackManifest manifest)
    {
        if (!string.Equals(manifest.ManifestType, "minecraftModpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ZIP manifest.json 不是 CurseForge Minecraft Java 整合包格式。");
        }

        manifest.Name = CleanDisplayValue(manifest.Name, 100, "整合包名称");
        manifest.Version = CleanDisplayValue(manifest.Version, 100, "整合包版本");
        manifest.Author = CleanDisplayValue(manifest.Author, 100, "整合包作者");
        if (manifest.Minecraft is null)
        {
            throw new InvalidDataException("CurseForge manifest.json 缺少 Minecraft 版本信息。");
        }

        if (manifest.Files is null || manifest.Minecraft.ModLoaders is null)
        {
            throw new InvalidDataException("CurseForge manifest.json 中的 files 或 modLoaders 字段无效。");
        }

        manifest.Minecraft.Version = CleanMinecraftVersion(manifest.Minecraft.Version);
        if (manifest.Files.Count > 20000)
        {
            throw new InvalidDataException("CurseForge 整合包声明了过多文件。");
        }

        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file.ProjectId <= 0 || file.FileId <= 0)
            {
                throw new InvalidDataException("CurseForge manifest.json 包含无效的项目或文件 ID。");
            }

            if (!fileIds.Add(GetFileKey(file)))
            {
                throw new InvalidDataException($"CurseForge manifest.json 重复引用文件 {GetFileKey(file)}。");
            }
        }

        var loader = manifest.Minecraft.ModLoaders.FirstOrDefault(item => item.Primary) ?? manifest.Minecraft.ModLoaders.FirstOrDefault();
        if (loader is not null)
        {
            (manifest.SelectedLoaderType, manifest.SelectedLoaderVersion) = ParseLoader(loader.Id);
        }
        else
        {
            manifest.SelectedLoaderType = MinecraftLoaderType.Vanilla;
            manifest.SelectedLoaderVersion = null;
        }
    }

    private static CurseForgeModpackDescription Describe(PackManifest manifest, int overrideFileCount) => new(
        manifest.Name,
        manifest.Version,
        manifest.Author,
        manifest.Minecraft!.Version,
        manifest.SelectedLoaderType,
        manifest.SelectedLoaderVersion,
        manifest.Files.Select(file => new CurseForgeModpackFile(file.ProjectId, file.FileId, file.Required)).ToArray(),
        overrideFileCount);

    private async Task<ResolvedCurseForgeFiles> ResolveFilesAsync(
        string apiKey,
        IReadOnlyList<ManifestFile> requestedFiles,
        CancellationToken cancellationToken)
    {
        var result = new List<CurseForgeApiFile>(requestedFiles.Count);
        foreach (var batch in requestedFiles.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}/mods/files")
            {
                Content = JsonContent.Create(new GetFilesRequest(batch.Select(file => file.FileId).ToArray()))
            };
            request.Headers.Add("x-api-key", apiKey);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException(response.StatusCode);
            }

            var payload = await response.Content.ReadFromJsonAsync<ApiResponse<List<CurseForgeApiFile>>>(JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("CurseForge API 返回空的文件信息。");
            result.AddRange(payload.Data ?? []);
        }

        var byId = result.GroupBy(file => file.Id).ToDictionary(group => group.Key, group => group.First());
        var downloads = new List<CurseForgeApiFile>(requestedFiles.Count);
        var manualDownloads = new List<CurseForgeManualDownload>();
        foreach (var requested in requestedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(requested.FileId, out var file) || file.ModId != requested.ProjectId)
            {
                throw new InvalidDataException($"CurseForge 文件 {GetFileKey(requested)} 不存在或与整合包清单不匹配。");
            }

            if (!file.IsAvailable)
            {
                manualDownloads.Add(await CreateManualDownloadAsync(apiKey, requested, file.FileName, cancellationToken));
                continue;
            }

            var url = file.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = await GetFileDownloadUrlAsync(apiKey, file.ModId, file.Id, cancellationToken);
                if (string.IsNullOrWhiteSpace(url))
                {
                    manualDownloads.Add(await CreateManualDownloadAsync(apiKey, requested, file.FileName, cancellationToken));
                    continue;
                }
            }

            var safeFileName = ValidateFileName(file.FileName);
            var sha1 = file.Hashes?.FirstOrDefault(hash => hash.Algorithm == 1)?.Value;
            if (string.IsNullOrWhiteSpace(sha1) || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"CurseForge 没有为 {safeFileName} 提供有效的 SHA-1 校验值。");
            }

            downloads.Add(file with
            {
                FileName = safeFileName,
                DownloadUrl = ValidateDownloadUrl(url, safeFileName),
                Sha1 = sha1.ToLowerInvariant(),
                Size = file.FileLength,
                PathHashIsUnique = true
            });
        }

        var duplicateNames = downloads.GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidDataException($"CurseForge 整合包中的文件重名：{duplicateNames[0].Key}。请联系整合包作者修正包清单。");
        }

        return new ResolvedCurseForgeFiles(downloads, manualDownloads);
    }

    private async Task<string?> GetFileDownloadUrlAsync(string apiKey, long modId, long fileId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/mods/{modId}/files/{fileId}/download-url");
        request.Headers.Add("x-api-key", apiKey);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                return null;
            }

            throw CreateApiException(response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<string>>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("CurseForge API 没有返回这个文件的下载地址。");
        return payload.Data;
    }

    private async Task<CurseForgeManualDownload> CreateManualDownloadAsync(
        string apiKey,
        ManifestFile requested,
        string? fileName,
        CancellationToken cancellationToken)
    {
        CurseForgeApiMod? project = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/mods/{requested.ProjectId}");
            request.Headers.Add("x-api-key", apiKey);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<ApiResponse<CurseForgeApiMod>>(JsonOptions, cancellationToken);
                project = payload?.Data;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Missing project-page metadata must not cancel the rest of the modpack install.
        }
        catch (JsonException)
        {
            // Missing project-page metadata must not cancel the rest of the modpack install.
        }

        var officialPageUrl = CreateOfficialFilePageUrl(project?.Links?.WebsiteUrl, requested.ProjectId, requested.FileId);
        var safeFileName = string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240 || fileName.Any(char.IsControl)
            ? $"项目 {requested.ProjectId} · 文件 {requested.FileId}"
            : fileName;
        return new CurseForgeManualDownload(requested.ProjectId, requested.FileId, safeFileName, project?.Name ?? $"项目 {requested.ProjectId}", officialPageUrl);
    }

    private static string CreateOfficialFilePageUrl(string? rawWebsiteUrl, long projectId, long fileId)
    {
        if (Uri.TryCreate(rawWebsiteUrl, UriKind.Absolute, out var projectUri) &&
            projectUri.Scheme == Uri.UriSchemeHttps &&
            (string.Equals(projectUri.Host, "curseforge.com", StringComparison.OrdinalIgnoreCase) ||
             projectUri.Host.EndsWith(".curseforge.com", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrEmpty(projectUri.Query) && string.IsNullOrEmpty(projectUri.Fragment))
        {
            return $"{projectUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/files/{fileId}";
        }

        return $"https://www.curseforge.com/minecraft/mc-mods?search={projectId}%20file%20{fileId}";
    }

    private static InvalidOperationException CreateApiException(System.Net.HttpStatusCode statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => new InvalidOperationException("CurseForge API Key 无效、未获准使用，或没有访问此文件的权限。"),
        (System.Net.HttpStatusCode)429 => new InvalidOperationException("CurseForge API 请求过于频繁，请稍后重试。"),
        _ => new InvalidOperationException($"CurseForge API 请求失败（HTTP {(int)statusCode}）。请稍后重试。")
    };

    private static IReadOnlyList<OverrideEntry> GetOverrideEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return GetOverrideEntries(archive);
    }

    private static IReadOnlyList<OverrideEntry> GetOverrideEntries(ZipArchive archive)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zipEntries = archive.Entries.Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.EndsWith("/", StringComparison.Ordinal)).ToArray();
        long totalBytes = 0;
        var entries = new List<OverrideEntry>(zipEntries.Length);
        foreach (var entry in zipEntries)
        {
            var relative = ValidateRelativePath(entry.FullName["overrides/".Length..]);
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"CurseForge overrides 包含不允许的链接：{relative}");
            }

            if (!paths.Add(relative))
            {
                throw new InvalidDataException($"CurseForge overrides 包含重复路径：{relative}");
            }

            if (entry.Length > MaximumSingleOverrideBytes || (totalBytes = checked(totalBytes + entry.Length)) > MaximumOverridesBytes)
            {
                throw new InvalidDataException($"CurseForge overrides 超过 Sakana 的文件解包上限：{relative}");
            }

            entries.Add(new OverrideEntry(relative.Replace('\\', '/'), entry.Length));
        }

        return entries;
    }

    private static async Task ExtractOverridesAsync(
        string archivePath,
        string destinationRoot,
        IReadOnlyList<OverrideEntry> validatedEntries,
        CancellationToken cancellationToken,
        IProgress<InstallationProgress>? progress,
        Guid profileId)
    {
        if (validatedEntries.Count == 0) return;
        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != validatedEntries.Count)
        {
            throw new InvalidDataException("CurseForge ZIP 在校验后发生变化，请重新选择整合包。");
        }

        long completedBytes = 0;
        var completedFiles = 0;
        var expectedEntries = validatedEntries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ValidateRelativePath(entry.FullName["overrides/".Length..]);
            var normalizedRelativePath = relativePath.Replace('\\', '/');
            if (!expectedEntries.TryGetValue(normalizedRelativePath, out var expected) || expected.Length != entry.Length)
            {
                throw new InvalidDataException("CurseForge ZIP 在校验后发生变化，请重新选择整合包。");
            }
            if (entry.Length > MaximumSingleOverrideBytes)
            {
                throw new InvalidDataException($"CurseForge override 文件超过解包上限：{relativePath}");
            }

            var destination = ResolveUnderRoot(destinationRoot, relativePath);
            var parent = Path.GetDirectoryName(destination)!;
            EnsureDirectoryNotReparsePoint(destinationRoot, parent);
            Directory.CreateDirectory(parent);
            var tempPath = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using var source = entry.Open();
                await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    copied += read;
                    if (copied > entry.Length || copied > MaximumSingleOverrideBytes)
                    {
                        throw new InvalidDataException($"CurseForge override 解压后大小超出声明：{relativePath}");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (copied != entry.Length)
                {
                    throw new InvalidDataException($"CurseForge override 文件长度与 ZIP 声明不一致：{relativePath}");
                }

                await output.FlushAsync(cancellationToken);
                File.Move(tempPath, destination, overwrite: true);
                completedBytes += copied;
                completedFiles++;
                progress?.Report(new InstallationProgress(profileId, InstallationStage.Extracting, completedBytes, MaximumOverridesBytes, completedFiles, entries.Length, normalizedRelativePath));
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    private static async Task CopyStagedFilesAsync(string stagingRoot, string gameDirectory, CancellationToken cancellationToken)
    {
        var stack = new Stack<(string Source, string Relative)>();
        stack.Push((stagingRoot, string.Empty));
        while (stack.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("CurseForge staging unexpectedly contains a redirected folder.");
                }

                stack.Push((directory, Path.Combine(current.Relative, Path.GetFileName(directory))));
            }

            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("CurseForge staging unexpectedly contains a redirected file.");
                }

                var relativePath = Path.Combine(current.Relative, Path.GetFileName(file));
                var destination = ResolveUnderRoot(gameDirectory, relativePath);
                var destinationParent = Path.GetDirectoryName(destination)!;
                EnsureDirectoryNotReparsePoint(gameDirectory, destinationParent);
                Directory.CreateDirectory(destinationParent);
                await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken);
            }
        }
    }

    private static string ResolveDataPath(string dataDirectory, string relativePath)
    {
        var root = Path.GetFullPath(dataDirectory);
        var resolved = ResolveUnderRoot(root, relativePath);
        return resolved;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(relativePath, fullRoot);
        if (!target.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("CurseForge archive attempted to access a path outside its destination.");
        }

        return target;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        EnsureNotReparsePoint(current);
        if (relative == ".") return;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureDirectoryNotReparsePoint(string root, string directory)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(directory);
        if (!string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Sakana refused a CurseForge path outside its data folder.");
        }

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        EnsureNotReparsePoint(current);
        if (relative == ".") return;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Sakana refused to traverse a redirected CurseForge directory.");
        }

        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Sakana refused to open a redirected CurseForge file.");
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingLinks(string path)
    {
        if (!Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            var childAttributes = File.GetAttributes(child);
            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryTreeWithoutFollowingLinks(child);
            }
            else
            {
                File.Delete(child);
            }
        }

        Directory.Delete(path);
    }

    private static async Task<byte[]> ReadEntryBoundedAsync(ZipArchiveEntry entry, long maxBytes, CancellationToken cancellationToken)
    {
        if (entry.Length > maxBytes) throw new InvalidDataException("CurseForge archive manifest exceeds the size limit.");
        await using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, maxBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidDataException("CurseForge archive manifest expanded beyond the size limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string ValidateDownloadUrl(string? rawUrl, string name)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedDownloadHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"CurseForge returned an invalid or unsupported download URL for {name}.");
        }

        return uri.AbsoluteUri;
    }

    private static string ValidateFileName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName) || rawName.Length > 240 ||
            !string.Equals(Path.GetFileName(rawName), rawName, StringComparison.Ordinal) ||
            rawName.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        {
            throw new InvalidDataException("CurseForge returned a file name that cannot be safely installed.");
        }

        return rawName;
    }

    private static string ValidateRelativePath(string rawPath)
    {
        var path = rawPath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Length > 1000 ||
            Path.IsPathRooted(path) || path.Any(char.IsControl) || path.Contains(':'))
        {
            throw new InvalidDataException("CurseForge overrides contains an invalid path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new InvalidDataException("CurseForge overrides contains a path traversal entry.");
        }

        if (segments.Any(IsWindowsDeviceName))
        {
            throw new InvalidDataException("CurseForge overrides contains a reserved Windows device path.");
        }

        return Path.Combine(segments);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    private static (MinecraftLoaderType Loader, string Version) ParseLoader(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) throw new InvalidDataException("CurseForge manifest.json includes a mod loader without a version.");
        var separator = rawId.IndexOf('-');
        if (separator <= 0 || separator == rawId.Length - 1) throw new InvalidDataException($"CurseForge mod loader ID is invalid: {rawId}");
        var kind = rawId[..separator].ToLowerInvariant();
        var version = rawId[(separator + 1)..];
        if (version.Length > 80 || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '+' and not '_'))
        {
            throw new InvalidDataException($"CurseForge mod loader version is invalid: {rawId}");
        }

        var loader = kind switch
        {
            "fabric" => MinecraftLoaderType.Fabric,
            "quilt" => MinecraftLoaderType.Quilt,
            "forge" => MinecraftLoaderType.Forge,
            "neoforge" => MinecraftLoaderType.NeoForge,
            _ => throw new NotSupportedException($"CurseForge package uses unsupported loader {kind}.")
        };
        return (loader, version);
    }

    private static string CleanMinecraftVersion(string? value)
    {
        var version = CleanDisplayValue(value, 32, "Minecraft 版本");
        if (version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException("CurseForge manifest.json contains an invalid Minecraft version.");
        }

        return version;
    }

    private static string CleanDisplayValue(string? value, int maximumLength, string description)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"CurseForge manifest.json contains an invalid {description}.");
        }

        return normalized;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static string GetFileKey(ManifestFile file) => $"{file.ProjectId}:{file.FileId}";

    private static string[] DeserializeSelectedOptionalFiles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ValidateApiKey(string apiKey)
    {
        if (apiKey.Length > 2048 || apiKey.Any(char.IsControl))
        {
            throw new InvalidDataException("Saved CurseForge API Key is invalid. Remove it from Settings and enter it again.");
        }
    }

    private sealed class PackManifest
    {
        [JsonPropertyName("manifestType")]
        public string ManifestType { get; set; } = string.Empty;
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;
        [JsonPropertyName("minecraft")]
        public MinecraftManifest? Minecraft { get; set; }
        [JsonPropertyName("files")]
        public List<ManifestFile> Files { get; set; } = [];
        [JsonIgnore]
        public MinecraftLoaderType SelectedLoaderType { get; set; }
        [JsonIgnore]
        public string? SelectedLoaderVersion { get; set; }
    }

    private sealed class MinecraftManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
        [JsonPropertyName("modLoaders")]
        public List<ModLoader> ModLoaders { get; set; } = [];
    }

    private sealed class ModLoader
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("primary")]
        public bool Primary { get; set; }
    }

    private sealed class ManifestFile
    {
        [JsonPropertyName("projectID")]
        public long ProjectId { get; set; }
        [JsonPropertyName("fileID")]
        public long FileId { get; set; }
        [JsonPropertyName("required")]
        public bool Required { get; set; } = true;
    }

    private sealed record GetFilesRequest(long[] FileIds);
    private sealed record ResolvedCurseForgeFiles(
        IReadOnlyList<CurseForgeApiFile> DownloadableFiles,
        IReadOnlyList<CurseForgeManualDownload> ManualDownloads);
    private sealed record OverrideEntry(string Path, long Length);
    private sealed class ApiResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private sealed record ApiHash(int Algorithm, string Value);

    private sealed record CurseForgeApiMod(string? Name, CurseForgeApiModLinks? Links);
    private sealed record CurseForgeApiModLinks(string? WebsiteUrl);

    private sealed record CurseForgeApiFile(
        long Id,
        long ModId,
        bool IsAvailable,
        string FileName,
        string? DownloadUrl,
        long FileLength,
        IReadOnlyList<ApiHash> Hashes,
        string Sha1 = "",
        long Size = 0,
        bool PathHashIsUnique = false);
}
