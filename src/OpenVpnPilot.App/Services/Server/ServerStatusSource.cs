using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// What the application shows about the server: the synchronisation, the session and how the server
/// answers, put together in one snapshot for the status bar, the banners, the tray and the storage
/// settings.
/// </summary>
/// <remarks>
/// <para>
/// Exists only in Server mode. How the server answers is asked separately from the synchronisation,
/// with <c>GET /api/v1/server/info</c> every 30 seconds for the round trip, and <c>/health/ready</c>
/// for whether it can serve; both are anonymous and carry nothing about the person. While the server
/// cannot be reached it is asked less often.
/// </para>
/// <para>
/// Until <see cref="Begin"/> the snapshot says nothing about the session, so the moment between the
/// window appearing and the stored session being picked up is not shown as "sign in required".
/// </para>
/// </remarks>
public interface IServerStatusSource
{
    public ServerStatusSnapshot Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changed, on a background thread.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts asking the server how it answers and lets the session count. Called once the stored
    /// session has been picked up.
    /// </summary>
    public void Begin();

    /// <summary>
    /// Asks for a synchronisation now, when somebody is signed in.
    /// </summary>
    public void RequestSync();
}

/// <summary>
/// How the server answered the last time it was asked.
/// </summary>
/// <param name="Reachable">Null before the first answer.</param>
/// <param name="Latency">The round trip of the last answer to <c>server/info</c>.</param>
/// <param name="Degraded">True when the server answered but its readiness check said it cannot serve.</param>
/// <param name="Info">What the server said about itself.</param>
public sealed record ServerReachability(bool? Reachable, TimeSpan? Latency, bool Degraded, ServerInfoResponse? Info)
{
    public static ServerReachability Unknown { get; } = new(null, null, false, null);

    /// <summary>
    /// True when the last attempt was refused over the server's certificate or the TLS agreement.
    /// </summary>
    /// <remarks>
    /// Not reachable either, but not offline: nothing about the network will put it right, so it is
    /// shown as the configuration problem it is.
    /// </remarks>
    public bool CertificateUntrusted { get; init; }
}

/// <summary>
/// Everything shown about the server at one moment. Nothing in it is secret.
/// </summary>
/// <param name="Address">The server's address in its normal form.</param>
/// <param name="Sync">The synchronisation's own status.</param>
/// <param name="SessionKnown">False until the stored session was picked up.</param>
/// <param name="SignedIn">True while somebody is signed in.</param>
/// <param name="User">The person signed in or last seen.</param>
/// <param name="Reachability">How the server answered last.</param>
public sealed record ServerStatusSnapshot(
    Uri Address,
    SyncStatus Sync,
    bool SessionKnown,
    bool SignedIn,
    CurrentUserResponse? User,
    ServerReachability Reachability)
{
    public string Host => Address.IsDefaultPort ? Address.Host : Address.Authority;

    /// <summary>
    /// The state to show, which is the synchronisation's except where the session or the latest
    /// answer of the server knows better.
    /// </summary>
    public SyncState State
    {
        get
        {
            // Before anything else: neither signing in nor waiting for the network gets past it.
            if (Reachability.CertificateUntrusted)
            {
                return SyncState.CertificateUntrusted;
            }

            if (SessionKnown && !SignedIn && Sync.State is not (SyncState.ClientOutdated or SyncState.CertificateUntrusted))
            {
                return SyncState.SignInRequired;
            }

            // The synchronisation asks every few minutes, the round trip every thirty seconds, so the
            // round trip is the first to notice the server going away or coming back unwell.
            if (Sync.State is SyncState.Idle or SyncState.Synchronised or SyncState.ChangesWaiting)
            {
                if (Reachability.Reachable == false)
                {
                    return SyncState.Offline;
                }

                if (Reachability.Degraded)
                {
                    return SyncState.Degraded;
                }
            }

            return Sync.State;
        }
    }
}

/// <inheritdoc cref="IServerStatusSource"/>
public sealed class ServerStatusSource : IServerStatusSource, IDisposable
{
    /// <summary>
    /// How often a server that answers is asked how quickly it does.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The waits while the server does not answer; the last one repeats.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

    private readonly IServerApi api;
    private readonly IServerSession session;
    private readonly ISyncEngine engine;
    private readonly IServerWipeSignal wipe;
    private readonly TimeProvider time;
    private readonly ILogger<ServerStatusSource> logger;

    private readonly Lock gate = new();

