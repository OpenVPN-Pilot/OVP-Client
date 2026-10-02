namespace OpenVpnPilot.App.Services.Server;

public sealed partial class SyncEngine
{
    /// <summary>
    /// How often a reachable server is asked for changes when nothing else asks sooner.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a burst of local changes may go on before it is pushed.
    /// </summary>
    public static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The waits after a cycle that could not reach the server, or found it failing. The last one
    /// repeats for as long as that lasts.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly Lock scheduleGate = new();
    private CancellationTokenSource? running;
    private Task? schedule;
    private ITimer? pushTimer;
    private int failuresInARow;
    private bool disposed;

    /// <summary>
    /// Starts the schedule. The first cycle runs at once, on the thread pool; this returns after the
    /// stored status was read and without waiting for the network.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        CancellationTokenSource source;

        lock (scheduleGate)
        {
            if (running is not null)
            {
                return;
            }

            source = new CancellationTokenSource();
            running = source;
            pushTimer = time.CreateTimer(_ => RequestSync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        outbox.PushRequested += OnPushRequested;
        network.AvailabilityChanged += OnAvailabilityChanged;

        await LoadStoredStatusAsync(cancellationToken);
        SyncEngineLog.Started(logger, Server);

        Task loop = Task.Run(() => RunScheduleAsync(source.Token), CancellationToken.None);

        lock (scheduleGate)
        {
            schedule = loop;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source;
        Task? loop;

        lock (scheduleGate)
        {
            source = running;
            loop = schedule;
            running = null;
            schedule = null;
        }

        if (source is null)
        {
            return;
        }

        Detach();
        await source.CancelAsync();

        if (loop is not null)
        {
            await loop.WaitAsync(cancellationToken);
        }

        // A cycle somebody asked for directly ends on its own token; this waits for it as well.
        await cycleGate.WaitAsync(cancellationToken);
        cycleGate.Release();

        source.Dispose();
        Publish(current => current with { State = SyncState.Idle });
        SyncEngineLog.Stopped(logger, Server);
    }

    public void RequestSync() => wake.Writer.TryWrite(true);

    /// <summary>
    /// Ends the schedule without waiting; <see cref="StopAsync"/> is the orderly way out.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? source;

        lock (scheduleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            source = running;
            running = null;
            schedule = null;
        }

        Detach();

        // Cancelled, not disposed: the schedule may still be on its way out and read the token.
        source?.Cancel();
    }

    private void Detach()
    {
        outbox.PushRequested -= OnPushRequested;
        network.AvailabilityChanged -= OnAvailabilityChanged;

        lock (scheduleGate)
        {
            pushTimer?.Dispose();
            pushTimer = null;
        }
    }

    private async Task RunScheduleAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !connection.Wipe.IsRequested)
            {
                if (delay > TimeSpan.Zero)
                {
                    SyncEngineLog.NextCycle(logger, delay);
                    await WaitAsync(delay, cancellationToken);
                }

                SyncCycleResult result;

                try
                {
                    result = await RunExclusiveAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Recording the outcome of a cycle failed too. The schedule is what keeps the
                    // copy current, so it is not given up for one bad cycle.
                    SyncEngineLog.CycleFailed(logger, Server, exception);
                    result = new SyncCycleResult(false, SyncState.ChangesWaiting, 0, 0, false);
                }

                delay = NextDelay(result.State);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    /// <summary>
    /// Waits for the delay, or less when something asks for a cycle meanwhile.
    /// </summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task elapsed = Task.Delay(delay, time, either.Token);
        Task<bool> asked = wake.Reader.WaitToReadAsync(either.Token).AsTask();

        await Task.WhenAny(elapsed, asked);
        await either.CancelAsync();

        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan NextDelay(SyncState state)
    {
        TimeSpan delay;

        if (SyncFailures.BacksOff(state))
        {
            int step = Interlocked.Increment(ref failuresInARow) - 1;
            delay = Backoff[Math.Min(step, Backoff.Count - 1)];
        }
        else
        {
            Interlocked.Exchange(ref failuresInARow, 0);
            delay = Interval;
        }

        return retryAfter is { } asked && asked > delay ? asked : delay;
    }

    private void OnPushRequested(object? sender, EventArgs arguments)
    {
        lock (scheduleGate)
        {
            pushTimer?.Change(PushDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnAvailabilityChanged(object? sender, bool available)
    {
        if (!available)
        {
            return;
        }

        // A network that came back is worth trying at once, and from the start of the back off.
        Interlocked.Exchange(ref failuresInARow, 0);
        RequestSync();
    }
}
