using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// The stores note what the server has to hear about, and only when there is a server.
/// </summary>
public sealed class RecordingStoreTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async Task InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RenameProfileAsync_ServerMode_RecordsOneUpdateForSeveralEdits()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: true);

        await store.RenameProfileAsync(profile.Id, "example-site-a");
        await store.SetProfileNotesAsync(profile.Id, "Reached through the office.");
        await store.SetRouteProtectionAsync(profile.Id, true);
        await store.SetProfileTagsAsync(profile.Id, ["Office"]);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(PendingChangeKind.ProfileUpdate, marker.Kind);
        Assert.Equal(profile.Id, marker.EntityId);
    }

    [Fact]
    public async Task UpdateConfigurationAsync_ServerMode_RecordsAnUpdate()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: true);

        ConfigurationUpdate result = await store.UpdateConfigurationAsync(
            profile.Id,
            "client\nremote vpn.example.com 443 tcp\n");

        Assert.True(result.Saved);
        Assert.Equal(PendingChangeKind.ProfileUpdate, Assert.Single(await database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task RenameProfileAsync_SameName_RecordsNothing()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: true);

        await store.RenameProfileAsync(profile.Id, "example-site");

        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task RenameProfileAsync_LocalMode_RecordsNothing()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: false);

        await store.RenameProfileAsync(profile.Id, "example-site-a");
        await store.SetFavouriteSlotAsync(profile.Id, 1);
        await store.DeleteProfileAsync(profile.Id);

        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task SetFavouriteAsync_ServerMode_RecordsTheFavouritesOnce()
    {
        Profile first = await database.AddProfileAsync("example-site-a");
        Profile second = await database.AddProfileAsync("example-site-b", "client\nremote vpn.example.com 1195\n");
        ProfileStore store = CreateProfileStore(serverMode: true);

        await store.SetFavouriteAsync(first.Id, true);
        await store.SetFavouriteSlotAsync(second.Id, 2);
        await store.SetFavouriteAsync(first.Id, false);

        Assert.Equal(PendingChangeKind.Favourites, Assert.Single(await database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task RecordConnectionAsync_ServerMode_RecordsNothing()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: true);

        await store.RecordConnectionAsync(profile.Id);

        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task DeleteProfileAsync_ServerMode_RemovesTheProfileAndRecordsTheDeleteInTheSameSave()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        ProfileStore store = CreateProfileStore(serverMode: true);

        await store.SetProfileTagsAsync(profile.Id, ["Office"]);
        await store.DeleteProfileAsync(profile.Id);

        PendingChange marker = Assert.Single(await database.MarkersAsync());
        Assert.Equal(PendingChangeKind.ProfileDelete, marker.Kind);

        await using PilotDbContext context = await database.Factory.CreateDbContextAsync();
        Assert.Empty(await context.Profiles.ToListAsync());
        Assert.Empty(await context.ProfileTags.ToListAsync());
        Assert.Empty(await context.Tags.ToListAsync());
    }

    [Fact]
    public async Task DeleteProfileAsync_ProfileCreatedOffline_LeavesNothingToSend()
    {
        Profile profile = await database.AddProfileAsync("example-site");
        Outbox outbox = CreateOutbox();
        await outbox.RecordAsync(PendingChangeKind.ProfileCreate, profile.Id);

        ProfileStore store = CreateProfileStore(serverMode: true, outbox);
        await store.RenameProfileAsync(profile.Id, "example-site-a");
        await store.DeleteProfileAsync(profile.Id);

        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task SetBindingAsync_ServerMode_RecordsTheShortcutsOnce()
    {
        HotkeyStore store = CreateHotkeyStore(serverMode: true);

        await store.SetBindingAsync("ToggleQuickSwitcher", "Control+Alt+V");
        await store.SetBindingAsync("ConnectLastUsed", "Control+Alt+L");
        await store.SetBindingAsync("ConnectLastUsed", null);

        Assert.Equal(PendingChangeKind.Hotkeys, Assert.Single(await database.MarkersAsync()).Kind);
    }

    [Fact]
    public async Task SetBindingAsync_Unchanged_RecordsNothing()
    {
        HotkeyStore local = CreateHotkeyStore(serverMode: false);
        await local.SetBindingAsync("ToggleQuickSwitcher", "Control+Alt+V");

        HotkeyStore store = CreateHotkeyStore(serverMode: true);
        await store.SetBindingAsync("ToggleQuickSwitcher", "Control+Alt+V");

        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task EnsureDefaultsAsync_ServerMode_RecordsNothing()
    {
        HotkeyStore store = CreateHotkeyStore(serverMode: true);

        await store.EnsureDefaultsAsync();

        Assert.NotEmpty(await store.GetBindingsAsync());
        Assert.Empty(await database.MarkersAsync());
    }

    [Fact]
    public async Task RecordAsync_SettingsInServerMode_RecordsAndInLocalModeDoesNot()
    {
        await new ChangeRecorder(CreateOutbox(), new FixedStorageMode(false)).RecordAsync(PendingChangeKind.Settings);
        Assert.Empty(await database.MarkersAsync());

        await new ChangeRecorder(CreateOutbox(), new FixedStorageMode(true)).RecordAsync(PendingChangeKind.Settings);
        Assert.Equal(PendingChangeKind.Settings, Assert.Single(await database.MarkersAsync()).Kind);
    }

    private Outbox CreateOutbox() => new(database.Factory, TimeProvider.System, NullLogger<Outbox>.Instance);

    private ProfileStore CreateProfileStore(bool serverMode, Outbox? outbox = null) => new(
        database.Factory,
        TimeProvider.System,
        new ChangeRecorder(outbox ?? CreateOutbox(), new FixedStorageMode(serverMode)));

    private HotkeyStore CreateHotkeyStore(bool serverMode) => new(
        database.Factory,
        new ChangeRecorder(CreateOutbox(), new FixedStorageMode(serverMode)));
}
