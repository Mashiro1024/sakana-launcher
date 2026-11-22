namespace Sakana.Application;

public interface ISakanaLogger
{
    Task InformationAsync(string eventName, string message, CancellationToken cancellationToken = default);
    Task ErrorAsync(string eventName, string message, Exception exception, CancellationToken cancellationToken = default);
}
