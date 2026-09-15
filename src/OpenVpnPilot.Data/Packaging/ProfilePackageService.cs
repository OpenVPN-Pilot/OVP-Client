using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Tagging;

namespace OpenVpnPilot.Data.Packaging;

/// <summary>
/// Fills a package from the store, and applies one back into it.
/// </summary>
/// <remarks>
/// Applying a package never overwrites what is already there. A configuration that is already stored
/// is recognised by its content and skipped, and a name that is taken gets a suffix. Importing the
/// same package twice therefore changes nothing the second time, which is what makes it safe to hand
/// around.
///
/// Both directions take a choice. Whoever writes a package decides which profiles go in and whether
/// the shortcuts, the settings and the sign ins go with them; whoever opens one sees what it holds
/// and decides what to take. A package is a set somebody assembled for a reason, and the person
/// receiving it rarely wants every part of it.
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
    /// <param name="includeHotkeys">Whether the shortcut bindings travel with the profiles.</param>
    /// <param name="settings">The settings to carry, already stripped of what describes this machine.</param>
    public async Task<ProfilePackageContent> CreateAsync(
        IReadOnlyCollection<Guid>? profileIds = null,
        IReadOnlyList<PackagedCredential>? credentials = null,
        bool includeHotkeys = true,
        JsonElement? settings = null,
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

        List<HotkeyBinding> hotkeys = includeHotkeys
            ? await context.HotkeyBindings.AsNoTracking().ToListAsync(cancellationToken)
            : [];

        return new ProfilePackageContent
        {
            CreatedAt = timeProvider.GetUtcNow(),
            WrittenBy = typeof(ProfilePackageService).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            Profiles = profiles.Select(profile => Pack(profile, tags)).ToList(),
            Hotkeys = hotkeys
                .Select(binding => new PackagedHotkey(binding.ActionId, binding.Gesture, binding.IsEnabled))
                .ToList(),
            Credentials = credentials ?? [],
            Settings = settings,
        };
    }

    /// <summary>
    /// Says what applying a package would do, without doing any of it.
    /// </summary>
    public async Task<PackagePreview> PreviewAsync(
        ProfilePackageContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        Dictionary<string, string> storedByHash = await StoredNamesByHashAsync(cancellationToken);

        List<PackagePreviewProfile> profiles = [];

        foreach (PackagedProfile packaged in content.Profiles ?? [])
        {
            storedByHash.TryGetValue(ProfileImporter.ComputeHash(packaged.Configuration ?? string.Empty), out string? stored);

            profiles.Add(new PackagePreviewProfile(
                packaged,
                stored,
                (content.Credentials ?? []).Count(credential => credential.ProfileId == packaged.Id)));
        }

        (int addable, int clashing) = await CountHotkeysAsync(content.Hotkeys ?? [], cancellationToken);

        return new PackagePreview(profiles, addable, clashing, content.Settings is { ValueKind: JsonValueKind.Object });
    }

    /// <summary>
    /// Writes a package into the store, skipping anything already present.
    /// </summary>
    /// <remarks>
    /// Credentials the package carries come back in the result rather than being written here. They
    /// belong in protected storage, which is a platform facility, and the store deliberately holds
    /// no passwords at all. They are addressed to the profiles they now belong to, which are new
    /// profiles for anything added and the existing ones for anything the store already held: a
    /// package sent to fill in credentials for a set that is already imported has to work.
    /// </remarks>
    public async Task<PackageApplyResult> ApplyAsync(
        ProfilePackageContent content,
        PackageApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        options ??= new PackageApplyOptions();

        Dictionary<string, Guid> existingByHash = await context.Profiles
            .Select(profile => new { profile.ContentHash, profile.Id })
            .ToDictionaryAsync(row => row.ContentHash, row => row.Id, StringComparer.Ordinal, cancellationToken);

        TagCatalogue tags = await TagCatalogue.LoadAsync(context, cancellationToken);

        // What each packaged profile turned into, so the credentials can follow it.
        Dictionary<Guid, Guid> resolved = [];

        DateTimeOffset now = timeProvider.GetUtcNow();
        int added = 0;
        int skipped = 0;

        foreach (PackagedProfile packaged in content.Profiles ?? [])
        {
            if (options.ProfileIds is { } chosen && !chosen.Contains(packaged.Id))
            {
                continue;
            }

            string configuration = packaged.Configuration ?? string.Empty;
            string hash = ProfileImporter.ComputeHash(configuration);

            if (existingByHash.TryGetValue(hash, out Guid existing))
            {
                resolved[packaged.Id] = existing;
                skipped++;
                continue;
            }

            Profile profile = new()
            {
                Name = await UniqueNameAsync(packaged.Name ?? string.Empty, cancellationToken),
                Configuration = configuration,
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
            ApplyTags(profile, packaged.Tags, tags);

            existingByHash[hash] = profile.Id;
            resolved[packaged.Id] = profile.Id;
            added++;
        }

        await context.SaveChangesAsync(cancellationToken);

        int hotkeys = options.IncludeHotkeys
            ? await ApplyHotkeysAsync(content.Hotkeys ?? [], cancellationToken)
            : 0;

        return new PackageApplyResult(added, skipped, hotkeys)
        {
            Credentials = options.IncludeCredentials
                ? Readdress(content.Credentials ?? [], resolved)
                : [],
        };
    }

    private static PackagedProfile Pack(Profile profile, Dictionary<Guid, List<string>> tags) => new()
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
        UpdatedAt = profile.UpdatedAt,
    };

    /// <summary>
    /// Points each credential at the profile it belongs to in this store.
    /// </summary>
    private static List<PackagedCredential> Readdress(
        IReadOnlyList<PackagedCredential> credentials,
        Dictionary<Guid, Guid> resolved) =>
        credentials
            .Where(credential => resolved.ContainsKey(credential.ProfileId))
            .Select(credential => credential with { ProfileId = resolved[credential.ProfileId] })
            .ToList();

    private async Task<Dictionary<string, string>> StoredNamesByHashAsync(CancellationToken cancellationToken)
    {
        var rows = await context.Profiles
            .AsNoTracking()
            .Select(profile => new { profile.ContentHash, profile.Name })
            .ToListAsync(cancellationToken);

        Dictionary<string, string> byHash = new(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            byHash.TryAdd(row.ContentHash, row.Name);
        }

        return byHash;
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

    private void ApplyTags(Profile profile, IReadOnlyList<string>? names, TagCatalogue tags)
    {
        // A package is read from a file somebody else wrote, so a missing list is not assumed away.
        if (names is null)
        {
            return;
        }

        HashSet<Guid> linked = [];

        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            Tag tag = tags.Resolve(name);

            if (!linked.Add(tag.Id))
            {
                continue;
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
    /// How many of the packaged shortcuts would be taken, and how many would not because this
    /// machine already binds the action or the combination.
    /// </summary>
    private async Task<(int Addable, int Clashing)> CountHotkeysAsync(
        IReadOnlyList<PackagedHotkey> hotkeys,
        CancellationToken cancellationToken)
    {
        HashSet<string> known = await context.HotkeyBindings
            .Select(binding => binding.ActionId)
            .ToHashSetAsync(cancellationToken);

        HashSet<string> taken = await context.HotkeyBindings
            .Select(binding => binding.Gesture)
            .ToHashSetAsync(cancellationToken);

        int addable = 0;

        foreach (PackagedHotkey hotkey in hotkeys)
        {
            if (!known.Contains(hotkey.ActionId) && taken.Add(hotkey.Gesture))
            {
                addable++;
            }
        }

        return (addable, hotkeys.Count - addable);
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
            if (hotkey is null
                || string.IsNullOrWhiteSpace(hotkey.ActionId)
                || string.IsNullOrWhiteSpace(hotkey.Gesture)
                || known.Contains(hotkey.ActionId)
                || !taken.Add(hotkey.Gesture))
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

    private sealed record TagLink(Guid ProfileId, string Name);
}

/// <summary>
/// What to take from a package.
/// </summary>
public sealed record PackageApplyOptions
{
    /// <summary>
    /// The packaged profiles to take, by the identifier they carry in the package. Null takes all.
    /// </summary>
    public IReadOnlySet<Guid>? ProfileIds { get; init; }

    /// <summary>
    /// Whether the sign ins of the chosen profiles are handed back to be stored.
    /// </summary>
    public bool IncludeCredentials { get; init; } = true;

    /// <summary>
    /// Whether the shortcut bindings the store does not have yet are added.
    /// </summary>
    public bool IncludeHotkeys { get; init; } = true;
}

/// <summary>
/// What a package holds, measured against the store it would be applied to.
/// </summary>
/// <param name="Profiles">Every packaged profile, with what the store already has of it.</param>
/// <param name="HotkeysToAdd">Shortcuts the store has no binding for yet.</param>
/// <param name="HotkeysClashing">Shortcuts left out because the action or the combination is bound here.</param>
/// <param name="HasSettings">True when the package carries settings.</param>
public sealed record PackagePreview(
    IReadOnlyList<PackagePreviewProfile> Profiles,
    int HotkeysToAdd,
    int HotkeysClashing,
    bool HasSettings);

/// <summary>
/// One packaged profile, and the stored profile with the same configuration if there is one.
/// </summary>
/// <param name="Profile">The profile as the package carries it.</param>
/// <param name="StoredAs">The name of the stored profile with the same configuration, or null.</param>
/// <param name="Credentials">How many sign ins the package carries for it.</param>
public sealed record PackagePreviewProfile(PackagedProfile Profile, string? StoredAs, int Credentials)
{
    public bool IsStored => StoredAs is not null;
}

/// <summary>
/// What applying a package changed.
/// </summary>
/// <param name="Added">Profiles created.</param>
/// <param name="Skipped">Profiles the store already held, recognised by their contents.</param>
/// <param name="Hotkeys">Shortcut bindings created.</param>
public sealed record PackageApplyResult(int Added, int Skipped, int Hotkeys)
{
    /// <summary>
    /// Credentials the package carried, already addressed to the profiles in this store.
    /// </summary>
    /// <remarks>
    /// Handed back rather than stored, because passwords belong in the protected storage the
    /// platform offers and never in the profile database.
    /// </remarks>
    public IReadOnlyList<PackagedCredential> Credentials { get; init; } = [];
}
