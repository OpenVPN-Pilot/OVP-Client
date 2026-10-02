using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// The server's segment of the status bar, its banners, and the storage page of the settings.
/// </summary>
public sealed class ServerStatusViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Directory.CreateTempSubdirectory("ovp-status-").FullName;

    [Fact]
    public void LocalMode_ShowsTheGreyLocalLabelAndNoBanner()
    {
        using ServerStatusViewModel model = new(new StubLocalizer(), new FakeSettingsService(), new ImmediateThread(), new ManualTime(Now));

        Assert.True(model.IsLocal);
        Assert.Equal("statusBar.local", model.Text);
        Assert.Equal(ServerStatusTone.Neutral, model.Tone);
        Assert.False(model.HasBanner);
    }

    [Fact]
    public void SessionEnded_IsRedWithASignInBanner()
    {
        FixedStatus source = new(Snapshot(SyncState.Synchronised, signedIn: false));
        using ServerStatusViewModel model = new(new StubLocalizer(), new FakeSettingsService(), new ImmediateThread(), new ManualTime(Now), source);

        Assert.True(model.IsProblem);
        Assert.True(model.HasBanner);
        Assert.True(model.NeedsSignIn);
        Assert.Equal("banner.signInTitle", model.BannerTitle);
        Assert.False(model.CanSyncNow);
    }

    [Fact]
    public void Offline_IsAmberWithoutABanner()
    {
        FixedStatus source = new(Snapshot(SyncState.Offline, signedIn: true));
        using ServerStatusViewModel model = new(new StubLocalizer(), new FakeSettingsService(), new ImmediateThread(), new ManualTime(Now), source);

        Assert.True(model.IsWarning);
        Assert.False(model.HasBanner);
    }

    [Fact]
    public void Compact_SynchronisedWithWaitingChanges_NamesHostLatencyTimeAndCount()
    {
        ServerStatusSnapshot snapshot = Snapshot(SyncState.ChangesWaiting, signedIn: true) with
        {
            Reachability = new ServerReachability(true, TimeSpan.FromMilliseconds(23), false, null),
        };

        string text = ServerStatusText.Compact(new StubLocalizer(), snapshot, Now);

        Assert.Equal("pilot.example.com · statusBar.latency · statusBar.syncedMinutes · statusBar.waiting", text);
    }

    [Fact]
    public void State_ServerUnreachableByTheProbe_IsOfflineBeforeTheNextCycle()
    {
        ServerStatusSnapshot snapshot = Snapshot(SyncState.Synchronised, signedIn: true) with
        {
            Reachability = new ServerReachability(false, null, false, null),
        };

        Assert.Equal(SyncState.Offline, snapshot.State);
    }

    [Fact]
    public void Storage_SwitchWhileATunnelIsUp_IsRefusedBeforeAnythingIsAsked()
    {
        CountedTunnels tunnels = new() { Count = 1 };
        RecordingSwitcher switcher = new();
        using StorageSettingsViewModel model = Storage(tunnels, switcher);

        model.RequestSwitchToLocalCommand.Execute(null);

        Assert.False(model.IsConfirming);
        Assert.Equal("storage.disconnectFirst", model.Message);
        Assert.Empty(switcher.Calls);
    }

    [Fact]
    public async Task Storage_SwitchToThisComputerConfirmed_Restarts()
    {
        RecordingSwitcher switcher = new();
        using StorageSettingsViewModel model = Storage(new CountedTunnels(), switcher);

        model.RequestSwitchToLocalCommand.Execute(null);
        Assert.True(model.IsConfirming);

        await model.ConfirmSwitchCommand.ExecuteAsync(null);

        Assert.Equal(["local"], switcher.Calls);
        Assert.False(model.IsConfirming);
    }

    [Fact]
    public async Task Storage_SwitchToServerConfirmed_OpensTheSignInStepsWithoutWritingAnything()
    {
        RecordingSwitcher switcher = new();
        using StorageSettingsViewModel model = Storage(new CountedTunnels(), switcher);
        AppScreen? opened = null;
        model.ScreenRequested += (_, screen) => opened = screen;

        model.RequestSwitchToServerCommand.Execute(null);
        await model.ConfirmSwitchCommand.ExecuteAsync(null);

        Assert.Equal(AppScreen.ServerSwitch, opened);
        Assert.Empty(switcher.Calls);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private static ServerStatusSnapshot Snapshot(SyncState state, bool signedIn) => new(
        new Uri("https://pilot.example.com/"),
        SyncStatus.Initial with { State = state, LastPullAt = Now.AddMinutes(-2), PendingChanges = 3 },
        SessionKnown: true,
        signedIn,
        signedIn ? ServerAnswers.Alice : null,
        ServerReachability.Unknown);

    private StorageSettingsViewModel Storage(IActiveTunnels tunnels, IStorageModeSwitcher switcher) => new(
        new StubLocalizer(),
        ActiveStorage.Resolve(new TemporaryPaths(root), new StorageSelection(StorageMode.Local, null)),
        tunnels,
        switcher,
        new ImmediateThread(),
        NullLogger<StorageSettingsViewModel>.Instance);

    private sealed class FixedStatus(ServerStatusSnapshot snapshot) : IServerStatusSource
    {
        public ServerStatusSnapshot Current { get; } = snapshot;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void Begin()
        {
        }

        public void RequestSync()
        {
        }
    }
}

/// <summary>
/// Runs what is meant for the user interface thread at once, on the caller's thread.
/// </summary>
internal sealed class ImmediateThread : IUserInterfaceThread
{
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default) => work();
}
