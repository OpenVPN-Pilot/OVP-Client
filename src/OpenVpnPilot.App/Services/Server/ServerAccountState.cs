using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
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
    /// The changes waiting to be sent go, because they would be sent in the new person's name. Their
    /// favourites and shortcuts go, because those are personal. The cursor goes, so the next
    /// synchronisation is a full one and brings the new person's own. The shared profiles, the history
    /// of this machine and the keystore entries stay: they belong to the team and to the machine.
    /// </remarks>
    public Task<PersonalDataDiscarded> DiscardPersonalDataAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// How much of the previous person's data was discarded. Counts, never values.
/// </summary>
public sealed record PersonalDataDiscarded(int PendingChanges, int Favourites, int Hotkeys);

/// <summary>
/// Keeps that state in the sync state row of the copy's database.
/// </summary>
public sealed class ServerAccountState : IServerAccountState
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IOutbox outbox;
    private readonly ILogger<ServerAccountState> logger;

    public ServerAccountState(
        IDbContextFactory<PilotDbContext> contextFactory,
        IOutbox outbox,
        ILogger<ServerAccountState> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.outbox = outbox;
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

        await context.SyncStates
            .Where(row => row.Id == SyncStateRow.SingletonId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Cursor, (long?)null), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        PersonalDataDiscarded discarded = new(pending, favourites, hotkeys);
        ServerAccountLog.PersonalDataDiscarded(logger, discarded.PendingChanges, discarded.Favourites, discarded.Hotkeys);
        return discarded;
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
