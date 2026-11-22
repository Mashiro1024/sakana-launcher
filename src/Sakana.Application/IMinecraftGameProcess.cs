using Sakana.Domain;

namespace Sakana.Application;

/// <summary>
/// A running Minecraft child process. Output is a bounded, single-consumer stream containing
/// the most recent lines; completion reports the Java process exit code.
/// </summary>
public interface IMinecraftGameProcess : IAsyncDisposable
{
    Guid InstanceId { get; }
    string VersionId { get; }
    int ProcessId { get; }
    DateTimeOffset StartedAt { get; }
    bool HasExited { get; }
    Task<int> Completion { get; }
    IAsyncEnumerable<MinecraftProcessOutput> ReadOutputAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IMinecraftProcessRunner
{
    Task<IMinecraftGameProcess> StartAsync(MinecraftLaunchPlan plan, CancellationToken cancellationToken = default);
}
