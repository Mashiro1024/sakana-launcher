namespace Sakana.Application;

public sealed class MinecraftClientIdNotAuthorizedException()
    : InvalidOperationException("Minecraft Services rejected the configured Microsoft client ID because it has not been authorized for Minecraft Java API access.");
