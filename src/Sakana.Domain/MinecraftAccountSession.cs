namespace Sakana.Domain;

/// <summary>
/// An authenticated Minecraft session kept in memory while the game is prepared or running.
/// The access token must never be persisted or written to logs.
/// </summary>
public sealed class MinecraftAccountSession(
    string playerName,
    string playerUuid,
    string accessToken,
    string clientId,
    string userType,
    string? xuid = null,
    string userPropertiesJson = "{}",
    bool isDemoUser = false,
    string? legacySessionId = null,
    string? skinTextureUrl = null,
    string? skinVariant = null)
{
    public string PlayerName { get; } = playerName;
    public string PlayerUuid { get; } = playerUuid;
    public string AccessToken { get; } = accessToken;
    public string ClientId { get; } = clientId;
    public string UserType { get; } = userType;
    public string? Xuid { get; } = xuid;
    public string UserPropertiesJson { get; } = userPropertiesJson;
    public bool IsDemoUser { get; } = isDemoUser;
    public string? LegacySessionId { get; } = legacySessionId;
    public string? SkinTextureUrl { get; } = skinTextureUrl;
    public string? SkinVariant { get; } = skinVariant;

    public override string ToString() => $"MinecraftAccountSession(PlayerName={PlayerName}, PlayerUuid={PlayerUuid}, AccessToken=[redacted])";
}
