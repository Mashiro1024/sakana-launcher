using Sakana.Domain;

namespace Sakana.Application;

public interface ILauncherService
{
    string DataDirectory { get; }

    event Action<InstallationProgress>? InstallationProgressChanged;
    event Action<MinecraftGameProcessStatus>? GameProcessStatusChanged;
    event Action<MinecraftGameProcessOutput>? GameOutputReceived;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<LauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default);
    Task SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JavaInstallation>> DetectJavaInstallationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<InstanceProfile> DuplicateProfileAsync(
        Guid profileId,
        string name,
        IProgress<InstanceCopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<InstanceProfile> RenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default);
    Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLaunchHistoryEntry>> GetLaunchHistoryAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftGameVersion>> GetReleaseVersionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLoaderVersion>> GetFabricLoaderVersionsAsync(string minecraftVersionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLoaderVersion>> GetQuiltLoaderVersionsAsync(string minecraftVersionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLoaderVersion>> GetForgeNeoForgeLoaderVersionsAsync(MinecraftLoaderType loaderType, string minecraftVersionId, CancellationToken cancellationToken = default);
    Task<ModrinthModpackDescription> InspectModrinthPackAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<ModrinthModpackUpdatePreview> PreviewModrinthPackUpdateAsync(Guid profileId, string newArchivePath, string newVersionNumber, CancellationToken cancellationToken = default);
    Task<ModrinthModpackUpdateResult> ApplyModrinthPackUpdateAsync(Guid profileId, string newArchivePath, string newVersionId, string newVersionNumber, IReadOnlyCollection<string> replaceConflictPaths, CancellationToken cancellationToken = default);
    Task<ModrinthModpackExportResult> ExportModrinthPackAsync(Guid profileId, string destinationPath, CancellationToken cancellationToken = default);
    Task<InstanceProfile> ImportModrinthPackAsync(
        string archivePath,
        string instanceName,
        IReadOnlyCollection<string> selectedOptionalFiles,
        CancellationToken cancellationToken = default,
        string? modrinthProjectId = null,
        string? modrinthVersionId = null,
        string? modrinthVersionNumber = null);
    Task<ModrinthProjectSearchPage> SearchModrinthModpacksAsync(string query, string sortOrder, int offset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthModRelease>> GetSupportedModrinthModpackVersionsAsync(string projectId, CancellationToken cancellationToken = default);
    Task<string> DownloadModrinthModpackVersionAsync(string versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftModFile>> ListLocalModsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<LocalModImportResult> ImportLocalModsAsync(Guid profileId, IReadOnlyCollection<string> sourcePaths, CancellationToken cancellationToken = default);
    Task<ModrinthProjectSearchPage> SearchModrinthModsAsync(Guid profileId, string query, string sortOrder, int offset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthVersionsAsync(Guid profileId, string projectId, CancellationToken cancellationToken = default);
    Task<ModrinthModInstallResult> InstallModrinthVersionAsync(Guid profileId, string versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthModUpdate>> CheckInstalledModUpdatesAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ModrinthModInstallResult> UpdateInstalledModAsync(Guid profileId, string fileName, string versionId, CancellationToken cancellationToken = default);
    Task<ModrinthProjectSearchPage> SearchModrinthContentAsync(Guid profileId, ModrinthContentKind kind, string query, string sortOrder, int offset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthModRelease>> GetCompatibleModrinthContentVersionsAsync(Guid profileId, ModrinthContentKind kind, string projectId, CancellationToken cancellationToken = default);
    Task<ModrinthContentInstallResult> InstallModrinthContentVersionAsync(Guid profileId, ModrinthContentKind kind, string versionId, string? worldName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListMinecraftWorldsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLocalPack>> ListLocalPacksAsync(Guid profileId, MinecraftLocalPackKind kind, string? worldName = null, CancellationToken cancellationToken = default);
    Task<LocalPackImportResult> ImportLocalPacksAsync(Guid profileId, MinecraftLocalPackKind kind, IReadOnlyCollection<string> sourcePaths, string? worldName = null, CancellationToken cancellationToken = default);
    Task SetLocalPackEnabledAsync(Guid profileId, MinecraftLocalPackKind kind, string fileName, bool isEnabled, string? worldName = null, CancellationToken cancellationToken = default);
    Task RemoveLocalPackAsync(Guid profileId, MinecraftLocalPackKind kind, string fileName, string? worldName = null, CancellationToken cancellationToken = default);
    Task SetLocalModEnabledAsync(Guid profileId, string fileName, bool isEnabled, CancellationToken cancellationToken = default);
    Task RemoveLocalModAsync(Guid profileId, string fileName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftServerBookmark>> ListServerBookmarksAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<MinecraftServerBookmark> AddServerBookmarkAsync(Guid profileId, string name, string address, CancellationToken cancellationToken = default);
    Task RemoveServerBookmarkAsync(Guid profileId, Guid bookmarkId, CancellationToken cancellationToken = default);
    Task<InstanceProfile> CreateProfileAsync(
        string name,
        string versionId,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        CancellationToken cancellationToken = default);
    Task<InstanceProfile> SaveInstanceMemoryAsync(Guid profileId, int minMemoryMb, int maxMemoryMb, CancellationToken cancellationToken = default);
    Task<InstanceProfile> SaveInstanceLaunchSettingsAsync(Guid profileId, InstanceLaunchSettings settings, CancellationToken cancellationToken = default);
    Task InstallProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task RepairProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<MinecraftLaunchPlan> PrepareLaunchPlanAsync(Guid profileId, MinecraftAccountSession session, CancellationToken cancellationToken = default, MinecraftQuickPlayTarget? quickPlayTarget = null);
    Task<IMinecraftGameProcess> LaunchGameAsync(Guid profileId, MinecraftAccountSession session, CancellationToken cancellationToken = default, MinecraftQuickPlayTarget? quickPlayTarget = null);
    Task StopGameAsync(Guid profileId, CancellationToken cancellationToken = default);
    IReadOnlyList<MinecraftGameProcessStatus> GetActiveGameProcesses();
    IReadOnlyList<MinecraftGameProcessOutput> GetRecentGameOutput(int limit = 250);
    Task<IReadOnlyList<MinecraftGameProcessOutput>> ReadInstanceGameOutputLogAsync(Guid instanceId, int limit = 5000, CancellationToken cancellationToken = default);
    Task ClearInstanceGameOutputLogAsync(Guid instanceId, CancellationToken cancellationToken = default);
    void CancelInstall(Guid profileId);
}
