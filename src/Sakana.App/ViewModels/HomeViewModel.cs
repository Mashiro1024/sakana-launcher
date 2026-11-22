using System.Collections.ObjectModel;
using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Dispatching;

namespace Sakana.App.ViewModels;

public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly ILauncherService _launcherService;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _hasProfiles;
    private string _profileCountText = "你的实例配置会显示在这里。";
    private bool _disposed;

    public HomeViewModel(ILauncherService launcherService, DispatcherQueue dispatcherQueue)
    {
        _launcherService = launcherService;
        _dispatcherQueue = dispatcherQueue;
        _launcherService.GameProcessStatusChanged += OnGameProcessStatusChanged;
    }

    public ObservableCollection<InstanceProfile> Profiles { get; } = [];
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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Profiles.Clear();
        foreach (var profile in await _launcherService.ListProfilesAsync(cancellationToken))
        {
            Profiles.Add(profile);
        }

        HasProfiles = Profiles.Count > 0;
        ProfileCountText = HasProfiles
            ? $"共 {Profiles.Count} 个实例配置"
            : "你的实例配置会显示在这里。";

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
