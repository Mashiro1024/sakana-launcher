namespace Sakana.Domain;

/// <summary>
/// Portable, shareable launch preferences. Machine-specific Java paths and custom
/// environment variables are deliberately excluded from exported presets.
/// </summary>
public sealed record InstanceLaunchSettingsPreset(
    int SchemaVersion,
    bool UseAutomaticMemory,
    int MinMemoryMb,
    int MaxMemoryMb,
    string CustomJavaArguments,
    bool Fullscreen,
    int WindowWidth,
    int WindowHeight)
{
    public const int CurrentSchemaVersion = 1;
}
