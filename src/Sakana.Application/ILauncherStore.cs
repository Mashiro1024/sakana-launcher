using Sakana.Domain;

namespace Sakana.Application;

public interface ILauncherStore
{
    string DataDirectory { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<LauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
    Task SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceProfile>> ListProfilesAsync(CancellationToken cancellationToken = default);
    Task<InstanceProfile?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<bool> TryCreateProfileAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task SetInstanceOrganizationAsync(Guid profileId, string? groupName, string tagsJson, CancellationToken cancellationToken = default);
    Task SetProfileIconAsync(Guid profileId, string? iconFileName, CancellationToken cancellationToken = default);
    Task UpdateModrinthPackSourceAsync(Guid profileId, string packPath, string versionId, string versionNumber, string selectedOptionalFilesJson, CancellationToken cancellationToken = default);
    Task<bool> TryRenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetInstanceLaunchSettingsAsync(Guid profileId, InstanceLaunchSettings settings, CancellationToken cancellationToken = default);
    Task SetProfileStateAsync(Guid profileId, InstanceProfileState state, string? error, CancellationToken cancellationToken = default);
    Task AddLaunchHistoryAsync(MinecraftLaunchHistoryEntry entry, CancellationToken cancellationToken = default);
    Task CompleteLaunchHistoryAsync(Guid launchId, DateTimeOffset endedAtUtc, int? exitCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MinecraftLaunchHistoryEntry>> ListLaunchHistoryAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task RecoverInterruptedInstallationsAsync(CancellationToken cancellationToken = default);
}
