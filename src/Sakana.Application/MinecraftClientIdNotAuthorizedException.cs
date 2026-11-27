namespace Sakana.Application;

public sealed class MinecraftClientIdNotAuthorizedException()
    : InvalidOperationException("Minecraft Services rejected the configured Microsoft client ID for Minecraft Java API access. Confirm that the approval covers this exact client ID and has taken effect.");
