using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// A wake is reported once, whichever source noticed it, and a burst of address changes once.
/// </summary>
public sealed class SleepWakeMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APlatformWake_IsReportedWithTheTimeTheComputerWentToSleep()
    {
        JumpingTime time = new(Start);
        FakePowerEvents power = new();
        await using SleepWakeMonitor monitor = Create(time, power);
        List<WakeEvent> wakes = Collect(monitor);

        monitor.Start();
        power.RaiseSuspending();
        time.Jump(TimeSpan.FromHours(2));
        power.RaiseResumed();

        WakeEvent wake = Assert.Single(wakes);
        Assert.Equal(Start, wake.SuspendedAt);
        Assert.Equal(TimeSpan.FromHours(2), wake.Duration);
        Assert.Equal(WakeSource.Platform, wake.Source);
        Assert.True(wake.IsLongSleep);
        Assert.Same(wake, monitor.LastWake);
    }

    /// <summary>
    /// Without any word from the platform, the clock jumping is enough.
    /// </summary>
    [Fact]
    public async Task AClockJump_IsReportedAsAWake()
    {
        JumpingTime time = new(Start);
        await using SleepWakeMonitor monitor = Create(time, new FakePowerEvents());
        List<WakeEvent> wakes = Collect(monitor);

        monitor.Start();
        time.Jump(TimeSpan.FromHours(2));
        await AdvanceTickAsync(time, monitor);

        await WaitUntilAsync(() => wakes.Count > 0);

        WakeEvent wake = Assert.Single(wakes);
        Assert.Equal(WakeSource.Clock, wake.Source);
        Assert.Equal(Start, wake.SuspendedAt);
    }

    [Fact]
    public async Task OrdinaryTicks_ReportNothing()
    {
        JumpingTime time = new(Start);
        await using SleepWakeMonitor monitor = Create(time, new FakePowerEvents());
        List<WakeEvent> wakes = Collect(monitor);

        monitor.Start();

        for (int tick = 0; tick < 20; tick++)
        {
            await AdvanceTickAsync(time, monitor);
            await Task.Delay(5);
        }

        Assert.Empty(wakes);
        Assert.Null(monitor.LastWake);
    }

    /// <summary>
    /// The platform and the clock both notice the same wake; it is one wake.
    /// </summary>
    [Fact]
    public async Task ThePlatformAndTheClockNoticingOneWake_ReportItOnce()
    {
        JumpingTime time = new(Start);
        FakePowerEvents power = new();
        await using SleepWakeMonitor monitor = Create(time, power);
        List<WakeEvent> wakes = Collect(monitor);

        monitor.Start();
        time.Jump(TimeSpan.FromHours(2));
        await AdvanceTickAsync(time, monitor);
        await WaitUntilAsync(() => wakes.Count > 0);

        power.RaiseResumed();

        Assert.Single(wakes);
    }

    [Fact]
    public async Task ABurstOfAddressChanges_IsReportedOnceItHasSettled()
    {
        JumpingTime time = new(Start);
        FakeNetworkChanges network = new();
        await using SleepWakeMonitor monitor = new(new FakePowerEvents(), network, time, NullLogger<SleepWakeMonitor>.Instance);
        int changes = 0;
        monitor.NetworkChanged += (_, _) => Interlocked.Increment(ref changes);

        monitor.Start();

        for (int change = 0; change < 6; change++)
        {
            network.Raise();
            time.Timers.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(0, changes);

        time.Timers.Advance(monitor.NetworkSettleDelay);

        Assert.Equal(1, changes);
    }

    private static SleepWakeMonitor Create(JumpingTime time, FakePowerEvents power) =>
        new(power, new FakeNetworkChanges(), time, NullLogger<SleepWakeMonitor>.Instance);

    private static List<WakeEvent> Collect(SleepWakeMonitor monitor)
    {
        List<WakeEvent> wakes = [];
        monitor.Woke += (_, wake) =>
        {
            lock (wakes)
            {
                wakes.Add(wake);
            }
        };

        return wakes;
    }

    /// <summary>
    /// Moves the timers on by one tick, once the monitor is waiting for it. The loop arms its timer
    /// on a thread of its own, and moving the clock before that would leave the timer behind.
    /// </summary>
    private static async Task AdvanceTickAsync(JumpingTime time, SleepWakeMonitor monitor)
    {
        await WaitUntilAsync(() => time.Timers.NextDue is not null);
        time.Timers.Advance(monitor.TickInterval);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A clock whose wall time can jump while its timers stay where they were, which is what a
    /// sleep looks like from inside a process.
    /// </summary>
    private sealed class JumpingTime(DateTimeOffset start) : TimeProvider
    {
        private TimeSpan jumped;

        public ManualTime Timers { get; } = new(start);

        public void Jump(TimeSpan by) => jumped += by;

        public override DateTimeOffset GetUtcNow() => Timers.GetUtcNow() + jumped;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timers.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class FakePowerEvents : ISystemPowerEvents
    {
        public event EventHandler? Suspending;

        public event EventHandler? Resumed;

        public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);

        public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeNetworkChanges : INetworkChangeEvents
    {
        public event EventHandler? AddressChanged;

        public void Raise() => AddressChanged?.Invoke(this, EventArgs.Empty);
    }
}
