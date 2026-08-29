using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Reads and writes the connection history.
/// </summary>
/// <remarks>
/// A session row is opened when a tunnel comes up and closed when it ends. Rows left open by a crash
/// are closed on the next start, so the history never shows a connection that is still running when
/// it is not.
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Opens a session for a profile that has just connected.
    /// </summary>
    /// <returns>The identifier used to close the session later.</returns>
    public Task<Guid> BeginAsync(
        Guid profileId,
        string? localAddress,
        string? serverAddress,
        int? serverPort,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the final counters and the reason a session ended.
    /// </summary>
    public Task EndAsync(
        Guid sessionId,
        SessionEndReason reason,
        long bytesReceived,
        long bytesSent,
        string? detail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a notable moment to the per session log.
    /// </summary>
    public Task AddEventAsync(
        Guid sessionId,
        string level,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes sessions a previous run left open, which is what a forced exit produces.
    /// </summary>
    /// <returns>How many sessions were closed.</returns>
    public Task<int> CloseAbandonedAsync(CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(
        SessionQuery query,
        CancellationToken cancellationToken = default);

    public Task<SessionTotals> GetTotalsAsync(
        SessionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes history rows older than the given moment.
    /// </summary>
    /// <returns>How many sessions were removed.</returns>
    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter for the history view.
/// </summary>
/// <param name="ProfileId">Restrict to one profile, or null for every profile.</param>
/// <param name="From">Earliest start time to include.</param>
/// <param name="To">Latest start time to include.</param>
/// <param name="Limit">Maximum rows, so a long history cannot stall the view.</param>
public sealed record SessionQuery(
    Guid? ProfileId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 500);

/// <summary>
/// One row of the history, already joined with the profile name.
/// </summary>
public sealed record SessionRecord(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long BytesReceived,
    long BytesSent,
    string? ServerAddress,
    int? ServerPort,
    SessionEndReason EndReason,
    string? EndDetail)
{
    public TimeSpan? Duration => EndedAt is { } ended ? ended - StartedAt : null;
}

/// <summary>
/// Aggregate figures for whatever the current filter selects.
/// </summary>
public sealed record SessionTotals(int Count, TimeSpan Duration, long BytesReceived, long BytesSent);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public SessionStore(IDbContextFactory<PilotDbContext> contextFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
    }

    public async Task<Guid> BeginAsync(
        Guid profileId,
        string? localAddress,
        string? serverAddress,
        int? serverPort,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Session session = new()
        {
            ProfileId = profileId,
            StartedAt = timeProvider.GetUtcNow(),
            LocalAddress = localAddress,
            ServerAddress = serverAddress,
            ServerPort = serverPort,
            EndReason = SessionEndReason.Running,
        };

        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);

        return session.Id;
    }

    public async Task EndAsync(
        Guid sessionId,
        SessionEndReason reason,
        long bytesReceived,
        long bytesSent,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Session? session = await context.Sessions.FindAsync([sessionId], cancellationToken);

        if (session is null || session.EndedAt is not null)
        {
            return;
        }

        session.EndedAt = timeProvider.GetUtcNow();
        session.EndReason = reason;
        session.BytesReceived = bytesReceived;
        session.BytesSent = bytesSent;
        session.EndDetail = Truncate(detail, 500);

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddEventAsync(
        Guid sessionId,
        string level,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(message);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        context.SessionEvents.Add(new SessionEvent
        {
            SessionId = sessionId,
            Timestamp = timeProvider.GetUtcNow(),
            Level = level,
            Message = Truncate(message, 1000) ?? string.Empty,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CloseAbandonedAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        // The end time is unknown, so the start time is used. Claiming the tunnel lasted until the
        // application happened to be restarted would overstate every crashed session.
        return await context.Sessions
            .Where(session => session.EndedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.EndedAt, session => session.StartedAt)
                    .SetProperty(session => session.EndReason, SessionEndReason.ApplicationClosed),
                cancellationToken);
    }

    public async Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(
        SessionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await Filter(context, query)
            .OrderByDescending(session => session.StartedAt)
            .Take(query.Limit)
            .Select(session => new SessionRecord(
                session.Id,
                session.ProfileId,
                session.Profile!.Name,
                session.StartedAt,
                session.EndedAt,
                session.BytesReceived,
                session.BytesSent,
                session.ServerAddress,
                session.ServerPort,
                session.EndReason,
                session.EndDetail))
            .ToListAsync(cancellationToken);
    }

    public async Task<SessionTotals> GetTotalsAsync(
        SessionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // SQLite has no interval type, so the durations are summed in memory over the projected
        // timestamps rather than being pushed into the query.
        List<Totals> rows = await Filter(context, query)
            .Select(session => new Totals(
                session.StartedAt,
                session.EndedAt,
                session.BytesReceived,
                session.BytesSent))
            .ToListAsync(cancellationToken);

        TimeSpan duration = TimeSpan.Zero;
        long received = 0;
        long sent = 0;

        foreach (Totals row in rows)
        {
            if (row.EndedAt is { } ended)
            {
                duration += ended - row.StartedAt;
            }

            received += row.BytesReceived;
            sent += row.BytesSent;
        }

        return new SessionTotals(rows.Count, duration, received, sent);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Sessions
            .Where(session => session.StartedAt < olderThan)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static IQueryable<Session> Filter(PilotDbContext context, SessionQuery query)
    {
        IQueryable<Session> sessions = context.Sessions.AsNoTracking();

        if (query.ProfileId is { } profileId)
        {
            sessions = sessions.Where(session => session.ProfileId == profileId);
        }

        if (query.From is { } from)
        {
            sessions = sessions.Where(session => session.StartedAt >= from);
        }

        if (query.To is { } to)
        {
            sessions = sessions.Where(session => session.StartedAt <= to);
        }

        return sessions;
    }

    private static string? Truncate(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length > maximum ? value[..maximum] : value;

    private sealed record Totals(
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        long BytesReceived,
        long BytesSent);
}
