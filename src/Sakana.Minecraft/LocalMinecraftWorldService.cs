using System.IO.Compression;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Minecraft;

/// <summary>Creates and restores portable ZIP backups of Minecraft worlds without overwriting existing saves.</summary>
public sealed class LocalMinecraftWorldService(string dataDirectory) : ILocalMinecraftWorldService
{
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumArchiveBytes = 32L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 100L * 1024 * 1024 * 1024;

    public async Task ExportBackupAsync(
        string instanceDirectoryName,
        string worldName,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sourceDirectory = GetWorldDirectory(instanceDirectoryName, worldName);
        var destination = ValidateZipPath(destinationPath);
        if (IsWithin(sourceDirectory, destination))
        {
            throw new InvalidOperationException("World backups must be saved outside the world folder.");
        }

        if (File.Exists(destination))
        {
            throw new IOException("A file already exists at the selected backup path. Choose another name to preserve it.");
        }

        var parentDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The backup path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var pendingDirectories = new Stack<string>();
                pendingDirectories.Push(sourceDirectory);
                while (pendingDirectories.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentDirectory = pendingDirectories.Pop();
                    foreach (var entryPath in Directory.EnumerateFileSystemEntries(currentDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(entryPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidDataException($"A world backup cannot include a symbolic link or reparse point: {Path.GetFileName(entryPath)}");
                        }

                        var relativePath = Path.GetRelativePath(sourceDirectory, entryPath).Replace('\\', '/');
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pendingDirectories.Push(entryPath);
                            var directoryEntry = archive.CreateEntry(relativePath.TrimEnd('/') + "/", CompressionLevel.Fastest);
                            directoryEntry.LastWriteTime = ToZipTimestamp(Directory.GetLastWriteTimeUtc(entryPath));
                            continue;
                        }

                        var info = new FileInfo(entryPath);
                        var archiveEntry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);
                        archiveEntry.LastWriteTime = ToZipTimestamp(info.LastWriteTimeUtc);
                        await using var input = new FileStream(
                            entryPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var entryStream = archiveEntry.Open();
                        await input.CopyToAsync(entryStream, 128 * 1024, cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<string> ImportBackupAsync(
        string instanceDirectoryName,
        string archivePath,
        string worldName,
        CancellationToken cancellationToken = default)
    {
        var normalizedWorldName = NormalizeWorldName(worldName);
        var sourceArchive = Path.GetFullPath(archivePath);
        if (!File.Exists(sourceArchive))
        {
            throw new FileNotFoundException("The selected world backup could not be found.", sourceArchive);
        }

        var archiveInfo = new FileInfo(sourceArchive);
        if (archiveInfo.Length <= 0 || archiveInfo.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("World backup archives must be larger than 0 bytes and no larger than 32 GB.");
        }

        var savesDirectory = GetSavesDirectory(instanceDirectoryName);
        Directory.CreateDirectory(savesDirectory);
        EnsureNotReparsePoint(savesDirectory);
        var gameDirectory = Path.GetDirectoryName(savesDirectory)
            ?? throw new InvalidOperationException("The instance game directory could not be resolved.");
        EnsureNotReparsePoint(gameDirectory);
        var destinationDirectory = Path.GetFullPath(Path.Combine(savesDirectory, normalizedWorldName));
        if (!IsWithin(savesDirectory, destinationDirectory))
        {
            throw new InvalidDataException("The requested world name resolves outside this instance's saves directory.");
        }

        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new IOException($"A save named “{normalizedWorldName}” already exists. Choose another world name; existing saves are never overwritten.");
        }

        var stagingDirectory = Path.Combine(gameDirectory, $".sakana-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var stagingPrefix = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            using var archive = ZipFile.OpenRead(sourceArchive);
            if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"The world backup must contain between 1 and {MaximumArchiveEntries} entries.");
            }

            var normalizedEntries = new List<NormalizedEntry>(archive.Entries.Count);
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var relativePath = NormalizeArchivePath(entry.FullName, allowEmpty: isDirectory);
                if (relativePath.Length == 0)
                {
                    continue;
                }

                var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixFileType == 0xA000)
                {
                    throw new InvalidDataException("World backup archives cannot contain symbolic links.");
                }

                if (!isDirectory)
                {
                    if (entry.Length > MaximumExpandedBytes - expandedBytes)
                    {
                        throw new InvalidDataException("The expanded world backup is larger than the 100 GB safety limit.");
                    }

                    expandedBytes += entry.Length;
                }

                normalizedEntries.Add(new NormalizedEntry(entry, relativePath, isDirectory));
            }

            var prefix = GetWorldArchivePrefix(normalizedEntries);
            var hasLevelData = false;
            foreach (var item in normalizedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = StripPrefix(item.RelativePath, prefix);
                if (relativePath is null || relativePath.Length == 0)
                {
                    continue;
                }

                if (string.Equals(relativePath, "level.dat", StringComparison.OrdinalIgnoreCase) && !item.IsDirectory)
                {
                    hasLevelData = true;
                }

                var targetPath = Path.GetFullPath(Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!targetPath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A world backup entry resolves outside the temporary import folder.");
                }

                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                var parentDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidDataException("A world backup file has an invalid path.");
                Directory.CreateDirectory(parentDirectory);
                await using var input = item.Entry.Open();
                await using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken);
            }

