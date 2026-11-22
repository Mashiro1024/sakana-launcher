using Sakana.Application;

namespace Sakana.App.ViewModels;

public sealed class ActiveGameProcessViewModel(MinecraftGameProcessStatus status)
{
    public Guid InstanceId { get; } = status.InstanceId;
    public string VersionText { get; } = $"Minecraft {status.VersionId}";
    public string ProcessIdText { get; } = $"进程 ID {status.ProcessId}";
    public string StartedAtText { get; } = $"启动于 {status.StartedAtUtc.ToLocalTime():HH:mm}";
}
