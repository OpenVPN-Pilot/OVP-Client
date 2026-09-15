using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Tagging;

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

    public Task<IReadOnlyList<TagSummary>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The tag names attached to each profile, so the list can be filtered without a second query
    /// per row.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetProfileTagsAsync(
        CancellationToken cancellationToken = default);

    public Task<string?> GetConfigurationAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a connection was established, for the recent list and the usage counter.
    /// </summary>
    public Task RecordConnectionAsync(Guid profileId, CancellationToken cancellationToken = default);

    public Task SetFavouriteAsync(Guid profileId, bool isFavourite, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a profile in a numbered favourite slot, or clears its slot when the number is null.
    /// </summary>
    /// <remarks>
    /// Slots are unique, so assigning one that is taken moves it rather than failing: the shortcut
    /// bound to that number has to lead somewhere unambiguous.
    /// </remarks>
    public Task SetFavouriteSlotAsync(Guid profileId, int? slot, CancellationToken cancellationToken = default);

    public Task<Guid?> GetProfileInSlotAsync(int slot, CancellationToken cancellationToken = default);

    /// <summary>
    /// The profile connected most recently, or null when nothing has been connected yet.
    /// </summary>
    public Task<Guid?> GetLastConnectedAsync(CancellationToken cancellationToken = default);

    public Task RenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default);

    public Task SetProfileNotesAsync(Guid profileId, string? notes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overrides the route protection for one profile, or returns it to the global setting.
    /// </summary>
    public Task SetRouteProtectionAsync(
        Guid profileId,
        bool? protectRoutes,
        CancellationToken cancellationToken = default);

    public Task SetProfileTagsAsync(
        Guid profileId,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default);

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the discovery marks, which is how the user says they have seen what a watched
    /// directory brought in.
    /// </summary>
    /// <returns>How many profiles were marked as seen.</returns>
    public Task<int> ClearDiscoveriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A tag with the number of profiles carrying it.
/// </summary>
public sealed record TagSummary(Guid Id, string Name, string? Colour, int ProfileCount);

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
                DiscoveredAt = profile.DiscoveredAt,
                RemoteHost = profile.RemoteHost,
                RemotePort = profile.RemotePort,
                Protocol = profile.Protocol,
                RequiresCredentials = profile.RequiresCredentials,
                IsFavourite = profile.IsFavourite,
                FavouriteSlot = profile.FavouriteSlot,
                HasUnsupportedOptions = profile.HasUnsupportedOptions,
                IsSelfContained = profile.IsSelfContained,
                ProtectRoutes = profile.ProtectRoutes,
                LastConnectedAt = profile.LastConnectedAt,
                ConnectCount = profile.ConnectCount,
                Colour = profile.Colour,
                Notes = profile.Notes,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TagSummary>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagSummary(tag.Id, tag.Name, tag.Colour, tag.Profiles.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetProfileTagsAsync(
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        List<TagLink> links = await context.ProfileTags
            .AsNoTracking()
            .Select(link => new TagLink(link.ProfileId, link.Tag!.Name))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, IReadOnlyList<string>> result = [];

        foreach (IGrouping<Guid, TagLink> group in links.GroupBy(link => link.ProfileId))
        {
            result[group.Key] = group.Select(link => link.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return result;
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

    public async Task SetFavouriteSlotAsync(
        Guid profileId,
        int? slot,
        CancellationToken cancellationToken = default)
    {
        if (slot is < 1 or > HotkeyActions.MaximumFavouriteSlot)
        {
            slot = slot is null ? null : throw new ArgumentOutOfRangeException(
                nameof(slot),
                slot,
                $"Favourite slots run from one to {HotkeyActions.MaximumFavouriteSlot}.");
        }

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        if (slot is { } number)
        {
            // The slot is unique, so whoever held it gives it up in the same transaction.
            Profile? previous = await context.Profiles
                .FirstOrDefaultAsync(other => other.FavouriteSlot == number, cancellationToken);

            if (previous is not null && previous.Id != profileId)
            {
                previous.FavouriteSlot = null;
            }

            profile.IsFavourite = true;
        }

        profile.FavouriteSlot = slot;
        profile.UpdatedAt = timeProvider.GetUtcNow();

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid?> GetProfileInSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.FavouriteSlot == slot)
            .Select(profile => (Guid?)profile.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid?> GetLastConnectedAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Profiles
            .AsNoTracking()
            .Where(profile => profile.LastConnectedAt != null)
            .OrderByDescending(profile => profile.LastConnectedAt)
            .Select(profile => (Guid?)profile.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RenameProfileAsync(
        Guid profileId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        profile.Name = name.Trim();
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetProfileNotesAsync(
        Guid profileId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        profile.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRouteProtectionAsync(
        Guid profileId,
        bool? protectRoutes,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return;
        }

        profile.ProtectRoutes = protectRoutes;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetProfileTagsAsync(
        Guid profileId,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagNames);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        List<string> wanted = tagNames
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ProfileTag> existing = await context.ProfileTags
            .Where(link => link.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        context.ProfileTags.RemoveRange(existing);

        TagCatalogue tags = await TagCatalogue.LoadAsync(context, cancellationToken);
        HashSet<Guid> linked = [];

        foreach (string name in wanted)
        {
            Tag tag = tags.Resolve(name);

            if (linked.Add(tag.Id))
            {
                context.ProfileTags.Add(new ProfileTag { ProfileId = profileId, TagId = tag.Id, Tag = tag });
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // A tag nobody uses is noise in the sidebar, so it goes when its last profile lets it go.
        await context.Tags
            .Where(tag => !tag.Profiles.Any())
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Profiles
            .Where(profile => profile.Id == profileId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Tags
            .Where(tag => !tag.Profiles.Any())
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> ClearDiscoveriesAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Profiles
            .Where(profile => profile.DiscoveredAt != null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(profile => profile.DiscoveredAt, (DateTimeOffset?)null),
                cancellationToken);
    }

    private sealed record TagLink(Guid ProfileId, string Name);
}
