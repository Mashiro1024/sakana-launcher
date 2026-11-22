namespace Sakana.Domain;

public sealed record GlobalLaunchSettings(
    bool UseAutomaticMemory = false,
    int MinMemoryMb = 512,
    int MaxMemoryMb = 4096,
    string? JavaExecutablePath = null,
    string CustomJavaArguments = "",
    string CustomEnvironmentVariablesJson = "{}",
    bool Fullscreen = false,
    int WindowWidth = 854,
    int WindowHeight = 480)
{
    public static GlobalLaunchSettings Default { get; } = new();
}
