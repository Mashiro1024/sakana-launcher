namespace Sakana.Application;

public sealed class MicrosoftDeviceCodePrompt(
    string deviceCode,
    string userCode,
    string verificationUri,
    string message,
    DateTimeOffset expiresAtUtc,
    int pollingIntervalSeconds)
{
    /// <summary>Short-lived OAuth device secret. Keep in memory and out of UI and logs.</summary>
    public string DeviceCode { get; } = deviceCode;
    public string UserCode { get; } = userCode;
    public string VerificationUri { get; } = verificationUri;
    public string Message { get; } = message;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public int ExpiresInSeconds => Math.Max(0, (int)(ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds);
    public int PollingIntervalSeconds { get; } = pollingIntervalSeconds;

    public override string ToString() => $"MicrosoftDeviceCodePrompt(UserCode={UserCode}, DeviceCode=[redacted])";
}
