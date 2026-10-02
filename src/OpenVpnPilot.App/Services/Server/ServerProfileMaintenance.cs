using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The database and keystore work on server profiles that the synchronisation needs and that no
/// screen does.
/// </summary>
public interface IServerProfileMaintenance
{
    /// <summary>
    /// Moves a profile created while the server could not be reached from its temporary id to the
    /// id the server gave it, with everything that hangs on that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sessions, shortcut bindings and pending markers follow the profile, the pending create is
    /// completed, its sign ins, stored or held in memory, move to the new id, and the favourites are
    /// marked to be sent again, because the server only now knows the profile they name.
    /// </para>
    /// <para>
    /// When a profile with the server's id is already held, which is what answering a duplicate
    /// upload finds after the next synchronisation, the temporary profile is folded into it: the
    /// server's copy keeps its configuration and tags, and gains the favourite, the usage and the
    /// history of this machine.
    /// </para>
    /// <para>
    /// A sign in already stored under the server's id is kept rather than overwritten, because it
    /// came from the shared vault everybody uses.
    /// </para>
    /// <para>
    /// All or nothing. When any step fails, the temporary profile, its markers and its keystore
    /// entries are exactly as they were, and the exception is passed on, so the create is tried
    /// again later.
    /// </para>
    /// </remarks>
    public Task<ProfileRekeyOutcome> RekeyAsync(
        Guid temporaryId,
        Guid serverId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The server profile that holds the same configuration as a temporary one, as the server would
    /// have stored it, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Answers the server's refusal of an upload as a duplicate: the profile is already there, and
    /// the temporary one is re-keyed onto it rather than uploaded again.
    /// </remarks>
    public Task<Guid?> FindServerDuplicateAsync(Guid temporaryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every keystore entry of the given profiles, whatever the realm.
    /// </summary>
    /// <remarks>
    /// A server profile that is deleted takes its stored sign ins with it, unlike a local one. The
    /// database rows are the caller's to delete, in its own transaction.
    /// </remarks>
    /// <returns>How many entries were removed.</returns>
    public Task<int> DeleteSecretsAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a re-key did.
/// </summary>
public enum ProfileRekeyOutcome
{
    /// <summary>
    /// The profile now lives under the server's id.
    /// </summary>
    Moved,

    /// <summary>
    /// The server's copy was already held, and the temporary profile was folded into it.
    /// </summary>
    Merged,

    /// <summary>
    /// There is no profile under the temporary id, so there was nothing to move.
    /// </summary>
    NotFound,
}

/// <summary>
/// Entity Framework and keystore backed implementation.
/// </summary>
public sealed class ServerProfileMaintenance : IServerProfileMaintenance
{
    private static readonly PendingChangeKind[] ProfileKinds =
    [
        PendingChangeKind.ProfileCreate,
        PendingChangeKind.ProfileUpdate,
        PendingChangeKind.ProfileDelete,
        PendingChangeKind.VaultAdd,
    ];

    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly ISecretStore secrets;
    private readonly IHeldVaultSecrets held;
    private readonly IOutbox outbox;
    private readonly ILogger<ServerProfileMaintenance> logger;

    public ServerProfileMaintenance(
        IDbContextFactory<PilotDbContext> contextFactory,
        ISecretStore secrets,
        IHeldVaultSecrets held,
        IOutbox outbox,
        ILogger<ServerProfileMaintenance> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.secrets = secrets;
        this.held = held;
        this.outbox = outbox;
        this.logger = logger;
    }

    public async Task<ProfileRekeyOutcome> RekeyAsync(
        Guid temporaryId,
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        if (temporaryId == serverId)
        {
            throw new ArgumentException("A profile cannot be re-keyed onto its own id.", nameof(serverId));
        }

        // Read before anything changes, so a failure knows exactly what it has to take back.
        List<(string Reference, string Realm)> formerSecrets = await ListSecretsAsync(temporaryId, cancellationToken);
        List<(string Reference, string Realm)> copied = [];

        ProfileRekeyOutcome outcome;
        Moved moved;

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            Profile? temporary = await context.Profiles.FirstOrDefaultAsync(profile => profile.Id == temporaryId, cancellationToken);

            if (temporary is null)
            {
                return ProfileRekeyOutcome.NotFound;
            }

            Profile? existing = await context.Profiles.FirstOrDefaultAsync(profile => profile.Id == serverId, cancellationToken);

            outcome = existing is null ? ProfileRekeyOutcome.Moved : ProfileRekeyOutcome.Merged;
            moved = await MoveRowsAsync(context, temporary, existing, serverId, cancellationToken);

            // Copied before the transaction commits and removed from the old id only after it has,
            // so at no point is a sign in held under neither id. One already held under the server's
            // id came from the shared vault and stays.
            foreach ((string reference, string realm) in formerSecrets)
            {
                string target = SecretReference.ForProfile(serverId, realm);

                if (await secrets.TryReadAsync(target, cancellationToken) is not null)
                {
                    continue;
                }

                if (await secrets.TryReadAsync(reference, cancellationToken) is not { } secret)
                {
                    continue;
                }

                copied.Add((target, realm));
                await secrets.WriteAsync(target, secret, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // The database half is undone when the transaction is disposed uncommitted, which also
            // holds when the commit itself was what failed.
            // Taken back even when the failure was the caller cancelling, which is when it matters.
            await RemoveCopiesAsync(serverId, copied, CancellationToken.None);
            OutboxLog.RekeyFailed(logger, temporaryId, serverId, exception);
            throw;
        }

        // A sign in kept only in memory follows the profile as well, or the marker that names it
        // under the new id would find nothing to share.
        held.Move(temporaryId, serverId);

        foreach ((string reference, string realm) in formerSecrets)
        {
            try
            {
                await secrets.DeleteAsync(reference, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The re-key has happened. An entry left under an id no profile has any more is
                // unused, and failing now would report a re-key that did take place as one that did not.
                OutboxLog.FormerSecretNotRemoved(logger, temporaryId, realm, exception);
            }
        }

        OutboxLog.Rekeyed(logger, temporaryId, serverId, outcome, moved.Sessions, moved.Bindings, copied.Count);
        return outcome;
    }

    public async Task<Guid?> FindServerDuplicateAsync(Guid temporaryId, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        string? configuration = await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.Id == temporaryId)
            .Select(profile => profile.Configuration)
            .FirstOrDefaultAsync(cancellationToken);

        if (configuration is null)
        {
            return null;
        }

        string hash = ServerContentHash.Compute(configuration);

        return await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.Source == ProfileSource.Server
                && profile.Id != temporaryId
                && profile.ContentHash == hash)
            .Select(profile => (Guid?)profile.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> DeleteSecretsAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        if (profileIds.Count == 0)
        {
            return 0;
        }

        HashSet<Guid> wanted = [.. profileIds];
        int removed = 0;

        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (SecretReference.TryParse(reference, out Guid profileId, out _) && wanted.Contains(profileId))
            {
                await secrets.DeleteAsync(reference, cancellationToken);
                removed++;
            }
        }

        OutboxLog.SecretsRemoved(logger, removed, wanted.Count);
        return removed;
    }

    /// <summary>
    /// Does the database half of a re-key inside the caller's transaction.
    /// </summary>
    private async Task<Moved> MoveRowsAsync(
        PilotDbContext context,
        Profile temporary,
        Profile? existing,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        Guid temporaryId = temporary.Id;
        int? slot = temporary.FavouriteSlot;

        // The slot is unique, so the old row lets go of it before the new one takes it.
        temporary.FavouriteSlot = null;

        Profile target;

        if (existing is null)
        {
            // Every column is copied through the model, so a column added later is carried too.
            PropertyValues values = context.Entry(temporary).CurrentValues.Clone();
            values[nameof(Profile.Id)] = serverId;
            target = (Profile)values.ToObject();
            target.Source = ProfileSource.Server;
            context.Profiles.Add(target);
        }
        else
        {
            target = existing;
            target.IsFavourite |= temporary.IsFavourite;
            target.ConnectCount += temporary.ConnectCount;

            if (temporary.LastConnectedAt > target.LastConnectedAt || target.LastConnectedAt is null)
            {
                target.LastConnectedAt = temporary.LastConnectedAt;
            }

            // The server's copy keeps a slot of its own; it takes this one only when it has none.
            slot = target.FavouriteSlot ?? slot;
            target.FavouriteSlot = null;
        }

        await context.SaveChangesAsync(cancellationToken);

        int sessions = await context.Sessions
            .Where(session => session.ProfileId == temporaryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.ProfileId, serverId), cancellationToken);

        if (existing is null)
        {
            await context.ProfileTags
                .Where(link => link.ProfileId == temporaryId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.ProfileId, serverId), cancellationToken);
        }

        int bindings = await context.HotkeyBindings
            .Where(binding => binding.ProfileId == temporaryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(binding => binding.ProfileId, serverId), cancellationToken);

        await MoveMarkersAsync(context, temporaryId, serverId, cancellationToken);

        // Whatever is still linked to the old row, such as its tags when folding into the server's
        // copy, goes with it through the cascade the schema declares.
        context.Profiles.Remove(temporary);
        await context.SaveChangesAsync(cancellationToken);

        target.FavouriteSlot = slot;

        // The server only now knows the profile these lists may name.
        await outbox.StageAsync(context, PendingChangeKind.Favourites, cancellationToken: cancellationToken);

        if (bindings > 0)
        {
            await outbox.StageAsync(context, PendingChangeKind.Hotkeys, cancellationToken: cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new Moved(sessions, bindings);
    }

    /// <summary>
    /// Completes the pending create and points every other marker of the profile at its new id.
    /// </summary>
    private static async Task MoveMarkersAsync(
        PilotDbContext context,
        Guid temporaryId,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        List<PendingChange> markers = await context.PendingChanges
            .Where(change => ProfileKinds.Contains(change.Kind)
                && (change.EntityId == temporaryId || change.EntityId == serverId))
            .ToListAsync(cancellationToken);

        foreach (PendingChange marker in markers.Where(change => change.EntityId == temporaryId))
        {
            PendingChange? same = markers.FirstOrDefault(other =>
                other.EntityId == serverId
                && other.Kind == marker.Kind
                && string.Equals(other.Realm, marker.Realm, StringComparison.Ordinal));

            if (same is not null)
            {
                // An edit of the configuration is still one when it is folded into another marker.
                same.ConfigurationChanged |= marker.ConfigurationChanged;
            }

            if (marker.Kind == PendingChangeKind.ProfileCreate || same is not null)
            {
                context.PendingChanges.Remove(marker);
            }
            else
            {
                marker.EntityId = serverId;
            }
        }
    }

    private async Task<List<(string Reference, string Realm)>> ListSecretsAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        List<(string Reference, string Realm)> result = [];

        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (SecretReference.TryParse(reference, out Guid owner, out string realm) && owner == profileId)
            {
                result.Add((reference, realm));
            }
        }

        return result;
    }

    private async Task RemoveCopiesAsync(
        Guid serverId,
        List<(string Reference, string Realm)> copied,
        CancellationToken cancellationToken)
    {
        foreach ((string reference, string realm) in copied)
        {
            try
            {
                await secrets.DeleteAsync(reference, cancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The original failure is what the caller has to hear about; this one is written down
                // so a stray copy can be explained.
                OutboxLog.CopiedSecretNotRemoved(logger, serverId, realm, exception);
            }
        }
    }

    private readonly record struct Moved(int Sessions, int Bindings);
}
