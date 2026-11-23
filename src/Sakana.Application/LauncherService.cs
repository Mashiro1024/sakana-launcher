using Sakana.Domain;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Sakana.Application;

public sealed class LauncherService(
    ILauncherStore store,
    IMinecraftVersionCatalog versionCatalog,
    IFabricLoaderCatalog fabricLoaderCatalog,
    IQuiltLoaderCatalog quiltLoaderCatalog,
    IForgeNeoForgeLoaderCatalog forgeNeoForgeLoaderCatalog,
    IModrinthModpackService modrinthModpackService,
    IModrinthModCatalog modrinthModCatalog,
    ILocalModService localModService,
    ILocalPackService localPackService,
    ILocalMinecraftWorldService localMinecraftWorldService,
    ILocalMinecraftServerService localMinecraftServerService,
    IJavaRuntimeCatalog javaRuntimeCatalog,
    IMinecraftInstaller installer,
    IMinecraftLaunchPlanner launchPlanner,
    IMinecraftProcessRunner processRunner,
    ISakanaLogger logger) : ILauncherService
{
    private const int MaxRecentGameOutputLines = 500;
    private const int MinimumMemoryLimitMb = 256;
    private const int MaximumMemoryLimitMb = 65536;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeInstalls = new();
    private readonly ConcurrentDictionary<Guid, string> _instanceDirectoryNames = new();
    private readonly ConcurrentDictionary<Guid, byte> _launchReservations = new();
    private readonly ConcurrentDictionary<Guid, byte> _contentReservations = new();
    private readonly ConcurrentDictionary<Guid, byte> _deletingProfiles = new();
    private readonly ConcurrentDictionary<Guid, IMinecraftGameProcess> _activeLaunches = new();
    private readonly ConcurrentQueue<MinecraftGameProcessOutput> _recentGameOutput = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gameOutputLogLocks = new();
    private int _recentGameOutputCount;

    public string DataDirectory => store.DataDirectory;

    public event Action<InstallationProgress>? InstallationProgressChanged;

    public event Action<MinecraftGameProcessStatus>? GameProcessStatusChanged;

    public event Action<MinecraftGameProcessOutput>? GameOutputReceived;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken);
        await store.RecoverInterruptedInstallationsAsync(cancellationToken);
        var profiles = await store.ListProfilesAsync(cancellationToken);
        var duplicateDirectoryNames = profiles
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDirectoryNames is not null)
        {
            throw new InvalidOperationException($"现有实例名称在 Windows 中会指向同一文件夹：{string.Join("、", duplicateDirectoryNames.Select(profile => profile.Name))}。请先处理重名实例。");
        }

        foreach (var profile in profiles)
        {
            var directoryName = InstanceDirectoryName.ForProfile(profile);
            try
            {
                ReconcileInstanceDirectory(profile, directoryName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await TryLogErrorAsync("profile.directory_migration_failed", $"Could not rename the instance folder for {profile.Name}.", exception);
                throw new IOException($"无法将实例“{profile.Name}”的文件夹迁移到按名称命名的目录。请检查磁盘权限或占用后重新启动 Sakana。", exception);
            }

            _instanceDirectoryNames[profile.Id] = directoryName;
        }

        SchedulePendingInstanceCleanup();
    }

    public Task<LauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        store.GetSettingsAsync(cancellationToken);

    public async Task SaveThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default)
    {
        var settings = await store.GetSettingsAsync(cancellationToken);
        await store.SaveSettingsAsync(settings with { Theme = theme }, cancellationToken);
    }

    public async Task SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var environmentVariables = ReadEnvironmentVariables(settings.CustomEnvironmentVariablesJson);
        var validationSettings = new InstanceLaunchSettings(
            settings.UseAutomaticMemory,
            settings.MinMemoryMb,
            settings.MaxMemoryMb,
            settings.JavaExecutablePath,
            settings.CustomJavaArguments,
            environmentVariables,
            settings.Fullscreen,
            settings.WindowWidth,
            settings.WindowHeight);
        ValidateLaunchSettings(validationSettings);
        await store.SaveGlobalLaunchSettingsAsync(settings with { JavaExecutablePath = NormalizeJavaPath(settings.JavaExecutablePath) }, cancellationToken);
    }

    public Task<IReadOnlyList<JavaInstallation>> DetectJavaInstallationsAsync(CancellationToken cancellationToken = default) =>
        javaRuntimeCatalog.DetectInstalledJavaAsync(cancellationToken);

    public Task<IReadOnlyList<InstanceProfile>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        store.ListProfilesAsync(cancellationToken);

    public async Task<InstanceProfile> DuplicateProfileAsync(
        Guid profileId,
        string name,
        IProgress<InstanceCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalizedName = InstanceDirectoryName.Validate(NormalizeRequired(name, "Profile name", 64));
        var sourceProfile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (sourceProfile.State == InstanceProfileState.Installing)
        {
            throw new InvalidOperationException("请等待实例安装完成后再复制。");
        }

        await EnsureUniqueProfileNameAsync(normalizedName, exceptProfileId: null, cancellationToken: cancellationToken);
        EnsureInstanceDirectoryAvailable(normalizedName);

        var instanceRoot = Path.GetFullPath(Path.Combine(DataDirectory, "instances"));
        EnsureNotReparsePoint(instanceRoot);
        var sourceDirectoryName = _instanceDirectoryNames.GetValueOrDefault(profileId, InstanceDirectoryName.ForProfile(sourceProfile));
        var sourceDirectory = Path.GetFullPath(Path.Combine(instanceRoot, sourceDirectoryName));
        var destinationDirectory = Path.GetFullPath(Path.Combine(instanceRoot, normalizedName));
        EnsureSafeInstanceDirectory(instanceRoot, sourceDirectory);
        EnsureSafeInstanceDirectory(instanceRoot, destinationDirectory);
        ReserveContentChanges(profileId);

        string? copiedPackPath = null;
        var importedPackRelativePath = sourceProfile.ModrinthPackPath;
        var profileSaved = false;
        try
        {
            var sourceDirectoryExists = Directory.Exists(sourceDirectory);
            IReadOnlyList<InstanceFileCopy> files = [];
            IReadOnlyList<string> directories = [];
            if (sourceDirectoryExists)
            {
                progress?.Report(new InstanceCopyProgress("正在扫描实例文件…", 0, 0, 0, 0));
                (directories, files) = await Task.Run(
                    () => CollectInstanceFilesForCopy(sourceDirectory, destinationDirectory, cancellationToken),
                    cancellationToken);
            }

            var importedPackSourcePath = string.IsNullOrWhiteSpace(sourceProfile.ModrinthPackPath)
                ? null
                : ResolveImportedPackPath(sourceProfile.ModrinthPackPath)
                    ?? throw new InvalidDataException("此实例引用的 Modrinth 整合包文件无法复制，请重新导入整合包后再复制实例。");
            long packSize = 0;
            if (importedPackSourcePath is not null)
            {
                if (!File.Exists(importedPackSourcePath))
                {
                    throw new FileNotFoundException("此实例引用的 Modrinth 整合包文件不存在。", importedPackSourcePath);
                }

                if ((File.GetAttributes(importedPackSourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Sakana refused to copy a redirected Modrinth pack file.");
                }

                packSize = new FileInfo(importedPackSourcePath).Length;
            }

            var instanceTotalBytes = files.Sum(file => file.Length);
            var totalBytes = instanceTotalBytes + packSize;
            var completedBytes = 0L;
            var completedFiles = 0;
            var lastProgressAt = Environment.TickCount64;
            void ReportCopyProgress(string status, string? currentFile = null, bool force = false)
            {
                var now = Environment.TickCount64;
                if (force || now - lastProgressAt >= 120)
                {
                    lastProgressAt = now;
                    progress?.Report(new InstanceCopyProgress(status, completedBytes, totalBytes, completedFiles, files.Count + (importedPackSourcePath is null ? 0 : 1), currentFile));
                }
            }

            if (sourceDirectoryExists)
            {
                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(directory);
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesBeforeFile = completedBytes;
                    ReportCopyProgress("正在复制实例文件…", Path.GetFileName(file.SourcePath), force: true);
                    await CopyFileAtomicallyAsync(
                        file.SourcePath,
                        file.DestinationPath,
                        cancellationToken,
                        copiedBytes =>
                        {
                            completedBytes = bytesBeforeFile + copiedBytes;
                            ReportCopyProgress("正在复制实例文件…", Path.GetFileName(file.SourcePath));
                        });
                    completedBytes = bytesBeforeFile + file.Length;
                    completedFiles++;
                    ReportCopyProgress("正在复制实例文件…", Path.GetFileName(file.SourcePath), force: true);
                }
            }
            else
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (importedPackSourcePath is not null)
            {
                var importsDirectory = Path.Combine(DataDirectory, "imports");
                EnsureNotReparsePoint(importsDirectory);
                Directory.CreateDirectory(importsDirectory);
                var storedFileName = $"{Guid.NewGuid():N}.mrpack";
                copiedPackPath = Path.Combine(importsDirectory, storedFileName);
                ReportCopyProgress("正在复制整合包清单…", Path.GetFileName(importedPackSourcePath), force: true);
                await CopyFileAtomicallyAsync(importedPackSourcePath, copiedPackPath, cancellationToken, copiedBytes =>
                {
                    completedBytes = instanceTotalBytes + copiedBytes;
                    ReportCopyProgress("正在复制整合包清单…", Path.GetFileName(importedPackSourcePath));
                });
                completedBytes = totalBytes;
                completedFiles++;
                ReportCopyProgress("正在复制整合包清单…", Path.GetFileName(importedPackSourcePath), force: true);
                importedPackRelativePath = Path.Combine("imports", storedFileName).Replace(Path.DirectorySeparatorChar, '/');
            }

            var duplicateProfile = sourceProfile with
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                State = sourceProfile.State == InstanceProfileState.Installed && sourceDirectoryExists
                    ? InstanceProfileState.Installed
                    : InstanceProfileState.ProfileOnly,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastError = null,
                ModrinthPackPath = importedPackRelativePath
            };
            if (!await store.TryCreateProfileAsync(duplicateProfile, cancellationToken))
            {
                throw new ProfileNameInUseException(normalizedName);
            }

            profileSaved = true;
            _instanceDirectoryNames[duplicateProfile.Id] = normalizedName;
            progress?.Report(new InstanceCopyProgress("副本已创建", totalBytes, totalBytes, completedFiles, files.Count + (importedPackSourcePath is null ? 0 : 1)));
            return duplicateProfile;
        }
        catch
        {
            if (!profileSaved && Directory.Exists(destinationDirectory))
            {
                await Task.Run(() => DeleteDirectoryTreeWithoutFollowingLinks(destinationDirectory), CancellationToken.None);
            }

            if (!profileSaved && copiedPackPath is not null && File.Exists(copiedPackPath))
            {
                File.Delete(copiedPackPath);
            }

            throw;
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<InstanceProfile> RenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalizedName = InstanceDirectoryName.Validate(name.Trim());
        if (normalizedName.Length is < 1 or > 64)
        {
            throw new ArgumentException("实例名称必须为 1–64 个字符，且不能包含控制字符。", nameof(name));
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        await EnsureUniqueProfileNameAsync(normalizedName, profileId, cancellationToken);
        if (profile.State == InstanceProfileState.Installing || _activeInstalls.ContainsKey(profileId) ||
            _contentReservations.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
            _deletingProfiles.ContainsKey(profileId) ||
            (_activeLaunches.TryGetValue(profileId, out var activeProcess) && !activeProcess.HasExited))
        {
            throw new InvalidOperationException("请等待实例安装或游戏进程结束后再重命名。");
        }

        if (string.Equals(profile.Name, normalizedName, StringComparison.Ordinal))
        {
            return profile;
        }

        var currentDirectoryName = _instanceDirectoryNames.GetValueOrDefault(profileId, InstanceDirectoryName.ForProfile(profile));
        var movedDirectory = MoveInstanceDirectoryForRename(profileId, currentDirectoryName, normalizedName);
        bool renamed;
        try
        {
            renamed = await store.TryRenameProfileAsync(profileId, normalizedName, cancellationToken);
        }
        catch
        {
            RollBackInstanceDirectoryRename(movedDirectory);
            throw;
        }

        if (!renamed)
        {
            RollBackInstanceDirectoryRename(movedDirectory);
            throw new InvalidOperationException($"已经有一个名为“{normalizedName}”的实例。");
        }

        _instanceDirectoryNames[profileId] = normalizedName;
        return await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
    }

    public async Task<InstanceProfile> SetInstanceOrganizationAsync(
        Guid profileId,
        string? groupName,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");

        var normalizedGroup = string.IsNullOrWhiteSpace(groupName)
            ? null
            : NormalizeRequired(groupName, "Group name", 40);
        if (normalizedGroup?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("分组名称不能包含控制字符。", nameof(groupName));
        }

        if (tags.Count > 20)
        {
            throw new ArgumentException("每个实例最多添加 20 个标签。", nameof(tags));
        }

        var normalizedTags = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var normalizedTag = tag.Trim();
            if (normalizedTag.Length > 32 || normalizedTag.Any(char.IsControl))
            {
                throw new ArgumentException("标签最多 32 个字符，且不能包含控制字符。", nameof(tags));
            }

            if (!normalizedTags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
            {
                normalizedTags.Add(normalizedTag);
            }
        }

        await store.SetInstanceOrganizationAsync(
            profileId,
            normalizedGroup,
            JsonSerializer.Serialize(normalizedTags),
            cancellationToken);
        return await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
    }

    public async Task<InstanceProfile> SetProfileIconAsync(
        Guid profileId,
        string? iconSourcePath,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        var instanceRoot = Path.GetFullPath(Path.Combine(DataDirectory, "instances"));
        EnsureNotReparsePoint(instanceRoot);
        Directory.CreateDirectory(instanceRoot);
        var directoryName = _instanceDirectoryNames.GetValueOrDefault(profileId, InstanceDirectoryName.ForProfile(profile));
        var instanceDirectory = Path.GetFullPath(Path.Combine(instanceRoot, directoryName));
        EnsureSafeInstanceDirectory(instanceRoot, instanceDirectory);
        EnsureNotReparsePoint(instanceDirectory);
        Directory.CreateDirectory(instanceDirectory);

        if (string.IsNullOrWhiteSpace(iconSourcePath))
        {
            await store.SetProfileIconAsync(profileId, null, cancellationToken);
            DeleteStoredProfileIcon(instanceDirectory, profile.IconFileName);
            return await store.GetProfileAsync(profileId, cancellationToken)
                ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        }

        var sourcePath = Path.GetFullPath(iconSourcePath.Trim());
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到所选图片。", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
        {
            throw new ArgumentException("实例图标仅支持 PNG、JPG 或 BMP 图片。", nameof(iconSourcePath));
        }

        var sourceAttributes = File.GetAttributes(sourcePath);
        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Sakana refused to import a redirected image file.");
        }

        if (new FileInfo(sourcePath).Length is 0 or > 5_242_880)
        {
            throw new ArgumentException("实例图标文件必须小于或等于 5 MB。", nameof(iconSourcePath));
        }

        var newIconFileName = $"Sakana.Icon-{Guid.NewGuid():N}{extension}";
        var destinationPath = Path.Combine(instanceDirectory, newIconFileName);
        await CopyFileAtomicallyAsync(sourcePath, destinationPath, cancellationToken);
        try
        {
            await store.SetProfileIconAsync(profileId, newIconFileName, cancellationToken);
        }
        catch
        {
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            throw;
        }

        DeleteStoredProfileIcon(instanceDirectory, profile.IconFileName);
        return await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
    }

    private static void DeleteStoredProfileIcon(string instanceDirectory, string? iconFileName)
    {
        if (string.IsNullOrWhiteSpace(iconFileName) || !string.Equals(Path.GetFileName(iconFileName), iconFileName, StringComparison.Ordinal))
        {
            return;
        }

        var iconPath = Path.Combine(instanceDirectory, iconFileName);
        if (File.Exists(iconPath) && (File.GetAttributes(iconPath) & FileAttributes.ReparsePoint) == 0)
        {
            File.Delete(iconPath);
        }
    }

    public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!_deletingProfiles.TryAdd(profileId, 0))
        {
            throw new InvalidOperationException("This instance is already being deleted.");
        }

        string? stagedDirectory = null;
        var instanceRoot = Path.GetFullPath(Path.Combine(store.DataDirectory, "instances"));
        var instanceDirectory = string.Empty;
        try
        {
            var profile = await store.GetProfileAsync(profileId, cancellationToken)
                ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
            instanceDirectory = Path.GetFullPath(Path.Combine(
                instanceRoot,
                _instanceDirectoryNames.GetValueOrDefault(profileId, InstanceDirectoryName.ForProfile(profile))));

            if (profile.State == InstanceProfileState.Installing || _activeInstalls.ContainsKey(profileId) ||
                _launchReservations.ContainsKey(profileId) || _contentReservations.ContainsKey(profileId) ||
                (_activeLaunches.TryGetValue(profileId, out var activeProcess) && !activeProcess.HasExited))
            {
                throw new InvalidOperationException("Cancel the installation or stop Minecraft, then wait for the instance operation to finish before deleting it.");
            }

            EnsureSafeInstanceDirectory(instanceRoot, instanceDirectory);
            var importedPackPath = ResolveImportedPackPath(profile.ModrinthPackPath);
            var packFileExists = importedPackPath is not null && File.Exists(importedPackPath);
            var instanceDirectoryExists = Directory.Exists(instanceDirectory);
            if (instanceDirectoryExists && (File.GetAttributes(instanceDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Sakana refused to delete an instance folder redirected outside its data directory.");
            }

            if (instanceDirectoryExists || packFileExists)
            {
                var quarantineRoot = Path.Combine(instanceRoot, ".deleting");
                EnsureNotReparsePoint(instanceRoot);
                Directory.CreateDirectory(quarantineRoot);
                EnsureNotReparsePoint(quarantineRoot);
                stagedDirectory = Path.Combine(quarantineRoot, $"{profileId:N}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagedDirectory);

                try
                {
                    if (instanceDirectoryExists)
                    {
                        Directory.Move(instanceDirectory, Path.Combine(stagedDirectory, "instance"));
                    }

                    if (packFileExists && importedPackPath is not null)
                    {
                        File.Move(importedPackPath, Path.Combine(stagedDirectory, "imported-pack.mrpack"));
                    }
                }
                catch
                {
                    RestoreStagedInstanceData(stagedDirectory, instanceDirectory, importedPackPath);
                    throw;
                }
            }

            try
            {
                await store.DeleteProfileAsync(profileId, cancellationToken);
            }
            catch
            {
                if (stagedDirectory is not null)
                {
                    RestoreStagedInstanceData(stagedDirectory, instanceDirectory, importedPackPath);
                }

                throw;
            }

            _instanceDirectoryNames.TryRemove(profileId, out _);

            if (stagedDirectory is null)
            {
                return true;
            }

            try
            {
                await Task.Run(() => DeleteDirectoryTreeWithoutFollowingLinks(stagedDirectory), CancellationToken.None);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await TryLogErrorAsync("profile.delete_cleanup_deferred", "The instance was removed, but some files are locked and cleanup has been deferred.", exception);
                return false;
            }
        }
        finally
        {
            _deletingProfiles.TryRemove(profileId, out _);
        }
    }

    private void SchedulePendingInstanceCleanup()
    {
        _ = Task.Run(async () =>
        {
            var instanceRoot = Path.GetFullPath(Path.Combine(store.DataDirectory, "instances"));
            var quarantineRoot = Path.Combine(instanceRoot, ".deleting");
            try
            {
                if (!Directory.Exists(quarantineRoot))
                {
                    return;
                }

                EnsureNotReparsePoint(instanceRoot);
                EnsureNotReparsePoint(quarantineRoot);
                foreach (var path in Directory.EnumerateDirectories(quarantineRoot))
                {
                    var name = Path.GetFileName(path);
                    var parts = name.Split('-', 2);
                    if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || !Guid.TryParseExact(parts[1], "N", out _))
                    {
                        continue;
                    }

                    try
                    {
                        await Task.Run(() => DeleteDirectoryTreeWithoutFollowingLinks(path));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        await TryLogErrorAsync("profile.delete_cleanup_retry_failed", "Sakana could not finish deleting a previously removed instance directory.", exception);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await TryLogErrorAsync("profile.delete_cleanup_scan_failed", "Sakana could not scan for deferred instance deletion cleanup.", exception);
            }
        });
    }

    private string? ResolveImportedPackPath(string? relativePackPath)
    {
        if (string.IsNullOrWhiteSpace(relativePackPath) || Path.IsPathRooted(relativePackPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(relativePackPath.Replace('/', Path.DirectorySeparatorChar), store.DataDirectory);
            var importsRoot = Path.GetFullPath(Path.Combine(store.DataDirectory, "imports"));
            if (Directory.Exists(importsRoot) && (File.GetAttributes(importsRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var importsPrefix = importsRoot.EndsWith(Path.DirectorySeparatorChar) ? importsRoot : importsRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(importsPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task EnsureUniqueProfileNameAsync(string name, Guid? exceptProfileId, CancellationToken cancellationToken)
    {
        var profiles = await store.ListProfilesAsync(cancellationToken);
        if (profiles.Any(profile => profile.Id != exceptProfileId &&
                profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProfileNameInUseException(name);
        }
    }

    private void EnsureInstanceDirectoryAvailable(string directoryName)
    {
        var instanceRoot = Path.GetFullPath(Path.Combine(DataDirectory, "instances"));
        if (Directory.Exists(instanceRoot))
        {
            EnsureNotReparsePoint(instanceRoot);
        }

        var target = Path.GetFullPath(Path.Combine(instanceRoot, InstanceDirectoryName.Validate(directoryName)));
        EnsureSafeInstanceDirectory(instanceRoot, target);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"磁盘上已经存在名为“{directoryName}”的文件夹或文件，请先移走它，或为实例选择其他名称。");
        }
    }

    private void ReconcileInstanceDirectory(InstanceProfile profile, string directoryName)
    {
        var instanceRoot = Path.GetFullPath(Path.Combine(DataDirectory, "instances"));
        Directory.CreateDirectory(instanceRoot);
        EnsureNotReparsePoint(instanceRoot);
        var namedDirectory = Path.GetFullPath(Path.Combine(instanceRoot, directoryName));
        var legacyDirectory = Path.GetFullPath(Path.Combine(instanceRoot, profile.Id.ToString("N")));
        EnsureSafeInstanceDirectory(instanceRoot, namedDirectory);
        EnsureSafeInstanceDirectory(instanceRoot, legacyDirectory);
        if (string.Equals(namedDirectory, legacyDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureNotReparsePoint(namedDirectory);
        EnsureNotReparsePoint(legacyDirectory);
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        if (!Directory.Exists(namedDirectory))
        {
            Directory.Move(legacyDirectory, namedDirectory);
            return;
        }

        MergeLegacyInstanceDirectory(legacyDirectory, namedDirectory, profile.Id);
    }

    private static void MergeLegacyInstanceDirectory(string legacyDirectory, string namedDirectory, Guid profileId)
    {
        var recoveryRoot = Path.Combine(namedDirectory, $".migration-recovery-{profileId:N}");
        MergeDirectoryContents(legacyDirectory, namedDirectory, recoveryRoot, string.Empty, profileId);
        if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
        {
            Directory.Delete(legacyDirectory);
        }
    }

    private static void MergeDirectoryContents(
        string sourceDirectory,
        string destinationDirectory,
        string recoveryRoot,
        string relativeDirectory,
        Guid profileId)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory).ToArray())
        {
            var sourceName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, sourceName);
            var attributes = File.GetAttributes(sourcePath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
            {
                if (isDirectory)
                {
                    Directory.Move(sourcePath, destinationPath);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }

                continue;
            }

            if (isDirectory && !isReparsePoint && Directory.Exists(destinationPath) &&
                (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) == 0)
            {
                MergeDirectoryContents(
                    sourcePath,
                    destinationPath,
                    recoveryRoot,
                    Path.Combine(relativeDirectory, sourceName),
                    profileId);
                if (!Directory.EnumerateFileSystemEntries(sourcePath).Any())
                {
                    Directory.Delete(sourcePath);
                }

                continue;
            }

            var recoveryDirectory = Path.Combine(recoveryRoot, relativeDirectory);
            Directory.CreateDirectory(recoveryDirectory);
            var recoveryPath = MakeUniqueRecoveryPath(recoveryDirectory, sourceName, isDirectory, profileId);
            if (isDirectory)
            {
                Directory.Move(sourcePath, recoveryPath);
            }
            else
            {
                File.Move(sourcePath, recoveryPath);
            }
        }
    }

    private static string MakeUniqueRecoveryPath(string directory, string sourceName, bool isDirectory, Guid profileId)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(sourceName);
        var name = isDirectory ? sourceName : Path.GetFileNameWithoutExtension(sourceName);
        var suffix = $" (legacy {profileId.ToString("N")[..8]})";
        var candidate = Path.Combine(directory, $"{name}{suffix}{extension}");
        var index = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name}{suffix} {index++}{extension}");
        }

        return candidate;
    }

    private sealed record InstanceDirectoryMove(string OriginalPath, string CurrentPath);

    private InstanceDirectoryMove? MoveInstanceDirectoryForRename(Guid profileId, string oldName, string newName)
    {
        var instanceRoot = Path.GetFullPath(Path.Combine(DataDirectory, "instances"));
        Directory.CreateDirectory(instanceRoot);
        EnsureNotReparsePoint(instanceRoot);
        var oldPath = Path.GetFullPath(Path.Combine(instanceRoot, oldName));
        var newPath = Path.GetFullPath(Path.Combine(instanceRoot, newName));
        EnsureSafeInstanceDirectory(instanceRoot, oldPath);
        EnsureSafeInstanceDirectory(instanceRoot, newPath);
        EnsureNotReparsePoint(oldPath);
        EnsureNotReparsePoint(newPath);
        if (!Directory.Exists(oldPath))
        {
            if (Directory.Exists(newPath))
            {
                throw new IOException($"实例目标文件夹已存在：{newName}");
            }

            return null;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            var temporaryPath = Path.Combine(instanceRoot, $".rename-{profileId:N}-{Guid.NewGuid():N}");
            Directory.Move(oldPath, temporaryPath);
            try
            {
                Directory.Move(temporaryPath, newPath);
                return new InstanceDirectoryMove(oldPath, newPath);
            }
            catch
            {
                if (Directory.Exists(temporaryPath) && !Directory.Exists(oldPath))
                {
                    Directory.Move(temporaryPath, oldPath);
                }

                throw;
            }
        }

        if (Directory.Exists(newPath))
        {
            throw new IOException($"实例目标文件夹已存在：{newName}");
        }

        Directory.Move(oldPath, newPath);
        return new InstanceDirectoryMove(oldPath, newPath);
    }

    private static void RollBackInstanceDirectoryRename(InstanceDirectoryMove? move)
    {
        if (move is null || !Directory.Exists(move.CurrentPath))
        {
            return;
        }

        var caseOnlyRename = string.Equals(move.CurrentPath, move.OriginalPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnlyRename && Directory.Exists(move.OriginalPath))
        {
            return;
        }

        if (caseOnlyRename)
        {
            var parent = Path.GetDirectoryName(move.OriginalPath)!;
            var temporaryPath = Path.Combine(parent, $".rename-rollback-{Guid.NewGuid():N}");
            Directory.Move(move.CurrentPath, temporaryPath);
            Directory.Move(temporaryPath, move.OriginalPath);
            return;
        }

        Directory.Move(move.CurrentPath, move.OriginalPath);
    }

    private static void RestoreStagedInstanceData(string stagedDirectory, string instanceDirectory, string? importedPackPath)
    {
        var stagedInstance = Path.Combine(stagedDirectory, "instance");
        if (Directory.Exists(stagedInstance) && !Directory.Exists(instanceDirectory))
        {
            Directory.Move(stagedInstance, instanceDirectory);
        }

        var stagedPack = Path.Combine(stagedDirectory, "imported-pack.mrpack");
        if (importedPackPath is not null && File.Exists(stagedPack) && !File.Exists(importedPackPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(importedPackPath)!);
            File.Move(stagedPack, importedPackPath);
        }

        if (Directory.Exists(stagedDirectory) && !Directory.EnumerateFileSystemEntries(stagedDirectory).Any())
        {
            Directory.Delete(stagedDirectory);
        }
    }

    private static void EnsureSafeInstanceDirectory(string instanceRoot, string instanceDirectory)
    {
        if (string.Equals(instanceDirectory, instanceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(instanceDirectory), instanceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sakana refused to delete a path outside this instance's data directory.");
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Sakana refused to traverse a redirected instance data directory.");
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingLinks(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            var childAttributes = File.GetAttributes(child);
            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(child);
                }
                else
                {
                    DeleteDirectoryTreeWithoutFollowingLinks(child);
                }
            }
            else
            {
                if ((childAttributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(child, childAttributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(child);
            }
        }

        Directory.Delete(path);
    }

    public Task<IReadOnlyList<MinecraftLaunchHistoryEntry>> GetLaunchHistoryAsync(
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        store.ListLaunchHistoryAsync(limit, cancellationToken);

    public IReadOnlyList<MinecraftGameProcessStatus> GetActiveGameProcesses() =>
        _activeLaunches.Values
            .Where(gameProcess => !gameProcess.HasExited)
            .Select(gameProcess => new MinecraftGameProcessStatus(
                gameProcess.InstanceId,
                gameProcess.VersionId,
                gameProcess.ProcessId,
                gameProcess.StartedAt,
                DateTimeOffset.UtcNow,
                MinecraftGameProcessState.Running,
                ExitCode: null))
            .ToArray();

    public IReadOnlyList<MinecraftGameProcessOutput> GetRecentGameOutput(int limit = 250)
    {
        if (limit is < 1 or > MaxRecentGameOutputLines)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"Output page size must be between 1 and {MaxRecentGameOutputLines}.");
        }

        return _recentGameOutput.ToArray().TakeLast(limit).ToArray();
    }

    public async Task<IReadOnlyList<MinecraftGameProcessOutput>> ReadInstanceGameOutputLogAsync(
        Guid instanceId,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("An instance ID is required.", nameof(instanceId));
        }
        if (limit is < 1 or > 20000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The log page size must be between 1 and 20,000 lines.");
        }

        var path = GetInstanceGameOutputLogPath(instanceId);
        if (!File.Exists(path))
        {
            return Array.Empty<MinecraftGameProcessOutput>();
        }

        var gate = _gameOutputLogLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<MinecraftGameProcessOutput>();
            }

            var tail = new Queue<MinecraftGameProcessOutput>(Math.Min(limit, 2048));
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<MinecraftGameProcessOutput>(line);
                    if (entry?.InstanceId != instanceId)
                    {
                        continue;
                    }

                    if (tail.Count == limit)
                    {
                        tail.Dequeue();
                    }
                    tail.Enqueue(entry);
                }
                catch (System.Text.Json.JsonException)
                {
                    // Ignore a partially written final line and preserve the rest of the log.
                }
            }

            return tail.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearInstanceGameOutputLogAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("An instance ID is required.", nameof(instanceId));
        }
        if (_activeLaunches.ContainsKey(instanceId) || _launchReservations.ContainsKey(instanceId))
        {
            throw new InvalidOperationException("Stop the game before clearing its log.");
        }

        var gate = _gameOutputLogLocks.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetInstanceGameOutputLogPath(instanceId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var previousPath = path + ".previous";
            if (File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<MinecraftGameVersion>> GetReleaseVersionsAsync(CancellationToken cancellationToken = default) =>
        versionCatalog.GetReleaseVersionsAsync(cancellationToken);

    public Task<IReadOnlyList<MinecraftLoaderVersion>> GetFabricLoaderVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken = default) =>
        fabricLoaderCatalog.GetVersionsAsync(minecraftVersionId, cancellationToken);

    public Task<IReadOnlyList<MinecraftLoaderVersion>> GetQuiltLoaderVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken = default) =>
        quiltLoaderCatalog.GetVersionsAsync(minecraftVersionId, cancellationToken);

    public Task<IReadOnlyList<MinecraftLoaderVersion>> GetForgeNeoForgeLoaderVersionsAsync(
        MinecraftLoaderType loaderType,
        string minecraftVersionId,
        CancellationToken cancellationToken = default) =>
        forgeNeoForgeLoaderCatalog.GetVersionsAsync(loaderType, minecraftVersionId, cancellationToken);

    public Task<ModrinthModpackDescription> InspectModrinthPackAsync(
        string archivePath,
        CancellationToken cancellationToken = default) =>
        modrinthModpackService.InspectAsync(archivePath, cancellationToken);

    public async Task<ModrinthModpackExportResult> ExportModrinthPackAsync(
        Guid profileId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install this instance before exporting it as a modpack.");
        }

        if (_activeLaunches.ContainsKey(profileId) || _activeInstalls.ContainsKey(profileId) || _contentReservations.ContainsKey(profileId))
        {
            throw new InvalidOperationException("Stop the game and finish other instance changes before exporting a modpack.");
        }

        return await modrinthModpackService.ExportAsync(profile, DataDirectory, destinationPath, cancellationToken);
    }

    public async Task<ModrinthModpackUpdatePreview> PreviewModrinthPackUpdateAsync(
        Guid profileId,
        string newArchivePath,
        string newVersionNumber,
        CancellationToken cancellationToken = default)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State != InstanceProfileState.Installed || string.IsNullOrWhiteSpace(profile.ModrinthProjectId))
        {
            throw new InvalidOperationException("Only an installed Modrinth-linked instance can check for pack updates.");
        }

        if (_activeLaunches.ContainsKey(profileId) || _activeInstalls.ContainsKey(profileId) || _contentReservations.ContainsKey(profileId))
        {
            throw new InvalidOperationException("Stop Minecraft and finish other instance changes before checking this pack.");
        }

        return await modrinthModpackService.PreviewUpdateAsync(profile, DataDirectory, newArchivePath, newVersionNumber, cancellationToken);
    }

    public async Task<ModrinthModpackUpdateResult> ApplyModrinthPackUpdateAsync(
        Guid profileId,
        string newArchivePath,
        string newVersionId,
        string newVersionNumber,
        IReadOnlyCollection<string> replaceConflictPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replaceConflictPaths);
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State != InstanceProfileState.Installed || string.IsNullOrWhiteSpace(profile.ModrinthProjectId))
        {
            throw new InvalidOperationException("Only an installed Modrinth-linked instance can be updated in place.");
        }

        var validatedVersionId = ValidateOptionalModrinthId(newVersionId, "Modrinth version ID")
            ?? throw new ArgumentException("A Modrinth version ID is required.", nameof(newVersionId));
        var validatedVersionNumber = ValidateOptionalModrinthVersionNumber(newVersionNumber)
            ?? throw new ArgumentException("A Modrinth version number is required.", nameof(newVersionNumber));
        var sourcePath = Path.GetFullPath(newArchivePath);
        if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select an existing Modrinth .mrpack file.", nameof(newArchivePath));
        }

        ReserveContentChanges(profileId);
        var importsDirectory = Path.Combine(DataDirectory, "imports");
        var storedFileName = $"{Guid.NewGuid():N}.mrpack";
        var storedPath = Path.Combine(importsDirectory, storedFileName);
        var relativePackPath = Path.Combine("imports", storedFileName).Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            EnsureNotReparsePoint(importsDirectory);
            Directory.CreateDirectory(importsDirectory);
            await CopyFileAtomicallyAsync(sourcePath, storedPath, cancellationToken);
            return await modrinthModpackService.ApplyUpdateAsync(
                profile,
                DataDirectory,
                storedPath,
                replaceConflictPaths,
                (selectedOptionalFiles, token) => store.UpdateModrinthPackSourceAsync(
                    profileId,
                    relativePackPath,
                    validatedVersionId,
                    validatedVersionNumber,
                    JsonSerializer.Serialize(selectedOptionalFiles),
                    token),
                cancellationToken);
        }
        catch
        {
            if (File.Exists(storedPath))
            {
                try { File.Delete(storedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            throw;
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<InstanceProfile> CreateProfileAsync(
        string name,
        string versionId,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = InstanceDirectoryName.Validate(name.Trim());
        await EnsureUniqueProfileNameAsync(normalizedName, exceptProfileId: null, cancellationToken: cancellationToken);
        EnsureInstanceDirectoryAvailable(normalizedName);
        var profile = await CreateProfileRecordAsync(
            normalizedName,
            versionId,
            loaderType,
            loaderVersion,
            modrinthPackPath: null,
            modrinthSelectedOptionalFilesJson: "[]",
            cancellationToken);

        if (!await store.TryCreateProfileAsync(profile, cancellationToken))
        {
            throw new ProfileNameInUseException(profile.Name);
        }

        _instanceDirectoryNames[profile.Id] = InstanceDirectoryName.ForProfile(profile);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "instances", InstanceDirectoryName.ForProfile(profile)));
        return profile;
    }

    public async Task<InstanceProfile> ImportModrinthPackAsync(
        string archivePath,
        string instanceName,
        IReadOnlyCollection<string> selectedOptionalFiles,
        CancellationToken cancellationToken = default,
        string? modrinthProjectId = null,
        string? modrinthVersionId = null,
        string? modrinthVersionNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(selectedOptionalFiles);
        var sourcePath = Path.GetFullPath(archivePath);
        if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select an existing Modrinth .mrpack file.", nameof(archivePath));
        }

        const long maxArchiveBytes = 4L * 1024 * 1024 * 1024;
        if (new FileInfo(sourcePath).Length > maxArchiveBytes)
        {
            throw new InvalidDataException("The selected modpack archive exceeds Sakana's 4 GB import limit.");
        }

        var importsDirectory = Path.Combine(DataDirectory, "imports");
        Directory.CreateDirectory(importsDirectory);
        var storedFileName = $"{Guid.NewGuid():N}.mrpack";
        var storedPath = Path.Combine(importsDirectory, storedFileName);
        var relativePackPath = Path.Combine("imports", storedFileName).Replace(Path.DirectorySeparatorChar, '/');
        var profileCreated = false;
        try
        {
            await CopyFileAtomicallyAsync(sourcePath, storedPath, cancellationToken);
            var description = await modrinthModpackService.InspectAsync(storedPath, cancellationToken);
            var validOptionalPaths = description.OptionalFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            var selectedPaths = selectedOptionalFiles.Distinct(StringComparer.Ordinal).ToArray();
            if (selectedPaths.Any(path => !validOptionalPaths.Contains(path)))
            {
                throw new InvalidDataException("The selected optional files do not match the imported modpack.");
            }

            var profile = await CreateProfileRecordAsync(
                InstanceDirectoryName.Validate(instanceName.Trim()),
                description.MinecraftVersion,
                description.LoaderType,
                description.LoaderVersion,
                relativePackPath,
                JsonSerializer.Serialize(selectedPaths),
                cancellationToken,
                validateLoaderAvailability: false,
                modrinthProjectId: modrinthProjectId,
                modrinthVersionId: modrinthVersionId,
                modrinthVersionNumber: modrinthVersionNumber);
            await EnsureUniqueProfileNameAsync(profile.Name, exceptProfileId: null, cancellationToken: cancellationToken);
            EnsureInstanceDirectoryAvailable(InstanceDirectoryName.ForProfile(profile));
            if (!await store.TryCreateProfileAsync(profile, cancellationToken))
            {
                throw new ProfileNameInUseException(profile.Name);
            }

            profileCreated = true;
            _instanceDirectoryNames[profile.Id] = InstanceDirectoryName.ForProfile(profile);
            Directory.CreateDirectory(Path.Combine(DataDirectory, "instances", InstanceDirectoryName.ForProfile(profile)));
            return profile;
        }
        finally
        {
            if (!profileCreated && File.Exists(storedPath))
            {
                File.Delete(storedPath);
            }
        }
    }

    public Task<ModrinthProjectSearchPage> SearchModrinthModpacksAsync(
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default) =>
        modrinthModCatalog.SearchModpacksAsync(query, sortOrder, offset, cancellationToken);

    public Task<IReadOnlyList<ModrinthModRelease>> GetSupportedModrinthModpackVersionsAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        modrinthModCatalog.GetSupportedModpackVersionsAsync(projectId, cancellationToken);

    public Task<string> DownloadModrinthModpackVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default) =>
        modrinthModCatalog.DownloadModpackVersionAsync(versionId, cancellationToken);

    public async Task<IReadOnlyList<MinecraftModFile>> ListLocalModsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        return await localModService.ListAsync(InstanceDirectoryName.ForProfile(profile), cancellationToken);
    }

    public async Task<LocalModImportResult> ImportLocalModsAsync(
        Guid profileId,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await localModService.ImportAsync(InstanceDirectoryName.ForProfile(profile), sourcePaths, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task SetLocalModEnabledAsync(
        Guid profileId,
        string fileName,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localModService.SetEnabledAsync(InstanceDirectoryName.ForProfile(profile), fileName, isEnabled, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<ModrinthProjectSearchPage> SearchModrinthModsAsync(
        Guid profileId,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        return await modrinthModCatalog.SearchModsAsync(profile, query, sortOrder, offset, cancellationToken);
    }

    public async Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthVersionsAsync(
        Guid profileId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        return await modrinthModCatalog.GetCompatibleVersionsAsync(profile, projectId, cancellationToken);
    }

    public async Task<ModrinthModInstallResult> InstallModrinthVersionAsync(
        Guid profileId,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await modrinthModCatalog.InstallVersionAsync(profile, versionId, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<IReadOnlyList<ModrinthModUpdate>> CheckInstalledModUpdatesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        return await modrinthModCatalog.CheckInstalledModUpdatesAsync(profile, cancellationToken);
    }

    public async Task<ModrinthModInstallResult> UpdateInstalledModAsync(
        Guid profileId,
        string fileName,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await modrinthModCatalog.UpdateInstalledModAsync(profile, fileName, versionId, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<ModrinthProjectSearchPage> SearchModrinthContentAsync(
        Guid profileId,
        ModrinthContentKind kind,
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        return await modrinthModCatalog.SearchContentAsync(profile, kind, query, sortOrder, offset, cancellationToken);
    }

    public async Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthContentVersionsAsync(
        Guid profileId,
        ModrinthContentKind kind,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        return await modrinthModCatalog.GetCompatibleContentVersionsAsync(profile, kind, projectId, cancellationToken);
    }

    public async Task<ModrinthContentInstallResult> InstallModrinthContentVersionAsync(
        Guid profileId,
        ModrinthContentKind kind,
        string versionId,
        string? worldName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await modrinthModCatalog.InstallContentVersionAsync(profile, kind, versionId, worldName, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<IReadOnlyList<string>> ListMinecraftWorldsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        return await localPackService.ListWorldsAsync(InstanceDirectoryName.ForProfile(profile), cancellationToken);
    }

    public async Task ExportWorldBackupAsync(
        Guid profileId,
        string worldName,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localMinecraftWorldService.ExportBackupAsync(
                InstanceDirectoryName.ForProfile(profile),
                worldName,
                destinationPath,
                cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<string> ImportWorldBackupAsync(
        Guid profileId,
        string archivePath,
        string worldName,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await localMinecraftWorldService.ImportBackupAsync(
                InstanceDirectoryName.ForProfile(profile),
                archivePath,
                worldName,
                cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<IReadOnlyList<MinecraftServerBookmark>> ListServerBookmarksAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        return await localMinecraftServerService.ListAsync(InstanceDirectoryName.ForProfile(profile), cancellationToken);
    }

    public async Task<MinecraftServerBookmark> AddServerBookmarkAsync(Guid profileId, string name, string address, CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await localMinecraftServerService.AddAsync(InstanceDirectoryName.ForProfile(profile), name, address, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task RemoveServerBookmarkAsync(Guid profileId, Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localMinecraftServerService.RemoveAsync(InstanceDirectoryName.ForProfile(profile), bookmarkId, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task<IReadOnlyList<MinecraftLocalPack>> ListLocalPacksAsync(
        Guid profileId,
        MinecraftLocalPackKind kind,
        string? worldName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        return await localPackService.ListAsync(InstanceDirectoryName.ForProfile(profile), kind, worldName, cancellationToken);
    }

    public async Task<LocalPackImportResult> ImportLocalPacksAsync(
        Guid profileId,
        MinecraftLocalPackKind kind,
        IReadOnlyCollection<string> sourcePaths,
        string? worldName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            return await localPackService.ImportAsync(InstanceDirectoryName.ForProfile(profile), kind, sourcePaths, worldName, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task SetLocalPackEnabledAsync(
        Guid profileId,
        MinecraftLocalPackKind kind,
        string fileName,
        bool isEnabled,
        string? worldName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localPackService.SetEnabledAsync(InstanceDirectoryName.ForProfile(profile), kind, fileName, isEnabled, worldName, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task RemoveLocalPackAsync(
        Guid profileId,
        MinecraftLocalPackKind kind,
        string fileName,
        string? worldName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledProfileForContentAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localPackService.RemoveAsync(InstanceDirectoryName.ForProfile(profile), kind, fileName, worldName, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    public async Task RemoveLocalModAsync(
        Guid profileId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var profile = await GetInstalledModLoaderProfileForModsAsync(profileId, cancellationToken);
        ReserveContentChanges(profileId);
        try
        {
            await localModService.RemoveAsync(InstanceDirectoryName.ForProfile(profile), fileName, cancellationToken);
        }
        finally
        {
            _contentReservations.TryRemove(profileId, out _);
        }
    }

    private async Task<InstanceProfile> GetInstalledModLoaderProfileForModsAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.LoaderType is not (MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge))
        {
            throw new NotSupportedException("Local mod management is available for Fabric, Quilt, Forge, and NeoForge instances.");
        }

        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException($"Install this {profile.LoaderType} instance before managing its mods.");
        }

        return profile;
    }

    private async Task<InstanceProfile> GetInstalledProfileForContentAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install this instance before managing resource or shader packs.");
        }

        return profile;
    }

    private void ReserveContentChanges(Guid profileId)
    {
        if (_deletingProfiles.ContainsKey(profileId))
        {
            throw new InvalidOperationException("This instance is being deleted and cannot be changed.");
        }

        if (!_contentReservations.TryAdd(profileId, 0))
        {
            throw new InvalidOperationException("Another content change is already running for this instance.");
        }

        if (_deletingProfiles.ContainsKey(profileId))
        {
            _contentReservations.TryRemove(profileId, out _);
            throw new InvalidOperationException("This instance is being deleted and cannot be changed.");
        }

        if (_activeInstalls.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
            (_activeLaunches.TryGetValue(profileId, out var gameProcess) && !gameProcess.HasExited))
        {
            _contentReservations.TryRemove(profileId, out _);
            throw new InvalidOperationException("Stop Minecraft and wait for instance operations to finish before changing instance content.");
        }
    }

    private async Task<InstanceProfile> CreateProfileRecordAsync(
        string name,
        string versionId,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        string? modrinthPackPath,
        string modrinthSelectedOptionalFilesJson,
        CancellationToken cancellationToken,
        bool validateLoaderAvailability = true,
        string? modrinthProjectId = null,
        string? modrinthVersionId = null,
        string? modrinthVersionNumber = null)
    {
        var normalizedName = InstanceDirectoryName.Validate(NormalizeRequired(name, "Profile name", 40));
        var normalizedVersion = NormalizeRequired(versionId, "Minecraft version", 80);

        if (normalizedVersion.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Use letters, numbers, periods, underscores, or hyphens for the version ID.", nameof(versionId));
        }

        if (!Enum.IsDefined(loaderType))
        {
            throw new ArgumentOutOfRangeException(nameof(loaderType));
        }

        string? normalizedLoaderVersion = null;
        if (loaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge)
        {
            var loaderName = loaderType.ToString();
            normalizedLoaderVersion = NormalizeRequired(loaderVersion ?? string.Empty, $"{loaderName} loader version", 100);
            if (normalizedLoaderVersion.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '+'))
            {
                throw new ArgumentException($"The {loaderName} loader version contains unsupported characters.", nameof(loaderVersion));
            }

            if (validateLoaderAvailability)
            {
                var compatibleVersions = loaderType switch
                {
                    MinecraftLoaderType.Fabric => await fabricLoaderCatalog.GetVersionsAsync(normalizedVersion, cancellationToken),
                    MinecraftLoaderType.Quilt => await quiltLoaderCatalog.GetVersionsAsync(normalizedVersion, cancellationToken),
                    _ => await forgeNeoForgeLoaderCatalog.GetVersionsAsync(loaderType, normalizedVersion, cancellationToken)
                };
                if (!compatibleVersions.Any(version => string.Equals(version.Version, normalizedLoaderVersion, StringComparison.Ordinal)))
                {
                    throw new ArgumentException($"{loaderName} Loader {normalizedLoaderVersion} is not available for Minecraft {normalizedVersion}.", nameof(loaderVersion));
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(loaderVersion))
        {
            throw new ArgumentException("Vanilla instances cannot have a loader version.", nameof(loaderVersion));
        }

        var profile = new InstanceProfile(
            Guid.NewGuid(),
            normalizedName,
            normalizedVersion,
            InstanceProfileState.ProfileOnly,
            DateTimeOffset.UtcNow,
            LoaderType: loaderType,
            LoaderVersion: normalizedLoaderVersion,
            ModrinthPackPath: modrinthPackPath,
            ModrinthSelectedOptionalFilesJson: modrinthSelectedOptionalFilesJson,
            UseGlobalLaunchSettings: true,
            ModrinthProjectId: ValidateOptionalModrinthId(modrinthProjectId, "Modrinth project ID"),
            ModrinthPackVersionId: ValidateOptionalModrinthId(modrinthVersionId, "Modrinth version ID"),
            ModrinthPackVersionNumber: ValidateOptionalModrinthVersionNumber(modrinthVersionNumber));

        if ((profile.ModrinthProjectId is null) != (profile.ModrinthPackVersionId is null) ||
            (profile.ModrinthProjectId is null) != (profile.ModrinthPackVersionNumber is null))
        {
            throw new ArgumentException("A Modrinth pack source requires the project ID, version ID, and version number together.");
        }

        return profile;
    }

    private static string? ValidateOptionalModrinthId(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = NormalizeRequired(value, label, 80);
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException($"The {label} contains unsupported characters.", label);
        }

        return normalized;
    }

    private static string? ValidateOptionalModrinthVersionNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = NormalizeRequired(value, "Modrinth pack version number", 100);
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The Modrinth pack version number contains unsupported characters.", nameof(value));
        }

        return normalized;
    }

    private static (IReadOnlyList<string> Directories, IReadOnlyList<InstanceFileCopy> Files) CollectInstanceFilesForCopy(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var files = new List<InstanceFileCopy>();
        VisitDirectory(sourceDirectory, destinationDirectory);
        return (directories, files);

        void VisitDirectory(string sourcePath, string destinationPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAttributes = File.GetAttributes(sourcePath);
            if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Sakana refused to copy an instance folder redirected outside its data directory.");
            }

            directories.Add(destinationPath);
            foreach (var sourceEntryPath in Directory.EnumerateFileSystemEntries(sourcePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(sourceEntryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Sakana refused to copy redirected instance content: {Path.GetFileName(sourceEntryPath)}");
                }

                var destinationEntryPath = Path.Combine(destinationPath, Path.GetFileName(sourceEntryPath));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    VisitDirectory(sourceEntryPath, destinationEntryPath);
                }
                else
                {
                    files.Add(new InstanceFileCopy(sourceEntryPath, destinationEntryPath, new FileInfo(sourceEntryPath).Length));
                }
            }
        }
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<long>? copiedBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long totalCopied = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalCopied += bytesRead;
                    copiedBytes?.Invoke(totalCopied);
                }

                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record InstanceFileCopy(string SourcePath, string DestinationPath, long Length);

    public async Task<InstanceProfile> SaveInstanceMemoryAsync(
        Guid profileId,
        int minMemoryMb,
        int maxMemoryMb,
        CancellationToken cancellationToken = default)
    {
        if (minMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb)
        {
            throw new ArgumentOutOfRangeException(nameof(minMemoryMb), $"Minimum memory must be between {MinimumMemoryLimitMb} and {MaximumMemoryLimitMb} MB.");
        }

        if (maxMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMemoryMb), $"Maximum memory must be between {MinimumMemoryLimitMb} and {MaximumMemoryLimitMb} MB.");
        }

        if (minMemoryMb > maxMemoryMb)
        {
            throw new ArgumentException("Minimum memory cannot be greater than maximum memory.", nameof(minMemoryMb));
        }

        if (_activeInstalls.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
            (_activeLaunches.TryGetValue(profileId, out var runningProcess) && !runningProcess.HasExited))
        {
            throw new InvalidOperationException("Stop the current installation or Minecraft process before changing launch settings.");
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State == InstanceProfileState.Installing)
        {
            throw new InvalidOperationException("Wait for the instance installation to finish before changing launch settings.");
        }

        var settings = new InstanceLaunchSettings(
            profile.UseAutomaticMemory,
            minMemoryMb,
            maxMemoryMb,
            profile.JavaExecutablePath,
            profile.CustomJavaArguments,
            ReadEnvironmentVariables(profile.CustomEnvironmentVariablesJson),
            profile.Fullscreen,
            profile.WindowWidth,
            profile.WindowHeight,
            profile.UseGlobalLaunchSettings);
        await store.SetInstanceLaunchSettingsAsync(profileId, settings, cancellationToken);
        return await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
    }

    public async Task<InstanceProfile> SaveInstanceLaunchSettingsAsync(
        Guid profileId,
        InstanceLaunchSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseAutomaticMemory &&
            (settings.MinMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb ||
             settings.MaxMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb ||
             settings.MinMemoryMb > settings.MaxMemoryMb))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Memory values must be whole numbers between 256 MB and 65536 MB, with minimum not greater than maximum.");
        }

        if (settings.WindowWidth is < 640 or > 16384 || settings.WindowHeight is < 480 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Game window width must be 640–16384 and height must be 480–16384.");
        }

        if (settings.CustomJavaArguments.Length > 8192 || settings.CustomJavaArguments.Any(char.IsControl) && settings.CustomJavaArguments.Any(character => character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("Custom Java arguments are too long or contain unsupported control characters.", nameof(settings));
        }

        if (settings.CustomEnvironmentVariables.Count > 64)
        {
            throw new ArgumentException("Use no more than 64 custom environment variables.", nameof(settings));
        }

        foreach (var pair in settings.CustomEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Key.Contains('=') || pair.Key.Any(char.IsControl) ||
                pair.Value.Length > 4096 || pair.Value.Any(character => character is '\0' or '\r' or '\n'))
            {
                throw new ArgumentException("Environment variables must use a valid name and a single-line value of at most 4096 characters.", nameof(settings));
            }
        }

        string? javaPath = null;
        if (!string.IsNullOrWhiteSpace(settings.JavaExecutablePath))
        {
            javaPath = Path.GetFullPath(settings.JavaExecutablePath.Trim());
            if (!string.Equals(Path.GetFileName(javaPath), "java.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Choose java.exe as the custom Java executable.", nameof(settings));
            }
        }

        if (_activeInstalls.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
            (_activeLaunches.TryGetValue(profileId, out var runningProcess) && !runningProcess.HasExited))
        {
            throw new InvalidOperationException("Stop the current installation or Minecraft process before changing launch settings.");
        }

        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        if (profile.State == InstanceProfileState.Installing)
        {
            throw new InvalidOperationException("Wait for the instance installation to finish before changing launch settings.");
        }

        var normalized = settings with { JavaExecutablePath = javaPath };
        await store.SetInstanceLaunchSettingsAsync(profileId, normalized, cancellationToken);
        return await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironmentVariables(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved custom environment variables are invalid JSON.", exception);
        }
    }

    private static void ValidateLaunchSettings(InstanceLaunchSettings settings)
    {
        if (!settings.UseAutomaticMemory &&
            (settings.MinMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb ||
             settings.MaxMemoryMb is < MinimumMemoryLimitMb or > MaximumMemoryLimitMb ||
             settings.MinMemoryMb > settings.MaxMemoryMb))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Memory values must be whole numbers between 256 MB and 65536 MB, with minimum not greater than maximum.");
        }

        if (settings.WindowWidth is < 640 or > 16384 || settings.WindowHeight is < 480 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Game window width must be 640–16384 and height must be 480–16384.");
        }

        if (settings.CustomJavaArguments.Length > 8192 || settings.CustomJavaArguments.Any(char.IsControl) && settings.CustomJavaArguments.Any(character => character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("Custom Java arguments are too long or contain unsupported control characters.", nameof(settings));
        }

        if (settings.CustomEnvironmentVariables is null || settings.CustomEnvironmentVariables.Count > 64)
        {
            throw new ArgumentException("Use no more than 64 custom environment variables.", nameof(settings));
        }

        foreach (var pair in settings.CustomEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Key.Contains('=') || pair.Key.Any(char.IsControl) ||
                pair.Value.Length > 4096 || pair.Value.Any(character => character is '\0' or '\r' or '\n'))
            {
                throw new ArgumentException("Environment variables must use a valid name and a single-line value of at most 4096 characters.", nameof(settings));
            }
        }
    }

    private static string? NormalizeJavaPath(string? javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath)) return null;
        var fullPath = Path.GetFullPath(javaPath.Trim());
        if (!string.Equals(Path.GetFileName(fullPath), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose java.exe as the custom Java executable.", nameof(javaPath));
        }

        return fullPath;
    }

    private static InstanceProfile ApplyGlobalLaunchSettings(InstanceProfile profile, GlobalLaunchSettings settings) => profile with
    {
        UseAutomaticMemory = settings.UseAutomaticMemory,
        MinMemoryMb = settings.MinMemoryMb,
        MaxMemoryMb = settings.MaxMemoryMb,
        JavaExecutablePath = settings.JavaExecutablePath,
        CustomJavaArguments = settings.CustomJavaArguments,
        CustomEnvironmentVariablesJson = settings.CustomEnvironmentVariablesJson,
        Fullscreen = settings.Fullscreen,
        WindowWidth = settings.WindowWidth,
        WindowHeight = settings.WindowHeight
    };

    public async Task InstallProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await RunProfileInstallAsync(profileId, requireInstalled: false, cancellationToken);
    }

    public async Task RepairProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await RunProfileInstallAsync(profileId, requireInstalled: true, cancellationToken);
    }

    private async Task RunProfileInstallAsync(
        Guid profileId,
        bool requireInstalled,
        CancellationToken cancellationToken)
    {
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeInstalls.TryAdd(profileId, installCancellation))
        {
            throw new InvalidOperationException("This instance is already being installed or repaired.");
        }

        var stateChangedToInstalling = false;
        try
        {
            if (_deletingProfiles.ContainsKey(profileId))
            {
                throw new InvalidOperationException("This instance is being deleted and cannot be installed or repaired.");
            }

            if (_contentReservations.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
                (_activeLaunches.TryGetValue(profileId, out var runningProcess) && !runningProcess.HasExited))
            {
                throw new InvalidOperationException("Stop the running Minecraft game before installing or repairing this instance.");
            }

            var profile = await store.GetProfileAsync(profileId, installCancellation.Token)
                ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");

            if (requireInstalled && profile.State != InstanceProfileState.Installed)
            {
                throw new InvalidOperationException("Only an installed instance can be checked and repaired.");
            }

            if (!requireInstalled && profile.State == InstanceProfileState.Installed)
            {
                return;
            }

            // Recheck after reserving the install slot so a concurrent launch cannot start
            // while the repair is replacing a client or shared library file.
            if (_contentReservations.ContainsKey(profileId) || _launchReservations.ContainsKey(profileId) ||
                (_activeLaunches.TryGetValue(profileId, out runningProcess) && !runningProcess.HasExited))
            {
                throw new InvalidOperationException("Stop the running Minecraft game before installing or repairing this instance.");
            }

            var progress = new Progress<InstallationProgress>(update => InstallationProgressChanged?.Invoke(update));
            var launchSettings = await store.GetSettingsAsync(installCancellation.Token);
            var effectiveProfile = profile.UseGlobalLaunchSettings
                ? ApplyGlobalLaunchSettings(profile, launchSettings.GlobalLaunchSettings)
                : profile;
            await store.SetProfileStateAsync(
                profileId,
                InstanceProfileState.Installing,
                error: null,
                cancellationToken: installCancellation.Token);
            stateChangedToInstalling = true;
            await installer.InstallAsync(
                effectiveProfile,
                progress,
                installCancellation.Token,
                forceRevalidation: requireInstalled);
            await store.SetProfileStateAsync(
                profileId,
                InstanceProfileState.Installed,
                error: null,
                cancellationToken: installCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (stateChangedToInstalling)
            {
                await store.SetProfileStateAsync(profileId, InstanceProfileState.Cancelled, error: null, cancellationToken: CancellationToken.None);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (stateChangedToInstalling)
            {
                await store.SetProfileStateAsync(profileId, InstanceProfileState.Failed, exception.Message, CancellationToken.None);
            }
            throw;
        }
        finally
        {
            _activeInstalls.TryRemove(profileId, out _);
        }
    }

    public async Task<MinecraftLaunchPlan> PrepareLaunchPlanAsync(
        Guid profileId,
        MinecraftAccountSession session,
        CancellationToken cancellationToken = default,
        MinecraftQuickPlayTarget? quickPlayTarget = null)
    {
        var profile = await store.GetProfileAsync(profileId, cancellationToken)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");

        if (profile.State != InstanceProfileState.Installed)
        {
            throw new InvalidOperationException("Install the game files and matching Java runtime before preparing a launch.");
        }

        if (profile.UseGlobalLaunchSettings)
        {
            var settings = await store.GetSettingsAsync(cancellationToken);
            profile = ApplyGlobalLaunchSettings(profile, settings.GlobalLaunchSettings);
        }

        return await launchPlanner.CreatePlanAsync(profile, session, cancellationToken, quickPlayTarget);
    }

    public async Task<IMinecraftGameProcess> LaunchGameAsync(
        Guid profileId,
        MinecraftAccountSession session,
        CancellationToken cancellationToken = default,
        MinecraftQuickPlayTarget? quickPlayTarget = null)
    {
        if (!_launchReservations.TryAdd(profileId, 0))
        {
            throw new InvalidOperationException("This instance is already starting a game process.");
        }

        try
        {
            if (_deletingProfiles.ContainsKey(profileId))
            {
                throw new InvalidOperationException("This instance is being deleted and cannot be launched.");
            }

            if (_activeInstalls.ContainsKey(profileId) || _contentReservations.ContainsKey(profileId))
            {
                throw new InvalidOperationException("This instance is being installed or repaired. Try launching it again when the operation finishes.");
            }

            if (_activeLaunches.ContainsKey(profileId))
            {
                throw new InvalidOperationException("This instance already has a running game process.");
            }

            var plan = await PrepareLaunchPlanAsync(profileId, session, cancellationToken, quickPlayTarget);
            var gameProcess = await processRunner.StartAsync(plan, cancellationToken);
            if (!_activeLaunches.TryAdd(profileId, gameProcess))
            {
                await gameProcess.DisposeAsync();
                throw new InvalidOperationException("This instance already has a running game process.");
            }

            _ = ForwardProcessOutputAsync(gameProcess);
            var historyEntry = new MinecraftLaunchHistoryEntry(
                Guid.NewGuid(),
                profileId,
                gameProcess.VersionId,
                gameProcess.StartedAt,
                EndedAtUtc: null,
                ExitCode: null);
            try
            {
                await store.AddLaunchHistoryAsync(historyEntry, cancellationToken);
            }
            catch (Exception exception)
            {
                await TryLogErrorAsync("launch.history_start_failed", "The game started, but Sakana could not record its launch history.", exception);
            }

            await PublishProcessStatusAsync(new MinecraftGameProcessStatus(
                profileId,
                gameProcess.VersionId,
                gameProcess.ProcessId,
                gameProcess.StartedAt,
                DateTimeOffset.UtcNow,
                MinecraftGameProcessState.Running,
                ExitCode: null));
            _ = ReleaseProcessWhenCompleteAsync(profileId, gameProcess, historyEntry.Id);
            return gameProcess;
        }
        finally
        {
            _launchReservations.TryRemove(profileId, out _);
        }
    }

    public async Task StopGameAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (!_activeLaunches.TryGetValue(profileId, out var gameProcess))
        {
            return;
        }

        await gameProcess.StopAsync(cancellationToken);
    }

    public void CancelInstall(Guid profileId)
    {
        if (_activeInstalls.TryGetValue(profileId, out var cancellationSource))
        {
            try
            {
                cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The install completed between reading the active-operation map and signaling it.
            }
        }
    }

    private async Task ReleaseProcessWhenCompleteAsync(
        Guid profileId,
        IMinecraftGameProcess gameProcess,
        Guid historyEntryId)
    {
        try
        {
            var exitCode = await gameProcess.Completion.ConfigureAwait(false);
            var endedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await store.CompleteLaunchHistoryAsync(historyEntryId, endedAtUtc, exitCode, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await TryLogErrorAsync("launch.history_complete_failed", "Sakana could not update the completed launch history entry.", exception)
                    .ConfigureAwait(false);
            }
            await PublishProcessStatusAsync(new MinecraftGameProcessStatus(
                gameProcess.InstanceId,
                gameProcess.VersionId,
                gameProcess.ProcessId,
                gameProcess.StartedAt,
                endedAtUtc,
                MinecraftGameProcessState.Exited,
                exitCode)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await TryLogErrorAsync("launch.process_completion_failed", "Sakana could not observe the game process exit.", exception)
                .ConfigureAwait(false);
            await PublishProcessStatusAsync(new MinecraftGameProcessStatus(
                gameProcess.InstanceId,
                gameProcess.VersionId,
                gameProcess.ProcessId,
                gameProcess.StartedAt,
                DateTimeOffset.UtcNow,
                MinecraftGameProcessState.MonitoringFailed,
                ExitCode: null)).ConfigureAwait(false);
        }
        finally
        {
            _activeLaunches.TryRemove(profileId, out _);
            try
            {
                await gameProcess.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await TryLogErrorAsync("launch.process_release_failed", "Sakana could not release the completed game process handle.", exception)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task TryLogErrorAsync(string eventName, string message, Exception exception)
    {
        try
        {
            await logger.ErrorAsync(eventName, message, exception, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Logging failures must not break game process management.
        }
    }

    private string GetInstanceGameOutputLogPath(Guid instanceId) =>
        Path.Combine(DataDirectory, "instances", _instanceDirectoryNames.GetValueOrDefault(instanceId, instanceId.ToString("N")), "logs", "game-console.jsonl");

    private async Task PersistGameOutputAsync(MinecraftGameProcessOutput entry)
    {
        var gate = _gameOutputLogLocks.GetOrAdd(entry.InstanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetInstanceGameOutputLogPath(entry.InstanceId);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            const long maximumLogBytes = 16 * 1024 * 1024;
            if (File.Exists(path) && new FileInfo(path).Length >= maximumLogBytes)
            {
                var previousPath = path + ".previous";
                File.Move(path, previousPath, overwrite: true);
            }

            await File.AppendAllTextAsync(
                path,
                System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PublishProcessStatusAsync(MinecraftGameProcessStatus status)
    {
        if (GameProcessStatusChanged is not { } handlers)
        {
            return;
        }

        foreach (Action<MinecraftGameProcessStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(status);
            }
            catch (Exception exception)
            {
                await TryLogErrorAsync("launch.status_subscriber_failed", "A launch status subscriber could not process a game state change.", exception)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ForwardProcessOutputAsync(IMinecraftGameProcess gameProcess)
    {
        try
        {
            await foreach (var output in gameProcess.ReadOutputAsync().ConfigureAwait(false))
            {
                var entry = new MinecraftGameProcessOutput(gameProcess.InstanceId, gameProcess.VersionId, output);
                try
                {
                    await PersistGameOutputAsync(entry).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await TryLogErrorAsync("launch.output_persist_failed", "Sakana could not save a line from the game console log.", exception)
                        .ConfigureAwait(false);
                }

                _recentGameOutput.Enqueue(entry);
                Interlocked.Increment(ref _recentGameOutputCount);
                while (Volatile.Read(ref _recentGameOutputCount) > MaxRecentGameOutputLines &&
                       _recentGameOutput.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _recentGameOutputCount);
                }

                if (GameOutputReceived is not { } handlers)
                {
                    continue;
                }

                foreach (Action<MinecraftGameProcessOutput> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(entry);
                    }
                    catch (Exception exception)
                    {
                        await TryLogErrorAsync("launch.output_subscriber_failed", "A process output subscriber failed.", exception)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            await TryLogErrorAsync("launch.output_read_failed", "Sakana could not continue reading game output.", exception)
                .ConfigureAwait(false);
        }
    }

    private static string NormalizeRequired(string value, string label, int maxLength)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException($"{label} is required.", label);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{label} must be {maxLength} characters or fewer.", label);
        }

        return normalized;
    }
}
