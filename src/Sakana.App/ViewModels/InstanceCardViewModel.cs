using Sakana.Application;
using Sakana.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;

namespace Sakana.App.ViewModels;

public sealed class InstanceCardViewModel : ViewModelBase
{
    private InstanceProfile _profile;
    private readonly GlobalLaunchSettings _globalLaunchSettings;
    private readonly string _dataDirectory;
    private double _progressValue;
    private string _progressStatus = string.Empty;
    private bool _isRunning;
    private bool _isLaunching;
    private int? _processId;
    private string? _lastLaunchStatus;
    private ImageSource? _iconImage;

    public InstanceCardViewModel(InstanceProfile profile, GlobalLaunchSettings? globalLaunchSettings = null, string? dataDirectory = null)
    {
        _profile = profile;
        _globalLaunchSettings = globalLaunchSettings ?? GlobalLaunchSettings.Default;
        _dataDirectory = dataDirectory ?? string.Empty;
        UpdateIconImage();
    }

    public Guid Id => _profile.Id;
    public InstanceProfileState ProfileState => _profile.State;
    public string? ModrinthProjectId => _profile.ModrinthProjectId;
    public string? ModrinthPackVersionId => _profile.ModrinthPackVersionId;
    public string? ModrinthPackVersionNumber => _profile.ModrinthPackVersionNumber;
    public bool SupportsModManagement => _profile.LoaderType is MinecraftLoaderType.Fabric or MinecraftLoaderType.Quilt or MinecraftLoaderType.Forge or MinecraftLoaderType.NeoForge;
    public string Name => _profile.Name;
    public string VersionId => _profile.VersionId;
    public DateTimeOffset CreatedAtUtc => _profile.CreatedAtUtc;
    public string? GroupName => _profile.GroupName;
    public string? IconFileName => _profile.IconFileName;
    public IReadOnlyList<string> Tags => ParseTags(_profile.TagsJson);
    public ImageSource? IconImage => _iconImage;
    public Visibility CustomIconVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DefaultIconVisibility => _iconImage is null ? Visibility.Visible : Visibility.Collapsed;
    public string OrganizationLabel
    {
        get
        {
            var items = new List<string>();
            if (!string.IsNullOrWhiteSpace(_profile.GroupName)) items.Add(_profile.GroupName);
            items.AddRange(Tags);
            return string.Join(" · ", items);
        }
    }
    public Visibility OrganizationVisibility => string.IsNullOrWhiteSpace(OrganizationLabel) ? Visibility.Collapsed : Visibility.Visible;
    public string VersionLabel => _profile.LoaderType switch
    {
        MinecraftLoaderType.Fabric => $"Minecraft {_profile.VersionId} · Fabric {_profile.LoaderVersion}",
        MinecraftLoaderType.Quilt => $"Minecraft {_profile.VersionId} · Quilt {_profile.LoaderVersion}",
        MinecraftLoaderType.Forge => $"Minecraft {_profile.VersionId} · Forge {_profile.LoaderVersion}",
        MinecraftLoaderType.NeoForge => $"Minecraft {_profile.VersionId} · NeoForge {_profile.LoaderVersion}",
        _ => $"Minecraft {_profile.VersionId}"
    };
    public int MinMemoryMb => _profile.UseGlobalLaunchSettings ? _globalLaunchSettings.MinMemoryMb : _profile.MinMemoryMb;
    public int MaxMemoryMb => _profile.UseGlobalLaunchSettings ? _globalLaunchSettings.MaxMemoryMb : _profile.MaxMemoryMb;
    public bool UseGlobalLaunchSettings => _profile.UseGlobalLaunchSettings;
    public bool UseAutomaticMemory => _profile.UseGlobalLaunchSettings ? _globalLaunchSettings.UseAutomaticMemory : _profile.UseAutomaticMemory;
    public string? JavaExecutablePath => _profile.JavaExecutablePath;
    public string CustomJavaArguments => _profile.CustomJavaArguments;
    public string CustomEnvironmentVariablesJson => _profile.CustomEnvironmentVariablesJson;
    public bool Fullscreen => _profile.Fullscreen;
    public int WindowWidth => _profile.WindowWidth;
    public int WindowHeight => _profile.WindowHeight;
    public string MemoryLabel => UseAutomaticMemory
        ? "内存 · 自动分配"
        : $"内存 · {MinMemoryMb}–{MaxMemoryMb} MB";
    public string? LastError => _profile.LastError;
    public bool IsRunning => _isRunning;
    public bool IsLaunching => _isLaunching;

