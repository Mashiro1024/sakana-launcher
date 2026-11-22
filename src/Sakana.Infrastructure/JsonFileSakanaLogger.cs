using System.Text.Json;
using Sakana.Application;

namespace Sakana.Infrastructure;

public sealed class JsonFileSakanaLogger : ISakanaLogger
{
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonFileSakanaLogger(string dataDirectory)
    {
        _logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public Task InformationAsync(string eventName, string message, CancellationToken cancellationToken = default) =>
        WriteAsync("Information", eventName, message, exception: null, cancellationToken);

    public Task ErrorAsync(string eventName, string message, Exception exception, CancellationToken cancellationToken = default) =>
        WriteAsync("Error", eventName, message, exception, cancellationToken);

    private async Task WriteAsync(
        string level,
        string eventName,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new LogEntry(
            timestamp,
            level,
            eventName,
            message,
            exception?.GetType().FullName,
            exception?.Message,
            exception?.StackTrace);
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        var filePath = Path.Combine(_logDirectory, $"sakana-{timestamp:yyyyMMdd}.jsonl");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(filePath, line, cancellationToken);
        }
        catch (IOException)
        {
            // Logging must not prevent the launcher UI from opening or saving data.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must not prevent the launcher UI from opening or saving data.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed record LogEntry(
        DateTimeOffset TimestampUtc,
        string Level,
        string EventName,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage,
        string? StackTrace);
}
