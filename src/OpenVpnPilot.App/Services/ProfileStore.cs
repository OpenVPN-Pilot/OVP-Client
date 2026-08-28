using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Reads and updates stored profiles for the user interface.
/// </summary>
/// <remarks>
/// The interface exists so view models never depend on Entity Framework directly, which keeps them
/// testable and keeps database concerns out of the presentation layer.
/// </remarks>
public interface IProfileStore
{
    public Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<Folder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    public Task<string?> GetConfigurationAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a connection was established, for the recent list and the usage counter.
    /// </summary>
    public Task RecordConnectionAsync(Guid profileId, CancellationToken cancellationToken = default);

    public Task SetFavouriteAsync(Guid profileId, bool isFavourite, CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public ProfileStore(IDbContextFactory<PilotDbContext> contextFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The configuration text is large and only needed at connection time, so it is left out here.
        return await context.Profiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .Select(profile => new Profile
            {
                Id = profile.Id,
                Name = profile.Name,
                Configuration = string.Empty,
                ContentHash = profile.ContentHash,
                FolderId = profile.FolderId,
                RemoteHost = profile.RemoteHost,
                RemotePort = profile.RemotePort,
                Protocol = profile.Protocol,
                RequiresCredentials = profile.RequiresCredentials,
                IsFavourite = profile.IsFavourite,
                FavouriteSlot = profile.FavouriteSlot,
                HasUnsupportedOptions = profile.HasUnsupportedOptions,
                IsSelfContained = profile.IsSelfContained,
                LastConnectedAt = profile.LastConnectedAt,
                ConnectCount = profile.ConnectCount,
                Colour = profile.Colour,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Folder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Folders
            .AsNoTracking()
            .OrderBy(folder => folder.SortOrder)
            .ThenBy(folder => folder.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetConfigurationAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.Configuration)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RecordConnectionAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        profile.LastConnectedAt = timeProvider.GetUtcNow();
        profile.ConnectCount++;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetFavouriteAsync(
        Guid profileId,
        bool isFavourite,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        profile.IsFavourite = isFavourite;

        // A slot only makes sense while the profile is a favourite.
        if (!isFavourite)
        {
            profile.FavouriteSlot = null;
        }

        profile.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }
}
