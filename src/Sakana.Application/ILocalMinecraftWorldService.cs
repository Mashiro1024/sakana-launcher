namespace Sakana.Application;

public interface ILocalMinecraftWorldService
{
    Task ExportBackupAsync(
        string instanceDirectoryName,
        string worldName,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<string> ImportBackupAsync(
        string instanceDirectoryName,
        string archivePath,
        string worldName,
        CancellationToken cancellationToken = default);
}
