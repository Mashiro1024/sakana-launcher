using System.Collections.ObjectModel;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.App.ViewModels;

public sealed class DownloadsViewModel : ViewModelBase, IDisposable
{
    private readonly ILauncherService _launcherService;
    private bool _hasItems;

    public DownloadsViewModel(ILauncherService launcherService)
    {
        _launcherService = launcherService;
        _launcherService.InstallationProgressChanged += OnInstallationProgressChanged;
    }

    public ObservableCollection<InstanceCardViewModel> Items { get; } = [];

    public bool HasItems
    {
        get => _hasItems;
        private set
        {
            if (_hasItems == value) return;
            _hasItems = value;
            OnPropertyChanged();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Items.Clear();
        var profiles = await _launcherService.ListProfilesAsync(cancellationToken);
        foreach (var profile in profiles.Where(profile => profile.State != InstanceProfileState.ProfileOnly))
        {
            Items.Add(new InstanceCardViewModel(profile));
        }

        HasItems = Items.Count > 0;
    }

    public void CancelInstall(Guid profileId) => _launcherService.CancelInstall(profileId);

    public void Dispose() => _launcherService.InstallationProgressChanged -= OnInstallationProgressChanged;

    private void OnInstallationProgressChanged(InstallationProgress progress)
    {
        var item = Items.FirstOrDefault(candidate => candidate.Id == progress.InstanceId);
        item?.ApplyProgress(progress);
    }
}
