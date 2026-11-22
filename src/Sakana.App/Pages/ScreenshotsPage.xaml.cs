using Sakana.App.ViewModels;
using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Sakana.App.Pages;

public sealed partial class ScreenshotsPage : Page
{
    private readonly ILauncherService _launcherService;
    private bool _isLoading;
    private long _refreshGeneration;

    public ScreenshotsPage() : this(App.CurrentApp.LauncherService)
    {
    }

    public ScreenshotsPage(ILauncherService launcherService)
    {
        _launcherService = launcherService;
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    public ObservableCollection<ScreenshotItemViewModel> Screenshots { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _isLoading = true;
            InstancePicker.ItemsSource = await _launcherService.ListProfilesAsync();
            InstancePicker.SelectedIndex = InstancePicker.Items.Count > 0 ? 0 : -1;
            _isLoading = false;
            await RefreshScreenshotsAsync();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("screenshots.load_failed", "Could not load Minecraft screenshot instances.", exception);
            EmptyTitle.Text = "无法读取截图";
            EmptyDescription.Text = exception.Message;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void InstancePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            await RefreshScreenshotsAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshScreenshotsAsync();

    private async Task RefreshScreenshotsAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        Screenshots.Clear();
        if (InstancePicker.SelectedItem is not InstanceProfile profile)
        {
            EmptyTitle.Text = "还没有实例";
            EmptyDescription.Text = "创建 Minecraft 实例后，游戏截图会按实例整理在这里。";
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = "正在读取截图…";
        EmptyDescription.Text = "Sakana 正在查找此实例的截图文件。";

        string[] files;
        try
        {
            files = await Task.Run(() =>
            {
                var screenshotDirectory = Path.Combine(
                    _launcherService.DataDirectory,
                    "instances",
                    InstanceDirectoryName.ForProfile(profile),
                    "game",
                    "screenshots");
                if (!Directory.Exists(screenshotDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(screenshotDirectory)
                    .Where(path =>
                    {
                        var extension = Path.GetExtension(path);
                        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();
            });
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                EmptyTitle.Text = "无法读取截图";
                EmptyDescription.Text = exception.Message;
                EmptyState.Visibility = Visibility.Visible;
            }
            return;
        }

        if (generation != Volatile.Read(ref _refreshGeneration))
        {
            return;
        }

        foreach (var path in files)
        {
            try
            {
                Screenshots.Add(new ScreenshotItemViewModel(path));
            }
            catch (Exception exception)
            {
                await App.CurrentApp.Logger.ErrorAsync("screenshots.image_failed", $"Could not preview screenshot {Path.GetFileName(path)}.", exception);
            }
        }

        EmptyState.Visibility = Screenshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = "暂无截图";
        EmptyDescription.Text = "在游戏中按 F2 截图，保存的图片会显示在这里。";
    }

    private async void ScreenshotGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScreenshotItemViewModel screenshot)
        {
            return;
        }

        var preview = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(screenshot.Path), UriKind.Absolute)),
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            MaxWidth = 1100,
            MaxHeight = 680
        };
        var dialog = new ContentDialog
        {
            Title = screenshot.FileName,
            Content = new ScrollViewer
            {
                Content = preview,
                MaxHeight = 700,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (InstancePicker.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var directory = Path.Combine(
            _launcherService.DataDirectory,
            "instances",
            InstanceDirectoryName.ForProfile(profile),
            "game",
            "screenshots");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
