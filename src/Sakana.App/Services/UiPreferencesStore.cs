using System.Text.Json;

namespace Sakana.App.Services;

public sealed record UiPreferences(
    bool LibraryGuideDismissed = false,
    string LibraryViewMode = "grid");

public static class UiPreferencesStore
{
    private const string FileName = "ui-preferences.json";

    public static UiPreferences Load(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return new UiPreferences();
        }

        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path))
            {
                return new UiPreferences();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiPreferences>(json) ?? new UiPreferences();
        }
        catch
        {
            return new UiPreferences();
        }
    }

    public static void Save(string dataDirectory, UiPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, FileName);
            var json = JsonSerializer.Serialize(preferences);
            File.WriteAllText(path, json);
        }
        catch
        {
            // UI layout preferences are non-critical; ignore transient IO errors.
        }
    }
}
