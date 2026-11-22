using Sakana.Domain;

namespace Sakana.App.ViewModels;

public sealed class LaunchHistoryCardViewModel(MinecraftLaunchHistoryEntry entry, string instanceName, bool isRunning)
{
    public string InstanceName { get; } = instanceName;
    public string VersionText { get; } = $"Minecraft {entry.VersionId}";
    public string StartedAtText { get; } = entry.StartedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

    public string StatusText { get; } = isRunning
        ? "正在运行"
        : entry.EndedAtUtc is null
            ? "退出状态未知"
            : entry.ExitCode == 0
                ? "正常退出"
                : entry.ExitCode is { } exitCode
                    ? $"退出代码 {exitCode}"
                    : "已退出";
}
