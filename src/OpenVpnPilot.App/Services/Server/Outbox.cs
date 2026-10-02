using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The local changes the server has not been told about yet.
/// </summary>
/// <remarks>
/// A marker names what is dirty, never a copy of it: the push reads the entity's current state. That
/// is what lets markers collapse. Several edits of a profile become one update, a delete supersedes
/// the edits before it, and a profile created and deleted again before the server could be reached
/// leaves nothing to send at all.
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Raised after markers were written, so the synchronisation can push soon.
    /// </summary>
    /// <remarks>
    /// Raised once per save that wrote markers, which during a burst of edits is often. Whoever
    /// pushes is expected to wait for things to settle, about two seconds, rather than push per event.
    /// </remarks>
    public event EventHandler? PushRequested;

    /// <summary>
    /// Adds a marker to a unit of work that is not saved yet.
    /// </summary>
    /// <remarks>
    /// The caller's own save writes the marker together with the change it describes, so neither
    /// can exist without the other.
    /// </remarks>
    /// <param name="configurationChanged">
    /// For a profile update only: the change includes an edit of the configuration, which the update
    /// then sends. It stays set on the marker whatever collapses into it later.
    /// </param>
    /// <returns>True when the pending markers changed, false when the marker collapsed into them.</returns>
    public Task<bool> StageAsync(
        PilotDbContext context,
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        bool configurationChanged = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a marker on its own, for a change that is not stored in this database, such as the
    /// settings.
    /// </summary>
    public Task RecordAsync(
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every pending marker, in the order they are to be pushed.
    /// </summary>
    public Task<IReadOnlyList<PendingChange>> GetPendingAsync(CancellationToken cancellationToken = default);

    public Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Notes an attempt that failed in a way worth trying again, and the code the server gave.
    /// </summary>
    public Task RecordFailedAttemptAsync(
        long markerId,
        string? errorCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a marker, because it was pushed or because the server refused it for good.
    /// </summary>
    public Task DropAsync(long markerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every marker, for a different person signing in on this copy.
    /// </summary>
    /// <returns>How many markers were removed.</returns>
    public Task<int> ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class Outbox : IOutbox
{
    private static readonly PendingChangeKind[] ProfileKinds =
    [
        PendingChangeKind.ProfileCreate,
        PendingChangeKind.ProfileUpdate,
        PendingChangeKind.ProfileDelete,
        PendingChangeKind.VaultAdd,
    ];

    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<Outbox> logger;

    // Remembers which units of work already announce their save, so one save raises one request.
    private readonly ConditionalWeakTable<PilotDbContext, object> announcing = [];

    public Outbox(
        IDbContextFactory<PilotDbContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<Outbox> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public event EventHandler? PushRequested;

    public async Task<bool> StageAsync(
        PilotDbContext context,
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        bool configurationChanged = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(kind, entityId, realm);

        if (configurationChanged && kind != PendingChangeKind.ProfileUpdate)
        {
            throw new ArgumentException("Only a profile update says whether the configuration changed.", nameof(configurationChanged));
        }

        List<PendingChange> pending = await LoadRelatedAsync(context, kind, entityId, cancellationToken);
        bool changed = Collapse(context, pending, kind, entityId, realm, configurationChanged);

        if (changed)
        {
            AnnounceOnSave(context);
        }

        return changed;
    }

    public async Task RecordAsync(
        PendingChangeKind kind,
        Guid? entityId = null,
        string? realm = null,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (await StageAsync(context, kind, entityId, realm, cancellationToken: cancellationToken))
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PendingChange>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PendingChanges
            .AsNoTracking()
            .OrderBy(change => change.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PendingChanges.CountAsync(cancellationToken);
    }

    public async Task RecordFailedAttemptAsync(
        long markerId,
        string? errorCode,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        int updated = await context.PendingChanges
            .Where(change => change.Id == markerId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(change => change.Attempts, change => change.Attempts + 1)
                    .SetProperty(change => change.LastErrorCode, errorCode),
                cancellationToken);

        if (updated > 0)
        {
            OutboxLog.AttemptFailed(logger, markerId, errorCode);
        }
    }

    public async Task DropAsync(long markerId, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        int removed = await context.PendingChanges
            .Where(change => change.Id == markerId)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
        {
            OutboxLog.Dropped(logger, markerId);
        }
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        int removed = await context.PendingChanges.ExecuteDeleteAsync(cancellationToken);
        OutboxLog.Cleared(logger, removed);
        return removed;
    }

    private static void Validate(PendingChangeKind kind, Guid? entityId, string? realm)
    {
        switch (kind)
        {
            case PendingChangeKind.Favourites:
            case PendingChangeKind.Hotkeys:
            case PendingChangeKind.Settings:
                if (entityId is not null || realm is not null)
                {
                    throw new ArgumentException($"A {kind} marker covers the whole list and names nothing.", nameof(entityId));
                }

                break;

            case PendingChangeKind.VaultAdd:
                if (entityId is null)
                {
                    throw new ArgumentNullException(nameof(entityId), "A vault marker names the profile.");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(realm);
                break;

            case PendingChangeKind.ProfileCreate:
            case PendingChangeKind.ProfileUpdate:
            case PendingChangeKind.ProfileDelete:
            case PendingChangeKind.TagDelete:
                if (entityId is null)
                {
                    throw new ArgumentNullException(nameof(entityId), $"A {kind} marker names what it is about.");
                }

                if (realm is not null)
                {
                    throw new ArgumentException("Only a vault marker carries a realm.", nameof(realm));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of change the outbox knows.");
        }
    }

    /// <summary>
    /// The pending markers a new one may collapse with, saved or only staged in this unit of work.
    /// </summary>
    private static async Task<List<PendingChange>> LoadRelatedAsync(
        PilotDbContext context,
        PendingChangeKind kind,
        Guid? entityId,
        CancellationToken cancellationToken)
    {
        PendingChangeKind[] related = ProfileKinds.Contains(kind) ? ProfileKinds : [kind];

        // Loading into the change tracker and then reading the local view sees markers this unit of
        // work staged and has not saved, and leaves out those it already removed.
        await context.PendingChanges
            .Where(change => related.Contains(change.Kind) && change.EntityId == entityId)
            .LoadAsync(cancellationToken);

        return context.PendingChanges.Local
            .Where(change => related.Contains(change.Kind) && change.EntityId == entityId)
            .OrderBy(change => change.Id)
            .ToList();
    }

    private bool Collapse(
        PilotDbContext context,
        List<PendingChange> pending,
        PendingChangeKind kind,
        Guid? entityId,
        string? realm,
        bool configurationChanged)
    {
        bool Has(PendingChangeKind wanted) => pending.Any(change => change.Kind == wanted);

        switch (kind)
        {
            case PendingChangeKind.ProfileCreate:
                if (Has(PendingChangeKind.ProfileCreate))
                {
                    return false;
                }

                break;

            case PendingChangeKind.ProfileUpdate:
                // A pending create already sends the latest state, and a deleted profile has none.
                if (Has(PendingChangeKind.ProfileCreate) || Has(PendingChangeKind.ProfileDelete))
                {
                    return false;
                }

                if (pending.FirstOrDefault(change => change.Kind == PendingChangeKind.ProfileUpdate) is { } update)
                {
                    // An edit of the configuration is never forgotten by a later rename.
                    if (!configurationChanged || update.ConfigurationChanged)
                    {
                        return false;
                    }

                    update.ConfigurationChanged = true;
                    OutboxLog.Recorded(logger, kind, entityId, realm);
                    return true;
                }

                break;

            case PendingChangeKind.ProfileDelete:
                if (Has(PendingChangeKind.ProfileDelete))
                {
                    return false;
                }

                // Nothing about a deleted profile is worth sending any more.
                bool neverReachedServer = Has(PendingChangeKind.ProfileCreate);
                Supersede(context, pending, kind, entityId);

                if (neverReachedServer)
                {
                    // The server never knew the profile, so there is nothing to delete there either.
                    return pending.Count > 0;
                }

                break;

            case PendingChangeKind.VaultAdd:
                if (Has(PendingChangeKind.ProfileDelete) || pending.Any(change =>
                        change.Kind == PendingChangeKind.VaultAdd
                        && string.Equals(change.Realm, realm, StringComparison.Ordinal)))
                {
                    return false;
                }

                break;

            default:
                // The lists are sent whole and a tag is deleted once, so one marker covers any number
                // of changes.
                if (pending.Count > 0)
                {
                    return false;
                }

                break;
        }

        context.PendingChanges.Add(new PendingChange
        {
            Kind = kind,
            EntityId = entityId,
            Realm = realm,
            ConfigurationChanged = configurationChanged,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        OutboxLog.Recorded(logger, kind, entityId, realm);
        return true;
    }

    private void Supersede(
        PilotDbContext context,
        List<PendingChange> pending,
        PendingChangeKind kind,
        Guid? entityId)
    {
        if (pending.Count == 0)
        {
            return;
        }

        context.PendingChanges.RemoveRange(pending);
        OutboxLog.Superseded(logger, kind, entityId, pending.Count);
    }

    private void AnnounceOnSave(PilotDbContext context)
    {
        if (!announcing.TryAdd(context, new object()))
        {
            return;
        }

        void OnSaved(object? sender, SavedChangesEventArgs arguments)
        {
            context.SavedChanges -= OnSaved;
            announcing.Remove(context);
            PushRequested?.Invoke(this, EventArgs.Empty);
        }

        context.SavedChanges += OnSaved;
    }
}
