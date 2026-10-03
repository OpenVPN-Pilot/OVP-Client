using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// What the copy of a server remembers about the person signed in to it.
/// </summary>
/// <remarks>
/// The last known user lives in the copy's own database, beside the cursor it belongs with, so the
/// role can be shown while the server cannot be reached and a sign in can tell the same person from
/// somebody else. Nothing here is secret: tokens are the keystore's.
/// </remarks>
public interface IServerAccountState
{
    /// <summary>
    /// The person who was signed in last, or null when nobody has been.
    /// </summary>
    public Task<CurrentUserResponse?> ReadLastKnownUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers the person as the server last described them.
    /// </summary>
    public Task RememberUserAsync(CurrentUserResponse user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards everything that belonged to the previous person, for somebody else signing in.
    /// </summary>
    /// <remarks>
    /// The changes waiting to be sent go, because they would be sent in the new person's name. So do
    /// the profiles the previous person created that never reached the server, still waiting to be
    /// uploaded or refused by it, with their stored sign ins: they are changes waiting too, and kept
    /// without their marker they would pass for the server's. Their favourites and shortcuts go,
    /// because those are personal. The cursor goes, so the next synchronisation is a full one and
    /// brings the new person's own. The server's profiles, the history of this machine with them and
    /// their keystore entries stay: they belong to the team and to the machine.
    /// </remarks>
    public Task<PersonalDataDiscarded> DiscardPersonalDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties the copy, for the person signing out.
    /// </summary>
    /// <remarks>
    /// Signing out leaves nothing of the server on this computer that could be used without signing
    /// in again: every profile goes with its tags, history and stored sign ins, and so do the
    /// shortcuts, the changes waiting and the memory of who was signed in, so the next sign in, by
    /// anyone, starts with a complete synchronisation. The database itself stays, because the
    /// running application keeps it open.
    /// </remarks>
    public Task<CopyErased> EraseCopyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// How much of the previous person's data was discarded. Counts, never values.
/// </summary>
public sealed record PersonalDataDiscarded(int PendingChanges, int Favourites, int Hotkeys, int TemporaryProfiles);

/// <summary>
/// What emptying the copy removed. Counts, never values.
/// </summary>
public sealed record CopyErased(int Profiles, int Secrets, int PendingChanges);

/// <summary>
/// Keeps that state in the sync state row of the copy's database.
/// </summary>
public sealed class ServerAccountState : IServerAccountState
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IOutbox outbox;
    private readonly IServerProfileMaintenance maintenance;
    private readonly ILogger<ServerAccountState> logger;

    public ServerAccountState(
        IDbContextFactory<PilotDbContext> contextFactory,
        IOutbox outbox,
        IServerProfileMaintenance maintenance,
        ILogger<ServerAccountState> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.outbox = outbox;
        this.maintenance = maintenance;
        this.logger = logger;
    }

    public async Task<CurrentUserResponse?> ReadLastKnownUserAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        SyncStateRow? state = await context.SyncStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == SyncStateRow.SingletonId, cancellationToken);

        if (state is not { UserId: { } id, Username: { } username, UserRole: { } role, UserProvider: { } provider })
        {
            return null;
        }

        return new CurrentUserResponse(id, username, state.UserDisplayName, role, provider);
    }

    public async Task RememberUserAsync(CurrentUserResponse user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Only these columns, so a cursor the synchronisation writes at the same moment is not lost.
        if (await UpdateUserAsync(context, user, cancellationToken) > 0)
        {
            return;
        }

        context.SyncStates.Add(new SyncStateRow
        {
            UserId = user.Id,
            Username = user.Username,
            UserDisplayName = user.DisplayName,
            UserRole = user.Role,
            UserProvider = user.Provider,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The synchronisation created the row between the update and the insert. It exists now,
            // so the update finds it.
            context.ChangeTracker.Clear();
            await UpdateUserAsync(context, user, cancellationToken);
        }
    }

    public async Task<PersonalDataDiscarded> DiscardPersonalDataAsync(CancellationToken cancellationToken = default)
    {
        int pending = await outbox.ClearAsync(cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        int favourites = await context.Profiles
            .Where(profile => profile.IsFavourite || profile.FavouriteSlot != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(profile => profile.IsFavourite, false)
                    .SetProperty(profile => profile.FavouriteSlot, (int?)null),
                cancellationToken);

        int hotkeys = await context.HotkeyBindings.ExecuteDeleteAsync(cancellationToken);

        // Every profile of a server's copy that is not the server's is one that never reached it.
        List<Guid> temporary = await context.Profiles
            .Where(profile => profile.Source != ProfileSource.Server)
            .Select(profile => profile.Id)
            .ToListAsync(cancellationToken);

        await context.Profiles
            .Where(profile => temporary.Contains(profile.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await context.SyncStates
            .Where(row => row.Id == SyncStateRow.SingletonId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Cursor, (long?)null), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // After the commit: the keystore cannot join the transaction, and a sign in left behind for a
        // profile that is gone is unused, while one removed for a profile that stayed would be lost.
        await maintenance.DeleteSecretsAsync(temporary, cancellationToken);

        PersonalDataDiscarded discarded = new(pending, favourites, hotkeys, temporary.Count);
        ServerAccountLog.PersonalDataDiscarded(logger, discarded.PendingChanges, discarded.Favourites, discarded.Hotkeys, discarded.TemporaryProfiles);
        return discarded;
    }

    public async Task<CopyErased> EraseCopyAsync(CancellationToken cancellationToken = default)
    {
        int pending = await outbox.ClearAsync(cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read inside the transaction, so a profile the keystore is cleared for is one that is deleted.
        List<Guid> profiles = await context.Profiles.Select(profile => profile.Id).ToListAsync(cancellationToken);

        // Tag links, sessions and their events go with the profiles by cascade.
        await context.Profiles.ExecuteDeleteAsync(cancellationToken);
        await context.Tags.ExecuteDeleteAsync(cancellationToken);
        await context.HotkeyBindings.ExecuteDeleteAsync(cancellationToken);
        await context.SyncStates.ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // After the commit, for the reason given in DiscardPersonalDataAsync.
        int secrets = await maintenance.DeleteSecretsAsync(profiles, cancellationToken);

        CopyErased erased = new(profiles.Count, secrets, pending);
        ServerAccountLog.CopyErased(logger, erased.Profiles, erased.Secrets, erased.PendingChanges);
        return erased;
    }

    private static Task<int> UpdateUserAsync(PilotDbContext context, CurrentUserResponse user, CancellationToken cancellationToken) =>
        context.SyncStates
            .Where(row => row.Id == SyncStateRow.SingletonId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.UserId, user.Id)
                    .SetProperty(row => row.Username, user.Username)
                    .SetProperty(row => row.UserDisplayName, user.DisplayName)
                    .SetProperty(row => row.UserRole, user.Role)
                    .SetProperty(row => row.UserProvider, user.Provider),
                cancellationToken);
}
