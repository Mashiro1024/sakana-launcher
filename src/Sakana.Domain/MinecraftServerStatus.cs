namespace Sakana.Domain;

public sealed record MinecraftServerStatus(
    bool IsOnline,
    int? LatencyMilliseconds,
    string? VersionName,
    int? OnlinePlayers,
    int? MaxPlayers,
    string Description,
    string? ErrorMessage);
