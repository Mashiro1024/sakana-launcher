namespace Sakana.Domain;

public sealed record LauncherSettings(ThemePreference Theme, GlobalLaunchSettings GlobalLaunchSettings)
{
    public int MaxConcurrentDownloads { get; init; } = 8;
    public IReadOnlyList<LibraryViewPreset> LibraryViewPresets { get; init; } = [];

    // Public desktop-client identifier; it is not a secret.
    public const string MicrosoftOAuthClientId = "712e578b-fa02-4b96-8a6f-d163bce3a247";
    public static LauncherSettings Default { get; } = new(ThemePreference.Dark, GlobalLaunchSettings.Default);
}
