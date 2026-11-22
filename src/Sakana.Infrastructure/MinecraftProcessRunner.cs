using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Sakana.Application;
using Sakana.Domain;

namespace Sakana.Infrastructure;

public sealed class MinecraftProcessRunner : IMinecraftProcessRunner
{
    private const int OutputBufferCapacity = 2048;

    public Task<IMinecraftGameProcess> StartAsync(
        MinecraftLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(plan.JavaExecutablePath))
        {
            throw new FileNotFoundException("The configured Java executable was not found.", plan.JavaExecutablePath);
        }

        if (!Directory.Exists(plan.WorkingDirectory))
        {
            throw new DirectoryNotFoundException("The configured Minecraft working directory was not found.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.JavaExecutablePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in plan.EnvironmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows did not start the Minecraft Java process.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            IMinecraftGameProcess gameProcess = new MinecraftGameProcess(process, plan);
            return Task.FromResult(gameProcess);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process may not have started, or may have exited during cleanup.
        }
        catch (Win32Exception)
        {
            // Best effort cleanup while unwinding a failed launch.
        }
    }

    private sealed class MinecraftGameProcess : IMinecraftGameProcess
    {
        private const string RedactedValue = "[redacted]";
        private readonly Process _process;
        private readonly Channel<MinecraftProcessOutput> _output;
        private readonly Task _standardOutputPump;
        private readonly Task _standardErrorPump;
        private readonly Task<int> _completion;
        private readonly IReadOnlyList<string> _sensitiveValues;
        private int _disposed;

        public MinecraftGameProcess(Process process, MinecraftLaunchPlan plan)
        {
            _process = process;
            InstanceId = plan.InstanceId;
            VersionId = plan.VersionId;
            ProcessId = process.Id;
            StartedAt = DateTimeOffset.UtcNow;
            _sensitiveValues = plan.SensitiveValues.OrderByDescending(value => value.Length).ToArray();
            _output = Channel.CreateBounded<MinecraftProcessOutput>(new BoundedChannelOptions(OutputBufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _standardOutputPump = PumpAsync(process.StandardOutput, MinecraftOutputStream.StandardOutput);
            _standardErrorPump = PumpAsync(process.StandardError, MinecraftOutputStream.StandardError);
            _completion = CompleteAsync();
        }

        public Guid InstanceId { get; }
        public string VersionId { get; }
        public int ProcessId { get; }
        public DateTimeOffset StartedAt { get; }
        public bool HasExited => _completion.IsCompleted;
        public Task<int> Completion => _completion;

        public IAsyncEnumerable<MinecraftProcessOutput> ReadOutputAsync(CancellationToken cancellationToken = default) =>
            _output.Reader.ReadAllAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_completion.IsCompleted)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (ObjectDisposedException) when (_completion.IsCompleted)
                {
                    // The completion observer released the process handle as it exited.
                }
                catch (InvalidOperationException)
                {
                    // It exited while StopAsync was checking its state.
                }
                catch (Win32Exception) when (_completion.IsCompleted)
                {
                    // It exited while StopAsync was signaling it.
                }
            }

            await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            _process.Dispose();
        }

        private async Task PumpAsync(StreamReader reader, MinecraftOutputStream stream)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    _output.Writer.TryWrite(new MinecraftProcessOutput(
                        DateTimeOffset.UtcNow,
                        stream,
                        Redact(line)));
                }
            }
            catch (ObjectDisposedException)
            {
                // Process disposal can close redirected streams after exit.
            }
            catch (IOException)
            {
                // A redirected stream can close while the child process is exiting.
            }
        }

        private async Task<int> CompleteAsync()
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(_standardOutputPump, _standardErrorPump).ConfigureAwait(false);
                return _process.ExitCode;
            }
            finally
            {
                _output.Writer.TryComplete();
            }
        }

        private string Redact(string line)
        {
            foreach (var value in _sensitiveValues)
            {
                line = line.Replace(value, RedactedValue, StringComparison.Ordinal);
            }

            return line;
        }
    }
}
