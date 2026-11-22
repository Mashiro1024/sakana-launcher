using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sakana.Minecraft;

internal sealed class MojangVersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersionIds Latest { get; init; } = new();

    [JsonPropertyName("versions")]
    public List<MojangVersionEntry> Versions { get; init; } = [];
}

internal sealed class LatestVersionIds
{
    [JsonPropertyName("release")]
    public string Release { get; init; } = string.Empty;

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; init; } = string.Empty;
}

internal sealed class MojangVersionEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("releaseTime")]
    public string ReleaseTime { get; init; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string Sha1 { get; init; } = string.Empty;
}

internal sealed class MojangVersionMetadata
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; init; }

    [JsonPropertyName("assets")]
    public string? Assets { get; init; }

    [JsonPropertyName("assetIndex")]
    public DownloadArtifact? AssetIndex { get; init; }

    [JsonPropertyName("downloads")]
    public Dictionary<string, DownloadArtifact> Downloads { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("libraries")]
    public List<MojangLibrary> Libraries { get; init; } = [];

    [JsonPropertyName("logging")]
    public LoggingMetadata? Logging { get; init; }

    [JsonPropertyName("javaVersion")]
    public MojangJavaVersion? JavaVersion { get; init; }

    [JsonPropertyName("mainClass")]
    public string MainClass { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public MojangVersionArguments? Arguments { get; init; }

    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; init; }

    [JsonIgnore]
    public string VersionType { get; set; } = "release";
}

internal sealed class MojangVersionArguments
{
    [JsonPropertyName("game")]
    public JsonElement Game { get; init; }

    [JsonPropertyName("jvm")]
    public JsonElement Jvm { get; init; }
}

internal sealed class MojangJavaVersion
{
    [JsonPropertyName("component")]
    public string Component { get; init; } = string.Empty;

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; init; }
}

internal sealed class DownloadArtifact
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("totalSize")]
    public long? TotalSize { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

internal sealed class MojangLibrary
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("rules")]
    public JsonElement Rules { get; init; }

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; init; }

    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; init; }

    [JsonPropertyName("extract")]
    public NativeExtractRules? Extract { get; init; }
}

internal sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadArtifact? Artifact { get; init; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadArtifact>? Classifiers { get; init; }
}

internal sealed class NativeExtractRules
{
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; init; } = [];
}

internal sealed class LoggingMetadata
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; init; }
}

internal sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string Argument { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public DownloadArtifact? File { get; init; }
}

internal sealed class MojangAssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, MojangAssetObject> Objects { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class MojangAssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }
}
