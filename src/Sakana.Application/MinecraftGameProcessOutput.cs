namespace Sakana.Application;

public sealed record MinecraftGameProcessOutput(
    Guid InstanceId,
    string VersionId,
    MinecraftProcessOutput Output);
