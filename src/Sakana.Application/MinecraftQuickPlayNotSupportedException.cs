namespace Sakana.Application;

public sealed class MinecraftQuickPlayNotSupportedException(string versionId)
    : NotSupportedException($"Minecraft {versionId} does not advertise the official Quick Play launch arguments.")
{
    public string VersionId { get; } = versionId;
}