    public string LaunchStatusText => _isLaunching
        ? "正在启动 Minecraft…"
        : _isRunning
            ? $"正在运行 · PID {_processId}"
            : _lastLaunchStatus ?? string.Empty;

    public string StateText => _profile.State switch
    {
        InstanceProfileState.Installing => string.IsNullOrWhiteSpace(_progressStatus) ? "正在准备安装" : _progressStatus,
        InstanceProfileState.Installed => _isLaunching || _isRunning || _lastLaunchStatus is not null
            ? LaunchStatusText
            : "游戏文件与 Java 已安装",
        InstanceProfileState.Cancelled => "安装已取消",
        InstanceProfileState.Failed => "安装失败",
        _ => "尚未安装"
    };

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            if (Math.Abs(_progressValue - value) < 0.01) return;
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    public string ProgressStatus
    {
        get => _progressStatus;
        private set
        {
            if (_progressStatus == value) return;
            _progressStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public Visibility InstallActionVisibility => _profile.State is InstanceProfileState.ProfileOnly or InstanceProfileState.Cancelled or InstanceProfileState.Failed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RepairActionVisibility => _profile.State == InstanceProfileState.Installed && !_isRunning && !_isLaunching
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CancelActionVisibility => _profile.State == InstanceProfileState.Installing
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ProgressVisibility => _profile.State == InstanceProfileState.Installing
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility InstalledVisibility => _profile.State == InstanceProfileState.Installed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LaunchActionVisibility => _profile.State == InstanceProfileState.Installed && !_isRunning
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility StopActionVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LaunchSettingsVisibility => _profile.State != InstanceProfileState.Installing && !_isRunning && !_isLaunching
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ManageModsActionVisibility =>
        _profile.State == InstanceProfileState.Installed && SupportsModManagement
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ManagePacksActionVisibility => _profile.State == InstanceProfileState.Installed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool ManageModsActionEnabled => !_isRunning && !_isLaunching;

    public bool LaunchActionEnabled => !_isLaunching;

    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(_profile.LastError)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void SetState(InstanceProfileState state, string? lastError)
    {
        _profile = _profile with { State = state, LastError = lastError };
        NotifyStateProperties();
    }

    public void Apply(InstanceProfile profile)
    {
        _profile = profile;
        UpdateIconImage();
        if (profile.State != InstanceProfileState.Installing)
        {
            ProgressValue = profile.State == InstanceProfileState.Installed ? 100 : 0;
            ProgressStatus = string.Empty;
        }

        NotifyStateProperties();
        OnPropertyChanged(nameof(MinMemoryMb));
        OnPropertyChanged(nameof(MaxMemoryMb));
        OnPropertyChanged(nameof(UseGlobalLaunchSettings));
        OnPropertyChanged(nameof(UseAutomaticMemory));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(IconFileName));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(OrganizationLabel));
        OnPropertyChanged(nameof(OrganizationVisibility));
        OnPropertyChanged(nameof(JavaExecutablePath));
        OnPropertyChanged(nameof(CustomJavaArguments));
        OnPropertyChanged(nameof(CustomEnvironmentVariablesJson));
        OnPropertyChanged(nameof(Fullscreen));
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        OnPropertyChanged(nameof(MemoryLabel));
    }

