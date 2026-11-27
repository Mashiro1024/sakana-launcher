namespace Sakana.Domain;

public sealed record InstanceLaunchSettings(
    bool UseAutomaticMemory,
    int MinMemoryMb,
    int MaxMemoryMb,
    string? JavaExecutablePath,
    string CustomJavaArguments,
    IReadOnlyDictionary<string, string> CustomEnvironmentVariables,
    bool Fullscreen,
    int WindowWidth,
    int WindowHeight,
    bool UseGlobalSettings = false,
    string? MinecraftAccountUuid = null);
