using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// The directories the application keeps profiles in step with.
/// </summary>
public interface IWatchedFolderStore
{
    public Task<IReadOnlyList<WatchedFolderRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a directory to watch, or returns the one already watching that path.
    /// </summary>
    public Task<Guid> AddAsync(
        string path,
        bool recursive,
        bool autoImport,
        CancellationToken cancellationToken = default);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a directory has just been scanned.
    /// </summary>
    public Task MarkScannedAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// One watched directory as the interface needs it.
/// </summary>
public sealed record WatchedFolderRecord(
    Guid Id,
    string Path,
    bool IsRecursive,
    bool AutoImport,
    DateTimeOffset? LastScanAt);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class WatchedFolderStore : IWatchedFolderStore
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public WatchedFolderStore(IDbContextFactory<PilotDbContext> contextFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<WatchedFolderRecord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.WatchedFolders
            .AsNoTracking()
            .OrderBy(folder => folder.Path)
            .Select(folder => new WatchedFolderRecord(
                folder.Id,
                folder.Path,
                folder.IsRecursive,
                folder.AutoImport,
                folder.LastScanAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> AddAsync(
        string path,
        bool recursive,
        bool autoImport,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        WatchedFolder? existing = await context.WatchedFolders
            .FirstOrDefaultAsync(folder => folder.Path == full, cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        WatchedFolder created = new()
        {
            Path = full,
            IsRecursive = recursive,
            AutoImport = autoImport,
        };

        context.WatchedFolders.Add(created);
        await context.SaveChangesAsync(cancellationToken);

        return created.Id;
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.WatchedFolders
            .Where(folder => folder.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task MarkScannedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        await context.WatchedFolders
            .Where(folder => folder.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(folder => folder.LastScanAt, now),
                cancellationToken);
    }
}
