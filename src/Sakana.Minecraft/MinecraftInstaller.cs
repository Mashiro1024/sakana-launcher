using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class MinecraftInstaller : IMinecraftInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _dataDirectory;
    private readonly MojangMetadataClient _metadataClient;
    private readonly IFabricLoaderCatalog _fabricLoaderCatalog;
    private readonly IQuiltLoaderCatalog _quiltLoaderCatalog;
    private readonly IForgeNeoForgeLoaderCatalog _forgeNeoForgeLoaderCatalog;
    private readonly ParallelArtifactDownloader _downloader;
    private readonly MojangJavaRuntimeManager _javaRuntimeManager;
    private readonly IModrinthModpackService _modrinthModpackService;
    private readonly ICurseForgeModpackService _curseForgeModpackService;

    public MinecraftInstaller(
        HttpClient httpClient,
        string dataDirectory,
        MojangMetadataClient metadataClient,
        IFabricLoaderCatalog fabricLoaderCatalog,
        IQuiltLoaderCatalog quiltLoaderCatalog,
        IForgeNeoForgeLoaderCatalog forgeNeoForgeLoaderCatalog,
        MojangJavaRuntimeManager javaRuntimeManager,
        IModrinthModpackService modrinthModpackService,
        ICurseForgeModpackService curseForgeModpackService,
        DownloadConcurrencyOptions? downloadConcurrencyOptions = null)
    {
        _httpClient = httpClient;
        _dataDirectory = dataDirectory;
        _metadataClient = metadataClient;
        _fabricLoaderCatalog = fabricLoaderCatalog;
        _quiltLoaderCatalog = quiltLoaderCatalog;
        _forgeNeoForgeLoaderCatalog = forgeNeoForgeLoaderCatalog;
        _downloader = new ParallelArtifactDownloader(httpClient, downloadConcurrencyOptions);
        _javaRuntimeManager = javaRuntimeManager;
        _modrinthModpackService = modrinthModpackService;
        _curseForgeModpackService = curseForgeModpackService;
    }

    public async Task<IReadOnlyList<CurseForgeManualDownload>> InstallAsync(
        InstanceProfile profile,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRevalidation = false)
    {
        var safeVersionId = GetSafeSegment(profile.VersionId);
        var instanceDirectoryName = InstanceDirectoryName.ForProfile(profile);
        var instanceDirectory = Path.Combine(_dataDirectory, "instances", instanceDirectoryName);
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        var versionDirectory = Path.Combine(gameDirectory, "versions", safeVersionId);
        var sharedDirectory = Path.Combine(_dataDirectory, "shared");
        var assetsDirectory = Path.Combine(sharedDirectory, "assets");
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var nativesDirectory = Path.Combine(gameDirectory, "natives");

        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(librariesDirectory);
        Directory.CreateDirectory(assetsDirectory);

        Report(InstallationStage.Resolving, 0, 0, 0, 0, safeVersionId);
        var (vanillaMetadata, metadataJson) = await _metadataClient.GetVersionMetadataAsync(safeVersionId, cancellationToken);
        var metadata = vanillaMetadata;
        MojangVersionMetadata? loaderProfile = null;
        var isForgeFamily = profile.LoaderType is MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge;
        if (profile.LoaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt)
        {
            if (string.IsNullOrWhiteSpace(profile.LoaderVersion))
            {
                throw new InvalidDataException($"The {profile.LoaderType} instance does not specify a loader version.");
            }

            var loaderProfileJson = profile.LoaderType == MinecraftLoaderType.Fabric
                ? await _fabricLoaderCatalog.GetProfileJsonAsync(safeVersionId, profile.LoaderVersion, cancellationToken)
                : await _quiltLoaderCatalog.GetProfileJsonAsync(safeVersionId, profile.LoaderVersion, cancellationToken);
            loaderProfile = MojangMetadataClient.DeserializeLoaderProfile(
                loaderProfileJson,
                profile.LoaderType,
                profile.LoaderVersion,
                safeVersionId);
            metadata = MergeLoaderProfile(vanillaMetadata, loaderProfile, profile.LoaderType);
            var loaderProfilePath = Path.Combine(instanceDirectory, "loader", $"{profile.LoaderType.ToString().ToLowerInvariant()}-profile.json");
            await WriteAtomicallyAsync(loaderProfilePath, loaderProfileJson, cancellationToken);
        }
        else if (profile.LoaderType is not (MinecraftLoaderType.Vanilla or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge))
        {
            throw new NotSupportedException($"The {profile.LoaderType} loader is not supported yet.");
        }
        if (isForgeFamily && string.IsNullOrWhiteSpace(profile.LoaderVersion))
        {
            throw new InvalidDataException($"The {profile.LoaderType} instance does not specify a loader version.");
        }
        if (!metadata.Downloads.TryGetValue("client", out var clientJar))
        {
            throw new InvalidDataException($"Minecraft {safeVersionId} does not provide a client download.");
        }

        var assetIndex = metadata.AssetIndex
            ?? throw new InvalidDataException($"Minecraft {safeVersionId} does not provide an asset index.");
        var assetIndexId = GetSafeSegment(assetIndex.Id ?? metadata.Assets ?? safeVersionId);
        var assetIndexPath = Path.Combine(assetsDirectory, "indexes", $"{assetIndexId}.json");
        await _downloader.DownloadAllAsync(
            profile.Id,
            _dataDirectory,
            [ToArtifact(assetIndex, Path.GetRelativePath(_dataDirectory, assetIndexPath))],
            progress: null,
            cancellationToken);

        var assetIndexBytes = await File.ReadAllBytesAsync(assetIndexPath, cancellationToken);
        var parsedAssetIndex = MojangMetadataClient.DeserializeAssetIndex(assetIndexBytes);
        await WriteAtomicallyAsync(Path.Combine(versionDirectory, $"{safeVersionId}.json"), metadataJson, cancellationToken);

        var artifacts = new List<InstallArtifact>
        {
            ToArtifact(clientJar, Path.Combine("instances", instanceDirectoryName, "game", "versions", safeVersionId, $"{safeVersionId}.jar"))
        };
        var nativeArtifacts = new List<InstallArtifact>();

        AddLibraryArtifacts(
            isForgeFamily ? vanillaMetadata.Libraries : metadata.Libraries,
            artifacts,
            nativeArtifacts,
            instanceDirectoryName);

        if (metadata.Logging?.Client?.File is { } loggingFile)
        {
            var loggingId = GetSafeSegment(loggingFile.Id ?? "client-logging.xml");
            artifacts.Add(ToArtifact(loggingFile, Path.Combine("shared", "assets", "log_configs", loggingId)));
        }

        foreach (var asset in parsedAssetIndex.Objects)
        {
            var hash = asset.Value.Hash;
            if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"The asset index contains an invalid hash for {asset.Key}.");
            }

            var hashPrefix = hash[..2].ToLowerInvariant();
            var normalizedHash = hash.ToLowerInvariant();
            artifacts.Add(new InstallArtifact(
                Path.Combine("shared", "assets", "objects", hashPrefix, normalizedHash),
                $"https://resources.download.minecraft.net/{hashPrefix}/{normalizedHash}",
                hash,
                asset.Value.Size,
                IsContentAddressed: true));
        }

        Report(InstallationStage.Downloading, 0, artifacts.Where(item => item.Size.HasValue).Sum(item => item.Size!.Value), 0, artifacts.Count, null);
        await _downloader.DownloadAllAsync(
            profile.Id,
            _dataDirectory,
            artifacts,
            progress,
            cancellationToken,
            forceRevalidation);

        var requiredJavaMajor = vanillaMetadata.JavaVersion?.MajorVersion ?? 8;
        Report(InstallationStage.Resolving, 0, 0, 0, 0, $"Java {requiredJavaMajor}");
        var javaExecutablePath = await _javaRuntimeManager.EnsureRuntimeAsync(
            profile.Id,
            vanillaMetadata.JavaVersion,
            progress,
            cancellationToken,
            forceRevalidation,
            profile.JavaExecutablePath);

        if (nativeArtifacts.Count > 0)
        {
            Report(InstallationStage.Extracting, 0, 0, 0, nativeArtifacts.Count, null);
            Directory.CreateDirectory(nativesDirectory);
            foreach (var nativeArtifact in nativeArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archivePath = Path.GetFullPath(nativeArtifact.RelativePath, _dataDirectory);
                ExtractNatives(archivePath, nativesDirectory, nativeArtifact.NativeExclusions ?? [], cancellationToken);
            }
        }

        if (isForgeFamily)
        {
            loaderProfile = await InstallForgeFamilyAsync(
                profile,
                safeVersionId,
                instanceDirectory,
                javaExecutablePath,
                progress,
                cancellationToken,
                forceRevalidation);
            metadata = MergeLoaderProfile(vanillaMetadata, loaderProfile, profile.LoaderType);
            var loaderArtifacts = new List<InstallArtifact>();
            var loaderNativeArtifacts = new List<InstallArtifact>();
            AddLibraryArtifacts(loaderProfile.Libraries, loaderArtifacts, loaderNativeArtifacts, instanceDirectoryName);
            await _downloader.DownloadAllAsync(
                profile.Id,
                _dataDirectory,
                loaderArtifacts,
                progress,
                cancellationToken,
                forceRevalidation);
            if (loaderNativeArtifacts.Count > 0)
            {
                Directory.CreateDirectory(nativesDirectory);
                foreach (var nativeArtifact in loaderNativeArtifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var archivePath = Path.GetFullPath(nativeArtifact.RelativePath, _dataDirectory);
                    ExtractNatives(archivePath, nativesDirectory, nativeArtifact.NativeExclusions ?? [], cancellationToken);
                }
            }
            artifacts.AddRange(loaderArtifacts);
        }

        await _modrinthModpackService.InstallAsync(profile, _dataDirectory, progress, cancellationToken);
        var manualDownloads = await _curseForgeModpackService.InstallAsync(profile, _dataDirectory, progress, cancellationToken);

        Report(InstallationStage.Completed, 1, 1, artifacts.Count, artifacts.Count, null);
        return manualDownloads;

        void Report(InstallationStage stage, long bytes, long totalBytes, int completedFiles, int totalFiles, string? currentFile) =>
            progress?.Report(new InstallationProgress(profile.Id, stage, bytes, totalBytes, completedFiles, totalFiles, currentFile));
    }

    private async Task<MojangVersionMetadata> InstallForgeFamilyAsync(
        InstanceProfile profile,
        string minecraftVersionId,
        string instanceDirectory,
        string javaExecutablePath,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken,
        bool forceRevalidation)
    {
        var loaderType = profile.LoaderType;
        var loaderVersion = profile.LoaderVersion
            ?? throw new InvalidDataException($"The {loaderType} instance does not specify a loader version.");
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        var loaderProfilePath = Path.Combine(instanceDirectory, "loader", $"{loaderType.ToString().ToLowerInvariant()}-profile.json");
        if (File.Exists(loaderProfilePath))
        {
            var existingJson = await File.ReadAllBytesAsync(loaderProfilePath, cancellationToken);
            try
            {
                var existingProfile = MojangMetadataClient.DeserializeForgeFamilyProfile(existingJson, loaderType, loaderVersion, minecraftVersionId);
                var existingJar = FindLoaderClientJar(gameDirectory, existingProfile.Id);
                if (File.Exists(existingJar) || HasInstalledLegacyForgeLibrary(existingProfile, InstanceDirectoryName.ForProfile(profile)))
                {
                    return existingProfile;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
            {
                // An incomplete or stale generated profile is rebuilt from the official installer.
            }
        }

        var installerUrl = BuildInstallerUrl(loaderType, minecraftVersionId, loaderVersion);
        var checksumText = await _httpClient.GetStringAsync($"{installerUrl}.sha1", cancellationToken);
        var expectedSha1 = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expectedSha1 is null || expectedSha1.Length != 40 || !expectedSha1.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"The official {loaderType} installer did not provide a valid SHA-1 checksum.");
        }

        var installerFileName = $"{loaderType.ToString().ToLowerInvariant()}-{minecraftVersionId}-{loaderVersion}-installer.jar";
        var installerRelativePath = Path.Combine("cache", "loaders", loaderType.ToString().ToLowerInvariant(), installerFileName);
        var installerPath = Path.Combine(_dataDirectory, installerRelativePath);
        await _downloader.DownloadAllAsync(
            profile.Id,
            _dataDirectory,
            [new InstallArtifact(installerRelativePath, installerUrl, expectedSha1.ToLowerInvariant(), null)],
            progress,
            cancellationToken,
            forceRevalidation);

        progress?.Report(new InstallationProgress(profile.Id, InstallationStage.Resolving, 0, 0, 0, 0, $"安装 {loaderType} {loaderVersion}"));
        Directory.CreateDirectory(gameDirectory);
        var launcherProfilesPath = Path.Combine(gameDirectory, "launcher_profiles.json");
        if (!File.Exists(launcherProfilesPath))
        {
            // Forge and NeoForge's official client installers use this file as a marker
            // that the target directory is a launcher-managed Minecraft installation.
            // In an isolated Sakana instance, an empty profiles object is sufficient;
            // the installer will add its own launcher profile after generating the files.
            await WriteAtomicallyAsync(launcherProfilesPath, Encoding.UTF8.GetBytes("{\"profiles\":{}}"), cancellationToken);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutablePath,
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-Djava.awt.headless=true");
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(gameDirectory);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start the official {loaderType} installer.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var details = string.Join(Environment.NewLine, new[] { stderr, stdout }.Where(value => !string.IsNullOrWhiteSpace(value)));
                details = details.Length > 4000 ? details[^4000..] : details;
                throw new InvalidOperationException($"The official {loaderType} installer failed with exit code {process.ExitCode}. {details}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var installedProfile = FindInstalledLoaderProfile(gameDirectory, loaderType, loaderVersion, minecraftVersionId);
        var installedJarPath = FindLoaderClientJar(gameDirectory, installedProfile.Id);
        if (!File.Exists(installedJarPath) && !HasInstalledLegacyForgeLibrary(installedProfile, InstanceDirectoryName.ForProfile(profile)))
        {
            throw new InvalidDataException($"The {loaderType} installer completed without producing its client jar ({Path.GetFileName(installedJarPath)}).");
        }

        var installedProfileJsonPath = Path.Combine(gameDirectory, "versions", installedProfile.Id, $"{installedProfile.Id}.json");
        var installedProfileJson = await File.ReadAllBytesAsync(installedProfileJsonPath, cancellationToken);
        await WriteAtomicallyAsync(loaderProfilePath, installedProfileJson, cancellationToken);
        return installedProfile;
    }

    private bool HasInstalledLegacyForgeLibrary(MojangVersionMetadata profile, string instanceDirectoryName)
    {
        var instanceLibraries = Path.Combine(_dataDirectory, "instances", InstanceDirectoryName.Validate(instanceDirectoryName), "game", "libraries");
        var sharedLibraries = Path.Combine(_dataDirectory, "shared", "libraries");
        foreach (var library in profile.Libraries.Where(library => library.Name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase) ||
                                                                   library.Name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = library.Downloads?.Artifact?.Path ?? BuildMavenPath(library.Name);
            var instanceLibraryPath = Path.GetFullPath(relativePath, instanceLibraries);
            var sharedLibraryPath = Path.GetFullPath(relativePath, sharedLibraries);
            var instanceRoot = Path.GetFullPath(instanceLibraries) + Path.DirectorySeparatorChar;
            var sharedRoot = Path.GetFullPath(sharedLibraries) + Path.DirectorySeparatorChar;
            if ((instanceLibraryPath.StartsWith(instanceRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(instanceLibraryPath)) ||
                (sharedLibraryPath.StartsWith(sharedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(sharedLibraryPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildInstallerUrl(MinecraftLoaderType loaderType, string minecraftVersionId, string loaderVersion) => loaderType switch
    {
        MinecraftLoaderType.Forge => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraftVersionId}-{loaderVersion}/forge-{minecraftVersionId}-{loaderVersion}-installer.jar",
        MinecraftLoaderType.NeoForge => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
        _ => throw new ArgumentOutOfRangeException(nameof(loaderType))
    };

    private static MojangVersionMetadata FindInstalledLoaderProfile(
        string instanceDirectory,
        MinecraftLoaderType loaderType,
        string loaderVersion,
        string minecraftVersionId)
    {
        var versionsDirectory = Path.Combine(instanceDirectory, "versions");
        foreach (var profilePath in Directory.EnumerateFiles(versionsDirectory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllBytes(profilePath);
                var profile = MojangMetadataClient.DeserializeForgeFamilyProfile(json, loaderType, loaderVersion, minecraftVersionId);
                if (string.Equals(Path.GetFileNameWithoutExtension(profilePath), profile.Id, StringComparison.Ordinal))
                {
                    return profile;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
            {
                // Other installed versions are not candidates for the selected loader profile.
            }
        }

        throw new InvalidDataException($"The official installer did not generate a {loaderType} profile for Minecraft {minecraftVersionId} / {loaderVersion}.");
    }

    private static string FindLoaderClientJar(string gameDirectory, string profileId) =>
        Path.Combine(gameDirectory, "versions", profileId, $"{profileId}.jar");

    private void AddLibraryArtifacts(
        IEnumerable<MojangLibrary> libraries,
        List<InstallArtifact> artifacts,
        List<InstallArtifact> nativeArtifacts,
        string instanceDirectoryName)
    {
        var libraryRoot = Path.Combine("instances", InstanceDirectoryName.Validate(instanceDirectoryName), "game", "libraries");
        foreach (var library in libraries)
        {
            if (!IsAllowedOnWindows(library.Rules))
            {
                continue;
            }

            if (library.Downloads?.Artifact is { } artifact)
            {
                var relativePath = artifact.Path ?? BuildMavenPath(library.Name);
                artifacts.Add(ToArtifact(WithResolvedUrl(artifact, library.Url, relativePath), Path.Combine(libraryRoot, relativePath)));
            }
            else if (library.Downloads is null)
            {
                var relativePath = BuildMavenPath(library.Name);
                var legacyUrl = BuildLegacyLibraryUrl(library.Url, relativePath);
                artifacts.Add(new InstallArtifact(
                    Path.Combine(libraryRoot, relativePath),
                    legacyUrl,
                    Sha1: library.Sha1,
                    Size: library.Size));
            }

            var nativeClassifier = GetWindowsNativeClassifier(library.Natives);
            if (nativeClassifier is not null && library.Downloads?.Classifiers?.TryGetValue(nativeClassifier, out var nativeArtifact) == true)
            {
                var relativePath = nativeArtifact.Path ?? BuildMavenPath(library.Name, nativeClassifier);
                var exclusions = library.Extract?.Exclude ?? [];
                var installArtifact = ToArtifact(WithResolvedUrl(nativeArtifact, library.Url, relativePath), Path.Combine(libraryRoot, relativePath), exclusions);
                artifacts.Add(installArtifact);
                nativeArtifacts.Add(installArtifact);
            }
            else if (nativeClassifier is not null && library.Downloads is null)
            {
                var relativePath = BuildMavenPath(library.Name, nativeClassifier);
                var installArtifact = new InstallArtifact(
                    Path.Combine(libraryRoot, relativePath),
                    BuildLegacyLibraryUrl(library.Url, relativePath),
                    Sha1: null,
                    Size: null,
                    library.Extract?.Exclude ?? []);
                artifacts.Add(installArtifact);
                nativeArtifacts.Add(installArtifact);
            }
        }
    }

    private static MojangVersionMetadata MergeLoaderProfile(
        MojangVersionMetadata vanilla,
        MojangVersionMetadata loader,
        MinecraftLoaderType loaderType)
    {
        if (!string.Equals(loader.InheritsFrom, vanilla.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{loaderType}'s profile does not inherit from Minecraft {vanilla.Id}.");
        }

        if (string.IsNullOrWhiteSpace(loader.MainClass))
        {
            throw new InvalidDataException($"{loaderType}'s launcher profile does not specify a main class.");
        }

        return new MojangVersionMetadata
        {
            Id = vanilla.Id,
            Assets = vanilla.Assets,
            AssetIndex = vanilla.AssetIndex,
            Downloads = vanilla.Downloads,
            Libraries = [.. vanilla.Libraries, .. loader.Libraries],
            Logging = vanilla.Logging,
            JavaVersion = vanilla.JavaVersion,
            MainClass = loader.MainClass,
            Arguments = vanilla.Arguments,
            MinecraftArguments = vanilla.MinecraftArguments,
            VersionType = vanilla.VersionType
        };
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static InstallArtifact ToArtifact(DownloadArtifact artifact, string relativePath, IReadOnlyList<string>? nativeExclusions = null)
    {
        if (string.IsNullOrWhiteSpace(artifact.Url))
        {
            throw new InvalidDataException($"The official metadata did not provide a URL for {relativePath}.");
        }

        return new InstallArtifact(relativePath, artifact.Url, artifact.Sha1, artifact.Size, nativeExclusions);
    }

    private static DownloadArtifact WithResolvedUrl(DownloadArtifact artifact, string? baseUrl, string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Url))
        {
            return artifact;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidDataException($"The official metadata did not provide a library URL for {relativePath}.");
        }

        return new DownloadArtifactProxy(artifact, $"{baseUrl.TrimEnd('/')}/{relativePath.Replace('\\', '/')}").ToArtifact();
    }

    internal static bool IsAllowedOnWindows(
        System.Text.Json.JsonElement rules,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
        {
            return true;
        }

        if (rules.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatchesWindows(rule, features))
            {
                continue;
            }

            allowed = string.Equals(rule.TryGetProperty("action", out var action) ? action.GetString() : null, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleMatchesWindows(System.Text.Json.JsonElement rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.TryGetProperty("os", out var operatingSystem) && operatingSystem.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (operatingSystem.TryGetProperty("name", out var name) &&
                !string.Equals(name.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (operatingSystem.TryGetProperty("arch", out var arch))
            {
                var currentArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X86 => "x86",
                    System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                    _ => "x86_64"
                };
                var expression = arch.GetString();
                if (!string.IsNullOrWhiteSpace(expression) && !Regex.IsMatch(currentArch, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return false;
                }
            }

            if (operatingSystem.TryGetProperty("version", out var version))
            {
                var expression = version.GetString();
                if (!string.IsNullOrWhiteSpace(expression) && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), expression, RegexOptions.CultureInvariant))
                {
                    return false;
                }
            }
        }

        if (features is not null && rule.TryGetProperty("features", out var requiredFeatures) && requiredFeatures.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var feature in requiredFeatures.EnumerateObject())
            {
                if (feature.Value.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) ||
                    !features.TryGetValue(feature.Name, out var enabled) || enabled != feature.Value.GetBoolean())
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? GetWindowsNativeClassifier(IReadOnlyDictionary<string, string>? natives)
    {
        if (natives is null || !natives.TryGetValue("windows", out var classifier))
        {
            return null;
        }

        var architecture = Environment.Is64BitOperatingSystem ? "64" : "32";
        return classifier.Replace("${arch}", architecture, StringComparison.Ordinal);
    }

    internal static string BuildMavenPath(string coordinate, string? classifier = null)
    {
        var versionAndExtension = coordinate.Split('@', 2);
        var extension = versionAndExtension.Length == 2 ? versionAndExtension[1] : "jar";
        var parts = versionAndExtension[0].Split(':');
        if (parts.Length is < 3 or > 4)
        {
            throw new InvalidDataException($"The library coordinate '{coordinate}' is invalid.");
        }

        var resolvedClassifier = classifier ?? (parts.Length == 4 ? parts[3] : null);
        var fileName = $"{parts[1]}-{parts[2]}{(string.IsNullOrWhiteSpace(resolvedClassifier) ? string.Empty : $"-{resolvedClassifier}")}.{extension}";
        return Path.Combine(parts[0].Replace('.', Path.DirectorySeparatorChar), parts[1], parts[2], fileName);
    }

    private static string BuildLegacyLibraryUrl(string? baseUrl, string relativePath)
    {
        var repository = string.IsNullOrWhiteSpace(baseUrl) ? "https://libraries.minecraft.net/" : baseUrl;
        return $"{repository.TrimEnd('/')}/{relativePath.Replace('\\', '/')}";
    }

    private static string GetSafeSegment(string value)
    {
        if (value.Length == 0 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("The official metadata contained an invalid version or asset ID.");
        }

        return value;
    }

    private static void ExtractNatives(string archivePath, string targetDirectory, IReadOnlyList<string> exclusions, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = entry.FullName.Replace('\\', '/');
            if (entryPath.Length == 0 || entryPath.EndsWith('/') ||
                entryPath.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                exclusions.Any(exclusion => entryPath.StartsWith(exclusion.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(entryPath.Replace('/', Path.DirectorySeparatorChar), targetDirectory);
            var targetPrefix = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destinationPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A native archive attempted to write outside its extraction directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private sealed class DownloadArtifactProxy(DownloadArtifact source, string url)
    {
        public DownloadArtifact ToArtifact() => new()
        {
            Id = source.Id,
            Url = url,
            Sha1 = source.Sha1,
            Size = source.Size,
            TotalSize = source.TotalSize,
            Path = source.Path
        };
    }
}
