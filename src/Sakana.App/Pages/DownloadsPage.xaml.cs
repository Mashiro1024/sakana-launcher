using Sakana.Application;
using Sakana.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sakana.App.Pages;

public sealed partial class DownloadsPage : Page
{
    public DownloadsPage() : this(App.CurrentApp.LauncherService)
    {
    }

    public DownloadsPage(ILauncherService launcherService)
    {
        ViewModel = new DownloadsViewModel(launcherService);
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public DownloadsViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAsync();
            UpdateEmptyState();
            LoadErrorInfoBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("downloads.load_failed", "Could not load installation status.", exception);
            LoadErrorInfoBar.IsOpen = true;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.Dispose();

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid profileId })
        {
            ViewModel.CancelInstall(profileId);
        }
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        JobsList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
    }
}
