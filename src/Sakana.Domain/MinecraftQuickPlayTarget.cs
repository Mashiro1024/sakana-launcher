namespace Sakana.Domain;

public enum MinecraftQuickPlayKind
{
    SingleplayerWorld,
    MultiplayerServer
}

public sealed record MinecraftQuickPlayTarget(MinecraftQuickPlayKind Kind, string Value);
