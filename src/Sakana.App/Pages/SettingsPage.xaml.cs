using Sakana.Application;
using Sakana.App.ViewModels;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Windows.Storage.Pickers;

namespace Sakana.App.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly JavaRuntimeChoice[] JavaRuntimeChoices =
    [
        new(8, "适用于 Minecraft 1.16.5 及更早版本"),
        new(16, "适用于 Minecraft 1.17"),
        new(17, "适用于 Minecraft 1.18–1.20.4"),
        new(21, "适用于 Minecraft 1.20.5–1.21.x"),
        new(25, "适用于 Minecraft 26.1 及之后版本")
    ];

    private bool _isLoading;
    private CancellationTokenSource? _javaDownloadCancellation;
    private readonly List<Button> _javaDownloadButtons = [];

    public SettingsPage() : this(App.CurrentApp.LauncherService)
    {
    }

    public SettingsPage(ILauncherService launcherService)
    {
        ViewModel = new SettingsViewModel(launcherService);
        InitializeComponent();
        BuildJavaDownloadOptions();
        Loaded += Page_Loaded;
    }

    public SettingsViewModel ViewModel { get; }

    private async void OpenSettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string destination })
        {
            return;
        }

        ShowSettingsSection(destination);
        if (destination == "curseforge")
        {
            await RefreshCurseForgeApiKeyStatusAsync();
        }
        if (destination == "java-runtimes")
        {
            await DetectInstalledJavaAsync();
        }
    }

    private void BackToSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsSection("home");

    private void ShowSettingsSection(string destination)
    {
        SettingsHomePanel.Visibility = destination == "home" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = destination == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        JavaMemoryPanel.Visibility = destination == "java-memory" ? Visibility.Visible : Visibility.Collapsed;
        GameWindowPanel.Visibility = destination == "game-window" ? Visibility.Visible : Visibility.Collapsed;
        JavaRuntimesPanel.Visibility = destination == "java-runtimes" ? Visibility.Visible : Visibility.Collapsed;
        DownloadSettingsPanel.Visibility = destination == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = destination == "data" ? Visibility.Visible : Visibility.Collapsed;
        CurseForgePanel.Visibility = destination == "curseforge" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = destination == "about" ? Visibility.Visible : Visibility.Collapsed;

        var selectedPanel = destination switch
        {
            "appearance" => AppearancePanel,
            "java-memory" => JavaMemoryPanel,
            "game-window" => GameWindowPanel,
            "java-runtimes" => JavaRuntimesPanel,
            "downloads" => DownloadSettingsPanel,
            "data" => DataPanel,
            "curseforge" => CurseForgePanel,
            "about" => AboutPanel,
            _ => SettingsHomePanel
        };

        var visual = ElementCompositionPreview.GetElementVisual(selectedPanel);
        visual.Opacity = 0;
        var fadeIn = visual.Compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fadeIn);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            DataDirectoryText.Text = ViewModel.DataDirectory;
            await ViewModel.LoadAsync();
            ApplyGlobalLaunchSettings(ViewModel.GlobalLaunchSettings);
            DownloadConcurrencySlider.Value = ViewModel.MaxConcurrentDownloads;
            AppVersionText.Text = $"Sakana {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
            ThemeCombo.SelectedItem = ThemeCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), ViewModel.Theme.ToString(), StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.load_failed", "Could not load launcher settings.", exception);
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "无法读取设置。请查看 Sakana 日志。";
            SavedInfoBar.IsOpen = true;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || ThemeCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<ThemePreference>(item.Tag?.ToString(), out var theme))
        {
            return;
        }

        try
        {
            await ViewModel.SaveThemeAsync(theme);
            await App.CurrentApp.Logger.InformationAsync("settings.theme_changed", $"Theme changed to {theme}.");
            App.CurrentApp.NotifyThemeChanged(theme);
            SavedInfoBar.Severity = InfoBarSeverity.Success;
            SavedInfoBar.Message = "设置已保存。";
            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.save_failed", "Could not save launcher settings.", exception);
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "无法保存设置。请查看 Sakana 日志。";
            SavedInfoBar.IsOpen = true;
        }
    }

    private void ApplyGlobalLaunchSettings(GlobalLaunchSettings settings)
    {
        GlobalAutomaticMemoryToggle.IsOn = settings.UseAutomaticMemory;
        GlobalMinMemoryInput.Value = settings.MinMemoryMb;
        GlobalMaxMemoryInput.Value = settings.MaxMemoryMb;
        GlobalMinMemoryInput.IsEnabled = !settings.UseAutomaticMemory;
        GlobalMaxMemoryInput.IsEnabled = !settings.UseAutomaticMemory;
        GlobalCustomJavaToggle.IsOn = !string.IsNullOrWhiteSpace(settings.JavaExecutablePath);
        GlobalJavaPathInput.Text = settings.JavaExecutablePath ?? string.Empty;
        GlobalJavaArgumentsInput.Text = settings.CustomJavaArguments;
        GlobalEnvironmentVariablesInput.Text = FormatEnvironmentVariables(settings.CustomEnvironmentVariablesJson);
        GlobalFullscreenToggle.IsOn = settings.Fullscreen;
        GlobalWindowWidthInput.Value = settings.WindowWidth;
        GlobalWindowHeightInput.Value = settings.WindowHeight;
        GlobalWindowWidthInput.IsEnabled = !settings.Fullscreen;
        GlobalWindowHeightInput.IsEnabled = !settings.Fullscreen;
        SetCustomJavaFieldsEnabled(GlobalCustomJavaToggle.IsOn);
    }

    private void GlobalAutomaticMemoryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        GlobalMinMemoryInput.IsEnabled = !GlobalAutomaticMemoryToggle.IsOn;
        GlobalMaxMemoryInput.IsEnabled = !GlobalAutomaticMemoryToggle.IsOn;
    }

    private void GlobalCustomJavaToggle_Toggled(object sender, RoutedEventArgs e) =>
        SetCustomJavaFieldsEnabled(GlobalCustomJavaToggle.IsOn);

    private void SetCustomJavaFieldsEnabled(bool enabled)
    {
        GlobalJavaPathInput.IsEnabled = enabled;
        BrowseGlobalJavaButton.IsEnabled = enabled;
    }

    private void GlobalFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        GlobalWindowWidthInput.IsEnabled = !GlobalFullscreenToggle.IsOn;
        GlobalWindowHeightInput.IsEnabled = !GlobalFullscreenToggle.IsOn;
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await DetectInstalledJavaAsync();

    private async Task DetectInstalledJavaAsync()
    {
        try
        {
            DetectJavaButton.IsEnabled = false;
            JavaDetectionProgressRing.IsActive = true;
            JavaDetectionStatusText.Text = "正在检查 PATH、注册表、常见安装目录和 Sakana 运行时…";
            InstalledJavaListPanel.Children.Clear();
            var installations = await ViewModel.DetectJavaInstallationsAsync();
            foreach (var installation in installations)
            {
                AddInstalledJavaCard(installation);
            }

            JavaDetectionStatusText.Text = installations.Count == 0
                ? "没有检测到已安装的 Java。可以在下方下载 Minecraft 常用运行时，或继续使用自动匹配。"
                : $"找到 {installations.Count} 个 Java 安装。可逐个查看路径，并选择一个用于全局启动设置。";
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.java_detection_failed", "Could not detect local Java installations.", exception);
            JavaDetectionStatusText.Text = $"Java 检测失败：{exception.Message}";
        }
        finally
        {
            JavaDetectionProgressRing.IsActive = false;
            DetectJavaButton.IsEnabled = true;
        }
    }

    private void AddInstalledJavaCard(JavaInstallation installation)
    {
        if (InstalledJavaListPanel.Children.OfType<Border>()
            .Any(item => string.Equals(item.Tag as string, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var border = new Border
        {
            Tag = installation.ExecutablePath,
            Padding = new Thickness(14, 10, 10, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaSurfaceRaisedBrush"]
        };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = $"Java {installation.MajorVersion} · {installation.Version}",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaTextBrush"]
        });
        details.Children.Add(new TextBlock
        {
            Text = installation.ExecutablePath,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
        });
        var selectButton = new Button { Content = "用于全局设置", Tag = installation, VerticalAlignment = VerticalAlignment.Center };
        selectButton.Click += SelectGlobalJava_Click;
        Grid.SetColumn(selectButton, 1);
        row.Children.Add(details);
        row.Children.Add(selectButton);
        border.Child = row;
        InstalledJavaListPanel.Children.Add(border);
    }

    private void SelectGlobalJava_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JavaInstallation installation }) return;
        GlobalJavaPathInput.Text = installation.ExecutablePath;
        GlobalCustomJavaToggle.IsOn = true;
        ShowSettingsSection("java-memory");
        SavedInfoBar.Severity = InfoBarSeverity.Informational;
        SavedInfoBar.Message = $"已选择 Java {installation.MajorVersion}。检查 Java 和内存设置后，点保存应用。";
        SavedInfoBar.IsOpen = true;
    }

    private void BuildJavaDownloadOptions()
    {
        foreach (var choice in JavaRuntimeChoices)
        {
            var border = new Border
            {
                Padding = new Thickness(14, 10, 10, 10),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaSurfaceBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaStrokeBrush"],
                BorderThickness = new Thickness(1)
            };
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock
            {
                Text = $"Java {choice.MajorVersion}",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaTextBrush"]
            });
            details.Children.Add(new TextBlock
            {
                Text = choice.Compatibility,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SakanaMutedBrush"]
            });
            var downloadButton = new Button { Content = "下载", Tag = choice.MajorVersion, VerticalAlignment = VerticalAlignment.Center };
            downloadButton.Click += DownloadJavaRuntime_Click;
            _javaDownloadButtons.Add(downloadButton);
            Grid.SetColumn(downloadButton, 1);
            row.Children.Add(details);
            row.Children.Add(downloadButton);
            border.Child = row;
            JavaDownloadOptionsPanel.Children.Add(border);
        }
    }

    private async void DownloadJavaRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int majorVersion } || _javaDownloadCancellation is not null) return;

        var cancellation = new CancellationTokenSource();
        _javaDownloadCancellation = cancellation;
        foreach (var button in _javaDownloadButtons) button.IsEnabled = false;
        JavaDownloadProgressBar.Visibility = Visibility.Visible;
        JavaDownloadProgressBar.IsIndeterminate = true;
        JavaDownloadProgressBar.Value = 0;
        CancelJavaRuntimeDownloadButton.Visibility = Visibility.Visible;
        JavaDownloadStatusText.Text = $"正在准备 Java {majorVersion} 官方运行时…";

        var progress = new Progress<InstallationProgress>(update =>
        {
            JavaDownloadProgressBar.IsIndeterminate = update.TotalBytes <= 0;
            JavaDownloadProgressBar.Value = update.PercentComplete;
            var phase = update.Stage == InstallationStage.Verifying ? "校验" : "下载";
            JavaDownloadStatusText.Text = $"Java {majorVersion}：{phase} {update.FilesCompleted}/{update.FilesTotal} 个文件（{update.PercentComplete:F0}%）";
        });

        try
        {
            var installation = await ViewModel.InstallJavaRuntimeAsync(majorVersion, progress, cancellation.Token);
            AddInstalledJavaCard(installation);
            JavaDetectionStatusText.Text = "已加入 Sakana 管理的运行时。自动匹配模式会按 Minecraft 版本使用兼容的主版本。";
            JavaDownloadStatusText.Text = $"Java {majorVersion} 已安装：{installation.ExecutablePath}";
            JavaDownloadProgressBar.IsIndeterminate = false;
            JavaDownloadProgressBar.Value = 100;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            JavaDownloadStatusText.Text = $"Java {majorVersion} 下载已取消。";
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.java_runtime_download_failed", $"Could not install managed Java {majorVersion}.", exception);
            JavaDownloadStatusText.Text = $"Java {majorVersion} 下载失败：{exception.Message}";
        }
        finally
        {
            _javaDownloadCancellation = null;
            cancellation.Dispose();
            foreach (var button in _javaDownloadButtons) button.IsEnabled = true;
            CancelJavaRuntimeDownloadButton.Visibility = Visibility.Collapsed;
            JavaDownloadProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelJavaRuntimeDownload_Click(object sender, RoutedEventArgs e) => _javaDownloadCancellation?.Cancel();

    private async void OpenGitHubLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string destination }) return;
        var url = destination switch
        {
            "repository" => "https://github.com/Mashiro1024/sakana-launcher",
            "license" => "https://github.com/Mashiro1024/sakana-launcher/blob/main/LICENSE",
            "releases" => "https://github.com/Mashiro1024/sakana-launcher/releases",
            "issues" => "https://github.com/Mashiro1024/sakana-launcher/issues",
            "curseforge-apply" => "https://support.curseforge.com/support/solutions/articles/9000208346",
            "curseforge-terms" => "https://support.curseforge.com/support/solutions/articles/9000207405-curse-forge-3rd-party-api-terms-and-conditions",
            _ => null
        };
        if (url is null) return;

        try
        {
            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            if (!launched)
            {
                SavedInfoBar.Severity = InfoBarSeverity.Warning;
                SavedInfoBar.Message = "无法打开链接。请检查 Windows 的默认浏览器设置。";
                SavedInfoBar.IsOpen = true;
            }
        }
        catch (Exception exception)
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = $"无法打开链接：{exception.Message}";
            SavedInfoBar.IsOpen = true;
        }
    }

    private async Task RefreshCurseForgeApiKeyStatusAsync()
    {
        try
        {
            var apiKey = await App.CurrentApp.CurseForgeApiKeyStore.ReadAsync();
            CurseForgeApiKeyInput.Password = string.Empty;
            CurseForgeKeyStatusInfoBar.Severity = InfoBarSeverity.Informational;
            CurseForgeKeyStatusInfoBar.Message = string.IsNullOrWhiteSpace(apiKey)
                ? "尚未配置 API Key。仍可导入 CurseForge ZIP 并安装基础游戏、加载器和覆盖文件；无法通过 API 获取的 Mod 会提供官方文件页，供你手动下载并导入。"
                : "已配置 API Key。密钥不会显示在界面中，并使用当前 Windows 用户的 DPAPI 加密。";
        }
        catch (Exception exception)
        {
            CurseForgeKeyStatusInfoBar.Severity = InfoBarSeverity.Error;
            CurseForgeKeyStatusInfoBar.Message = $"无法读取 CurseForge API Key：{exception.Message}";
        }
    }

    private async void SaveCurseForgeApiKey_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = CurseForgeApiKeyInput.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 2048 || apiKey.Any(char.IsControl))
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "请输入有效的 CurseForge API Key。";
            SavedInfoBar.IsOpen = true;
            return;
        }

        try
        {
            await App.CurrentApp.CurseForgeApiKeyStore.WriteAsync(apiKey);
            CurseForgeApiKeyInput.Password = string.Empty;
            await RefreshCurseForgeApiKeyStatusAsync();
            SavedInfoBar.Severity = InfoBarSeverity.Success;
            SavedInfoBar.Message = "CurseForge API Key 已加密保存。";
            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = $"无法保存 CurseForge API Key：{exception.Message}";
            SavedInfoBar.IsOpen = true;
        }
    }

    private async void RemoveCurseForgeApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.CurrentApp.CurseForgeApiKeyStore.ClearAsync();
            CurseForgeApiKeyInput.Password = string.Empty;
            await RefreshCurseForgeApiKeyStatusAsync();
            SavedInfoBar.Severity = InfoBarSeverity.Success;
            SavedInfoBar.Message = "CurseForge API Key 已从此设备移除。";
            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = $"无法移除 CurseForge API Key：{exception.Message}";
            SavedInfoBar.IsOpen = true;
        }
    }

    private sealed record JavaRuntimeChoice(int MajorVersion, string Compatibility);

    private async void BrowseGlobalJava_Click(object sender, RoutedEventArgs e)
    {
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.CurrentApp.WindowHandle);
        var picker = new FileOpenPicker(windowId);
        picker.FileTypeFilter.Add(".exe");
        picker.CommitButtonText = "选择 Java";
        var selectedFile = await picker.PickSingleFileAsync();
        if (selectedFile is not null)
        {
            GlobalJavaPathInput.Text = selectedFile.Path;
            GlobalCustomJavaToggle.IsOn = true;
        }
    }

    private void UseAutomaticJava_Click(object sender, RoutedEventArgs e)
    {
        GlobalJavaPathInput.Text = string.Empty;
        GlobalCustomJavaToggle.IsOn = false;
    }

    private async void SaveGlobalLaunchSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!IsWholeNumber(GlobalMinMemoryInput.Value) || !IsWholeNumber(GlobalMaxMemoryInput.Value) ||
            !IsWholeNumber(GlobalWindowWidthInput.Value) || !IsWholeNumber(GlobalWindowHeightInput.Value))
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "内存和窗口尺寸都必须是整数。";
            SavedInfoBar.IsOpen = true;
            return;
        }

        if (GlobalCustomJavaToggle.IsOn && string.IsNullOrWhiteSpace(GlobalJavaPathInput.Text))
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "请选择一个 Java 运行时，或切回自动匹配。";
            SavedInfoBar.IsOpen = true;
            return;
        }

        try
        {
            var settings = new GlobalLaunchSettings(
                GlobalAutomaticMemoryToggle.IsOn,
                (int)GlobalMinMemoryInput.Value,
                (int)GlobalMaxMemoryInput.Value,
                GlobalCustomJavaToggle.IsOn ? GlobalJavaPathInput.Text.Trim() : null,
                GlobalJavaArgumentsInput.Text,
                ToEnvironmentVariablesJson(GlobalEnvironmentVariablesInput.Text),
                GlobalFullscreenToggle.IsOn,
                (int)GlobalWindowWidthInput.Value,
                (int)GlobalWindowHeightInput.Value);
            await ViewModel.SaveGlobalLaunchSettingsAsync(settings);
            await App.CurrentApp.Logger.InformationAsync("settings.global_launch_saved", "Global launch settings were updated.");
            SavedInfoBar.Severity = InfoBarSeverity.Success;
            SavedInfoBar.Message = "全局启动设置已保存；跟随全局设置的实例会使用新配置。";
            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.global_launch_save_failed", "Could not save global launch settings.", exception);
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = exception.Message;
            SavedInfoBar.IsOpen = true;
        }
    }

    private void DownloadConcurrencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        DownloadConcurrencyValueText.Text = $"{value} 个文件";
    }

    private async void SaveDownloadConcurrency_Click(object sender, RoutedEventArgs e)
    {
        if (!IsWholeNumber(DownloadConcurrencySlider.Value) || DownloadConcurrencySlider.Value is < 1 or > 256)
        {
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = "同时下载文件数必须是 1 到 256 之间的整数。";
            SavedInfoBar.IsOpen = true;
            return;
        }

        try
        {
            await ViewModel.SaveMaxConcurrentDownloadsAsync((int)DownloadConcurrencySlider.Value);
            await App.CurrentApp.Logger.InformationAsync(
                "settings.download_concurrency_saved",
                $"Maximum concurrent downloads set to {ViewModel.MaxConcurrentDownloads}.");
            SavedInfoBar.Severity = InfoBarSeverity.Success;
            SavedInfoBar.Message = $"已保存：最多同时下载 {ViewModel.MaxConcurrentDownloads} 个文件。";
            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.download_concurrency_save_failed", "Could not save download concurrency.", exception);
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = exception.Message;
            SavedInfoBar.IsOpen = true;
        }
    }

    private static bool IsWholeNumber(double value) => double.IsFinite(value) && value == Math.Truncate(value);

    private static string FormatEnvironmentVariables(string json)
    {
        try
        {
            var variables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
            return string.Join(Environment.NewLine, variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private static string ToEnvironmentVariablesJson(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new FormatException($"环境变量格式无效：{line}。请按 KEY=VALUE 每行填写一项。");
            var name = line[..separator].Trim();
            if (!variables.TryAdd(name, line[(separator + 1)..])) throw new FormatException($"环境变量名称重复：{name}。");
        }

        return System.Text.Json.JsonSerializer.Serialize(variables);
    }

}
