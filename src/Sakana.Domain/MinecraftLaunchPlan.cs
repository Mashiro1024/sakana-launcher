namespace Sakana.Domain;

/// <summary>
/// A fully expanded, in-memory Minecraft process plan. Arguments can contain a short-lived access token.
/// Keep the object out of logs and persistent storage.
/// </summary>
public sealed class MinecraftLaunchPlan(
    Guid instanceId,
    string versionId,
    string javaExecutablePath,
    string workingDirectory,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string>? environmentVariables = null,
    IReadOnlyList<string>? sensitiveValues = null)
{
    public Guid InstanceId { get; } = instanceId;
    public string VersionId { get; } = versionId;
    public string JavaExecutablePath { get; } = javaExecutablePath;
    public string WorkingDirectory { get; } = workingDirectory;
    public IReadOnlyList<string> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environmentVariables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
    public IReadOnlyList<string> SensitiveValues { get; } = Array.AsReadOnly(
        (sensitiveValues ?? Array.Empty<string>()).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray());

    public override string ToString() => $"MinecraftLaunchPlan(InstanceId={InstanceId}, VersionId={VersionId}, Arguments=[redacted])";
}