    public bool MatchesFilter(string query, string? groupFilter, string? loaderFilter, string? stateFilter)
    {
        if (!string.IsNullOrWhiteSpace(groupFilter) &&
            !string.Equals(_profile.GroupName, groupFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(loaderFilter))
        {
            var loaderName = _profile.LoaderType switch
            {
                MinecraftLoaderType.Vanilla => "vanilla",
                MinecraftLoaderType.Fabric => "fabric",
                MinecraftLoaderType.Quilt => "quilt",
                MinecraftLoaderType.Forge => "forge",
                MinecraftLoaderType.NeoForge => "neoforge",
                _ => string.Empty
            };
            if (!string.Equals(loaderName, loaderFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var matchesState = stateFilter?.ToLowerInvariant() switch
        {
            "installed" => _profile.State == InstanceProfileState.Installed,
            "uninstalled" => _profile.State is InstanceProfileState.ProfileOnly or InstanceProfileState.Cancelled,
            "installing" => _profile.State == InstanceProfileState.Installing,
            "failed" => _profile.State == InstanceProfileState.Failed,
            "running" => _isRunning || _isLaunching,
            _ => true
        };
        if (!matchesState) return false;

        if (string.IsNullOrWhiteSpace(query)) return true;
        return _profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               VersionLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (_profile.GroupName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateIconImage()
    {
        _iconImage = null;
        if (!string.IsNullOrWhiteSpace(_profile.IconFileName) &&
            string.Equals(Path.GetFileName(_profile.IconFileName), _profile.IconFileName, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_dataDirectory))
        {
            var directoryName = InstanceDirectoryName.ForProfile(_profile);
            var iconPath = Path.Combine(_dataDirectory, "instances", directoryName, _profile.IconFileName);
            if (File.Exists(iconPath))
            {
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 128 };
                    bitmap.UriSource = new Uri(Path.GetFullPath(iconPath), UriKind.Absolute);
                    _iconImage = bitmap;
                }
                catch (Exception)
                {
                    _iconImage = null;
                }
            }
        }

        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(CustomIconVisibility));
        OnPropertyChanged(nameof(DefaultIconVisibility));
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(tagsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void ApplyProgress(InstallationProgress progress)
    {
        if (progress.InstanceId != Id)
        {
            return;
        }

        _profile = _profile with { State = InstanceProfileState.Installing, LastError = null };
        ProgressValue = progress.PercentComplete;
        ProgressStatus = progress.TotalBytes > 0
            ? $"{StageLabel(progress.Stage)} · {progress.PercentComplete:0}% · {progress.FilesCompleted}/{progress.FilesTotal} 个文件"
            : $"{StageLabel(progress.Stage)} · {progress.FilesCompleted}/{progress.FilesTotal} 个文件";
        NotifyStateProperties();
    }

    public void SetLaunching(bool isLaunching)
    {
        _isLaunching = isLaunching;
        if (isLaunching)
        {
            _lastLaunchStatus = null;
        }

        NotifyLaunchProperties();
    }

    public void SetRunning(bool isRunning, int? processId = null, int? exitCode = null)
    {
        _isLaunching = false;
        _isRunning = isRunning;
        _processId = isRunning ? processId : null;
        _lastLaunchStatus = isRunning
            ? null
            : exitCode is null
                ? "游戏进程已结束"
                : exitCode == 0
                    ? "游戏正常退出"
                    : $"游戏退出代码 {exitCode}";
        NotifyLaunchProperties();
    }

    public void ApplyProcessStatus(MinecraftGameProcessStatus status)
    {
        if (status.State == MinecraftGameProcessState.Running)
        {
            SetRunning(true, status.ProcessId);
            return;
        }

        _isLaunching = false;
        _isRunning = false;
        _processId = null;
        _lastLaunchStatus = status.State == MinecraftGameProcessState.MonitoringFailed
            ? "无法确认游戏进程状态"
            : status.ExitCode is null
                ? "游戏进程已结束"
                : status.ExitCode == 0
                    ? "游戏正常退出"
                    : $"游戏退出代码 {status.ExitCode}";
        NotifyLaunchProperties();
    }

    private void NotifyStateProperties()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProfileState));
        OnPropertyChanged(nameof(SupportsModManagement));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(InstallActionVisibility));
        OnPropertyChanged(nameof(RepairActionVisibility));
        OnPropertyChanged(nameof(LaunchSettingsVisibility));
        OnPropertyChanged(nameof(ManageModsActionVisibility));
        OnPropertyChanged(nameof(ManagePacksActionVisibility));
        OnPropertyChanged(nameof(CancelActionVisibility));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(InstalledVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(LastError));
        NotifyLaunchProperties();
    }

    private void NotifyLaunchProperties()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(LaunchStatusText));
        OnPropertyChanged(nameof(LaunchActionVisibility));
        OnPropertyChanged(nameof(StopActionVisibility));
        OnPropertyChanged(nameof(LaunchActionEnabled));
        OnPropertyChanged(nameof(LaunchSettingsVisibility));
        OnPropertyChanged(nameof(ManageModsActionEnabled));
        OnPropertyChanged(nameof(RepairActionVisibility));
    }

    private static string StageLabel(InstallationStage stage) => stage switch
    {
        InstallationStage.Resolving => "正在解析版本",
        InstallationStage.Verifying => "正在校验文件",
        InstallationStage.Extracting => "正在准备原生库",
        InstallationStage.Completed => "安装完成",
        _ => "正在下载"
    };
}
