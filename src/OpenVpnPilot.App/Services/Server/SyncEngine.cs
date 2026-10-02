using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Pushes this copy's changes to the server, then pulls the server's, one cycle at a time.
/// </summary>
/// <remarks>
/// <para>
/// A cycle is the push, then the change feed, then the person's favourites, shortcuts and settings,
/// then matching uploads the server called duplicates. A failure that means nothing can be sent now
/// ends the cycle where it happened; nothing is pulled after a push that could not finish, because
/// the pull would put back what is still waiting to be sent.
/// </para>
/// <para>
/// Cycles never overlap. Whoever asks while one runs waits for it and then gets one of their own,
/// and the schedule's requests during a cycle become exactly one more cycle after it.
/// </para>
/// </remarks>
public sealed partial class SyncEngine : ISyncEngine, IDisposable
{
    private readonly IServerConnection connection;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IOutbox outbox;
    private readonly IServerProfileMaintenance maintenance;
    private readonly ILibraryChangeNotifier notifier;
    private readonly INetworkAvailability network;
    private readonly TimeProvider time;
    private readonly ILogger<SyncEngine> logger;

    private readonly OutboxPusher pusher;
    private readonly ChangeFeedPuller puller;
    private readonly PersonalDataSync personal;

    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Lock statusGate = new();
    private SyncStatus status = SyncStatus.Initial;
    private int droppedSinceSuccess;
    private bool unreachable;
    private TimeSpan? retryAfter;

    public SyncEngine(
        IServerConnection connection,
        IDbContextFactory<PilotDbContext> contextFactory,
        IOutbox outbox,
        IServerProfileMaintenance maintenance,
        ISecretStore secrets,
        IHeldVaultSecrets heldSecrets,
        IRemovedProfileTunnels tunnels,
        IServerNotices notices,
        IPortableSettings settings,
        ILibraryChangeNotifier notifier,
        INetworkAvailability network,
        TimeProvider time,
        ILogger<SyncEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(heldSecrets);
        ArgumentNullException.ThrowIfNull(tunnels);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        this.connection = connection;
        this.contextFactory = contextFactory;
        this.outbox = outbox;
        this.maintenance = maintenance;
        this.notifier = notifier;
        this.network = network;
        this.time = time;
        this.logger = logger;

        personal = new PersonalDataSync(connection.Api, contextFactory, outbox, settings, logger);
        pusher = new OutboxPusher(connection.Api, contextFactory, outbox, maintenance, secrets, heldSecrets, settings, personal, logger);
        puller = new ChangeFeedPuller(connection.Api, contextFactory, secrets, maintenance, tunnels, notices, time, logger);
    }

    public SyncStatus Status
    {
        get
        {
            lock (statusGate)
            {
                return status;
            }
        }
    }

    public event EventHandler? StatusChanged;

    private string Server => connection.BaseAddress.Host;

