namespace Sakana.Application;

public sealed class MinecraftAccountRequiredException(string message) : InvalidOperationException(message);
