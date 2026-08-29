using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Data.Packaging;

/// <summary>
/// Fills a package from the store, and applies one back into it.
/// </summary>
/// <remarks>
/// Applying a package never overwrites what is already there. A configuration that is already stored
/// is recognised by its content and skipped, and a name that is taken gets a suffix. Importing the
/// same package twice therefore changes nothing the second time, which is what makes it safe to hand
/// around.
/// </remarks>
public sealed class ProfilePackageService
{
    private readonly PilotDbContext context;
    private readonly TimeProvider timeProvider;

    public ProfilePackageService(PilotDbContext context, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.context = context;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads the store into a package.
    /// </summary>
    /// <param name="profileIds">The profiles to include, or null for everything.</param>
    /// <param name="credentials">
    /// Credentials to include. Only supply these when the package will be given a passphrase.
    /// </param>
    public async Task<ProfilePackageContent> CreateAsync(
        IReadOnlyCollection<Guid>? profileIds = null,
        IReadOnlyList<PackagedCredential>? credentials = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Profile> query = context.Profiles.AsNoTracking();

        if (profileIds is { Count: > 0 })
        {
            query = query.Where(profile => profileIds.Contains(profile.Id));
        }

        List<Profile> profiles = await query.OrderBy(profile => profile.Name).ToListAsync(cancellationToken);

        Dictionary<Guid, List<string>> tags = await ReadTagsAsync(
            profiles.Select(profile => profile.Id).ToHashSet(),
            cancellationToken);

        List<HotkeyBinding> hotkeys = await context.HotkeyBindings
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new ProfilePackageContent
        {
            CreatedAt = timeProvider.GetUtcNow(),
            WrittenBy = typeof(ProfilePackageService).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            Profiles = profiles.Select(profile => new PackagedProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Configuration = profile.Configuration,
                RemoteHost = profile.RemoteHost,
                RemotePort = profile.RemotePort,
                Protocol = profile.Protocol,
                RequiresCredentials = profile.RequiresCredentials,
                IsFavourite = profile.IsFavourite,
                FavouriteSlot = profile.FavouriteSlot,
                ProtectRoutes = profile.ProtectRoutes,
                Notes = profile.Notes,
                Colour = profile.Colour,
                Tags = tags.TryGetValue(profile.Id, out List<string>? names) ? names : [],
            }).ToList(),
            Hotkeys = hotkeys
                .Select(binding => new PackagedHotkey(binding.ActionId, binding.Gesture, binding.IsEnabled))
                .ToList(),
            Credentials = credentials ?? [],
        };
    }

    /// <summary>
    /// Writes a package into the store, skipping anything already present.
    /// </summary>
    public async Task<PackageApplyResult> ApplyAsync(
        ProfilePackageContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        HashSet<string> existingHashes = await context.Profiles
            .Select(profile => profile.ContentHash)
            .ToHashSetAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();
        int added = 0;
        int skipped = 0;

        foreach (PackagedProfile packaged in content.Profiles)
        {
            string hash = HashOf(packaged.Configuration);

            if (!existingHashes.Add(hash))
            {
                skipped++;
                continue;
            }

            Profile profile = new()
            {
                Name = await UniqueNameAsync(packaged.Name, cancellationToken),
                Configuration = packaged.Configuration,
                ContentHash = hash,
                Source = ProfileSource.Imported,
                RemoteHost = packaged.RemoteHost,
                RemotePort = packaged.RemotePort,
                Protocol = packaged.Protocol,
                RequiresCredentials = packaged.RequiresCredentials,
                IsFavourite = packaged.IsFavourite,
                ProtectRoutes = packaged.ProtectRoutes,
                Notes = packaged.Notes,
                Colour = packaged.Colour,
                CreatedAt = now,
                UpdatedAt = now,
            };

            context.Profiles.Add(profile);
            await ApplyTagsAsync(profile, packaged.Tags, cancellationToken);

            added++;
        }

        await context.SaveChangesAsync(cancellationToken);

        int hotkeys = await ApplyHotkeysAsync(content.Hotkeys, cancellationToken);

        return new PackageApplyResult(added, skipped, hotkeys);
    }

    private async Task<Dictionary<Guid, List<string>>> ReadTagsAsync(
        HashSet<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        List<TagLink> links = await context.ProfileTags
            .AsNoTracking()
            .Where(link => profileIds.Contains(link.ProfileId))
            .Select(link => new TagLink(link.ProfileId, link.Tag!.Name))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, List<string>> result = [];

        foreach (TagLink link in links)
        {
            if (!result.TryGetValue(link.ProfileId, out List<string>? names))
            {
                names = [];
                result[link.ProfileId] = names;
            }

            names.Add(link.Name);
        }

        return result;
    }

    private async Task ApplyTagsAsync(
        Profile profile,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Tag? tag = await context.Tags
                .FirstOrDefaultAsync(candidate => candidate.Name == name, cancellationToken);

            if (tag is null)
            {
                tag = new Tag { Name = name };
                context.Tags.Add(tag);
            }

            context.ProfileTags.Add(new ProfileTag
            {
                ProfileId = profile.Id,
                TagId = tag.Id,
                Tag = tag,
            });
        }
    }

    /// <summary>
    /// Writes the shortcut bindings, leaving any the store already has alone.
    /// </summary>
    /// <remarks>
    /// A shortcut is a property of the machine rather than of the profile set, so a package must not
    /// take one away from whoever is importing it.
    /// </remarks>
    private async Task<int> ApplyHotkeysAsync(
        IReadOnlyList<PackagedHotkey> hotkeys,
        CancellationToken cancellationToken)
    {
        if (hotkeys.Count == 0)
        {
            return 0;
        }

        HashSet<string> known = await context.HotkeyBindings
            .Select(binding => binding.ActionId)
            .ToHashSetAsync(cancellationToken);

        HashSet<string> taken = await context.HotkeyBindings
            .Select(binding => binding.Gesture)
            .ToHashSetAsync(cancellationToken);

        int added = 0;

        foreach (PackagedHotkey hotkey in hotkeys)
        {
            if (known.Contains(hotkey.ActionId) || !taken.Add(hotkey.Gesture))
            {
                continue;
            }

            context.HotkeyBindings.Add(new HotkeyBinding
            {
                ActionId = hotkey.ActionId,
                Gesture = hotkey.Gesture,
                IsEnabled = hotkey.IsEnabled,
            });

            added++;
        }

        if (added > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// Finds a name that is not taken, so an import never silently replaces an existing profile.
    /// </summary>
    private async Task<string> UniqueNameAsync(string wanted, CancellationToken cancellationToken)
    {
        if (!await context.Profiles.AnyAsync(profile => profile.Name == wanted, cancellationToken))
        {
            return wanted;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = $"{wanted} ({suffix})";

            if (!await context.Profiles.AnyAsync(profile => profile.Name == candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return $"{wanted} ({Guid.NewGuid():N})";
    }

    private static string HashOf(string configuration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))).ToLowerInvariant();

    private sealed record TagLink(Guid ProfileId, string Name);
}

/// <summary>
/// What applying a package changed.
/// </summary>
/// <param name="Added">Profiles created.</param>
/// <param name="Skipped">Profiles the store already held, recognised by their contents.</param>
/// <param name="Hotkeys">Shortcut bindings created.</param>
public sealed record PackageApplyResult(int Added, int Skipped, int Hotkeys);
