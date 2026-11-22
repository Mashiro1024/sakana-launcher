using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

public sealed class MojangJavaRuntimeManager : IJavaRuntimeCatalog
{
    private const string RuntimeCatalogUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    private readonly HttpClient _httpClient;
    private readonly string _dataDirectory;
    private readonly string _cacheDirectory;
    private readonly ParallelArtifactDownloader _downloader;

    public MojangJavaRuntimeManager(HttpClient httpClient, string dataDirectory)
        : this(httpClient, dataDirectory, new ParallelArtifactDownloader(httpClient))
    {
    }

    internal MojangJavaRuntimeManager(HttpClient httpClient, string dataDirectory, ParallelArtifactDownloader downloader)
    {
        _httpClient = httpClient;
        _dataDirectory = dataDirectory;
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "minecraft", "java-runtime");
        _downloader = downloader;
    }

    internal string GetInstalledRuntimeExecutablePath(MojangJavaVersion? javaVersion)
    {
        var requirement = ResolveRequirement(javaVersion);
        var platform = GetWindowsPlatform();
        return Path.Combine(
            _dataDirectory,
            "runtimes",
            ValidateSegment(requirement.Component, "Java runtime component"),
            platform,
            requirement.Component,
            "bin",
            "java.exe");
    }

    public async Task<IReadOnlyList<JavaInstallation>> DetectInstalledJavaAsync(CancellationToken cancellationToken = default)
    {
        var installations = new List<JavaInstallation>();
        foreach (var executablePath in GetJavaCandidates().Take(24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installation = await ReadJavaInstallationAsync(executablePath, cancellationToken);
            if (installation is not null && installations.All(item => !string.Equals(item.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                installations.Add(installation);
            }
        }

        return installations
            .OrderByDescending(item => item.MajorVersion)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<string> EnsureRuntimeAsync(
        Guid instanceId,
        MojangJavaVersion? javaVersion,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken,
        bool forceRevalidation = false,
        string? preferredJavaExecutablePath = null)
    {
        var requirement = ResolveRequirement(javaVersion);
        if (!string.IsNullOrWhiteSpace(preferredJavaExecutablePath))
        {
            var preferred = await ReadJavaInstallationAsync(Path.GetFullPath(preferredJavaExecutablePath), cancellationToken);
            if (preferred is null || preferred.MajorVersion != requirement.MajorVersion)
            {
                throw new InvalidOperationException($"The selected Java executable must be Java {requirement.MajorVersion}. Choose a matching Java version or use automatic selection.");
            }

            return preferred.ExecutablePath;
        }

        var installedJava = await DetectInstalledJavaAsync(cancellationToken);
        var compatibleJava = installedJava.FirstOrDefault(item => item.MajorVersion == requirement.MajorVersion);
        if (compatibleJava is not null)
        {
            return compatibleJava.ExecutablePath;
        }

        var managedRuntime = GetInstalledRuntimeExecutablePath(javaVersion);
        if (!forceRevalidation && File.Exists(managedRuntime))
        {
            return managedRuntime;
        }

        var platform = GetWindowsPlatform();
        var component = ValidateSegment(requirement.Component, "Java runtime component");
        var catalogBytes = await GetRuntimeCatalogAsync(cancellationToken);
        var runtimeEntry = SelectRuntimeEntry(catalogBytes, platform, component);
        var manifestBytes = await GetVerifiedPackageManifestAsync(runtimeEntry.ManifestUrl, runtimeEntry.ManifestSha1, cancellationToken);

        var runtimeRelativeRoot = Path.Combine("runtimes", component, platform, component);
        var runtimeRoot = Path.GetFullPath(runtimeRelativeRoot, _dataDirectory);
        var artifacts = new List<InstallArtifact>();
        var directories = new List<string>();
        var includesJava = false;

        using (var document = JsonDocument.Parse(manifestBytes, JsonOptions))
        {
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Mojang Java runtime manifest does not contain a files object.");
            }

            foreach (var fileEntry in files.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeFilePath = ValidateRelativePath(fileEntry.Name);
                if (!fileEntry.Value.TryGetProperty("type", out var typeElement))
                {
                    throw new InvalidDataException($"The Java runtime manifest omitted the file type for {relativeFilePath}.");
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add(Path.Combine(runtimeRelativeRoot, relativeFilePath));
                    continue;
                }

                if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException($"The Windows Java runtime contains an unsupported '{type}' entry: {relativeFilePath}.");
                }

                if (!fileEntry.Value.TryGetProperty("downloads", out var downloads) ||
                    !downloads.TryGetProperty("raw", out var rawDownload))
                {
                    throw new InvalidDataException($"The Java runtime manifest omitted the download information for {relativeFilePath}.");
                }

                var url = GetRequiredString(rawDownload, "url", relativeFilePath);
                var sha1 = GetRequiredString(rawDownload, "sha1", relativeFilePath);
                var size = rawDownload.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : throw new InvalidDataException($"The Java runtime manifest omitted the file size for {relativeFilePath}.");

                if (Path.GetFileName(relativeFilePath).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    includesJava = true;
                }

                artifacts.Add(new InstallArtifact(
                    Path.Combine(runtimeRelativeRoot, relativeFilePath),
                    url,
                    sha1,
                    size));
            }
        }

        if (!includesJava || artifacts.Count == 0)
        {
            throw new InvalidDataException($"The Mojang runtime component {component} does not include a Windows Java executable.");
        }

        Directory.CreateDirectory(runtimeRoot);
        foreach (var relativeDirectory in directories)
        {
            var safeDirectory = Path.GetFullPath(relativeDirectory, _dataDirectory);
            EnsureInsideRoot(safeDirectory, Path.GetFullPath(_dataDirectory));
            Directory.CreateDirectory(safeDirectory);
        }

        await _downloader.DownloadAllAsync(
            instanceId,
            _dataDirectory,
            artifacts,
            progress,
            cancellationToken,
            forceRevalidation);

        var javaPath = Path.Combine(runtimeRoot, "bin", "java.exe");
        if (!File.Exists(javaPath))
        {
            throw new InvalidDataException($"Mojang Java {requirement.MajorVersion} was downloaded but java.exe is missing.");
        }

        return javaPath;
    }

    private static IEnumerable<string> GetJavaCandidates()
    {
        var candidates = new List<string>();
        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(fullPath) && !candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fullPath);
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            AddCandidate(Path.Combine(javaHome, "bin", "java.exe"));
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            AddCandidate(Path.Combine(directory.Trim().Trim('"'), "java.exe"));
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        var vendorNames = new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru" };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var vendor in vendorNames)
            {
                var vendorDirectory = Path.Combine(root, vendor);
                AddCandidate(Path.Combine(vendorDirectory, "bin", "java.exe"));
                if (!Directory.Exists(vendorDirectory)) continue;
                foreach (var installationDirectory in Directory.EnumerateDirectories(vendorDirectory, "*", SearchOption.TopDirectoryOnly).Take(8))
                {
                    AddCandidate(Path.Combine(installationDirectory, "bin", "java.exe"));
                }
            }
        }

        return candidates;
    }

    private static async Task<JavaInstallation?> ReadJavaInstallationAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath)) return null;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add("-version");
        try
        {
            if (!process.Start()) return null;
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            var output = $"{await standardError}\n{await standardOutput}";
            var match = Regex.Match(output, "(?:version \\\"(?<version>[^\\\"]+)\\\"|openjdk (?<version>\\d+(?:\\.\\d+)*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            var version = match.Groups["version"].Value;
            var majorText = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..].Split('.')[0] : version.Split('.')[0];
            if (!int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return null;
            return new JavaInstallation(version, major, Path.GetFullPath(executablePath));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<byte[]> GetRuntimeCatalogAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, "runtime-catalog.json");
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(RuntimeCatalogUrl, cancellationToken);
            using var document = JsonDocument.Parse(bytes, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Mojang Java runtime catalog has an invalid root object.");
            }

            await WriteAtomicallyAsync(cachePath, bytes, cancellationToken);
            return bytes;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            (exception is HttpRequestException or IOException or InvalidDataException or JsonException))
        {
            if (!File.Exists(cachePath))
            {
                throw;
            }

            var cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            using var document = JsonDocument.Parse(cachedBytes, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The cached Mojang Java runtime catalog is invalid.");
            }

            return cachedBytes;
        }
    }

    private async Task<byte[]> GetVerifiedPackageManifestAsync(string url, string expectedSha1, CancellationToken cancellationToken)
    {
        ValidateHttpsUrl(url);
        ValidateHash(expectedSha1, "Java runtime package SHA-1");
        var packageCacheDirectory = Path.Combine(_cacheDirectory, "packages");
        Directory.CreateDirectory(packageCacheDirectory);
        var cachePath = Path.Combine(packageCacheDirectory, $"{expectedSha1.ToLowerInvariant()}.json");

        if (File.Exists(cachePath))
        {
            var cachedBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            if (HasExpectedSha1(cachedBytes, expectedSha1))
            {
                return cachedBytes;
            }

            File.Delete(cachePath);
        }

        var bytes = await _httpClient.GetByteArrayAsync(url, cancellationToken);
        if (!HasExpectedSha1(bytes, expectedSha1))
        {
            throw new InvalidDataException("The Mojang Java runtime manifest failed its SHA-1 verification.");
        }

        await WriteAtomicallyAsync(cachePath, bytes, cancellationToken);
        return bytes;
    }

    private static RuntimeEntry SelectRuntimeEntry(byte[] catalogBytes, string platform, string component)
    {
        using var document = JsonDocument.Parse(catalogBytes, JsonOptions);
        if (!document.RootElement.TryGetProperty(platform, out var platformCatalog) ||
            !platformCatalog.TryGetProperty(component, out var componentVersions) ||
            componentVersions.ValueKind != JsonValueKind.Array || componentVersions.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Mojang does not currently publish the Java runtime component {component} for {platform}.");
        }

        var selectedEntry = componentVersions.EnumerateArray()
            .Select(entry => new
            {
                Entry = entry,
                Released = TryGetRuntimeReleaseTime(entry)
            })
            .OrderByDescending(item => item.Released)
            .First().Entry;

        if (!selectedEntry.TryGetProperty("manifest", out var manifest))
        {
            throw new InvalidDataException($"The Mojang Java runtime catalog did not provide a manifest for {component}.");
        }

        return new RuntimeEntry(
            GetRequiredString(manifest, "url", component),
            GetRequiredString(manifest, "sha1", component));
    }

    private static DateTimeOffset TryGetRuntimeReleaseTime(JsonElement entry)
    {
        if (entry.TryGetProperty("version", out var version) &&
            version.TryGetProperty("released", out var released) &&
            DateTimeOffset.TryParse(released.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MinValue;
    }

    private static JavaRuntimeRequirement ResolveRequirement(MojangJavaVersion? javaVersion)
    {
        if (javaVersion is null)
        {
            return new JavaRuntimeRequirement(8, "jre-legacy");
        }

        var component = javaVersion.Component;
        if (string.IsNullOrWhiteSpace(component))
        {
            component = javaVersion.MajorVersion switch
            {
                8 => "jre-legacy",
                16 => "java-runtime-alpha",
                17 => "java-runtime-gamma",
                21 => "java-runtime-delta",
                25 => "java-runtime-epsilon",
                _ => throw new NotSupportedException($"Minecraft metadata does not name a Java runtime for Java {javaVersion.MajorVersion}.")
            };
        }

        if (javaVersion.MajorVersion <= 0)
        {
            throw new InvalidDataException("Minecraft metadata contains an invalid Java major version.");
        }

        return new JavaRuntimeRequirement(javaVersion.MajorVersion, component);
    }

    private static string GetWindowsPlatform() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "windows-arm64",
        System.Runtime.InteropServices.Architecture.X86 => "windows-x86",
        _ => "windows-x64"
    };

    private static string ValidateSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException($"The Mojang manifest contains an invalid {label}.");
        }

        return value;
    }

    private static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("The Mojang Java runtime manifest contains an invalid relative path.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            throw new InvalidDataException("The Mojang Java runtime manifest contains a path traversal entry.");
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string itemDescription)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"The Mojang Java runtime metadata omitted {propertyName} for {itemDescription}.");
        }

        return property.GetString()!;
    }

    private static void ValidateHash(string hash, string label)
    {
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"The Mojang Java runtime metadata contains an invalid {label}.");
        }
    }

    private static bool HasExpectedSha1(ReadOnlySpan<byte> bytes, string expectedSha1) =>
        Convert.ToHexString(SHA1.HashData(bytes)).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);

    private static void ValidateHttpsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The Mojang Java runtime metadata contains a non-HTTPS URL.");
        }
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureInsideRoot(string path, string root)
    {
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Mojang Java runtime manifest attempted to create a folder outside the Sakana data directory.");
        }
    }

    private sealed record JavaRuntimeRequirement(int MajorVersion, string Component);
    private sealed record RuntimeEntry(string ManifestUrl, string ManifestSha1);
}
