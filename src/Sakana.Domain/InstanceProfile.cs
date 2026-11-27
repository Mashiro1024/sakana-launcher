namespace Sakana.Domain;

public enum InstanceProfileState
{
    ProfileOnly,
    Installing,
    Installed,
    Cancelled,
    Failed
}

public sealed record InstanceProfile(
    Guid Id,
    string Name,
    string VersionId,
    InstanceProfileState State,
    DateTimeOffset CreatedAtUtc,
    string? LastError = null,
    int MinMemoryMb = 512,
    int MaxMemoryMb = 4096,
    MinecraftLoaderType LoaderType = MinecraftLoaderType.Vanilla,
    string? LoaderVersion = null,
    string? ModrinthPackPath = null,
    string ModrinthSelectedOptionalFilesJson = "[]",
    bool UseAutomaticMemory = false,
    string? JavaExecutablePath = null,
    string CustomJavaArguments = "",
    string CustomEnvironmentVariablesJson = "{}",
    bool Fullscreen = false,
    int WindowWidth = 854,
    int WindowHeight = 480,
    bool UseGlobalLaunchSettings = false,
    string? ModrinthProjectId = null,
    string? ModrinthPackVersionId = null,
    string? ModrinthPackVersionNumber = null,
    string? GroupName = null,
    string TagsJson = "[]",
    string? IconFileName = null,
    string? MinecraftAccountUuid = null,
    string? CurseForgePackPath = null);
