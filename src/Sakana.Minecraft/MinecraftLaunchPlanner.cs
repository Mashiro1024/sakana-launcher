using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class MinecraftLaunchPlanner : IMinecraftLaunchPlanner
{
    private static readonly Regex PlaceholderPattern = new(@"\$\{(?<name>[^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _dataDirectory;
    private readonly MojangMetadataClient _metadataClient;
    private readonly MojangJavaRuntimeManager _javaRuntimeManager;

    public MinecraftLaunchPlanner(
        string dataDirectory,
        MojangMetadataClient metadataClient,
        MojangJavaRuntimeManager javaRuntimeManager)
    {
        _dataDirectory = dataDirectory;
        _metadataClient = metadataClient;
        _javaRuntimeManager = javaRuntimeManager;
    }

    public async Task<bool> SupportsQuickPlayAsync(string minecraftVersionId, CancellationToken cancellationToken = default)
    {
        var safeVersionId = ValidateSegment(minecraftVersionId, "Minecraft version ID");
        var (metadata, _) = await _metadataClient.GetVersionMetadataAsync(safeVersionId, cancellationToken);
        return SupportsQuickPlay(metadata.Arguments?.Game ?? default);
    }

    public async Task<MinecraftLaunchPlan> CreatePlanAsync(
        InstanceProfile profile,
        MinecraftAccountSession session,
        CancellationToken cancellationToken = default,
        MinecraftQuickPlayTarget? quickPlayTarget = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);

        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install the game files and matching Java runtime before preparing a launch.");
        }

        var safeVersionId = ValidateSegment(profile.VersionId, "Minecraft version ID");
        var (metadata, _) = await _metadataClient.GetVersionMetadataAsync(safeVersionId, cancellationToken);
        var supportsQuickPlay = SupportsQuickPlay(metadata.Arguments?.Game ?? default);
        if (quickPlayTarget is not null && !supportsQuickPlay)
        {
            throw new MinecraftQuickPlayNotSupportedException(safeVersionId);
        }

        quickPlayTarget = ValidateQuickPlayTarget(quickPlayTarget);
        MojangVersionMetadata? loaderProfile = null;
        if (profile.LoaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge)
        {
            if (string.IsNullOrWhiteSpace(profile.LoaderVersion))
            {
                throw new InvalidDataException($"The {profile.LoaderType} instance does not specify a loader version.");
            }

            var loaderProfilePath = Path.Combine(
                _dataDirectory,
                "instances",
                InstanceDirectoryName.ForProfile(profile),
                "loader",
                $"{profile.LoaderType.ToString().ToLowerInvariant()}-profile.json");
            if (!File.Exists(loaderProfilePath))
            {
                throw new InvalidOperationException($"The {profile.LoaderType} profile is missing. Repair this instance before launching.");
            }

            var loaderProfileJson = await File.ReadAllBytesAsync(loaderProfilePath, cancellationToken);
            loaderProfile = profile.LoaderType is MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge
                ? MojangMetadataClient.DeserializeForgeFamilyProfile(loaderProfileJson, profile.LoaderType, profile.LoaderVersion, safeVersionId)
                : MojangMetadataClient.DeserializeLoaderProfile(loaderProfileJson, profile.LoaderType, profile.LoaderVersion, safeVersionId);
        }
        else if (profile.LoaderType != MinecraftLoaderType.Vanilla)
        {
            throw new NotSupportedException($"The {profile.LoaderType} loader is not supported yet.");
        }

        var mainClass = loaderProfile?.MainClass ?? metadata.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidDataException($"Minecraft {profile.VersionId} does not provide a main class.");
        }

        var instanceDirectory = Path.Combine(_dataDirectory, "instances", InstanceDirectoryName.ForProfile(profile));
        var gameDirectory = Path.Combine(instanceDirectory, "game");
        await MigrateLegacyRuntimeFilesAsync(
            instanceDirectory,
            gameDirectory,
            safeVersionId,
            loaderProfile,
            profile.LoaderType,
            metadata.Libraries.Concat(loaderProfile?.Libraries ?? []).ToArray(),
            session.IsDemoUser,
            cancellationToken);
        var nativesDirectory = Path.Combine(gameDirectory, "natives");
        var assetsDirectory = Path.Combine(_dataDirectory, "shared", "assets");
        var libraryDirectory = Path.Combine(gameDirectory, "libraries");
        var legacyLibraryDirectory = Path.Combine(_dataDirectory, "shared", "libraries");
        var versionDirectory = Path.Combine(gameDirectory, "versions", safeVersionId);
        var baseClientJarPath = Path.Combine(versionDirectory, $"{safeVersionId}.jar");
        var clientJarPath = baseClientJarPath;
        if (profile.LoaderType is MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge)
        {
            var safeLoaderProfileId = ValidateSegment(loaderProfile!.Id, "loader profile ID");
            var loaderClientJarPath = Path.Combine(gameDirectory, "versions", safeLoaderProfileId, $"{safeLoaderProfileId}.jar");
            if (File.Exists(loaderClientJarPath))
            {
                clientJarPath = loaderClientJarPath;
            }
        }
        var requiredJavaMajor = metadata.JavaVersion?.MajorVersion ?? 8;
        string javaPath;
        if (string.IsNullOrWhiteSpace(profile.JavaExecutablePath))
        {
            var detectedJava = (await _javaRuntimeManager.DetectInstalledJavaAsync(cancellationToken))
                .FirstOrDefault(installation => installation.MajorVersion == requiredJavaMajor);
            javaPath = detectedJava?.ExecutablePath ?? _javaRuntimeManager.GetInstalledRuntimeExecutablePath(metadata.JavaVersion);
        }
        else
        {
            javaPath = Path.GetFullPath(profile.JavaExecutablePath);
        }

        if (!File.Exists(javaPath))
        {
            throw new FileNotFoundException(
                string.IsNullOrWhiteSpace(profile.JavaExecutablePath)
                    ? "The matching Java runtime is missing. Retry the instance installation before launching."
                    : "The custom Java executable was not found. Update the Java path in this instance's launch settings.",
                javaPath);
        }

        if (!File.Exists(clientJarPath))
        {
            throw new InvalidOperationException(profile.LoaderType is MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge
                ? $"The generated {profile.LoaderType} client file is missing. Repair this instance before launching."
                : "The Minecraft client file is missing. Retry the instance installation before launching.");
        }

        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(nativesDirectory);
        await SetFullscreenOptionAsync(gameDirectory, profile.Fullscreen, cancellationToken);

        var assetIndexName = ValidateSegment(metadata.AssetIndex?.Id ?? metadata.Assets ?? safeVersionId, "asset index ID");
        var assetIndexPath = Path.Combine(assetsDirectory, "indexes", $"{assetIndexName}.json");
        if (!File.Exists(assetIndexPath))
        {
            throw new InvalidOperationException("The Minecraft asset index is missing. Retry the instance installation before launching.");
        }

        var logConfigPath = GetLoggingConfigPath(metadata, assetsDirectory);
        var classPath = string.Join(Path.PathSeparator, BuildClassPath(metadata, loaderProfile?.Libraries, libraryDirectory, legacyLibraryDirectory, clientJarPath, session));
        var placeholders = BuildPlaceholders(
            profile,
            loaderProfile?.Id ?? profile.VersionId,
            session,
            gameDirectory,
            assetsDirectory,
            assetIndexName,
            nativesDirectory,
            libraryDirectory,
            javaPath,
            clientJarPath,
            classPath,
            logConfigPath,
            metadata.VersionType,
            profile.WindowWidth,
            profile.WindowHeight,
            quickPlayTarget,
            supportsQuickPlay ? "quickPlay/sakana-log.json" : string.Empty);

        var hasCustomResolution = profile.WindowWidth is >= 640 and <= 16384 && profile.WindowHeight is >= 480 and <= 16384;
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = session.IsDemoUser,
            ["has_custom_resolution"] = hasCustomResolution,
            ["has_quick_plays_support"] = supportsQuickPlay,
            ["is_quick_play_singleplayer"] = quickPlayTarget?.Kind == MinecraftQuickPlayKind.SingleplayerWorld,
            ["is_quick_play_multiplayer"] = quickPlayTarget?.Kind == MinecraftQuickPlayKind.MultiplayerServer,
            ["is_quick_play_realms"] = false
        };

        var jvmArguments = ReadArgumentList(metadata.Arguments?.Jvm ?? default, placeholders, features);
        if (loaderProfile is not null)
        {
            jvmArguments.AddRange(ReadArgumentList(loaderProfile.Arguments?.Jvm ?? default, placeholders, features));
        }
        if (jvmArguments.Count == 0)
        {
            jvmArguments.Add($"-Djava.library.path={nativesDirectory}");
        }

        if (!jvmArguments.Any(argument => argument is "-cp" or "-classpath" or "--class-path"))
        {
            jvmArguments.Add("-cp");
            jvmArguments.Add(classPath);
        }

        AddLoggingArgument(jvmArguments, metadata, placeholders);
        if (!profile.UseAutomaticMemory &&
            (profile.MinMemoryMb is < 256 or > 65536 || profile.MaxMemoryMb is < 256 or > 65536 || profile.MinMemoryMb > profile.MaxMemoryMb))
        {
            throw new InvalidDataException("The instance memory settings are invalid. Set a minimum and maximum between 256 MB and 65536 MB, with minimum not greater than maximum.");
        }

        var maxMemoryMb = profile.UseAutomaticMemory ? GetAutomaticMemoryLimitMb() : profile.MaxMemoryMb;
        var minMemoryMb = profile.UseAutomaticMemory ? Math.Min(512, maxMemoryMb) : profile.MinMemoryMb;
        jvmArguments.RemoveAll(argument =>
            argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(profile.CustomJavaArguments))
        {
            jvmArguments.AddRange(SplitCommandLine(profile.CustomJavaArguments, "Custom Java arguments"));
        }

        jvmArguments.RemoveAll(argument =>
            argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
        jvmArguments.Add($"-Xms{minMemoryMb}m");
        jvmArguments.Add($"-Xmx{maxMemoryMb}m");

        var gameArguments = ReadArgumentList(metadata.Arguments?.Game ?? default, placeholders, features);
        if (loaderProfile is not null)
        {
            gameArguments.AddRange(ReadArgumentList(loaderProfile.Arguments?.Game ?? default, placeholders, features));
        }
        var legacyArguments = !string.IsNullOrWhiteSpace(loaderProfile?.MinecraftArguments)
            ? loaderProfile.MinecraftArguments
            : metadata.MinecraftArguments;
        if (gameArguments.Count == 0 && !string.IsNullOrWhiteSpace(legacyArguments))
        {
            gameArguments = SplitCommandLine(legacyArguments)
                .Select(argument => ExpandPlaceholders(argument, placeholders))
                .ToList();
        }

        if (gameArguments.Count == 0)
        {
            throw new InvalidDataException($"Minecraft {profile.VersionId} does not provide game launch arguments.");
        }

        if (quickPlayTarget is not null)
        {
            var quickPlayArgument = quickPlayTarget.Kind == MinecraftQuickPlayKind.SingleplayerWorld
                ? "--quickPlaySingleplayer"
                : "--quickPlayMultiplayer";
            if (!gameArguments.Contains(quickPlayArgument, StringComparer.Ordinal))
            {
                gameArguments.Add(quickPlayArgument);
                gameArguments.Add(quickPlayTarget.Value);
            }

            Directory.CreateDirectory(Path.Combine(gameDirectory, "quickPlay"));
        }

        if (hasCustomResolution)
        {
            if (!gameArguments.Contains("--width", StringComparer.Ordinal))
            {
                gameArguments.Add("--width");
                gameArguments.Add(profile.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!gameArguments.Contains("--height", StringComparer.Ordinal))
            {
                gameArguments.Add("--height");
                gameArguments.Add(profile.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var launchArguments = new List<string>(jvmArguments.Count + gameArguments.Count + 1);
        launchArguments.AddRange(jvmArguments);
        launchArguments.Add(mainClass);
        launchArguments.AddRange(gameArguments);

        var customEnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.CustomEnvironmentVariablesJson)
            ?? new Dictionary<string, string>();
        return new MinecraftLaunchPlan(
            profile.Id,
            profile.VersionId,
            javaPath,
            gameDirectory,
            launchArguments,
            customEnvironmentVariables,
            [session.AccessToken]);
    }

    private Dictionary<string, string> BuildPlaceholders(
        InstanceProfile profile,
        string launchVersionId,
        MinecraftAccountSession session,
        string gameDirectory,
        string assetsDirectory,
        string assetIndexName,
        string nativesDirectory,
        string libraryDirectory,
        string javaPath,
        string clientJarPath,
        string classPath,
        string? logConfigPath,
        string versionType,
        int windowWidth,
        int windowHeight,
        MinecraftQuickPlayTarget? quickPlayTarget,
        string quickPlayPath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = session.PlayerName,
            ["version_name"] = launchVersionId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsDirectory,
            ["game_assets"] = assetsDirectory,
            ["assets_index_name"] = assetIndexName,
            ["auth_uuid"] = NormalizeUuid(session.PlayerUuid),
            ["auth_access_token"] = session.AccessToken,
            ["clientid"] = session.ClientId,
            ["auth_xuid"] = session.Xuid ?? string.Empty,
            ["user_type"] = session.UserType,
            ["user_properties"] = session.UserPropertiesJson,
            ["version_type"] = versionType,
            ["natives_directory"] = nativesDirectory,
            ["library_directory"] = libraryDirectory,
            ["classpath"] = classPath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = "Sakana",
            ["launcher_version"] = "0.1",
            ["client_jar"] = clientJarPath,
            ["java_executable"] = javaPath,
            ["resolution_width"] = windowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resolution_height"] = windowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["quickPlayPath"] = quickPlayPath,
            ["quickPlaySingleplayer"] = quickPlayTarget?.Kind == MinecraftQuickPlayKind.SingleplayerWorld ? quickPlayTarget.Value : string.Empty,
            ["quickPlayMultiplayer"] = quickPlayTarget?.Kind == MinecraftQuickPlayKind.MultiplayerServer ? quickPlayTarget.Value : string.Empty
        };

        if (!string.IsNullOrWhiteSpace(logConfigPath))
        {
            values["path"] = logConfigPath;
        }

        if (!string.IsNullOrWhiteSpace(session.LegacySessionId))
        {
            values["auth_session"] = session.LegacySessionId;
        }

        return values;
    }

    private static bool SupportsQuickPlay(JsonElement gameArguments) =>
        gameArguments.ValueKind == JsonValueKind.Array &&
        gameArguments.GetRawText().Contains("has_quick_plays_support", StringComparison.Ordinal);

    private static MinecraftQuickPlayTarget? ValidateQuickPlayTarget(MinecraftQuickPlayTarget? target)
    {
        if (target is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(target.Value) || target.Value.Length > 255 || target.Value.Any(char.IsControl))
        {
            throw new ArgumentException("Quick Play target is empty or invalid.", nameof(target));
        }

        if (target.Kind == MinecraftQuickPlayKind.SingleplayerWorld)
        {
            if (target.Value is "." or ".." || Path.GetFileName(target.Value) != target.Value ||
                target.Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || target.Value.Contains('/') || target.Value.Contains('\\'))
            {
                throw new ArgumentException("The selected world folder name is invalid.", nameof(target));
            }
        }
        else if (target.Kind != MinecraftQuickPlayKind.MultiplayerServer ||
                 target.Value.Any(char.IsWhiteSpace) || target.Value.Contains('/') || target.Value.Contains('\\'))
        {
            throw new ArgumentException("The selected server address is invalid.", nameof(target));
        }

        return target;
    }

    private static List<string> BuildClassPath(
        MojangVersionMetadata metadata,
        IReadOnlyList<MojangLibrary>? loaderLibraries,
        string libraryDirectory,
        string legacyLibraryDirectory,
        string clientJarPath,
        MinecraftAccountSession session)
    {
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = session.IsDemoUser,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in metadata.Libraries.Concat(loaderLibraries ?? []))
        {
            if (!MinecraftInstaller.IsAllowedOnWindows(library.Rules, features))
            {
                continue;
            }

            string? relativePath = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrWhiteSpace(relativePath) &&
                (library.Downloads?.Artifact is not null || library.Downloads is null))
            {
                relativePath = MinecraftInstaller.BuildMavenPath(library.Name);
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var path = ResolveSafePath(libraryDirectory, relativePath);
            if (!File.Exists(path))
            {
                var legacyPath = ResolveSafePath(legacyLibraryDirectory, relativePath);
                if (!File.Exists(legacyPath))
                {
                    throw new InvalidOperationException($"A required Minecraft library is missing: {Path.GetFileName(path)}. Repair this instance before launching.");
                }

                path = legacyPath;
            }

            if (seen.Add(path))
            {
                paths.Add(path);
            }
        }

        if (!File.Exists(clientJarPath))
        {
            throw new InvalidOperationException("The Minecraft client jar is missing.");
        }

        paths.Add(clientJarPath);
        return paths;
    }

    private static async Task MigrateLegacyRuntimeFilesAsync(
        string instanceDirectory,
        string gameDirectory,
        string minecraftVersionId,
        MojangVersionMetadata? loaderProfile,
        MinecraftLoaderType loaderType,
        IReadOnlyList<MojangLibrary> libraries,
        bool isDemoUser,
        CancellationToken cancellationToken)
    {
        var legacyVersionsDirectory = Path.Combine(instanceDirectory, "versions");
        var isolatedVersionsDirectory = Path.Combine(gameDirectory, "versions");
        await CopyDirectoryMissingFilesAsync(
            Path.Combine(legacyVersionsDirectory, minecraftVersionId),
            Path.Combine(isolatedVersionsDirectory, minecraftVersionId),
            cancellationToken);

        if (loaderProfile is not null && loaderType is MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge)
        {
            var loaderProfileId = ValidateSegment(loaderProfile.Id, "loader profile ID");
            await CopyDirectoryMissingFilesAsync(
                Path.Combine(legacyVersionsDirectory, loaderProfileId),
                Path.Combine(isolatedVersionsDirectory, loaderProfileId),
                cancellationToken);
        }

        var legacyNativesDirectory = Path.Combine(instanceDirectory, "natives");
        var isolatedNativesDirectory = Path.Combine(gameDirectory, "natives");
        if (Directory.Exists(legacyNativesDirectory) &&
            (!Directory.Exists(isolatedNativesDirectory) || !Directory.EnumerateFileSystemEntries(isolatedNativesDirectory).Any()))
        {
            await CopyDirectoryMissingFilesAsync(legacyNativesDirectory, isolatedNativesDirectory, cancellationToken);
        }

        var sharedLibrariesDirectory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(instanceDirectory)!)!, "shared", "libraries");
        var isolatedLibrariesDirectory = Path.Combine(gameDirectory, "libraries");
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = isDemoUser,
            ["has_custom_resolution"] = false,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };
        var seenLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MinecraftInstaller.IsAllowedOnWindows(library.Rules, features))
            {
                continue;
            }

            var relativePath = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrWhiteSpace(relativePath) &&
                (library.Downloads?.Artifact is not null || library.Downloads is null))
            {
                relativePath = MinecraftInstaller.BuildMavenPath(library.Name);
            }

            if (string.IsNullOrWhiteSpace(relativePath) || !seenLibraries.Add(relativePath))
            {
                continue;
            }

            var sourceFile = Path.GetFullPath(relativePath, sharedLibrariesDirectory);
            var targetFile = Path.GetFullPath(relativePath, isolatedLibrariesDirectory);
            var sourceRoot = Path.GetFullPath(sharedLibrariesDirectory) + Path.DirectorySeparatorChar;
            var targetRoot = Path.GetFullPath(isolatedLibrariesDirectory) + Path.DirectorySeparatorChar;
            if (!sourceFile.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) ||
                !targetFile.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A legacy Minecraft library resolved outside its expected directory.");
            }

            if (!File.Exists(sourceFile) || File.Exists(targetFile))
            {
                continue;
            }

            if (library.Downloads?.Artifact?.Size is long expectedSize && new FileInfo(sourceFile).Length != expectedSize)
            {
                continue;
            }

            await CopyFileMissingAsync(sourceFile, targetFile, cancellationToken);
        }
    }

    private static async Task CopyDirectoryMissingFilesAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.GetFullPath(relativePath, targetDirectory);
            var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
            if (!targetFile.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A legacy Minecraft file resolved outside the instance directory.");
            }

            if (File.Exists(targetFile))
            {
                continue;
            }

            await CopyFileMissingAsync(sourceFile, targetFile, cancellationToken);
        }
    }

    private static async Task CopyFileMissingAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
    {
        if (File.Exists(targetFile))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var partialPath = targetFile + $".{Guid.NewGuid():N}.part";
        try
        {
            await using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            await using var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            File.Move(partialPath, targetFile);
        }
        catch (IOException) when (File.Exists(targetFile))
        {
            // Another in-flight repair created the same immutable file.
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static string? GetLoggingConfigPath(MojangVersionMetadata metadata, string assetsDirectory)
    {
        var file = metadata.Logging?.Client?.File;
        if (file is null)
        {
            return null;
        }

        var id = file.Id;
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("The Minecraft logging metadata contains an invalid file ID.");
        }

        var path = Path.Combine(assetsDirectory, "log_configs", id);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The Minecraft logging configuration is missing. Retry the instance installation.");
        }

        return path;
    }

    private static List<string> ReadArgumentList(
        JsonElement source,
        IReadOnlyDictionary<string, string> placeholders,
        IReadOnlyDictionary<string, bool> features)
    {
        var result = new List<string>();
        if (source.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return result;
        }

        if (source.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Minecraft launch arguments were not an array.");
        }

        foreach (var argument in source.EnumerateArray())
        {
            if (argument.ValueKind == JsonValueKind.String)
            {
                result.Add(ExpandPlaceholders(argument.GetString()!, placeholders));
                continue;
            }

            if (argument.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Minecraft launch metadata contains an unsupported argument entry.");
            }

            if (argument.TryGetProperty("rules", out var rules) && !MinecraftInstaller.IsAllowedOnWindows(rules, features))
            {
                continue;
            }

            if (!argument.TryGetProperty("value", out var value))
            {
                throw new InvalidDataException("Minecraft launch metadata contains an argument entry without a value.");
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(ExpandPlaceholders(value.GetString()!, placeholders));
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("Minecraft launch metadata contains a non-string argument value.");
                    }

                    result.Add(ExpandPlaceholders(item.GetString()!, placeholders));
                }
            }
            else
            {
                throw new InvalidDataException("Minecraft launch metadata contains an unsupported argument value.");
            }
        }

        return result;
    }

    private static void AddLoggingArgument(
        ICollection<string> jvmArguments,
        MojangVersionMetadata metadata,
        IReadOnlyDictionary<string, string> placeholders)
    {
        var argument = metadata.Logging?.Client?.Argument;
        if (string.IsNullOrWhiteSpace(argument))
        {
            return;
        }

        foreach (var loggingArgument in SplitCommandLine(argument))
        {
            var expanded = ExpandPlaceholders(loggingArgument, placeholders);
            if (!jvmArguments.Contains(expanded, StringComparer.Ordinal))
            {
                jvmArguments.Add(expanded);
            }
        }
    }

    private static string ExpandPlaceholders(string input, IReadOnlyDictionary<string, string> placeholders) =>
        PlaceholderPattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;
            return placeholders.TryGetValue(name, out var value)
                ? value
                : throw new InvalidDataException($"Sakana does not support the Minecraft launch placeholder '{name}'.");
        });

    private static List<string> SplitCommandLine(string commandLine, string source = "Minecraft legacy launch arguments")
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var escaping = false;

        foreach (var character in commandLine)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
            }
            else if (character == '\\')
            {
                escaping = true;
            }
            else if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (inQuotes)
        {
            throw new InvalidDataException($"{source} contain an unmatched quote.");
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static int GetAutomaticMemoryLimitMb()
    {
        var availableBytes = TryGetWindowsAvailablePhysicalMemoryBytes(out var windowsAvailableBytes)
            ? checked((long)windowsAvailableBytes)
            : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableMb = availableBytes / (1024L * 1024L);
        var halfAvailable = availableMb / 2;
        return (int)Math.Clamp(halfAvailable, 1024, 16384);
    }

    private static bool TryGetWindowsAvailablePhysicalMemoryBytes(out ulong availableBytes)
    {
        availableBytes = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref status) || status.AvailablePhysicalBytes == 0)
        {
            return false;
        }

        availableBytes = status.AvailablePhysicalBytes;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static async Task SetFullscreenOptionAsync(string gameDirectory, bool fullscreen, CancellationToken cancellationToken)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        var lines = File.Exists(optionsPath)
            ? (await File.ReadAllLinesAsync(optionsPath, cancellationToken)).ToList()
            : [];
        var lineIndex = lines.FindIndex(line => line.StartsWith("fullscreen:", StringComparison.OrdinalIgnoreCase));
        var value = $"fullscreen:{fullscreen.ToString().ToLowerInvariant()}";
        if (lineIndex < 0)
        {
            lines.Add(value);
        }
        else
        {
            lines[lineIndex] = value;
        }

        await File.WriteAllLinesAsync(optionsPath, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static string NormalizeUuid(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 32 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The authenticated Minecraft profile UUID is invalid.", nameof(value));
        }

        return normalized.ToLowerInvariant();
    }

    private static void ValidateSession(MinecraftAccountSession session)
    {
        if (string.IsNullOrWhiteSpace(session.PlayerName) ||
            string.IsNullOrWhiteSpace(session.AccessToken) ||
            string.IsNullOrWhiteSpace(session.ClientId) ||
            string.IsNullOrWhiteSpace(session.UserType))
        {
            throw new ArgumentException("The authenticated Minecraft session is incomplete.", nameof(session));
        }

        if (string.IsNullOrWhiteSpace(session.UserPropertiesJson))
        {
            throw new ArgumentException("The Minecraft user properties value is required.", nameof(session));
        }

        _ = NormalizeUuid(session.PlayerUuid);
    }

    private static string ValidateSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException($"The Mojang metadata contains an invalid {label}.");
        }

        return value;
    }

    private static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The Minecraft version metadata contains an invalid library path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new InvalidDataException("The Minecraft version metadata contains a path traversal library entry.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), fullRoot);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Minecraft library path points outside Sakana's shared library folder.");
        }

        return fullPath;
    }
}