    // Cancelled on disposal, so a probe under way stops with the application.
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stopping;
    private ServerReachability reachability = ServerReachability.Unknown;
    private bool sessionKnown;
    private int failuresInARow;
    private ITimer? timer;
    private bool disposed;

    public ServerStatusSource(
        IServerApi api,
        IServerSession session,
        ISyncEngine engine,
        IServerWipeSignal wipe,
        TimeProvider time,
        ILogger<ServerStatusSource> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(wipe);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        this.api = api;
        this.session = session;
        this.engine = engine;
        this.wipe = wipe;
        this.time = time;
        this.logger = logger;

        stopping = lifetime.Token;

        engine.StatusChanged += OnChanged;
        session.Changed += OnSessionChanged;
    }

    public event EventHandler? Changed;

    public ServerStatusSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return new ServerStatusSnapshot(api.BaseAddress, engine.Status, sessionKnown, session.IsSignedIn, session.User, reachability);
            }
        }
    }

    public void Begin()
    {
        lock (gate)
        {
            if (disposed || timer is not null)
            {
                return;
            }

            sessionKnown = true;
            timer = time.CreateTimer(_ => _ = ProbeAndRescheduleAsync(stopping), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestSync()
    {
        if (session.IsSignedIn)
        {
            ServerStatusLog.SyncRequested(logger, "status");
            engine.RequestSync();
        }
    }

    /// <summary>
    /// Asks the server once and publishes what it said.
    /// </summary>
    /// <returns>True when the server answered.</returns>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        ServerResult<ServerInfoResponse> info = await api.GetServerInfoAsync(cancellationToken);
        TimeSpan latency = Stopwatch.GetElapsedTime(started);

        ServerReachability next;

        if (info.IsSuccess)
        {
            ServerResult ready = await api.GetReadinessAsync(cancellationToken);

            // Only an answer that says the server cannot serve counts; a readiness check that could
            // not be asked says nothing more than the round trip already did.
            bool degraded = ready.Outcome == ServerOutcome.Problem && ready.Status is >= 500;
            next = new ServerReachability(true, latency, degraded, info.Value);

            ServerStatusLog.Probed(logger, api.BaseAddress.Host, (long)latency.TotalMilliseconds, !degraded);

            if (degraded && !reachability.Degraded)
            {
                ServerStatusLog.Degraded(logger, api.BaseAddress.Host, ready.Status, ready.RequestId);
            }
        }
        else if (info.Outcome is ServerOutcome.Offline or ServerOutcome.TlsRefused)
        {
            next = reachability with
            {
                Reachable = false,
                Latency = null,
                Degraded = false,
                CertificateUntrusted = info.Outcome == ServerOutcome.TlsRefused,
            };
        }
        else
        {
            // Answered, though not with what was asked for: reachable, but nothing to measure.
            next = reachability with { Reachable = true, Latency = null, CertificateUntrusted = false };
        }

        bool changed;

        lock (gate)
        {
            if (next.Reachable == false && reachability.Reachable != false)
            {
                ServerStatusLog.Unreachable(logger, api.BaseAddress.Host, info.Outcome);
            }
            else if (next.Reachable == true && reachability.Reachable == false)
            {
                ServerStatusLog.Reachable(logger, api.BaseAddress.Host);
            }

            changed = next != reachability;
            reachability = next;
            failuresInARow = next.Reachable == false ? failuresInARow + 1 : 0;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return next.Reachable == true;
    }

    /// <summary>
    /// How long to wait before asking again.
    /// </summary>
    public TimeSpan NextDelay
    {
        get
        {
            lock (gate)
            {
                return failuresInARow == 0 ? Interval : Backoff[Math.Min(failuresInARow, Backoff.Count) - 1];
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer?.Dispose();
            timer = null;
        }

        lifetime.Cancel();
        lifetime.Dispose();

        engine.StatusChanged -= OnChanged;
        session.Changed -= OnSessionChanged;
    }

    private async Task ProbeAndRescheduleAsync(CancellationToken cancellationToken)
    {
        if (wipe.IsRequested)
        {
            return;
        }

        try
        {
            await ProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed while asking; there is nobody left to show the answer to, nor a next time.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The transport turns every failure of the network into an outcome, so this is a defect;
            // it is written down and the next attempt is made as usual.
            ServerStatusLog.ProbeFailed(logger, exception);
        }

        lock (gate)
        {
            if (!disposed && !wipe.IsRequested)
            {
                timer?.Change(NextDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnSessionChanged(object? sender, ServerSessionChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
