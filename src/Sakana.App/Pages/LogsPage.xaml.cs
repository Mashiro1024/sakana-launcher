using Sakana.Application;
using Sakana.App.ViewModels;
using Sakana.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace Sakana.App.Pages;

public sealed partial class LogsPage : Page
{
    private const int MaxVisibleLines = 5000;
    private readonly ILauncherService _launcherService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<MinecraftGameProcessOutput> _loadedEntries = [];
    private bool _isLoading;
    private long _loadGeneration;

    public LogsPage() : this(App.CurrentApp.LauncherService)
    {
    }

    public LogsPage(ILauncherService launcherService)
    {
        _launcherService = launcherService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        _launcherService.GameOutputReceived += OnGameOutputReceived;
        _launcherService.GameProcessStatusChanged += OnGameProcessStatusChanged;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public ObservableCollection<GameOutputLineViewModel> VisibleLogLines { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _isLoading = true;
            var profiles = await _launcherService.ListProfilesAsync();
            InstancePicker.ItemsSource = profiles;
            var activeInstanceIds = _launcherService.GetActiveGameProcesses().Select(item => item.InstanceId).ToHashSet();
            InstancePicker.SelectedItem = profiles.FirstOrDefault(item => activeInstanceIds.Contains(item.Id))
                ?? profiles.FirstOrDefault();
            _isLoading = false;
            ClearLogButtonState();
            await LoadSelectedLogAsync();
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("logs.load_failed", "Could not load instance game logs.", exception);
            LogStatusText.Text = $"无法加载日志：{exception.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _launcherService.GameOutputReceived -= OnGameOutputReceived;
        _launcherService.GameProcessStatusChanged -= OnGameProcessStatusChanged;
    }

    private async void InstancePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            await LoadSelectedLogAsync();
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadSelectedLogAsync();

    private async Task LoadSelectedLogAsync()
    {
        if (InstancePicker.SelectedItem is not InstanceProfile profile)
        {
            _loadedEntries.Clear();
            ApplySearch();
            LogStatusText.Text = "实例库中还没有实例。";
            ClearLogButtonState();
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        LogStatusText.Text = "正在读取日志…";
        try
        {
            var entries = await _launcherService.ReadInstanceGameOutputLogAsync(profile.Id, MaxVisibleLines);

            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            _loadedEntries.Clear();
            _loadedEntries.AddRange(entries.TakeLast(MaxVisibleLines));
            ApplySearch();
            var active = _launcherService.GetActiveGameProcesses().Any(item => item.InstanceId == profile.Id);
            LogStatusText.Text = $"{profile.Name} · {_loadedEntries.Count:N0} 行{(active ? " · 正在运行" : "")}";
            ClearLogButtonState();
            if (AutoScrollToggle.IsOn)
            {
                ScrollToTail();
            }
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("logs.read_failed", "Could not read the selected instance console log.", exception);
            LogStatusText.Text = $"读取日志失败：{exception.Message}";
        }
    }

    private static string GetEntryKey(MinecraftGameProcessOutput entry) =>
        $"{entry.Output.Timestamp.UtcDateTime.Ticks}|{entry.Output.Stream}|{entry.Output.Text}";

    private void OnGameOutputReceived(MinecraftGameProcessOutput entry)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (InstancePicker.SelectedItem is not InstanceProfile profile || profile.Id != entry.InstanceId)
            {
                return;
            }

            if (_loadedEntries.Count > 0 && GetEntryKey(_loadedEntries[^1]) == GetEntryKey(entry))
            {
                return;
            }

            _loadedEntries.Add(entry);
            if (_loadedEntries.Count > MaxVisibleLines)
            {
                _loadedEntries.RemoveAt(0);
            }
            ApplySearch();
            LogStatusText.Text = $"{profile.Name} · {_loadedEntries.Count:N0} 行 · 正在运行";
            if (AutoScrollToggle.IsOn)
            {
                _dispatcherQueue.TryEnqueue(ScrollToTail);
            }
        });
    }

    private void OnGameProcessStatusChanged(MinecraftGameProcessStatus status)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (InstancePicker.SelectedItem is not InstanceProfile profile || profile.Id != status.InstanceId)
            {
                return;
            }

            LogStatusText.Text = status.State == MinecraftGameProcessState.Running
                ? $"{profile.Name} · {_loadedEntries.Count:N0} 行 · 正在运行"
                : $"{profile.Name} · {_loadedEntries.Count:N0} 行 · 游戏已退出（退出代码 {status.ExitCode?.ToString() ?? "未知"}）";
            ClearLogButtonState();
        });
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        var query = SearchInput?.Text.Trim();
        var profile = InstancePicker?.SelectedItem as InstanceProfile;
        VisibleLogLines.Clear();
        foreach (var entry in _loadedEntries.Where(entry =>
                     string.IsNullOrEmpty(query) || entry.Output.Text.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleLogLines.Add(new GameOutputLineViewModel(entry, profile?.Name ?? $"Minecraft {entry.VersionId}"));
        }

        if (EmptyLogText is not null)
        {
            EmptyLogText.Visibility = VisibleLogLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLogText.Text = _loadedEntries.Count == 0
                ? "这个实例还没有保存过游戏日志。启动 Minecraft 后，控制台输出会显示在这里。"
                : "没有找到匹配的日志行。";
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (VisibleLogLines.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, VisibleLogLines.Select(line => line.Text)));
        Clipboard.SetContent(package);
        LogStatusText.Text = $"已复制 {VisibleLogLines.Count:N0} 行到剪贴板。";
    }

    private async void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (InstancePicker.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        if (_launcherService.GetActiveGameProcesses().Any(item => item.InstanceId == profile.Id))
        {
            LogStatusText.Text = "请先停止游戏，再清空日志。";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "清空游戏日志？",
            Content = $"将删除“{profile.Name}”保存的 Sakana 控制台日志。此操作无法撤销。",
            PrimaryButtonText = "清空日志",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _launcherService.ClearInstanceGameOutputLogAsync(profile.Id);
            _loadedEntries.Clear();
            ApplySearch();
            LogStatusText.Text = "已清空此实例的日志。";
        }
        catch (Exception exception)
        {
            LogStatusText.Text = $"清空失败：{exception.Message}";
        }
    }

    private void ClearLogButtonState()
    {
        var running = InstancePicker.SelectedItem is InstanceProfile profile &&
                      _launcherService.GetActiveGameProcesses().Any(item => item.InstanceId == profile.Id);
        ClearLogButton.IsEnabled = !running;
    }

    private void ScrollToTail_Click(object sender, RoutedEventArgs e) => ScrollToTail();

    private void ScrollToTail() => LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (InstancePicker.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var directory = Path.Combine(_launcherService.DataDirectory, "instances", InstanceDirectoryName.ForProfile(profile), "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
