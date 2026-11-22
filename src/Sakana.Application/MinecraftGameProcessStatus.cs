namespace Sakana.Application;

public enum MinecraftGameProcessState
{
    Running,
    Exited,
    MonitoringFailed
}

public sealed record MinecraftGameProcessStatus(
    Guid InstanceId,
    string VersionId,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ObservedAtUtc,
    MinecraftGameProcessState State,
    int? ExitCode);
