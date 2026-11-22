namespace Sakana.Application;

public enum MinecraftOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record MinecraftProcessOutput(
    DateTimeOffset Timestamp,
    MinecraftOutputStream Stream,
    string Text);
