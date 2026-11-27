using Sakana.Domain;
using Sakana.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    private void NavView_PaneOpening(NavigationView sender, object args)
    {
        PaneBrandHeader.Visibility = Visibility.Visible;
        AccountFooterMeta.Visibility = Visibility.Visible;
        AccountFooterButton.Margin = new Thickness(8, 0, 8, 4);
        AccountFooterButton.Padding = new Thickness(8, 7, 8, 7);
    }

    private void NavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        PaneBrandHeader.Visibility = Visibility.Collapsed;
        AccountFooterMeta.Visibility = Visibility.Collapsed;
        AccountFooterButton.Margin = new Thickness(4, 0, 4, 4);
        AccountFooterButton.Padding = new Thickness(4);
    }

    private void AccountFooter_Click(object sender, RoutedEventArgs e)
    {
        OnNavigationRequested("accounts");
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
        _ = RefreshAccountFooterAsync();
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

    private async Task RefreshAccountFooterAsync()
    {
        try
        {
            var accounts = await App.CurrentApp.MinecraftAccountService.GetAccountsAsync(LauncherSettings.MicrosoftOAuthClientId);
            var active = accounts.FirstOrDefault(account => account.IsActive) ?? accounts.FirstOrDefault();
            if (active is null)
            {
                AccountFooterInitial.Text = "?";
                AccountFooterName.Text = "未登录账户";
                AccountFooterStatus.Text = "点击连接 Microsoft";
                AccountDotMuted.Visibility = Visibility.Visible;
                AccountDotReady.Visibility = Visibility.Collapsed;
                AccountDotWarning.Visibility = Visibility.Collapsed;
                return;
            }

            AccountFooterInitial.Text = string.IsNullOrWhiteSpace(active.PlayerName)
                ? "M"
                : active.PlayerName[..1].ToUpperInvariant();
            AccountFooterName.Text = active.PlayerName;
            if (active.NeedsVerification)
            {
                AccountFooterStatus.Text = "需要重新验证";
                AccountDotMuted.Visibility = Visibility.Collapsed;
                AccountDotReady.Visibility = Visibility.Collapsed;
                AccountDotWarning.Visibility = Visibility.Visible;
            }
            else
            {
                AccountFooterStatus.Text = "Microsoft 已就绪";
                AccountDotMuted.Visibility = Visibility.Collapsed;
                AccountDotReady.Visibility = Visibility.Visible;
                AccountDotWarning.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Keep shell navigation resilient if credential store is temporarily unavailable.
        }
    }

    private static ElementTheme ToElementTheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.System => ElementTheme.Default,
        _ => ElementTheme.Dark
    };
}
