using Sakana.Domain;
using Sakana.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Sakana.App;

public sealed partial class MainWindow : Window
{
    private string? _currentDestination;

    public MainWindow(ThemePreference theme)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1360, 860));

        RootGrid.RequestedTheme = ToElementTheme(theme);
        App.CurrentApp.NavigationRequested += OnNavigationRequested;
        App.CurrentApp.ThemeChanged += OnThemeChanged;
        ShowPage("home");
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowPage("settings");
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            ShowPage(item.Tag?.ToString() ?? "home");
        }
    }

    private void OnNavigationRequested(string destination)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(menuItem => string.Equals(menuItem.Tag?.ToString(), destination, StringComparison.Ordinal));

        if (item is not null)
        {
            if (!ReferenceEquals(NavView.SelectedItem, item))
            {
                NavView.SelectedItem = item;
            }
            else
            {
                ShowPage(destination);
            }
        }
    }

    private void OnThemeChanged(ThemePreference theme) => RootGrid.RequestedTheme = ToElementTheme(theme);

    private void ShowPage(string destination)
    {
        if (string.Equals(_currentDestination, destination, StringComparison.Ordinal))
        {
            return;
        }

        var pageType = destination switch
        {
            "home" => typeof(HomePage),
            "library" => typeof(LibraryPage),
            "downloads" => typeof(DownloadsPage),
            "logs" => typeof(LogsPage),
            "screenshots" => typeof(ScreenshotsPage),
            "accounts" => typeof(AccountsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        // Frame navigation runs the configured WinUI NavigationThemeTransition.
        if (NavFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo()))
        {
            _currentDestination = destination;
            // This shell has no back stack UI; discard history to keep navigation bounded.
            NavFrame.BackStack.Clear();
        }
    }

    private static ElementTheme ToElementTheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.System => ElementTheme.Default,
        _ => ElementTheme.Dark
    };
}
