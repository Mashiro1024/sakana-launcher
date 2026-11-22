namespace Sakana.Domain;

public sealed record MinecraftGameVersion(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    bool IsLatestRelease);
