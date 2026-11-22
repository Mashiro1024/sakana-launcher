namespace Sakana.Domain;

public sealed record MinecraftLaunchHistoryEntry(
    Guid Id,
    Guid InstanceId,
    string VersionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    int? ExitCode);
