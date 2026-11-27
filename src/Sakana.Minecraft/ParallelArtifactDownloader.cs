using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Sakana.Domain;

namespace Sakana.Minecraft;

internal sealed record InstallArtifact(
    string RelativePath,
    string Url,
    string? Sha1,
    long? Size,
    IReadOnlyList<string>? NativeExclusions = null,
    string? Sha512 = null,
    IReadOnlyList<string>? FallbackUrls = null,
    IReadOnlyList<string>? AllowedHosts = null,
    bool IsContentAddressed = false);

internal sealed class ParallelArtifactDownloader(HttpClient httpClient, DownloadConcurrencyOptions? concurrencyOptions = null)
{
    private const int BufferSize = 128 * 1024;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadConcurrencyOptions _concurrencyOptions = concurrencyOptions ?? new DownloadConcurrencyOptions();

    public async Task DownloadAllAsync(
        Guid instanceId,
        string rootDirectory,
        IReadOnlyCollection<InstallArtifact> artifacts,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken,
        bool forceRevalidation = false)
    {
        var uniqueArtifacts = artifacts
            .GroupBy(artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var totalBytes = uniqueArtifacts.Sum(artifact => Math.Max(artifact.Size ?? 0, 0));
        var completedBytes = 0L;
        var filesCompleted = 0;
        var lastReportedAt = 0L;
        var activeBytes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        ReportProgress(force: true, currentFile: null);

        var tasks = uniqueArtifacts.Select(async artifact =>
        {
            using var concurrencyLease = await _concurrencyOptions.AcquireAsync(cancellationToken);
            SemaphoreSlim? pathGate = null;
            var pathGateAcquired = false;
            try
            {
                var targetPath = GetSafeTargetPath(rootDirectory, artifact.RelativePath);
                pathGate = _pathLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
                await pathGate.WaitAsync(cancellationToken);
                pathGateAcquired = true;

                ReportProgress(force: true, artifact.RelativePath, InstallationStage.Verifying);
                var existingLength = await GetValidExistingLengthAsync(
                    targetPath,
                    artifact,
                    forceRevalidation,
                    cancellationToken);
                if (existingLength.HasValue)
                {
                    Interlocked.Add(ref completedBytes, existingLength.Value);
                    var done = Interlocked.Increment(ref filesCompleted);
                    ReportProgress(done == uniqueArtifacts.Length, artifact.RelativePath, InstallationStage.Verifying);
                    return;
                }

                await DownloadWithRetryAsync(
                    targetPath,
                    artifact,
                    (bytes, stage) =>
                    {
                        activeBytes[artifact.RelativePath] = bytes;
                        ReportProgress(force: stage == InstallationStage.Verifying, artifact.RelativePath, stage);
                    },
                    cancellationToken);

                activeBytes.TryRemove(artifact.RelativePath, out _);
                var downloadedLength = new FileInfo(targetPath).Length;
                Interlocked.Add(ref completedBytes, downloadedLength);
                var completed = Interlocked.Increment(ref filesCompleted);
                ReportProgress(completed == uniqueArtifacts.Length, artifact.RelativePath, InstallationStage.Verifying);
            }
            finally
            {
                if (pathGateAcquired)
                {
                    pathGate!.Release();
                }
            }
        });

        await Task.WhenAll(tasks);

        void ReportProgress(bool force, string? currentFile, InstallationStage stage = InstallationStage.Downloading)
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref lastReportedAt);
            if (!force && now - previous < 120)
            {
                return;
            }

            Interlocked.Exchange(ref lastReportedAt, now);
            var active = activeBytes.Values.Sum();
            var bytes = Math.Min(Interlocked.Read(ref completedBytes) + active, Math.Max(totalBytes, 0));
            progress?.Report(new InstallationProgress(
                instanceId,
                stage,
                bytes,
                totalBytes,
                Volatile.Read(ref filesCompleted),
                uniqueArtifacts.Length,
                currentFile));
        }
    }

    private async Task<long?> GetValidExistingLengthAsync(
        string targetPath,
        InstallArtifact artifact,
        bool forceRevalidation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath))
        {
            return null;
        }

        var fileInfo = new FileInfo(targetPath);
        if (artifact.Size is long expectedSize && fileInfo.Length != expectedSize)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(artifact.Sha1) && string.IsNullOrWhiteSpace(artifact.Sha512) && artifact.Size is null)
        {
            return null;
        }

        // Mojang asset paths are their SHA-1. Routine installs use that immutable cache key
        // plus the declared size as a fast check; Repair bypasses this shortcut and hashes it.
        if (!forceRevalidation && CanReuseContentAddressedFile(targetPath, fileInfo, artifact))
        {
            return fileInfo.Length;
        }

        if (!await HasValidHashesAsync(targetPath, artifact, cancellationToken))
        {
            return null;
        }

        return fileInfo.Length;
    }

    private static bool CanReuseContentAddressedFile(
        string targetPath,
        FileInfo fileInfo,
        InstallArtifact artifact) =>
        artifact.IsContentAddressed &&
        artifact.Size is long expectedSize && fileInfo.Length == expectedSize &&
        !string.IsNullOrWhiteSpace(artifact.Sha1) &&
        string.Equals(Path.GetFileName(targetPath), artifact.Sha1, StringComparison.OrdinalIgnoreCase);

    private async Task DownloadWithRetryAsync(
        string targetPath,
        InstallArtifact artifact,
        Action<long, InstallationStage> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = targetPath + ".part";
        Exception? lastFailure = null;
        var urls = new[] { artifact.Url }
            .Concat(artifact.FallbackUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (urls.Length == 0)
        {
            throw new InvalidDataException($"No download URL was provided for {artifact.RelativePath}.");
        }

        foreach (var url in urls)
        {
            EnsureAllowedUrl(url, artifact.AllowedHosts, artifact.RelativePath);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    progress(0, InstallationStage.Downloading);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var finalUri = response.RequestMessage?.RequestUri;
                    if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                        (artifact.AllowedHosts is not null && !IsAllowedHost(finalUri.Host, artifact.AllowedHosts)))
                    {
                        throw new InvalidDataException($"The download redirected to an unsupported host for {artifact.RelativePath}.");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using (var destination = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[BufferSize];
                        long totalRead = 0;
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, cancellationToken);
                            if (read == 0)
                            {
                                break;
                            }

                            totalRead += read;
                            if (artifact.Size is long maximumSize && totalRead > maximumSize)
                            {
                                throw new InvalidDataException($"Downloaded file exceeded its declared size: {artifact.RelativePath}");
                            }

                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            progress(totalRead, InstallationStage.Downloading);
                        }

                        await destination.FlushAsync(cancellationToken);
                    }

                    var fileInfo = new FileInfo(temporaryPath);
                    progress(fileInfo.Length, InstallationStage.Verifying);
                    if (artifact.Size is long expectedSize && fileInfo.Length != expectedSize)
                    {
                        throw new InvalidDataException($"Downloaded file size did not match the official metadata: {artifact.RelativePath}");
                    }

                    if (!await HasValidHashesAsync(temporaryPath, artifact, cancellationToken))
                    {
                        throw new InvalidDataException($"Downloaded file failed its hash verification: {artifact.RelativePath}");
                    }

                    File.Move(temporaryPath, targetPath, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    throw;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    lastFailure = exception;
                    progress(0, InstallationStage.Downloading);
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
                    }
                }
                catch
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }

                    throw;
                }
            }
        }

        throw new IOException($"Could not download {artifact.RelativePath} from any of its {urls.Length} URLs after retries.", lastFailure);
    }

    private static async Task<bool> HasValidHashesAsync(string filePath, InstallArtifact artifact, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Sha1))
        {
            await using var file = File.OpenRead(filePath);
            var hash = await SHA1.HashDataAsync(file, cancellationToken);
            if (!Convert.ToHexString(hash).Equals(artifact.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(artifact.Sha512))
        {
            await using var file = File.OpenRead(filePath);
            var hash = await SHA512.HashDataAsync(file, cancellationToken);
            if (!Convert.ToHexString(hash).Equals(artifact.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetSafeTargetPath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The official metadata contained an invalid file path.");
        }

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (normalizedRelativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "."))
        {
            throw new InvalidDataException("The official metadata contained a path traversal entry.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullTarget = Path.GetFullPath(normalizedRelativePath, fullRoot);
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The official metadata attempted to write outside the Sakana data directory.");
        }

        return fullTarget;
    }

    private static void EnsureAllowedUrl(string url, IReadOnlyList<string>? allowedHosts, string relativePath)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            (allowedHosts is not null && !IsAllowedHost(uri.Host, allowedHosts)))
        {
            throw new InvalidDataException($"The download URL is not an allowed HTTPS URL for {relativePath}.");
        }
    }

    private static bool IsAllowedHost(string host, IReadOnlyList<string> allowedHosts) =>
        allowedHosts.Any(allowedHost => string.Equals(host, allowedHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{allowedHost}", StringComparison.OrdinalIgnoreCase));
}
