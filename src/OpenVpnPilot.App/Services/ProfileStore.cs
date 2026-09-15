using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Library;
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
    /// Replaces a profile's configuration, and everything the list reads from it.
    /// </summary>
    /// <remarks>
    /// Refused when another profile already holds exactly this configuration, because an identical
    /// configuration is how an import recognises a duplicate, and an edit must not create one that an
    /// import would have refused.
    /// </remarks>
    public Task<ConfigurationUpdate> UpdateConfigurationAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default);

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
}

/// <summary>
/// A tag with the number of profiles carrying it.
/// </summary>
public sealed record TagSummary(Guid Id, string Name, string? Colour, int ProfileCount);

/// <summary>
/// What replacing a configuration did.
/// </summary>
/// <param name="Saved">True when the configuration was written.</param>
/// <param name="DuplicateOf">The profile that already holds this configuration, when that is why not.</param>
public sealed record ConfigurationUpdate(bool Saved, string? DuplicateOf);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
/// <remarks>
/// When a profile was last changed is what a shared library settles two people's changes by, so it
/// moves only when something that is shared changes, and only when it actually changes. Saving the
/// editor without touching a field, or marking a favourite, which is nobody else's business, must
/// not make this machine's copy look newer than a colleague's real edit.
/// </remarks>
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
        if (profile is null || string.Equals(profile.Name, name.Trim(), StringComparison.Ordinal))
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

        string? normalised = string.IsNullOrWhiteSpace(notes) ? null : notes;

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null || string.Equals(profile.Notes, normalised, StringComparison.Ordinal))
        {
            return;
        }

        profile.Notes = normalised;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConfigurationUpdate> UpdateConfigurationAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string hash = ProfileImporter.ComputeHash(configuration);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        string? duplicate = await context.Profiles
            .AsNoTracking()
            .Where(other => other.ContentHash == hash && other.Id != profileId)
            .Select(other => other.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (duplicate is not null)
        {
            return new ConfigurationUpdate(false, duplicate);
        }

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null)
        {
            return new ConfigurationUpdate(false, null);
        }

        // Read the way an import reads it, so an edited profile looks in the list exactly as the
        // same file imported fresh would.
        ProfileConfigurationFacts.Apply(profile, configuration);
        profile.UpdatedAt = timeProvider.GetUtcNow();

        await context.SaveChangesAsync(cancellationToken);

        return new ConfigurationUpdate(true, null);
    }

    public async Task SetRouteProtectionAsync(
        Guid profileId,
        bool? protectRoutes,
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);
        if (profile is null || profile.ProtectRoutes == protectRoutes)
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
            .Include(link => link.Tag)
            .Where(link => link.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        HashSet<string> current = new(existing.Select(link => link.Tag!.Name), StringComparer.OrdinalIgnoreCase);

        if (current.SetEquals(wanted))
        {
            return;
        }

        Profile? profile = await context.Profiles.FindAsync([profileId], cancellationToken);

        if (profile is not null)
        {
            profile.UpdatedAt = timeProvider.GetUtcNow();
        }

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

    private sealed record TagLink(Guid ProfileId, string Name);
}
