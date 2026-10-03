using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The signed in person's favourites, shortcuts and settings: read from the copy for the push, and
/// taken from the server after the pull.
/// </summary>
/// <remarks>
/// <para>
/// What arrives from the server is written straight into the database and the settings, never
/// through the stores and screens that record changes, so taking the server's lists never sends them
/// back. A list with a change of this machine still waiting is left alone: the pending change wins
/// until it has been sent.
/// </para>
/// <para>
/// A server that holds nothing yet for this person is told this machine's state instead of handing
/// it an empty one. For the settings the contract says so (no stored document has no tag); for the
/// shortcuts an empty list is taken the same way while this machine has any, because otherwise the
/// first sign in would erase the shortcuts every installation starts with.
/// </para>
/// </remarks>
internal sealed class PersonalDataSync
{
    private readonly IServerApi api;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IOutbox outbox;
    private readonly IPortableSettings settings;
    private readonly ILogger logger;

    public PersonalDataSync(
        IServerApi api,
        IDbContextFactory<PilotDbContext> contextFactory,
        IOutbox outbox,
        IPortableSettings settings,
        ILogger logger)
    {
        this.api = api;
        this.contextFactory = contextFactory;
        this.outbox = outbox;
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>
    /// The favourites of the server's profiles, slotted ones first in slot order, as the server
    /// keeps them. A profile not uploaded yet is no favourite there.
    /// </summary>
    public async Task<FavouritesDocument> ReadFavouritesAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        List<FavouriteItem> items = await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.Source == ProfileSource.Server && profile.IsFavourite)
            .OrderBy(profile => profile.FavouriteSlot == null)
            .ThenBy(profile => profile.FavouriteSlot)
            .ThenBy(profile => profile.Name)
            .Select(profile => new FavouriteItem(profile.Id, profile.FavouriteSlot))
            .ToListAsync(cancellationToken);

