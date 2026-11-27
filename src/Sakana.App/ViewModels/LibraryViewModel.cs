using System.Collections.ObjectModel;
using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Dispatching;

namespace Sakana.App.ViewModels;

public sealed class LibraryViewModel : ViewModelBase, IDisposable
{
    private readonly ILauncherService _launcherService;
    private readonly IMinecraftAccountService _accountService;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _hasProfiles;
    private GlobalLaunchSettings _globalLaunchSettings = GlobalLaunchSettings.Default;
    private string _searchQuery = string.Empty;
    private string? _groupFilter;
    private string? _tagFilter;
    private string? _loaderFilter;
    private string? _stateFilter;
    private string _sortOrder = "created-desc";
    private readonly Dictionary<string, bool> _quickPlaySupportCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _quickPlayChecksInFlight = new(StringComparer.OrdinalIgnoreCase);

    public LibraryViewModel(
        ILauncherService launcherService,
        IMinecraftAccountService accountService,
        DispatcherQueue dispatcherQueue)
    {
        _launcherService = launcherService;
        _accountService = accountService;
        _dispatcherQueue = dispatcherQueue;
        _launcherService.InstallationProgressChanged += OnInstallationProgressChanged;
        _launcherService.GameProcessStatusChanged += OnGameProcessStatusChanged;
    }

    public ObservableCollection<InstanceCardViewModel> Profiles { get; } = [];

    public ObservableCollection<InstanceCardViewModel> VisibleProfiles { get; } = [];

    public ObservableCollection<MinecraftGameVersion> AvailableVersions { get; } = [];

    public ObservableCollection<MinecraftLoaderVersion> AvailableLoaderVersions { get; } = [];

    public string DataDirectory => _launcherService.DataDirectory;

