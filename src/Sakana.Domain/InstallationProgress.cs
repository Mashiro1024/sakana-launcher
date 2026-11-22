namespace Sakana.Domain;

public enum InstallationStage
{
    Resolving,
    Downloading,
    Verifying,
    Extracting,
    Completed
}

public sealed record InstallationProgress(
    Guid InstanceId,
    InstallationStage Stage,
    long BytesDownloaded,
    long TotalBytes,
    int FilesCompleted,
    int FilesTotal,
    string? CurrentFile = null)
{
    public double PercentComplete => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesDownloaded / TotalBytes * 100d, 0d, 100d);
}
