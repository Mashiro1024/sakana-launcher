using Sakana.Application;
using Sakana.App.ViewModels;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sakana.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage() : this(App.CurrentApp.LauncherService)
    {
    }

    public HomePage(ILauncherService launcherService)
    {
        ViewModel = new HomeViewModel(launcherService, DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        ViewModel.RecentLaunches.CollectionChanged += OnRecentLaunchesChanged;
        ViewModel.ActiveProcesses.CollectionChanged += OnActiveProcessesChanged;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public HomeViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAsync();
            EmptyProfilesState.Visibility = ViewModel.HasProfiles ? Visibility.Collapsed : Visibility.Visible;
            ProfileItems.Visibility = ViewModel.HasProfiles ? Visibility.Visible : Visibility.Collapsed;
            UpdateHistoryVisibility();
            UpdateActiveProcessVisibility();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("home.load_failed", "Could not load home page data.", exception);
            LoadErrorInfoBar.IsOpen = true;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RecentLaunches.CollectionChanged -= OnRecentLaunchesChanged;
        ViewModel.ActiveProcesses.CollectionChanged -= OnActiveProcessesChanged;
        ViewModel.Dispose();
    }

    private void OnRecentLaunchesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHistoryVisibility();

    private void OnActiveProcessesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateActiveProcessVisibility();

    private void UpdateHistoryVisibility()
    {
        var hasHistory = ViewModel.RecentLaunches.Count > 0;
        HistoryEmptyState.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryItems.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActiveProcessVisibility()
    {
        var hasActiveProcesses = ViewModel.ActiveProcesses.Count > 0;
        ActiveProcessEmptyState.Visibility = hasActiveProcesses ? Visibility.Collapsed : Visibility.Visible;
        ActiveProcessItems.Visibility = hasActiveProcesses ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateProfile_Click(object sender, RoutedEventArgs e) =>
        App.CurrentApp.RequestNavigation("library");

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) =>
        App.CurrentApp.RequestNavigation("library");
}
