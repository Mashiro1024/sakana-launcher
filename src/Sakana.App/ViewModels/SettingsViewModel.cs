using Sakana.Application;
using Sakana.Domain;

namespace Sakana.App.ViewModels;

public sealed class SettingsViewModel(ILauncherService launcherService) : ViewModelBase
{
    public string DataDirectory => launcherService.DataDirectory;

    public ThemePreference Theme { get; private set; } = ThemePreference.Dark;

    public GlobalLaunchSettings GlobalLaunchSettings { get; private set; } = GlobalLaunchSettings.Default;

    public int MaxConcurrentDownloads { get; private set; } = LauncherSettings.Default.MaxConcurrentDownloads;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await launcherService.GetSettingsAsync(cancellationToken);
        Theme = settings.Theme;
        GlobalLaunchSettings = settings.GlobalLaunchSettings;
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(GlobalLaunchSettings));
        OnPropertyChanged(nameof(MaxConcurrentDownloads));
    }

    public async Task SaveThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default)
    {
        await launcherService.SaveThemeAsync(theme, cancellationToken);
        Theme = theme;
        OnPropertyChanged(nameof(Theme));
    }

    public async Task SaveGlobalLaunchSettingsAsync(GlobalLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        await launcherService.SaveGlobalLaunchSettingsAsync(settings, cancellationToken);
        GlobalLaunchSettings = settings;
        OnPropertyChanged(nameof(GlobalLaunchSettings));
    }

    public async Task SaveMaxConcurrentDownloadsAsync(int maxConcurrentDownloads, CancellationToken cancellationToken = default)
    {
        await launcherService.SaveMaxConcurrentDownloadsAsync(maxConcurrentDownloads, cancellationToken);
        MaxConcurrentDownloads = maxConcurrentDownloads;
        OnPropertyChanged(nameof(MaxConcurrentDownloads));
    }

    public Task<IReadOnlyList<JavaInstallation>> DetectJavaInstallationsAsync(CancellationToken cancellationToken = default) =>
        launcherService.DetectJavaInstallationsAsync(cancellationToken);

    public Task<JavaInstallation> InstallJavaRuntimeAsync(
        int majorVersion,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        launcherService.InstallJavaRuntimeAsync(majorVersion, progress, cancellationToken);
}