        return new FavouritesDocument(items);
    }

    /// <summary>
    /// The shortcut bindings. One that names a profile the server does not know yet names none there.
    /// </summary>
    public async Task<HotkeysDocument> ReadHotkeysAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        HashSet<Guid> serverProfiles = await context.Profiles
            .Where(profile => profile.Source == ProfileSource.Server)
            .Select(profile => profile.Id)
            .ToHashSetAsync(cancellationToken);

        List<HotkeyBinding> bindings = await context.HotkeyBindings
            .AsNoTracking()
            .OrderBy(binding => binding.ActionId)
            .ToListAsync(cancellationToken);

        return new HotkeysDocument([.. bindings.Select(binding => new HotkeyItem(
            binding.ActionId,
            binding.Gesture,
            binding.ProfileId is { } id && serverProfiles.Contains(id) ? id : null,
            binding.IsEnabled))]);
    }

    public static bool SameItems<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) => first.SequenceEqual(second);

    /// <summary>
    /// Takes the server's favourites, shortcuts and settings, each unless a change of it is waiting.
    /// </summary>
    /// <returns>The failure that stopped it, or null.</returns>
    public async Task<ServerResult?> PullAsync(SyncCycle cycle, CancellationToken cancellationToken)
    {
        ServerResult<FavouritesDocument> favourites = await api.GetFavouritesAsync(cancellationToken);

        if (!favourites.IsSuccess)
        {
            return favourites;
        }

        if (await ApplyFavouritesAsync(favourites.Value, cancellationToken))
        {
            cycle.Changes |= LibraryChanges.Profiles;
        }

        ServerResult<HotkeysDocument> hotkeys = await api.GetHotkeysAsync(cancellationToken);

        if (!hotkeys.IsSuccess)
        {
            return hotkeys;
        }

        if (await ApplyHotkeysAsync(hotkeys.Value, cancellationToken))
        {
            cycle.Changes |= LibraryChanges.Hotkeys;
        }

        ServerResult<SettingsResponse> stored = await api.GetSettingsAsync(cancellationToken);

        if (!stored.IsSuccess)
        {
            return stored;
        }

        await ApplySettingsAsync(stored.Value, cancellationToken);
        return null;
    }

    private async Task<bool> ApplyFavouritesAsync(FavouritesDocument document, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read inside the transaction, so a change recorded meanwhile is either seen or not yet made.
        if (await IsPendingAsync(context, PendingChangeKind.Favourites, cancellationToken))
        {
            return false;
        }

        Dictionary<Guid, int?> wanted = [];

        foreach (FavouriteItem item in document.Items ?? [])
        {
            wanted.TryAdd(item.ProfileId, item.Slot);
        }

        HashSet<int> slotsTaken = [.. wanted.Values.OfType<int>()];
        List<Profile> profiles = await context.Profiles.ToListAsync(cancellationToken);
        List<(Profile Profile, bool Favourite, int? Slot)> targets = [];

        foreach (Profile profile in profiles)
        {
            if (profile.Source == ProfileSource.Server)
            {
                bool favourite = wanted.TryGetValue(profile.Id, out int? slot);
                targets.Add((profile, favourite, favourite ? slot : null));
            }
            else if (profile.FavouriteSlot is { } own && slotsTaken.Contains(own))
            {
                // A profile not uploaded yet keeps its favourite, but a slot is one shortcut and the
                // server's assignment of it wins.
                targets.Add((profile, profile.IsFavourite, null));
            }
        }

        if (targets.All(target => target.Profile.IsFavourite == target.Favourite && target.Profile.FavouriteSlot == target.Slot))
        {
            return false;
        }

        // The slot is unique, so every slot is let go before any is taken again.
        foreach ((Profile profile, _, _) in targets)
        {
            profile.FavouriteSlot = null;
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach ((Profile profile, bool favourite, int? slot) in targets)
        {
            profile.IsFavourite = favourite;
            profile.FavouriteSlot = slot;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        SyncEngineLog.PersonalApplied(logger, PendingChangeKind.Favourites, true);
        return true;
    }

    private async Task<bool> ApplyHotkeysAsync(HotkeysDocument document, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await IsPendingAsync(context, PendingChangeKind.Hotkeys, cancellationToken))
        {
            return false;
        }

        List<HotkeyBinding> existing = await context.HotkeyBindings.ToListAsync(cancellationToken);
        IReadOnlyList<HotkeyItem> items = document.Items ?? [];

        if (items.Count == 0 && existing.Count > 0)
        {
            await outbox.StageAsync(context, PendingChangeKind.Hotkeys, cancellationToken: cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            SyncEngineLog.PersonalSeeded(logger, PendingChangeKind.Hotkeys);
            return false;
        }

        // The action is unique here, and the server does not interpret what it stores.
        List<HotkeyItem> wanted = [.. items
            .Where(item => !string.IsNullOrWhiteSpace(item.ActionId) && !string.IsNullOrWhiteSpace(item.Gesture))
            .DistinctBy(item => item.ActionId, StringComparer.Ordinal)
            .OrderBy(item => item.ActionId, StringComparer.Ordinal)];

        List<HotkeyItem> current = [.. existing
            .OrderBy(binding => binding.ActionId, StringComparer.Ordinal)
            .Select(binding => new HotkeyItem(binding.ActionId, binding.Gesture, binding.ProfileId, binding.IsEnabled))];

        if (SameItems(wanted, current))
        {
            return false;
        }

        context.HotkeyBindings.RemoveRange(existing);
        await context.SaveChangesAsync(cancellationToken);

        context.HotkeyBindings.AddRange(wanted.Select(item => new HotkeyBinding
        {
            ActionId = item.ActionId,
            Gesture = item.Gesture,
            ProfileId = item.ProfileId,
            IsEnabled = item.IsEnabled,
        }));

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        SyncEngineLog.PersonalApplied(logger, PendingChangeKind.Hotkeys, true);
        return true;
    }

    private async Task ApplySettingsAsync(SettingsResponse stored, CancellationToken cancellationToken)
    {
        if (await IsPendingAsync(PendingChangeKind.Settings, cancellationToken))
        {
            return;
        }

        if (stored.ETag is null)
        {
            await outbox.RecordAsync(PendingChangeKind.Settings, cancellationToken: cancellationToken);
            SyncEngineLog.PersonalSeeded(logger, PendingChangeKind.Settings);
            return;
        }

        bool changed = await settings.ApplyFromServerAsync(stored.Document, cancellationToken);
        SyncEngineLog.PersonalApplied(logger, PendingChangeKind.Settings, changed);
    }

    private async Task<bool> IsPendingAsync(PendingChangeKind kind, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await IsPendingAsync(context, kind, cancellationToken);
    }

    private static Task<bool> IsPendingAsync(PilotDbContext context, PendingChangeKind kind, CancellationToken cancellationToken) =>
        context.PendingChanges.AnyAsync(change => change.Kind == kind, cancellationToken);
}
