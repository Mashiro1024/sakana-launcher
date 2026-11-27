namespace Sakana.Domain;

/// <summary>Non-secret metadata used to display and select a saved Minecraft account.</summary>
public sealed record MinecraftAccountInfo(
    string PlayerName,
    string PlayerUuid,
    bool IsActive,
    bool NeedsVerification = false);