            if (!hasLevelData || !File.Exists(Path.Combine(stagingDirectory, "level.dat")))
            {
                throw new InvalidDataException("This ZIP does not contain a Minecraft world with level.dat at its root.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destinationDirectory);
            return normalizedWorldName;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private string GetWorldDirectory(string instanceDirectoryName, string worldName)
    {
        var normalizedName = NormalizeWorldName(worldName);
        var savesDirectory = GetSavesDirectory(instanceDirectoryName);
        var worldDirectory = Path.GetFullPath(Path.Combine(savesDirectory, normalizedName));
        if (!IsWithin(savesDirectory, worldDirectory) || !Directory.Exists(worldDirectory))
        {
            throw new DirectoryNotFoundException("The selected world could not be found in this instance.");
        }

        EnsureNotReparsePoint(savesDirectory);
        EnsureNotReparsePoint(worldDirectory);
        if (!File.Exists(Path.Combine(worldDirectory, "level.dat")))
        {
            throw new InvalidDataException("The selected folder is not a Minecraft world because it has no level.dat file.");
        }

        return worldDirectory;
    }

    private string GetSavesDirectory(string instanceDirectoryName)
    {
        var validatedName = InstanceDirectoryName.Validate(instanceDirectoryName);
        var gameDirectory = Path.Combine(dataDirectory, "instances", validatedName, "game");
        return Path.GetFullPath(Path.Combine(gameDirectory, "saves"));
    }

    private static string ValidateZipPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a destination with the .zip extension.");
        }

        return fullPath;
    }

    private static string NormalizeWorldName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A world name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized is "." or ".." ||
            !string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal) ||
            normalized.EndsWith(' ') || normalized.EndsWith('.') ||
            normalized.Any(char.IsControl) || Path.GetInvalidFileNameChars().Any(normalized.Contains))
        {
            throw new ArgumentException("The world name contains characters that cannot be used for a folder name.", nameof(name));
        }

        var deviceName = normalized.Split('.')[0].TrimEnd(' ');
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Length == 4 &&
            (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            deviceName[3] is >= '1' and <= '9')
        {
            throw new ArgumentException("This name is reserved by Windows. Choose another world name.", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeArchivePath(string path, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(path))
        {
            return allowEmpty ? string.Empty : throw new InvalidDataException("A world backup contains an empty path.");
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return allowEmpty ? string.Empty : throw new InvalidDataException("A world backup contains an empty path.");
        }

        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new InvalidDataException("A world backup contains a rooted or drive-qualified path.");
        }

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(char.IsControl) || Path.GetInvalidFileNameChars().Any(segment.Contains))
            {
                throw new InvalidDataException("A world backup contains an unsafe path segment.");
            }
        }

        return string.Join('/', segments);
    }

    private static string GetWorldArchivePrefix(IReadOnlyList<NormalizedEntry> entries)
    {
        var levelDataPaths = entries
            .Where(item => !item.IsDirectory && item.RelativePath.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RelativePath[..^"level.dat".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.Any(item => !item.IsDirectory && string.Equals(item.RelativePath, "level.dat", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        if (levelDataPaths.Length != 1)
        {
            throw new InvalidDataException("This ZIP must contain one Minecraft world folder with a level.dat file.");
        }

        return levelDataPaths[0];
    }

    private static string? StripPrefix(string relativePath, string prefix)
    {
        if (prefix.Length == 0)
        {
            return relativePath;
        }

        if (string.Equals(relativePath.TrimEnd('/'), prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? relativePath[prefix.Length..]
            : null;
    }

    private static bool IsWithin(string parent, string child)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        return fullChild.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("World archives cannot read or write through symbolic links or reparse points.");
        }
    }

    private static DateTimeOffset ToZipTimestamp(DateTime utcTime)
    {
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc));
        if (timestamp.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (timestamp.Year > 2107) return new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return timestamp;
    }

    private sealed record NormalizedEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
