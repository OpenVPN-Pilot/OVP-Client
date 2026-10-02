using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data.Entities;
using SyncState = OpenVpnPilot.App.Services.Server.SyncState;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// When cycles run: one at a time, on a schedule, sooner after a change, and less often while the
/// server cannot be reached.
/// </summary>
public sealed class SyncEngineScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SynchronizeAsync_CalledTwiceAtOnce_RunsTheCyclesOneAfterTheOther()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();

        int running = 0;
        int mostAtOnce = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Server.On(HttpMethod.Get, "/api/v1/sync/changes", async request =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return harness.Server.DefaultAnswer(request);
        });

        Task<SyncCycleResult> first = harness.Engine.SynchronizeAsync(CancellationToken.None);
        Task<SyncCycleResult> second = harness.Engine.SynchronizeAsync(CancellationToken.None);

        await SyncHarness.EventuallyAsync(() => harness.Count(HttpMethod.Get, "/api/v1/sync/changes") == 1, "the first pull");
        await Task.Delay(100);
        Assert.Equal(1, harness.Count(HttpMethod.Get, "/api/v1/sync/changes"));

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, harness.Count(HttpMethod.Get, "/api/v1/sync/changes"));
        Assert.Equal(1, mostAtOnce);
    }

    [Fact]
    public async Task StartAsync_ServerUnreachable_BacksOffAndStartsOverWhenTheNetworkReturns()
    {
        ManualTime time = new(Start);
        await using SyncHarness harness = await SyncHarness.CreateAsync(time);
        harness.Server.Offline = true;

        int Pulls() => harness.Count(HttpMethod.Get, "/api/v1/sync/changes");

        await harness.Engine.StartAsync(CancellationToken.None);

        int expectedPulls = 1;

        foreach (int seconds in new[] { 5, 15, 30, 60, 60 })
        {
            await SyncHarness.EventuallyAsync(() => Pulls() == expectedPulls && time.NextDue is not null, $"pull {expectedPulls}");
            Assert.Equal(TimeSpan.FromSeconds(seconds), time.NextDue);
            Assert.Equal(SyncState.Offline, harness.Engine.Status.State);

            time.Advance(TimeSpan.FromSeconds(seconds));
            expectedPulls++;
        }

        await SyncHarness.EventuallyAsync(() => Pulls() == expectedPulls && time.NextDue is not null, "the pull after the back off");
        Assert.Equal(TimeSpan.FromSeconds(60), time.NextDue);

        harness.NetworkAvailability.Raise(true);
        expectedPulls++;

        await SyncHarness.EventuallyAsync(() => Pulls() == expectedPulls && time.NextDue is not null, "the pull for the network");
        Assert.Equal(TimeSpan.FromSeconds(5), time.NextDue);
    }

    [Fact]
    public async Task StartAsync_ServerReachable_SynchronisesEveryTwoMinutes()
    {
        ManualTime time = new(Start);
        await using SyncHarness harness = await SyncHarness.CreateAsync(time);

        await harness.Engine.StartAsync(CancellationToken.None);

        await SyncHarness.EventuallyAsync(() => harness.Engine.Status.State == SyncState.Synchronised && time.NextDue is not null, "the first cycle");
        Assert.Equal(SyncEngine.Interval, time.NextDue);

        time.Advance(SyncEngine.Interval);

        await SyncHarness.EventuallyAsync(() => harness.Count(HttpMethod.Get, "/api/v1/sync/changes") == 2, "the second cycle");
    }

    [Fact]
    public async Task StartAsync_LocalChange_IsPushedTwoSecondsLater()
    {
        ManualTime time = new(Start);
        await using SyncHarness harness = await SyncHarness.CreateAsync(time);

        await harness.Engine.StartAsync(CancellationToken.None);
        await SyncHarness.EventuallyAsync(() => harness.Engine.Status.State == SyncState.Synchronised && time.NextDue is not null, "the first cycle");

        await harness.Outbox.RecordAsync(PendingChangeKind.Settings);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        Assert.Equal(SyncEngine.PushDelay, time.NextDue);
        Assert.Equal(0, harness.Count(HttpMethod.Put, "/api/v1/me/settings"));

        time.Advance(SyncEngine.PushDelay);

        await SyncHarness.EventuallyAsync(() => harness.Count(HttpMethod.Put, "/api/v1/me/favourites") == 1, "the push");
        Assert.Equal(1, harness.Count(HttpMethod.Put, "/api/v1/me/settings"));
    }

    [Fact]
    public async Task RequestSync_DuringACycle_RunsExactlyOneMore()
    {
        ManualTime time = new(Start);
        await using SyncHarness harness = await SyncHarness.CreateAsync(time);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.On(HttpMethod.Get, "/api/v1/sync/changes", async request =>
        {
            await release.Task;
            return harness.Server.DefaultAnswer(request);
        });

        await harness.Engine.StartAsync(CancellationToken.None);
        await SyncHarness.EventuallyAsync(() => harness.Count(HttpMethod.Get, "/api/v1/sync/changes") == 1, "the first pull");

        harness.Engine.RequestSync();
        harness.Engine.RequestSync();
        harness.Engine.RequestSync();
        release.SetResult();

        await SyncHarness.EventuallyAsync(() => harness.Count(HttpMethod.Get, "/api/v1/sync/changes") == 2 && time.NextDue is not null, "the one more cycle");
        await Task.Delay(100);

        Assert.Equal(2, harness.Count(HttpMethod.Get, "/api/v1/sync/changes"));
        Assert.Equal(SyncEngine.Interval, time.NextDue);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        do
        {
            current = Volatile.Read(ref target);

            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