    public async Task<SyncCycleResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return await RunExclusiveAsync(cancellationToken);
    }

    private async Task<SyncCycleResult> RunExclusiveAsync(CancellationToken cancellationToken)
    {
        await cycleGate.WaitAsync(cancellationToken);

        try
        {
            // Whatever asked for a cycle before this one started is answered by it.
            while (wake.Reader.TryRead(out _))
            {
            }

            return await RunCycleAsync(cancellationToken);
        }
        finally
        {
            cycleGate.Release();
        }
    }

    private async Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (connection.Wipe.IsRequested)
        {
            return new SyncCycleResult(false, SyncState.Idle, 0, 0, false);
        }

        SyncStatus before = Status;
        Publish(current => current with { State = SyncState.Synchronising });

        long started = Stopwatch.GetTimestamp();
        SyncCycle cycle = new();
        bool pushed = false;

        try
        {
            if (await pusher.PushAsync(cycle, cancellationToken) is null)
            {
                pushed = true;

                if (await puller.PullAsync(cycle, cancellationToken) is { } pullFailure)
                {
                    cycle.Stop(pullFailure);
                }
                else if (await personal.PullAsync(cycle, cancellationToken) is { } personalFailure)
                {
                    cycle.Stop(personalFailure);
                }
                else
                {
                    await MatchDuplicatesAsync(cycle, cancellationToken);
                }
            }

            if (cycle.AdministratorChangesDropped)
            {
                await RefreshRoleAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            notifier.Notify(cycle.Changes);
            Publish(current => current with { State = before.State });
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A fault of this machine, the database most likely, rather than of the server. The
            // schedule goes on and the next cycle tries again from what is stored.
            SyncEngineLog.CycleFailed(logger, Server, exception);
            notifier.Notify(cycle.Changes);
            await FinishAsync(cycle, SyncState.ChangesWaiting, pushed, completed: false, CancellationToken.None);
            return new SyncCycleResult(false, SyncState.ChangesWaiting, cycle.Pushed, cycle.Dropped, cycle.Changes != LibraryChanges.None);
        }

        notifier.Notify(cycle.Changes);

        bool completed = cycle.Failure is null;
        SyncState state = await StateAfterAsync(cycle, cancellationToken);
        await FinishAsync(cycle, state, pushed, completed, cancellationToken);

        long duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogCycle(cycle, state, completed, duration);

        if (completed && cycle.FollowUp)
        {
            RequestSync();
        }

        return new SyncCycleResult(completed, state, cycle.Pushed, cycle.Dropped, cycle.Changes != LibraryChanges.None);
    }

    /// <summary>
    /// Matches each upload the server called a duplicate to the server's profile with the same
    /// configuration, which the pull has just brought, or removes it when there is none.
    /// </summary>
    private async Task MatchDuplicatesAsync(SyncCycle cycle, CancellationToken cancellationToken)
    {
        foreach (Guid temporaryId in cycle.Duplicates)
        {
            if (await maintenance.FindServerDuplicateAsync(temporaryId, cancellationToken) is { } serverId)
            {
                await maintenance.RekeyAsync(temporaryId, serverId, cancellationToken);
                SyncEngineLog.DuplicateMatched(logger, temporaryId, serverId);
            }
            else
            {
                await RemoveTemporaryAsync(temporaryId, cancellationToken);
                SyncEngineLog.DuplicateUnmatched(logger, temporaryId);
            }

            cycle.Changes |= LibraryChanges.Profiles;
        }
    }

    private async Task RemoveTemporaryAsync(Guid temporaryId, CancellationToken cancellationToken)
    {
        await using (PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await context.PendingChanges.Where(change => change.EntityId == temporaryId).ExecuteDeleteAsync(cancellationToken);
            await context.Profiles.Where(profile => profile.Id == temporaryId).ExecuteDeleteAsync(cancellationToken);
        }

        await maintenance.DeleteSecretsAsync([temporaryId], cancellationToken);
    }

    private async Task RefreshRoleAsync(CancellationToken cancellationToken)
    {
        ServerResult<CurrentUserResponse> user = await connection.Api.GetCurrentUserAsync(cancellationToken);

        if (!user.IsSuccess)
        {
            return;
        }

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        SyncStateRow row = await RowAsync(context, cancellationToken);

        row.UserId = user.Value.Id;
        row.Username = user.Value.Username;
        row.UserDisplayName = user.Value.DisplayName;
        row.UserRole = user.Value.Role;
        row.UserProvider = user.Value.Provider;

        await context.SaveChangesAsync(cancellationToken);
        SyncEngineLog.RoleRefreshed(logger, user.Value.Role);
    }

    /// <summary>
    /// The state a cycle leaves behind. A server error is asked about once more: a server whose
    /// readiness check cannot be reached either counts as offline.
    /// </summary>
    private async Task<SyncState> StateAfterAsync(SyncCycle cycle, CancellationToken cancellationToken)
    {
        if (cycle.Failure is not { } failure)
        {
            return await outbox.CountAsync(cancellationToken) == 0 ? SyncState.Synchronised : SyncState.ChangesWaiting;
        }

        SyncState state = SyncFailures.StateOf(failure);

        if (state == SyncState.Degraded
            && (await connection.Api.GetReadinessAsync(cancellationToken)).Outcome == ServerOutcome.Offline)
        {
            state = SyncState.Offline;
        }

        return state;
    }

    /// <summary>
    /// Stores how the cycle went and publishes the status.
    /// </summary>
    private async Task FinishAsync(SyncCycle cycle, SyncState state, bool pushed, bool completed, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        SyncStateRow row = await RowAsync(context, cancellationToken);

        if (pushed && cycle.Pushed > 0)
        {
            row.LastSuccessfulPushAt = time.GetUtcNow();
        }

        if (cycle.LastProblem is { } problem)
        {
            row.LastErrorCode = problem.Code ?? problem.Outcome.ToString();
            row.LastRequestId = problem.RequestId;
        }

        await context.SaveChangesAsync(cancellationToken);

        int pending = await outbox.CountAsync(cancellationToken);
        int dropped = completed && cycle.Dropped == 0 ? 0 : droppedSinceSuccess + cycle.Dropped;
        droppedSinceSuccess = dropped;
        retryAfter = cycle.Failure?.RetryAfter;

        string? detail = cycle.Failure?.Problem?.Detail;

        Publish(_ => new SyncStatus(
            state,
            row.LastSuccessfulPullAt,
            row.LastSuccessfulPushAt,
            pending,
            dropped,
            row.Cursor,
            row.LastErrorCode,
            row.LastRequestId,
            state == SyncState.ClockWrong ? detail : null));
    }

    private void LogCycle(SyncCycle cycle, SyncState state, bool completed, long duration)
    {
        bool offline = state == SyncState.Offline;

        if (offline && !unreachable)
        {
            SyncEngineLog.WentOffline(logger, Server, state);
        }
        else if (!offline && unreachable)
        {
            SyncEngineLog.BackOnline(logger, Server);
        }

        unreachable = offline;

        if (cycle.Failure?.Outcome == ServerOutcome.Wiped)
        {
            SyncEngineLog.Wiped(logger);
        }

        if (completed)
        {
            SyncEngineLog.CycleCompleted(
                logger,
                Server,
                cycle.Pushed,
                cycle.Dropped,
                cycle.PulledProfiles,
                cycle.PulledTags,
                cycle.PulledVaultEntries,
                cycle.Deletions,
                cycle.CursorFrom,
                cycle.CursorTo,
                duration);
        }
        else
        {
            SyncEngineLog.CycleStopped(
                logger,
                Server,
                state,
                cycle.Failure?.Code,
                cycle.Failure?.RequestId,
                cycle.Pushed,
                cycle.Dropped,
                duration);
        }
    }

    private async Task LoadStoredStatusAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        SyncStateRow? row = await context.SyncStates.AsNoTracking()
            .FirstOrDefaultAsync(state => state.Id == SyncStateRow.SingletonId, cancellationToken);

        int pending = await outbox.CountAsync(cancellationToken);

        Publish(current => current with
        {
            LastPullAt = row?.LastSuccessfulPullAt,
            LastPushAt = row?.LastSuccessfulPushAt,
            PendingChanges = pending,
            Cursor = row?.Cursor,
            LastErrorCode = row?.LastErrorCode,
            LastRequestId = row?.LastRequestId,
        });
    }

    private static async Task<SyncStateRow> RowAsync(PilotDbContext context, CancellationToken cancellationToken) =>
        await context.SyncStates.FindAsync([SyncStateRow.SingletonId], cancellationToken)
        ?? context.SyncStates.Add(new SyncStateRow()).Entity;

    private void Publish(Func<SyncStatus, SyncStatus> change)
    {
        lock (statusGate)
        {
            status = change(status);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
