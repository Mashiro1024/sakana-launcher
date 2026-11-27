using System.Collections.ObjectModel;
using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Sakana.App.ViewModels;

public sealed record HomeInstanceSummary(
    Guid Id,
    string Name,
    string VersionLabel,
    string StateLabel,
    Visibility ReadyVisibility,
    Visibility PendingVisibility);

public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly ILauncherService _launcherService;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _hasProfiles;
    private string _profileCountText = "你的实例配置会显示在这里。";
    private string _readySummaryText = "创建或导入实例后即可快速启动。";
    private bool _disposed;

    public HomeViewModel(ILauncherService launcherService, DispatcherQueue dispatcherQueue)
    {
        _launcherService = launcherService;
        _dispatcherQueue = dispatcherQueue;
        _launcherService.GameProcessStatusChanged += OnGameProcessStatusChanged;
    }

    public ObservableCollection<InstanceProfile> Profiles { get; } = [];
    public ObservableCollection<HomeInstanceSummary> ProfileSummaries { get; } = [];
    public ObservableCollection<LaunchHistoryCardViewModel> RecentLaunches { get; } = [];
    public ObservableCollection<ActiveGameProcessViewModel> ActiveProcesses { get; } = [];

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

    public string ProfileCountText
    {
        get => _profileCountText;
        private set
        {
            if (_profileCountText == value) return;
            _profileCountText = value;
            OnPropertyChanged();
        }
    }

    public string ReadySummaryText
    {
        get => _readySummaryText;
        private set
        {
            if (_readySummaryText == value) return;
            _readySummaryText = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Profiles.Clear();
        ProfileSummaries.Clear();
        var listed = await _launcherService.ListProfilesAsync(cancellationToken);
        var readyCount = 0;

        foreach (var profile in listed)
        {
            Profiles.Add(profile);
            var isReady = profile.State == InstanceProfileState.Installed;
            if (isReady)
            {
                readyCount++;
            }

            var versionLabel = profile.LoaderType switch
            {
                MinecraftLoaderType.Fabric => $"Minecraft {profile.VersionId} · Fabric",
                MinecraftLoaderType.Quilt => $"Minecraft {profile.VersionId} · Quilt",
                MinecraftLoaderType.Forge => $"Minecraft {profile.VersionId} · Forge",
                MinecraftLoaderType.NeoForge => $"Minecraft {profile.VersionId} · NeoForge",
                _ => $"Minecraft {profile.VersionId}"
            };

            var stateLabel = profile.State switch
            {
                InstanceProfileState.Installed => "环境已就绪",
                InstanceProfileState.Installing => "正在安装",
                InstanceProfileState.Failed => "安装失败",
                InstanceProfileState.Cancelled => "已取消",
                _ => "配置待安装"
            };

            ProfileSummaries.Add(new HomeInstanceSummary(
                profile.Id,
                profile.Name,
                versionLabel,
                stateLabel,
                isReady ? Visibility.Visible : Visibility.Collapsed,
                isReady ? Visibility.Collapsed : Visibility.Visible));
        }

        HasProfiles = Profiles.Count > 0;
        ProfileCountText = HasProfiles
            ? $"共 {Profiles.Count} 个实例配置"
            : "你的实例配置会显示在这里。";
        ReadySummaryText = HasProfiles
            ? $"{readyCount} / {Profiles.Count} 个实例已安装就绪，可在实例库直接启动。"
            : "创建或导入实例后，可在实例库一键管理与启动。";

        ActiveProcesses.Clear();
        foreach (var process in _launcherService.GetActiveGameProcesses())
        {
            ActiveProcesses.Add(new ActiveGameProcessViewModel(process));
        }

        await RefreshLaunchHistoryAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcherService.GameProcessStatusChanged -= OnGameProcessStatusChanged;
    }

    private async Task RefreshLaunchHistoryAsync(CancellationToken cancellationToken = default)
    {
        var runningIds = _launcherService.GetActiveGameProcesses()
            .Select(process => process.InstanceId)
            .ToHashSet();
        var profilesById = Profiles.ToDictionary(profile => profile.Id, profile => profile.Name);
        var history = await _launcherService.GetLaunchHistoryAsync(12, cancellationToken);

        RecentLaunches.Clear();
        foreach (var entry in history)
        {
            var instanceName = profilesById.TryGetValue(entry.InstanceId, out var name)
                ? name
                : "实例配置";
            RecentLaunches.Add(new LaunchHistoryCardViewModel(
                entry,
                instanceName,
                runningIds.Contains(entry.InstanceId) && entry.EndedAtUtc is null));
        }
    }

    private void OnGameProcessStatusChanged(MinecraftGameProcessStatus status)
    {
        _dispatcherQueue.TryEnqueue(() => ApplyGameProcessStatus(status));
    }

    private void ApplyGameProcessStatus(MinecraftGameProcessStatus status)
    {
        if (_disposed)
        {
            return;
        }

        var current = ActiveProcesses.FirstOrDefault(process => process.InstanceId == status.InstanceId);
        if (status.State == MinecraftGameProcessState.Running)
        {
            if (current is null)
            {
                ActiveProcesses.Add(new ActiveGameProcessViewModel(status));
            }
        }
        else if (current is not null)
        {
            ActiveProcesses.Remove(current);
        }

        _ = RefreshLaunchHistoryAfterProcessChangeAsync();
    }

    private async Task RefreshLaunchHistoryAfterProcessChangeAsync()
    {
        try
        {
            await RefreshLaunchHistoryAsync();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync(
                "home.launch_history_refresh_failed",
                "Could not refresh recent game launches after a process state change.",
                exception);
        }
    }
}
