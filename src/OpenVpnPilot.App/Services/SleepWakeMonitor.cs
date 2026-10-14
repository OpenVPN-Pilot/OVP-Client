using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// What the rest of the application learns about sleeps and network changes.
/// </summary>
public interface IWakeMonitor
{
    /// <summary>
    /// Raised once per wake, however many sources noticed it.
    /// </summary>
    public event EventHandler<WakeEvent>? Woke;

    /// <summary>
    /// Raised a moment after the interface addresses stopped changing.
    /// </summary>
    public event EventHandler? NetworkChanged;

    /// <summary>
    /// The most recent wake, or null when the computer has not slept since the application started.
    /// </summary>
    public WakeEvent? LastWake { get; }
}

/// <summary>
/// One wake: when the computer went to sleep, as far as that is known, and when it came back.
/// </summary>
/// <param name="SuspendedAt">When it went to sleep, or null when nothing recorded it.</param>
/// <param name="ResumedAt">When it woke.</param>
/// <param name="Source">What noticed it.</param>
public sealed record WakeEvent(DateTimeOffset? SuspendedAt, DateTimeOffset ResumedAt, WakeSource Source)
{
    /// <summary>
    /// The shortest sleep after which a tunnel is not expected to have survived unnoticed.
    /// </summary>
    /// <remarks>
    /// A server's keepalive timeout is a minute or two, and so is the time a NAT on the way keeps a
    /// silent mapping. A tunnel that slept for less may well be fine and has to prove otherwise; one
    /// that slept for longer has to prove that it is.
    /// </remarks>
    public static readonly TimeSpan LongSleep = TimeSpan.FromMinutes(1);

    public TimeSpan? Duration => SuspendedAt is { } suspended ? ResumedAt - suspended : null;

    /// <summary>
    /// True when the sleep lasted long enough that nothing about a tunnel can be assumed, or when
    /// its length is unknown.
    /// </summary>
    public bool IsLongSleep => Duration is not { } duration || duration >= LongSleep;
}

public enum WakeSource
{
    /// <summary>
    /// The operating system said so.
    /// </summary>
    Platform,

    /// <summary>
    /// The clock jumped further than the time that passed for this process.
    /// </summary>
    Clock,
}

/// <summary>
/// Notices the computer waking, from the operating system and from the clock, and the network
/// changing, and reports each once.
/// </summary>
/// <remarks>
/// Two sources for one event, because neither is enough alone. The operating system says when it
/// sleeps and wakes, but its notification can fail to register and, on some machines, is not sent
/// for every kind of standby. The clock always knows: a timer set for five seconds that finds the
/// wall clock an hour further on has been asleep for most of that hour, whatever the platform said.
/// The clock is checked against the wall time rather than against a timer, because what a timer
/// measures across a sleep differs between platforms and is not what is being asked.
///
/// Whichever source notices a wake first reports it, and the other one, arriving moments later, is
/// recognised as the same wake. A process that was merely held up for a minute, as App Nap can do
/// to an application without a window, is reported too. That is harmless: what follows a wake is a
/// check of every tunnel, which a healthy tunnel passes in a moment.
///
/// Address changes arrive in bursts, one per interface and per family, so they are reported once
/// the burst has settled.
/// </remarks>
public sealed class SleepWakeMonitor : IWakeMonitor, IAsyncDisposable
{
    /// <summary>
    /// How long two reports may be apart and still describe the same wake.
    /// </summary>
    private static readonly TimeSpan SameWake = TimeSpan.FromMinutes(2);

    private readonly ISystemPowerEvents power;
    private readonly INetworkChangeEvents network;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SleepWakeMonitor> logger;
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();

    private ITimer? settle;
    private Task? ticking;
    private DateTimeOffset lastTick;
    private DateTimeOffset? suspendedAt;
    private WakeEvent? lastWake;
    private bool started;
    private bool disposed;

    public SleepWakeMonitor(
        ISystemPowerEvents power,
        INetworkChangeEvents network,
        TimeProvider timeProvider,
        ILogger<SleepWakeMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.power = power;
        this.network = network;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public event EventHandler<WakeEvent>? Woke;

    public event EventHandler? NetworkChanged;

    /// <summary>
    /// How often the clock is looked at.
    /// </summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much further than expected the clock has to have moved to count as a sleep.
    /// </summary>
    public TimeSpan ClockJumpThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the addresses have to stay unchanged before a change is reported.
    /// </summary>
    public TimeSpan NetworkSettleDelay { get; init; } = TimeSpan.FromSeconds(3);

    public WakeEvent? LastWake
    {
        get
        {
            lock (gate)
            {
                return lastWake;
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (started)
            {
                return;
            }

            started = true;
            lastTick = timeProvider.GetUtcNow();
        }

        power.Suspending += OnSuspending;
        power.Resumed += OnResumed;
        network.AddressChanged += OnAddressChanged;
        settle = timeProvider.CreateTimer(_ => OnNetworkSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        power.Start();
        ticking = Task.Run(() => TickAsync(lifetime.Token), CancellationToken.None);
    }

    private void OnSuspending(object? sender, EventArgs e)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (gate)
        {
            suspendedAt = now;
        }

        WakeLog.Suspending(logger);
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset? asleepSince;

        lock (gate)
        {
            asleepSince = suspendedAt;

            // The tick that follows would otherwise see the whole sleep as a jump and report it again.
            lastTick = now;
        }

        Report(new WakeEvent(asleepSince, now, WakeSource.Platform));
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset previous;

            lock (gate)
            {
                previous = lastTick;
                lastTick = now;
            }

            if (now - previous > TickInterval + ClockJumpThreshold)
            {
                Report(new WakeEvent(previous, now, WakeSource.Clock));
            }
        }
    }

    private void Report(WakeEvent wake)
    {
        lock (gate)
        {
            if (lastWake is { } previous && (wake.ResumedAt - previous.ResumedAt).Duration() < SameWake)
            {
                return;
            }

            lastWake = wake;
            suspendedAt = null;
        }

        WakeLog.Woke(logger, wake.Source, wake.Duration?.TotalSeconds);
        Woke?.Invoke(this, wake);
    }

    private void OnAddressChanged(object? sender, EventArgs e) =>
        settle?.Change(NetworkSettleDelay, Timeout.InfiniteTimeSpan);

    private void OnNetworkSettled()
    {
        if (disposed)
        {
            return;
        }

        WakeLog.NetworkChanged(logger);
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        power.Suspending -= OnSuspending;
        power.Resumed -= OnResumed;
        network.AddressChanged -= OnAddressChanged;

        if (settle is not null)
        {
            await settle.DisposeAsync();
        }

        await lifetime.CancelAsync();

        if (ticking is not null)
        {
            await ticking;
        }

        lifetime.Dispose();
    }
}

/// <summary>
/// Source generated log messages for <see cref="SleepWakeMonitor"/>.
/// </summary>
internal static partial class WakeLog
{
    [LoggerMessage(EventId = 3320, Level = LogLevel.Information, Message = "The computer is going to sleep.")]
    public static partial void Suspending(ILogger logger);

    [LoggerMessage(
        EventId = 3321,
        Level = LogLevel.Information,
        Message = "The computer woke, noticed by {Source}, after a sleep of {Seconds} seconds.")]
    public static partial void Woke(ILogger logger, WakeSource source, double? seconds);

    [LoggerMessage(EventId = 3322, Level = LogLevel.Debug, Message = "The network interface addresses changed.")]
    public static partial void NetworkChanged(ILogger logger);
}
