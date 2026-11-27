namespace Sakana.Domain;

/// <summary>
/// Shared runtime limit for file downloads. A single instance is used by all download services,
/// so the configured limit is honored even when more than one download batch is active.
/// </summary>
public sealed class DownloadConcurrencyOptions
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _maxConcurrentDownloads;
    private int _activeDownloads;

    public DownloadConcurrencyOptions(int maxConcurrentDownloads = 8)
    {
        Validate(maxConcurrentDownloads);
        _maxConcurrentDownloads = maxConcurrentDownloads;
    }

    public int MaxConcurrentDownloads
    {
        get
        {
            lock (_sync)
            {
                return _maxConcurrentDownloads;
            }
        }
        set
        {
            Validate(value);
            List<Waiter> granted;
            lock (_sync)
            {
                _maxConcurrentDownloads = value;
                granted = GrantAvailableSlotsLocked();
            }

            CompleteGrantedWaiters(granted);
        }
    }

    public ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_sync)
        {
            if (_activeDownloads < _maxConcurrentDownloads && _waiters.Count == 0)
            {
                _activeDownloads++;
                return ValueTask.FromResult<IDisposable>(new DownloadLease(this));
            }

            waiter = new Waiter(cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state =>
                {
                    var (options, queuedWaiter) = ((DownloadConcurrencyOptions, Waiter))state!;
                    options.CancelWaiter(queuedWaiter);
                },
                (this, waiter));

            var disposeImmediately = false;
            lock (_sync)
            {
                waiter.Registration = registration;
                waiter.HasRegistration = true;
                disposeImmediately = waiter.IsCompleted;
            }

            if (disposeImmediately)
            {
                registration.Dispose();
            }
        }

        return new ValueTask<IDisposable>(AwaitLeaseAsync(waiter));
    }

    private async Task<IDisposable> AwaitLeaseAsync(Waiter waiter)
    {
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            CancellationTokenRegistration registration = default;
            var hasRegistration = false;
            lock (_sync)
            {
                if (waiter.HasRegistration)
                {
                    registration = waiter.Registration;
                    hasRegistration = true;
                }
            }

            if (hasRegistration)
            {
                registration.Dispose();
            }
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        var canceled = false;
        lock (_sync)
        {
            if (waiter.Node is not null)
            {
                _waiters.Remove(waiter.Node);
                waiter.Node = null;
                waiter.IsCompleted = true;
                canceled = true;
            }
        }

        if (canceled)
        {
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private void Release()
    {
        List<Waiter> granted;
        lock (_sync)
        {
            if (_activeDownloads <= 0)
            {
                throw new InvalidOperationException("A download concurrency slot was released more than once.");
            }

            _activeDownloads--;
            granted = GrantAvailableSlotsLocked();
        }

        CompleteGrantedWaiters(granted);
    }

    private List<Waiter> GrantAvailableSlotsLocked()
    {
        var granted = new List<Waiter>();
        while (_activeDownloads < _maxConcurrentDownloads && _waiters.First is { } first)
        {
            _waiters.RemoveFirst();
            var waiter = first.Value;
            waiter.Node = null;
            if (waiter.IsCompleted)
            {
                continue;
            }

            waiter.IsCompleted = true;
            _activeDownloads++;
            granted.Add(waiter);
        }

        return granted;
    }

    private void CompleteGrantedWaiters(IEnumerable<Waiter> granted)
    {
        foreach (var waiter in granted)
        {
            waiter.Completion.TrySetResult(new DownloadLease(this));
        }
    }

    private static void Validate(int value)
    {
        if (value is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "下载并发数必须在 1 到 256 之间。");
        }
    }

    private sealed class Waiter(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public bool HasRegistration { get; set; }
        public bool IsCompleted { get; set; }
    }

    private sealed class DownloadLease(DownloadConcurrencyOptions options) : IDisposable
    {
        private DownloadConcurrencyOptions? _options = options;

        public void Dispose() => Interlocked.Exchange(ref _options, null)?.Release();
    }
}