    public string GetInstanceGameDirectory(Guid profileId)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        var directoryName = profile is null
            ? profileId.ToString("N")
            : InstanceDirectoryName.ForName(profile.Name, profileId);
        return Path.Combine(_launcherService.DataDirectory, "instances", directoryName, "game");
    }

    public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var filesDeleted = await _launcherService.DeleteProfileAsync(profileId, cancellationToken);
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is not null)
        {
            Profiles.Remove(profile);
        }

        HasProfiles = Profiles.Count > 0;
        RefreshVisibleProfiles();
        return filesDeleted;
    }

    public async Task<InstanceProfile> DuplicateProfileAsync(
        Guid profileId,
        string name,
        IProgress<InstanceCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await _launcherService.DuplicateProfileAsync(profileId, name, progress, cancellationToken);
        AddProfile(profile);
        HasProfiles = true;
        return profile;
    }

    public async Task RenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default)
    {
        var updatedProfile = await _launcherService.RenameProfileAsync(profileId, name, cancellationToken);
        Profiles.FirstOrDefault(item => item.Id == profileId)?.Apply(updatedProfile);
        RefreshVisibleProfiles();
    }

    public async Task SetInstanceOrganizationAsync(Guid profileId, string? groupName, IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        var updatedProfile = await _launcherService.SetInstanceOrganizationAsync(profileId, groupName, tags, cancellationToken);
        Profiles.FirstOrDefault(item => item.Id == profileId)?.Apply(updatedProfile);
        RefreshVisibleProfiles();
    }

    public async Task SetProfileIconAsync(Guid profileId, string? iconSourcePath, CancellationToken cancellationToken = default)
    {
        var updatedProfile = await _launcherService.SetProfileIconAsync(profileId, iconSourcePath, cancellationToken);
        Profiles.FirstOrDefault(item => item.Id == profileId)?.Apply(updatedProfile);
    }

    public IReadOnlyList<string> GetGroupNames() => Profiles
        .Select(profile => profile.GroupName?.Trim())
        .Where(group => !string.IsNullOrWhiteSpace(group))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
        .Select(group => group!)
        .ToArray();

    public IReadOnlyList<string> GetTagNames() => Profiles
        .SelectMany(profile => profile.Tags)
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void ApplyLibraryFilter(
        string? searchQuery,
        string? groupFilter,
        string? loaderFilter = null,
        string? stateFilter = null,
        string? sortOrder = null,
        string? tagFilter = null)
    {
        _searchQuery = searchQuery?.Trim() ?? string.Empty;
        _groupFilter = string.IsNullOrWhiteSpace(groupFilter) ? null : groupFilter;
        _tagFilter = string.IsNullOrWhiteSpace(tagFilter) ? null : tagFilter.Trim();
        _loaderFilter = string.IsNullOrWhiteSpace(loaderFilter) ? null : loaderFilter;
        _stateFilter = string.IsNullOrWhiteSpace(stateFilter) ? null : stateFilter;
        _sortOrder = sortOrder is "name-asc" or "name-desc" or "version-desc" ? sortOrder : "created-desc";
        RefreshVisibleProfiles();
    }

    public Task<LauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        _launcherService.GetSettingsAsync(cancellationToken);

    public async Task<IReadOnlyList<LibraryViewPreset>> GetLibraryViewPresetsAsync(CancellationToken cancellationToken = default) =>
        (await _launcherService.GetSettingsAsync(cancellationToken)).LibraryViewPresets;

    public Task SaveLibraryViewPresetsAsync(IReadOnlyList<LibraryViewPreset> presets, CancellationToken cancellationToken = default) =>
        _launcherService.SaveLibraryViewPresetsAsync(presets, cancellationToken);

    public bool HasProfiles
    {
        get => _hasProfiles;
        private set
        {
            if (_hasProfiles == value) return;
            _hasProfiles = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _launcherService.GetSettingsAsync(cancellationToken);
        _globalLaunchSettings = settings.GlobalLaunchSettings;
        var listedProfiles = await _launcherService.ListProfilesAsync(cancellationToken);
        var existingProfiles = Profiles.ToDictionary(profile => profile.Id);
        var orderedProfiles = new List<InstanceCardViewModel>(listedProfiles.Count);
        foreach (var profile in listedProfiles)
        {
            if (existingProfiles.Remove(profile.Id, out var existing))
            {
                existing.Apply(profile);
                orderedProfiles.Add(existing);
            }
            else
            {
                orderedProfiles.Add(new InstanceCardViewModel(profile, _globalLaunchSettings, _launcherService.DataDirectory));
            }
        }

        ReconcileCollection(Profiles, orderedProfiles);

        foreach (var process in _launcherService.GetActiveGameProcesses())
        {
            Profiles.FirstOrDefault(profile => profile.Id == process.InstanceId)?.SetRunning(true, process.ProcessId);
        }

        HasProfiles = Profiles.Count > 0;
        RefreshVisibleProfiles();
        _ = RefreshQuickPlaySupportAsync(cancellationToken);
    }

    private async Task RefreshQuickPlaySupportAsync(CancellationToken cancellationToken)
    {
        var versionsToCheck = new List<string>();
        foreach (var group in Profiles.Where(profile => profile.ProfileState == InstanceProfileState.Installed).GroupBy(profile => profile.VersionId, StringComparer.OrdinalIgnoreCase))
        {
            if (_quickPlaySupportCache.TryGetValue(group.Key, out var cachedSupport))
            {
                foreach (var profile in group) profile.SetQuickPlaySupport(cachedSupport);
            }
            else
            {
                foreach (var profile in group) profile.SetQuickPlayChecking();
                if (_quickPlayChecksInFlight.Add(group.Key)) versionsToCheck.Add(group.Key);
            }
        }

        if (versionsToCheck.Count == 0) return;

        using var throttler = new SemaphoreSlim(3);
        await Task.WhenAll(versionsToCheck.Select(async versionId =>
        {
            bool? support = null;
            try
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    support = await _launcherService.SupportsQuickPlayAsync(versionId, cancellationToken);
                }
                finally
                {
                    throttler.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // Keep the library usable when version metadata is temporarily unavailable.
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                _quickPlayChecksInFlight.Remove(versionId);
                if (support is bool supported) _quickPlaySupportCache[versionId] = supported;
                foreach (var profile in Profiles.Where(profile => string.Equals(profile.VersionId, versionId, StringComparison.OrdinalIgnoreCase)))
                {
                    profile.SetQuickPlaySupport(support);
                }
            });
        }));
    }

    public async Task LoadAvailableVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await _launcherService.GetReleaseVersionsAsync(cancellationToken);
        AvailableVersions.Clear();
        foreach (var version in versions)
        {
            AvailableVersions.Add(version);
        }
    }

    public async Task<InstanceProfile> CreateProfileAsync(
        string name,
        string versionId,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        CancellationToken cancellationToken = default)
    {
        var profile = await _launcherService.CreateProfileAsync(name, versionId, loaderType, loaderVersion, cancellationToken);
        AddProfile(profile);
        HasProfiles = true;
        return profile;
    }

    public Task<ModrinthModpackDescription> InspectModrinthPackAsync(string archivePath, CancellationToken cancellationToken = default) =>
        _launcherService.InspectModrinthPackAsync(archivePath, cancellationToken);

    public Task<CurseForgeModpackDescription> InspectCurseForgePackAsync(string archivePath, CancellationToken cancellationToken = default) =>
        _launcherService.InspectCurseForgePackAsync(archivePath, cancellationToken);

    public Task<ModrinthModpackExportResult> ExportModrinthPackAsync(Guid profileId, string destinationPath, CancellationToken cancellationToken = default) =>
        _launcherService.ExportModrinthPackAsync(profileId, destinationPath, cancellationToken);

    public Task<ModrinthModpackUpdatePreview> PreviewModrinthPackUpdateAsync(Guid profileId, string archivePath, string versionNumber, CancellationToken cancellationToken = default) =>
        _launcherService.PreviewModrinthPackUpdateAsync(profileId, archivePath, versionNumber, cancellationToken);

    public async Task<ModrinthModpackUpdateResult> ApplyModrinthPackUpdateAsync(
        Guid profileId,
        string archivePath,
        string versionId,
        string versionNumber,
        IReadOnlyCollection<string> replaceConflictPaths,
        CancellationToken cancellationToken = default)
    {
        var result = await _launcherService.ApplyModrinthPackUpdateAsync(
            profileId,
            archivePath,
            versionId,
            versionNumber,
            replaceConflictPaths,
            cancellationToken);
        await RefreshAsync(cancellationToken);
        return result;
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
        var profile = await _launcherService.ImportModrinthPackAsync(
            archivePath,
            instanceName,
            selectedOptionalFiles,
            cancellationToken,
            modrinthProjectId,
            modrinthVersionId,
            modrinthVersionNumber);
        AddProfile(profile);
        HasProfiles = true;
        return profile;
    }

    public async Task<InstanceProfile> ImportCurseForgePackAsync(
        string archivePath,
        string instanceName,
        IReadOnlyCollection<string> selectedOptionalFiles,
        CancellationToken cancellationToken = default)
    {
        var profile = await _launcherService.ImportCurseForgePackAsync(archivePath, instanceName, selectedOptionalFiles, cancellationToken);
        AddProfile(profile);
        HasProfiles = true;
        return profile;
    }

    public Task<ModrinthProjectSearchPage> SearchModrinthModpacksAsync(
        string query,
        string sortOrder,
        int offset,
        CancellationToken cancellationToken = default,
        string? gameVersion = null,
        string? loader = null) =>
        _launcherService.SearchModrinthModpacksAsync(query, sortOrder, offset, cancellationToken, gameVersion, loader);

    public Task<IReadOnlyList<ModrinthModRelease>> GetSupportedModrinthModpackVersionsAsync(string projectId, CancellationToken cancellationToken = default) =>
        _launcherService.GetSupportedModrinthModpackVersionsAsync(projectId, cancellationToken);

    public Task<string> DownloadModrinthModpackVersionAsync(string versionId, CancellationToken cancellationToken = default) =>
        _launcherService.DownloadModrinthModpackVersionAsync(versionId, cancellationToken);

    public Task<IReadOnlyList<MinecraftModFile>> ListLocalModsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _launcherService.ListLocalModsAsync(profileId, cancellationToken);

    public Task<LocalModImportResult> ImportLocalModsAsync(
        Guid profileId,
        IReadOnlyCollection<string> sourcePaths,
        CancellationToken cancellationToken = default) =>
        _launcherService.ImportLocalModsAsync(profileId, sourcePaths, cancellationToken);

    public Task<ModrinthProjectSearchPage> SearchModrinthModsAsync(Guid profileId, string query, string sortOrder, int offset, CancellationToken cancellationToken = default) =>
        _launcherService.SearchModrinthModsAsync(profileId, query, sortOrder, offset, cancellationToken);

    public Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthVersionsAsync(Guid profileId, string projectId, CancellationToken cancellationToken = default) =>
        _launcherService.GetCompatibleModrinthVersionsAsync(profileId, projectId, cancellationToken);

    public Task<ModrinthModInstallResult> InstallModrinthVersionAsync(Guid profileId, string versionId, CancellationToken cancellationToken = default) =>
        _launcherService.InstallModrinthVersionAsync(profileId, versionId, cancellationToken);

    public Task<IReadOnlyList<ModrinthModUpdate>> CheckInstalledModUpdatesAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _launcherService.CheckInstalledModUpdatesAsync(profileId, cancellationToken);

    public Task<ModrinthModInstallResult> UpdateInstalledModAsync(Guid profileId, string fileName, string versionId, CancellationToken cancellationToken = default) =>
        _launcherService.UpdateInstalledModAsync(profileId, fileName, versionId, cancellationToken);

    public Task<ModrinthProjectSearchPage> SearchModrinthContentAsync(Guid profileId, ModrinthContentKind kind, string query, string sortOrder, int offset, CancellationToken cancellationToken = default) =>
        _launcherService.SearchModrinthContentAsync(profileId, kind, query, sortOrder, offset, cancellationToken);

    public Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthContentVersionsAsync(Guid profileId, ModrinthContentKind kind, string projectId, CancellationToken cancellationToken = default) =>
        _launcherService.GetCompatibleModrinthContentVersionsAsync(profileId, kind, projectId, cancellationToken);

    public Task<ModrinthContentInstallResult> InstallModrinthContentVersionAsync(Guid profileId, ModrinthContentKind kind, string versionId, string? worldName = null, CancellationToken cancellationToken = default) =>
        _launcherService.InstallModrinthContentVersionAsync(profileId, kind, versionId, worldName, cancellationToken);

    public Task<IReadOnlyList<string>> ListMinecraftWorldsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _launcherService.ListMinecraftWorldsAsync(profileId, cancellationToken);

    public Task ExportWorldBackupAsync(Guid profileId, string worldName, string destinationPath, CancellationToken cancellationToken = default) =>
        _launcherService.ExportWorldBackupAsync(profileId, worldName, destinationPath, cancellationToken);

    public Task<string> ImportWorldBackupAsync(Guid profileId, string archivePath, string worldName, CancellationToken cancellationToken = default) =>
        _launcherService.ImportWorldBackupAsync(profileId, archivePath, worldName, cancellationToken);

    public Task<IReadOnlyList<MinecraftServerBookmark>> ListServerBookmarksAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _launcherService.ListServerBookmarksAsync(profileId, cancellationToken);

    public Task<MinecraftServerStatus> GetServerBookmarkStatusAsync(Guid profileId, Guid bookmarkId, CancellationToken cancellationToken = default) =>
        _launcherService.GetServerBookmarkStatusAsync(profileId, bookmarkId, cancellationToken);

    public Task<MinecraftServerBookmark> AddServerBookmarkAsync(Guid profileId, string name, string address, CancellationToken cancellationToken = default) =>
        _launcherService.AddServerBookmarkAsync(profileId, name, address, cancellationToken);

    public Task<MinecraftServerBookmark> UpdateServerBookmarkAsync(Guid profileId, Guid bookmarkId, string name, string address, CancellationToken cancellationToken = default) =>
        _launcherService.UpdateServerBookmarkAsync(profileId, bookmarkId, name, address, cancellationToken);

    public Task RemoveServerBookmarkAsync(Guid profileId, Guid bookmarkId, CancellationToken cancellationToken = default) =>
        _launcherService.RemoveServerBookmarkAsync(profileId, bookmarkId, cancellationToken);

    public Task SetServerBookmarkPinnedAsync(Guid profileId, Guid bookmarkId, bool isPinned, CancellationToken cancellationToken = default) =>
        _launcherService.SetServerBookmarkPinnedAsync(profileId, bookmarkId, isPinned, cancellationToken);

    public Task<IReadOnlyList<MinecraftLocalPack>> ListLocalPacksAsync(Guid profileId, MinecraftLocalPackKind kind, string? worldName = null, CancellationToken cancellationToken = default) =>
        _launcherService.ListLocalPacksAsync(profileId, kind, worldName, cancellationToken);

    public Task<LocalPackImportResult> ImportLocalPacksAsync(Guid profileId, MinecraftLocalPackKind kind, IReadOnlyCollection<string> sourcePaths, string? worldName = null, CancellationToken cancellationToken = default) =>
        _launcherService.ImportLocalPacksAsync(profileId, kind, sourcePaths, worldName, cancellationToken);

    public Task SetLocalPackEnabledAsync(Guid profileId, MinecraftLocalPackKind kind, string fileName, bool isEnabled, string? worldName = null, CancellationToken cancellationToken = default) =>
        _launcherService.SetLocalPackEnabledAsync(profileId, kind, fileName, isEnabled, worldName, cancellationToken);

    public Task RemoveLocalPackAsync(Guid profileId, MinecraftLocalPackKind kind, string fileName, string? worldName = null, CancellationToken cancellationToken = default) =>
        _launcherService.RemoveLocalPackAsync(profileId, kind, fileName, worldName, cancellationToken);

    public Task SetLocalModEnabledAsync(Guid profileId, string fileName, bool isEnabled, CancellationToken cancellationToken = default) =>
        _launcherService.SetLocalModEnabledAsync(profileId, fileName, isEnabled, cancellationToken);

    public Task RemoveLocalModAsync(Guid profileId, string fileName, CancellationToken cancellationToken = default) =>
        _launcherService.RemoveLocalModAsync(profileId, fileName, cancellationToken);

    public async Task LoadLoaderVersionsAsync(MinecraftLoaderType loaderType, string minecraftVersionId, CancellationToken cancellationToken = default)
    {
        var versions = loaderType switch
        {
            MinecraftLoaderType.Fabric => await _launcherService.GetFabricLoaderVersionsAsync(minecraftVersionId, cancellationToken),
            MinecraftLoaderType.Quilt => await _launcherService.GetQuiltLoaderVersionsAsync(minecraftVersionId, cancellationToken),
            MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge => await _launcherService.GetForgeNeoForgeLoaderVersionsAsync(loaderType, minecraftVersionId, cancellationToken),
            _ => Array.Empty<MinecraftLoaderVersion>()
        };
        AvailableLoaderVersions.Clear();
        foreach (var version in versions)
        {
            AvailableLoaderVersions.Add(version);
        }
    }

    public async Task<IReadOnlyList<CurseForgeManualDownload>> InstallProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId);
        profile?.SetState(InstanceProfileState.Installing, lastError: null);
        RefreshVisibleProfiles();
        try
        {
            return await _launcherService.InstallProfileAsync(profileId, cancellationToken);
        }
        finally
        {
            await RefreshAsync(CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<CurseForgeManualDownload>> RepairProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        profile.SetState(InstanceProfileState.Installing, lastError: null);
        RefreshVisibleProfiles();
        try
        {
            return await _launcherService.RepairProfileAsync(profileId, cancellationToken);
        }
        finally
        {
            await RefreshAsync(CancellationToken.None);
        }
    }

    public async Task SaveInstanceMemoryAsync(Guid profileId, int minMemoryMb, int maxMemoryMb, CancellationToken cancellationToken = default)
    {
        var updatedProfile = await _launcherService.SaveInstanceMemoryAsync(profileId, minMemoryMb, maxMemoryMb, cancellationToken);
        Profiles.FirstOrDefault(item => item.Id == profileId)?.Apply(updatedProfile);
    }

    public async Task SaveInstanceLaunchSettingsAsync(Guid profileId, InstanceLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        var updatedProfile = await _launcherService.SaveInstanceLaunchSettingsAsync(profileId, settings, cancellationToken);
        Profiles.FirstOrDefault(item => item.Id == profileId)?.Apply(updatedProfile);
    }

    public Task<IReadOnlyList<MinecraftAccountInfo>> GetSavedAccountsAsync(CancellationToken cancellationToken = default) =>
        _accountService.GetAccountsAsync(LauncherSettings.MicrosoftOAuthClientId, cancellationToken);

    public Task<IReadOnlyList<JavaInstallation>> DetectJavaInstallationsAsync(CancellationToken cancellationToken = default) =>
        _launcherService.DetectJavaInstallationsAsync(cancellationToken);

    public Task<JavaInstallation> InstallJavaRuntimeAsync(
        int majorVersion,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _launcherService.InstallJavaRuntimeAsync(majorVersion, progress, cancellationToken);

    public void CancelInstall(Guid profileId) => _launcherService.CancelInstall(profileId);

    public async Task LaunchProfileAsync(Guid profileId, CancellationToken cancellationToken = default, MinecraftQuickPlayTarget? quickPlayTarget = null)
    {
        var profile = Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new KeyNotFoundException("The selected instance profile no longer exists.");
        profile.SetLaunching(true);
        RefreshVisibleProfiles();
        try
        {
            var session = await _accountService.GetSessionForAccountAsync(
                LauncherSettings.MicrosoftOAuthClientId,
                profile.MinecraftAccountUuid,
                cancellationToken);
            if (session is null)
            {
                throw new MinecraftAccountRequiredException(profile.MinecraftAccountUuid is null
                    ? "先连接 Microsoft 账户，再启动 Minecraft。"
                    : "此实例指定的账户需要重新登录。请在账户页重新连接该账户后再启动。");
            }

            var process = await _launcherService.LaunchGameAsync(profileId, session, cancellationToken, quickPlayTarget);
            if (!process.HasExited)
            {
                profile.SetRunning(true, process.ProcessId);
            }
        }
        finally
        {
            if (profile.IsLaunching)
            {
                profile.SetLaunching(false);
            }
            RefreshVisibleProfiles();
        }
    }

    public async Task StopProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _launcherService.StopGameAsync(profileId, cancellationToken);
    }

    public void Dispose()
    {
        _launcherService.InstallationProgressChanged -= OnInstallationProgressChanged;
        _launcherService.GameProcessStatusChanged -= OnGameProcessStatusChanged;
    }

    private void OnInstallationProgressChanged(InstallationProgress progress)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var profile = Profiles.FirstOrDefault(item => item.Id == progress.InstanceId);
            var wasInstalling = profile?.ProfileState == InstanceProfileState.Installing;
            profile?.ApplyProgress(progress);
            if (profile is not null && !wasInstalling)
            {
                RefreshVisibleProfiles();
            }
        });
    }

    private void AddProfile(InstanceProfile profile)
    {
        Profiles.Insert(0, new InstanceCardViewModel(profile, _globalLaunchSettings, _launcherService.DataDirectory));
        RefreshVisibleProfiles();
    }

    private void RefreshVisibleProfiles()
    {
        var matches = Profiles.Where(profile => profile.MatchesFilter(_searchQuery, _groupFilter, _loaderFilter, _stateFilter, _tagFilter));
        var sorted = _sortOrder switch
        {
            "name-asc" => matches.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase),
            "name-desc" => matches.OrderByDescending(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase),
            "version-desc" => matches
                .OrderByDescending(profile => profile.VersionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => matches.OrderByDescending(profile => profile.CreatedAtUtc)
        };
        ReconcileCollection(VisibleProfiles, sorted.ToList());
    }

    private static void ReconcileCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < collection.Count && EqualityComparer<T>.Default.Equals(collection[index], desired[index]))
            {
                continue;
            }

            var existingIndex = collection.IndexOf(desired[index]);
            if (existingIndex >= 0)
            {
                collection.Move(existingIndex, index);
            }
            else
            {
                collection.Insert(index, desired[index]);
            }
        }

        while (collection.Count > desired.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    private void OnGameProcessStatusChanged(MinecraftGameProcessStatus status)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var profile = Profiles.FirstOrDefault(item => item.Id == status.InstanceId);
            if (profile is null)
            {
                return;
            }

            profile.ApplyProcessStatus(status);
            RefreshVisibleProfiles();
        });
    }

}
