using Sakana.Application;
using Sakana.App.ViewModels;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Sakana.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isLoading;

    public SettingsPage() : this(App.CurrentApp.LauncherService)
    {
    }

    public SettingsPage(ILauncherService launcherService)
    {
        ViewModel = new SettingsViewModel(launcherService);
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    public SettingsViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            DataDirectoryText.Text = ViewModel.DataDirectory;
            await ViewModel.LoadAsync();
            ApplyGlobalLaunchSettings(ViewModel.GlobalLaunchSettings);
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
        DetectedJavaCombo.IsEnabled = true;
        BrowseGlobalJavaButton.IsEnabled = enabled;
    }

    private void GlobalFullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        GlobalWindowWidthInput.IsEnabled = !GlobalFullscreenToggle.IsOn;
        GlobalWindowHeightInput.IsEnabled = !GlobalFullscreenToggle.IsOn;
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DetectedJavaCombo.IsEnabled = false;
            DetectedJavaCombo.Items.Clear();
            SavedInfoBar.Severity = InfoBarSeverity.Informational;
            SavedInfoBar.Message = "正在检测本机 Java 安装…";
            SavedInfoBar.IsOpen = true;
            var installations = await ViewModel.DetectJavaInstallationsAsync();
            foreach (var installation in installations)
            {
                DetectedJavaCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"Java {installation.MajorVersion} ({installation.Version}) · {installation.ExecutablePath}",
                    Tag = installation
                });
            }

            if (installations.Count == 0)
            {
                SavedInfoBar.Severity = InfoBarSeverity.Warning;
                SavedInfoBar.Message = "未检测到本机 Java。保持自动模式即可；安装游戏实例时 Sakana 会按需下载兼容版本。";
            }
            else
            {
                SavedInfoBar.Severity = InfoBarSeverity.Success;
                SavedInfoBar.Message = $"检测到 {installations.Count} 个 Java 安装。选择一个可全局指定，也可保留自动检测。";
            }

            SavedInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            await App.CurrentApp.Logger.ErrorAsync("settings.java_detection_failed", "Could not detect local Java installations.", exception);
            SavedInfoBar.Severity = InfoBarSeverity.Error;
            SavedInfoBar.Message = $"Java 检测失败：{exception.Message}";
            SavedInfoBar.IsOpen = true;
        }
        finally
        {
            DetectedJavaCombo.IsEnabled = true;
        }
    }

    private void DetectedJavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetectedJavaCombo.SelectedItem is not ComboBoxItem { Tag: JavaInstallation installation })
        {
            return;
        }

        GlobalJavaPathInput.Text = installation.ExecutablePath;
        GlobalCustomJavaToggle.IsOn = true;
    }

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
        DetectedJavaCombo.SelectedItem = null;
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
            SavedInfoBar.Message = "请选择一个检测到的 Java 版本或浏览 java.exe；也可以切回自动检测。";
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
