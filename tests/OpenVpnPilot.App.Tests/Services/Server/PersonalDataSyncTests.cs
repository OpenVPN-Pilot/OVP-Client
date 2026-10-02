using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The person's favourites, shortcuts and settings: taken from the server without being sent back,
/// and recorded when the person changes them here.
/// </summary>
public sealed class PersonalDataSyncTests : IAsyncLifetime
{
    private const string Configuration = "client\nremote vpn.example.com 1194\n<ca>\nA\n</ca>\n";

    private static readonly Guid ServerProfile = Guid.Parse("5e000000-0000-0000-0000-0000000000a1");

    private SyncHarness harness = null!;

    public async Task InitializeAsync() => harness = await SyncHarness.CreateAsync();

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task SynchronizeAsync_ServersFavouritesShortcutsAndSettings_AreAppliedWithoutRecordingAnything()
    {
        harness.Server.Hold(ServerProfile, "example-site", Configuration);
        harness.Server.Favourites = new FavouritesDocument([new FavouriteItem(ServerProfile, 3)]);
        harness.Server.Hotkeys = new HotkeysDocument([new HotkeyItem("ConnectFavourite3", "Control+Alt+D3", null, true)]);

        PilotSettings stored = new();
        stored.Appearance.Theme = ThemePreference.Dark;
        harness.Server.Settings = SyncServer.StoredSettings(stored);

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.True(result.Completed);

        Profile profile = (await harness.ProfileAsync(ServerProfile))!;
        Assert.True(profile.IsFavourite);
        Assert.Equal(3, profile.FavouriteSlot);

        HotkeyBinding binding = await harness.QueryAsync(context => context.HotkeyBindings.SingleAsync());
        Assert.Equal("Control+Alt+D3", binding.Gesture);

        Assert.Equal(ThemePreference.Dark, harness.Settings.Current.Appearance.Theme);
        Assert.Empty(await harness.Database.MarkersAsync());
        Assert.Contains(harness.Notifier.Notified, changes => changes.HasFlag(LibraryChanges.Hotkeys));
    }

    [Fact]
    public async Task SynchronizeAsync_FavouritesChangedHereAndNotSentYet_AreNotOverwritten()
    {
        harness.Server.Hold(ServerProfile, "example-site", Configuration);
        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        await harness.ChangeAsync(async context =>
            (await context.Profiles.SingleAsync(candidate => candidate.Id == ServerProfile)).IsFavourite = true);
        await harness.Outbox.RecordAsync(PendingChangeKind.Favourites);

        // The server still says there are none, and the change here has not been sent yet.
        PersonalDataSync personal = new(
            harness.Network.Api,
            harness.Database.Factory,
            harness.Outbox,
            harness.Settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Null(await personal.PullAsync(new SyncCycle(), CancellationToken.None));

        Assert.True((await harness.ProfileAsync(ServerProfile))!.IsFavourite);
        Assert.Single(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_ServerHoldsNoSettingsYet_SendsThisMachinesInstead()
    {
        harness.Server.Settings = SyncServer.NothingStored;
        await harness.SettingsBackend.UpdateAsync(settings => settings.Appearance.Theme = ThemePreference.Light);

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(ThemePreference.Light, harness.Settings.Current.Appearance.Theme);
        Assert.Equal(PendingChangeKind.Settings, Assert.Single(await harness.Database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task SynchronizeAsync_ServerHoldsNoShortcutsYet_KeepsAndSendsThisMachines()
    {
        await harness.ChangeAsync(context =>
        {
            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = "ToggleQuickSwitcher", Gesture = "Control+Alt+V" });
            return Task.CompletedTask;
        });

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Single(await harness.QueryAsync(context => context.HotkeyBindings.ToListAsync()));
        Assert.Equal(PendingChangeKind.Hotkeys, Assert.Single(await harness.Database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task UpdateAsync_PortableSettingChanged_RecordsTheSettings()
    {
        await harness.Settings.UpdateAsync(settings => settings.Appearance.Theme = ThemePreference.Dark);

        Assert.Equal(PendingChangeKind.Settings, Assert.Single(await harness.Database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task UpdateAsync_OnlyThisMachinesValuesChanged_RecordsNothing()
    {
        await harness.Settings.UpdateAsync(settings => settings.General.StartWithSystem = !settings.General.StartWithSystem);
        await harness.Settings.ReplaceAsync(harness.Settings.Current.Clone());

        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task ApplyFromServerAsync_SameSettings_ChangesNothing()
    {
        bool changed = await harness.Settings.ApplyFromServerAsync(harness.Settings.Export());

        Assert.False(changed);
        Assert.Empty(await harness.Database.MarkersAsync());
    }

    [Fact]
    public async Task SynchronizeAsync_SlotTakenByTheServersFavourite_IsTakenFromAProfileNotUploadedYet()
    {
        harness.Server.Hold(ServerProfile, "example-site", Configuration);
        Profile local = await harness.Database.AddProfileAsync("example-local", Configuration + "verb 4\n");

        await harness.ChangeAsync(async context =>
        {
            Profile tracked = await context.Profiles.SingleAsync(candidate => candidate.Id == local.Id);
            tracked.IsFavourite = true;
            tracked.FavouriteSlot = 2;
        });

        harness.Server.Favourites = new FavouritesDocument([new FavouriteItem(ServerProfile, 2)]);
        harness.Server.Changes = since => SyncServer.Feed(false, 1, profiles: harness.Server.Held());

        await harness.Engine.SynchronizeAsync(CancellationToken.None);

        Assert.Equal(2, (await harness.ProfileAsync(ServerProfile))!.FavouriteSlot);
        Profile kept = (await harness.ProfileAsync(local.Id))!;
        Assert.True(kept.IsFavourite);
        Assert.Null(kept.FavouriteSlot);
        Assert.Equal(ProfileImporter.ComputeHash(Configuration + "verb 4\n"), kept.ContentHash);
    }
}
