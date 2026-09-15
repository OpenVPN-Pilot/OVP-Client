using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Packaging;
using OpenVpnPilot.Data.Tagging;

namespace OpenVpnPilot.Data.Library;

/// <summary>
/// Reconciles this machine's library with a shared one.
/// </summary>
/// <remarks>
/// Three versions take part: what this machine has now, what the shared file holds now, and what the
/// shared file held when this machine last synchronised with it, which is the common ancestor of the
/// other two. Comparing each side with the ancestor says who changed what. A field only one side
/// changed takes that side's value, so a colleague renaming a profile and this machine changing its
/// port both survive. Only a field both sides changed is a conflict, and there the later change wins
/// and the conflict is reported. Without an ancestor, the first time a machine joins a library, the
/// later version of a whole profile wins.
///
/// A deletion is recognised the same way: a profile the ancestor had and one side no longer has was
/// deleted there. The shared file also records deletions with their time, so a machine that joins
/// later, or has lost its ancestor, does not bring back a profile everyone else deleted. A deletion
/// never wins over a change the other side made after it; losing a colleague's edit to a delete that
/// crossed it is worse than a profile that has to be deleted twice.
///
/// Two profiles with identical configurations are one profile the library holds twice, usually
/// because two people imported the same file before either synchronised. The one whose identifier
/// sorts first is kept, on every machine alike, so the libraries converge rather than trading
/// duplicates back and forth; what this machine kept about the other, its favourite mark, its
/// history and its sign ins, moves to the one kept.
///
/// What is personal is never shared: favourites, shortcut slots, when a profile was last used and
/// the session history stay on the machine they belong to.
/// </remarks>
public sealed class LibraryMerger
{
    /// <summary>
    /// How long a deletion is remembered in the shared file.
    /// </summary>
    /// <remarks>
    /// Long enough for a machine that has been switched off for months to learn about it, short
    /// enough that the file does not carry every profile anyone ever deleted.
    /// </remarks>
    public static readonly TimeSpan DeletionMemory = TimeSpan.FromDays(365);

    private readonly PilotDbContext context;
    private readonly ISecretStore secrets;
    private readonly TimeProvider timeProvider;

    public LibraryMerger(PilotDbContext context, ISecretStore secrets, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(secrets);

        this.context = context;
        this.secrets = secrets;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The library as this machine has it, in the form the shared file holds it.
    /// </summary>
    public async Task<ProfilePackageContent> ReadLocalAsync(CancellationToken cancellationToken = default)
    {
        List<Profile> profiles = await LoadProfilesAsync(tracked: false, cancellationToken);
        HashSet<Guid> ids = [.. profiles.Select(profile => profile.Id)];

        return new ProfilePackageContent
        {
            CreatedAt = timeProvider.GetUtcNow(),
            WrittenBy = WrittenBy,
            Profiles = profiles.Select(Pack).ToList(),
            Credentials = [.. (await ReadCredentialsAsync(cancellationToken)).Values.Where(credential => ids.Contains(credential.ProfileId))],
        };
    }

    /// <summary>
    /// Brings the shared library into this machine's store, and says what the shared file should
    /// hold afterwards.
    /// </summary>
    /// <param name="remote">What the shared file holds now.</param>
    /// <param name="ancestor">What it held at the last synchronisation, or null when there was none.</param>
    /// <param name="protectedProfiles">
    /// Profiles that must not be removed from this machine now, because a tunnel is using them. Their
    /// deletion waits for the next synchronisation.
    /// </param>
    public async Task<LibraryMergeResult> MergeAsync(
        ProfilePackageContent remote,
        ProfilePackageContent? ancestor,
        IReadOnlySet<Guid>? protectedProfiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);

        protectedProfiles ??= new HashSet<Guid>();
        DateTimeOffset now = timeProvider.GetUtcNow();

        List<Profile> localProfiles = await LoadProfilesAsync(tracked: true, cancellationToken);
        Dictionary<Guid, Profile> localEntities = localProfiles.ToDictionary(profile => profile.Id);
        Dictionary<Guid, PackagedProfile> local = localProfiles.ToDictionary(profile => profile.Id, Pack);

        Dictionary<Guid, PackagedProfile> theirs = Index(remote.Profiles);
        Dictionary<Guid, PackagedProfile>? common = ancestor is null ? null : Index(ancestor.Profiles);

        Dictionary<Guid, DateTimeOffset> deletions = [];
        AddDeletions(deletions, ancestor?.DeletedProfiles);
        AddDeletions(deletions, remote.DeletedProfiles);

        List<LibraryConflict> conflicts = [];
        Dictionary<Guid, PackagedProfile> result = [];
        HashSet<Guid> deferred = [];

        foreach (Guid id in local.Keys.Union(theirs.Keys).Union(common?.Keys ?? Enumerable.Empty<Guid>()))
        {
            local.TryGetValue(id, out PackagedProfile? mine);
            theirs.TryGetValue(id, out PackagedProfile? other);
            PackagedProfile? before = null;
            common?.TryGetValue(id, out before);

            PackagedProfile? merged = Decide(id, mine, other, before, deletions, now, conflicts);

            if (merged is null && mine is not null && protectedProfiles.Contains(id))
            {
                // Kept here while its tunnel runs, and left out of the shared file, which already
                // says it is gone. The next synchronisation after the tunnel ends removes it.
                deferred.Add(id);
                continue;
            }

            if (merged is not null)
            {
                result[id] = merged;
            }
        }

        Dictionary<Guid, Guid> duplicates = CollapseDuplicates(result, protectedProfiles, deletions, now);

        Dictionary<string, PackagedCredential> localCredentials = await ReadCredentialsAsync(cancellationToken);
        Dictionary<string, PackagedCredential> mergedCredentials = MergeCredentials(
            localCredentials,
            CredentialIndex(remote.Credentials),
            ancestor is null ? null : CredentialIndex(ancestor.Credentials),
            result,
            duplicates);

        LibraryMergeResult outcome = await ApplyLocallyAsync(
            localEntities,
            result,
            deferred,
            duplicates,
            now,
            cancellationToken);

        int credentialsChanged = await ApplyCredentialsAsync(
            localCredentials,
            mergedCredentials,
            deferred,
            cancellationToken);

        // What was loaded to be merged is not what the store holds any longer in every detail, and
        // a caller reusing the context should read the store rather than this.
        context.ChangeTracker.Clear();

        foreach (Guid id in result.Keys)
        {
            deletions.Remove(id);
        }

        ProfilePackageContent shared = new()
        {
            CreatedAt = now,
            WrittenBy = WrittenBy,
            Profiles = result.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Credentials = mergedCredentials.Values.OrderBy(credential => credential.ProfileId).ThenBy(credential => credential.Realm, StringComparer.Ordinal).ToList(),
            DeletedProfiles = deletions
                .Where(entry => now - entry.Value < DeletionMemory)
                .OrderBy(entry => entry.Key)
                .Select(entry => new PackagedDeletion(entry.Key, entry.Value))
                .ToList(),
        };

        return outcome with
        {
            CredentialsChanged = credentialsChanged,
            Shared = shared,
            SharedChanged = !SameLibrary(shared, remote),
            Conflicts = conflicts,
        };
    }

    private static string WrittenBy =>
        typeof(LibraryMerger).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>
    /// What one profile becomes, given this machine's version, the shared one and their ancestor.
    /// </summary>
    /// <returns>The profile, or null when it is gone.</returns>
    private static PackagedProfile? Decide(
        Guid id,
        PackagedProfile? mine,
        PackagedProfile? other,
        PackagedProfile? before,
        Dictionary<Guid, DateTimeOffset> deletions,
        DateTimeOffset now,
        List<LibraryConflict> conflicts)
    {
        switch (mine, other, before)
        {
            case (not null, not null, not null):
                return MergeFields(mine, other, before, conflicts);

            case (not null, not null, null):
                // Nothing to tell who changed what, so the later version of the whole profile wins.
                return Same(mine, other) || Stamp(mine) >= Stamp(other) ? mine : other;

            case (not null, null, not null):
                // Deleted in the shared library since the last synchronisation.
                if (Same(mine, before))
                {
                    deletions.TryAdd(id, now);
                    return null;
                }

                conflicts.Add(new LibraryConflict(mine.Name, LibraryConflictKind.ChangedHereDeletedThere, KeptHere: true));
                deletions.Remove(id);
                return mine;

            case (null, not null, not null):
                // Deleted here since the last synchronisation.
                if (Same(other, before))
                {
                    deletions[id] = now;
                    return null;
                }

                conflicts.Add(new LibraryConflict(other.Name, LibraryConflictKind.DeletedHereChangedThere, KeptHere: false));
                return other;

            case (not null, null, null):
                // Never shared from here. A deletion the shared file remembers wins only when it came
                // after this machine's last change to the profile.
                if (deletions.TryGetValue(id, out DateTimeOffset deletedAt) && deletedAt >= Stamp(mine))
                {
                    return null;
                }

                deletions.Remove(id);
                return mine;

            case (null, not null, null):
                return other;

            default:
                return null;
        }
    }

    /// <summary>
    /// Takes each field from whichever side changed it, and the later change where both did.
    /// </summary>
    private static PackagedProfile MergeFields(
        PackagedProfile mine,
        PackagedProfile other,
        PackagedProfile before,
        List<LibraryConflict> conflicts)
    {
        bool mineIsLater = Stamp(mine) >= Stamp(other);
        bool conflicted = false;

        T Field<T>(T here, T there, T ancestor, IEqualityComparer<T> comparer)
        {
            if (comparer.Equals(here, there) || comparer.Equals(there, ancestor))
            {
                return here;
            }

            if (comparer.Equals(here, ancestor))
            {
                return there;
            }

            conflicted = true;
            return mineIsLater ? here : there;
        }

        string name = Field(mine.Name, other.Name, before.Name, StringComparer.Ordinal);
        string configuration = Field(mine.Configuration, other.Configuration, before.Configuration, StringComparer.Ordinal);
        string? notes = Field(Blank(mine.Notes), Blank(other.Notes), Blank(before.Notes), StringComparer.Ordinal);
        bool? protectRoutes = Field(mine.ProtectRoutes, other.ProtectRoutes, before.ProtectRoutes, EqualityComparer<bool?>.Default);
        string? colour = Field(mine.Colour, other.Colour, before.Colour, StringComparer.OrdinalIgnoreCase);
        List<string> tags = MergeTags(mine.Tags, other.Tags, before.Tags);

        PackagedProfile merged = other with
        {
            Name = name,
            Configuration = configuration,
            Notes = notes,
            ProtectRoutes = protectRoutes,
            Colour = colour,
            Tags = tags,
        };

        if (conflicted)
        {
            conflicts.Add(new LibraryConflict(name, LibraryConflictKind.ChangedOnBothSides, KeptHere: mineIsLater));
        }

        if (Same(merged, mine))
        {
            return mine;
        }

        return Same(merged, other)
            ? other
            : Describe(merged with { UpdatedAt = Max(Stamp(mine), Stamp(other)) });
    }

    /// <summary>
    /// Reads the server, port, protocol and sign in requirement from the configuration a profile now
    /// has, which after a field by field merge may be another side's than the rest of it.
    /// </summary>
    private static PackagedProfile Describe(PackagedProfile profile)
    {
        Profile facts = new() { Name = profile.Name, Configuration = string.Empty, ContentHash = string.Empty };
        ProfileConfigurationFacts.Apply(facts, profile.Configuration ?? string.Empty);

        return profile with
        {
            RemoteHost = facts.RemoteHost,
            RemotePort = facts.RemotePort,
            Protocol = facts.Protocol,
            RequiresCredentials = facts.RequiresCredentials,
        };
    }

    /// <summary>
    /// A tag added on either side is kept, and a tag removed on either side goes.
    /// </summary>
    private static List<string> MergeTags(
        IReadOnlyList<string>? mine,
        IReadOnlyList<string>? other,
        IReadOnlyList<string>? before)
    {
        HashSet<string> here = new(mine ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> there = new(other ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> ancestor = new(before ?? [], StringComparer.OrdinalIgnoreCase);

        HashSet<string> result = new(there, StringComparer.OrdinalIgnoreCase);
        result.UnionWith(here.Except(ancestor, StringComparer.OrdinalIgnoreCase));
        result.ExceptWith(ancestor.Except(here, StringComparer.OrdinalIgnoreCase));

        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Keeps one of every set of profiles with the same configuration.
    /// </summary>
    /// <returns>Each profile given up, mapped to the one kept in its place.</returns>
    private static Dictionary<Guid, Guid> CollapseDuplicates(
        Dictionary<Guid, PackagedProfile> result,
        IReadOnlySet<Guid> protectedProfiles,
        Dictionary<Guid, DateTimeOffset> deletions,
        DateTimeOffset now)
    {
        Dictionary<Guid, Guid> replaced = [];

        foreach (IGrouping<string, PackagedProfile> group in result.Values
            .GroupBy(profile => ProfileImporter.ComputeHash(profile.Configuration ?? string.Empty), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList())
        {
            List<PackagedProfile> ordered = group.OrderBy(profile => profile.Id.ToString("N")).ToList();

            // A profile whose tunnel is running keeps its identity until the tunnel ends.
            if (ordered.Skip(1).Any(profile => protectedProfiles.Contains(profile.Id)))
            {
                continue;
            }

            foreach (PackagedProfile dropped in ordered.Skip(1))
            {
                result.Remove(dropped.Id);
                replaced[dropped.Id] = ordered[0].Id;
                deletions[dropped.Id] = now;
            }
        }

        return replaced;
    }

    private static Dictionary<string, PackagedCredential> MergeCredentials(
        Dictionary<string, PackagedCredential> mine,
        Dictionary<string, PackagedCredential> other,
        Dictionary<string, PackagedCredential>? before,
        Dictionary<Guid, PackagedProfile> result,
        Dictionary<Guid, Guid> duplicates)
    {
        Dictionary<string, PackagedCredential> merged = new(StringComparer.Ordinal);

        foreach (string reference in mine.Keys.Union(other.Keys).Union(before?.Keys ?? Enumerable.Empty<string>()))
        {
            mine.TryGetValue(reference, out PackagedCredential? here);
            other.TryGetValue(reference, out PackagedCredential? there);
            PackagedCredential? ancestor = null;
            before?.TryGetValue(reference, out ancestor);

            PackagedCredential? chosen;

            if (SameCredential(here, there) || SameCredential(there, ancestor))
            {
                chosen = here;
            }
            else if (SameCredential(here, ancestor))
            {
                chosen = there;
            }
            else
            {
                // Both sides stored something different. A sign in is what the person at this
                // machine last typed and the server last accepted, so this machine's wins.
                chosen = here ?? there;
            }

            if (chosen is null)
            {
                continue;
            }

            if (duplicates.TryGetValue(chosen.ProfileId, out Guid kept))
            {
                chosen = chosen with { ProfileId = kept };
            }

            string key = SecretReference.ForProfile(chosen.ProfileId, chosen.Realm);

            if (result.ContainsKey(chosen.ProfileId))
            {
                merged.TryAdd(key, chosen);
            }
        }

        return merged;
    }

    private async Task<LibraryMergeResult> ApplyLocallyAsync(
        Dictionary<Guid, Profile> localEntities,
        Dictionary<Guid, PackagedProfile> result,
        HashSet<Guid> deferred,
        Dictionary<Guid, Guid> duplicates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TagCatalogue tags = await TagCatalogue.LoadAsync(context, cancellationToken);
        Dictionary<Guid, Profile> kept = [];
        List<(Profile From, Guid To)> moves = [];
        List<string> added = [];
        List<string> updated = [];
        List<string> removed = [];

        foreach ((Guid id, PackagedProfile wanted) in result)
        {
            if (localEntities.TryGetValue(id, out Profile? entity))
            {
                if (!Same(Pack(entity), wanted))
                {
                    Write(entity, wanted, tags);
                    updated.Add(wanted.Name);
                }
            }
            else
            {
                entity = new Profile
                {
                    Id = id,
                    Name = wanted.Name,
                    Configuration = string.Empty,
                    ContentHash = string.Empty,
                    Source = ProfileSource.Imported,
                    CreatedAt = now,
                };

                Write(entity, wanted, tags);
                context.Profiles.Add(entity);

                // A duplicate this machine already had arriving under another identifier is not news.
                if (!duplicates.Any(pair => pair.Value == id && localEntities.ContainsKey(pair.Key)))
                {
                    added.Add(wanted.Name);
                }
            }

            kept[id] = entity;
        }

        foreach ((Guid id, Profile entity) in localEntities)
        {
            if (result.ContainsKey(id) || deferred.Contains(id))
            {
                continue;
            }

            if (duplicates.TryGetValue(id, out Guid survivor))
            {
                moves.Add((entity, survivor));
            }
            else
            {
                removed.Add(entity.Name);
                context.Profiles.Remove(entity);
            }
        }

        // Moved before anything is removed, because removing a profile removes its history with it,
        // and a shortcut slot can only change hands once its old holder has given it up.
        foreach ((Profile from, Guid to) in moves)
        {
            await MovePersonalDataAsync(from, kept[to], cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach ((Profile from, Guid to) in moves)
        {
            if (from.FavouriteSlot is { } slot && kept[to].FavouriteSlot is null)
            {
                from.FavouriteSlot = null;
                await context.SaveChangesAsync(cancellationToken);
                kept[to].FavouriteSlot = slot;
            }

            context.Profiles.Remove(from);
        }

        await context.SaveChangesAsync(cancellationToken);

        await context.Tags
            .Where(tag => !tag.Profiles.Any())
            .ExecuteDeleteAsync(cancellationToken);

        return new LibraryMergeResult
        {
            Added = added,
            Updated = updated,
            Removed = removed,
            DeferredDeletions = deferred.Count,
        };
    }

    /// <summary>
    /// Makes the stored profile say what the merged one says, tags included.
    /// </summary>
    private void Write(Profile entity, PackagedProfile wanted, TagCatalogue tags)
    {
        entity.Name = wanted.Name;
        entity.Notes = Blank(wanted.Notes);
        entity.ProtectRoutes = wanted.ProtectRoutes;
        entity.Colour = wanted.Colour;
        entity.UpdatedAt = wanted.UpdatedAt ?? entity.UpdatedAt;

        if (!string.Equals(entity.Configuration, wanted.Configuration, StringComparison.Ordinal))
        {
            ProfileConfigurationFacts.Apply(entity, wanted.Configuration ?? string.Empty);
        }

        HashSet<string> current = new(entity.Tags.Select(link => link.Tag!.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> target = new(wanted.Tags ?? [], StringComparer.OrdinalIgnoreCase);

        if (current.SetEquals(target))
        {
            return;
        }

        foreach (ProfileTag link in entity.Tags.Where(link => !target.Contains(link.Tag!.Name)).ToList())
        {
            entity.Tags.Remove(link);
            context.ProfileTags.Remove(link);
        }

        foreach (string name in target.Where(name => !current.Contains(name)))
        {
            Tag tag = tags.Resolve(name);
            ProfileTag link = new() { ProfileId = entity.Id, TagId = tag.Id, Tag = tag, Profile = entity };
            entity.Tags.Add(link);
        }
    }

    /// <summary>
    /// Hands what this machine kept about a duplicate to the profile kept in its place, all but its
    /// shortcut slot, which moves once the duplicate has been saved without it.
    /// </summary>
    private async Task MovePersonalDataAsync(Profile from, Profile to, CancellationToken cancellationToken)
    {
        if (from.IsFavourite && !to.IsFavourite)
        {
            to.IsFavourite = true;
        }

        if (from.LastConnectedAt > to.LastConnectedAt || to.LastConnectedAt is null)
        {
            to.LastConnectedAt = from.LastConnectedAt ?? to.LastConnectedAt;
        }

        to.ConnectCount += from.ConnectCount;

        foreach (Session session in await context.Sessions.Where(session => session.ProfileId == from.Id).ToListAsync(cancellationToken))
        {
            session.ProfileId = to.Id;
        }
    }

    private async Task<int> ApplyCredentialsAsync(
        Dictionary<string, PackagedCredential> before,
        Dictionary<string, PackagedCredential> after,
        HashSet<Guid> deferred,
        CancellationToken cancellationToken)
    {
        if (!secrets.IsAvailable)
        {
            return 0;
        }

        int changed = 0;

        foreach ((string reference, PackagedCredential credential) in after)
        {
            if (before.TryGetValue(reference, out PackagedCredential? existing) && SameCredential(existing, credential))
            {
                continue;
            }

            await secrets.WriteAsync(reference, new StoredSecret(credential.Username, credential.Password), cancellationToken);
            changed++;
        }

        // A profile kept here while its tunnel runs keeps its sign ins too, or reconnecting it would ask.
        foreach ((string reference, PackagedCredential credential) in before.Where(entry => !after.ContainsKey(entry.Key)))
        {
            if (deferred.Contains(credential.ProfileId))
            {
                continue;
            }

            await secrets.DeleteAsync(reference, cancellationToken);
            changed++;
        }

        return changed;
    }

    private async Task<List<Profile>> LoadProfilesAsync(bool tracked, CancellationToken cancellationToken)
    {
        IQueryable<Profile> query = context.Profiles.Include(profile => profile.Tags).ThenInclude(link => link.Tag);

        return tracked
            ? await query.ToListAsync(cancellationToken)
            : await query.AsNoTracking().ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<string, PackagedCredential>> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, PackagedCredential> credentials = new(StringComparer.Ordinal);

        if (!secrets.IsAvailable)
        {
            return credentials;
        }

        foreach (string reference in await secrets.ListAsync(cancellationToken))
        {
            if (!SecretReference.TryParse(reference, out Guid profileId, out string realm))
            {
                continue;
            }

            if (await secrets.TryReadAsync(reference, cancellationToken) is { } secret)
            {
                credentials[reference] = new PackagedCredential(profileId, realm, secret.Username, secret.Password);
            }
        }

        return credentials;
    }

    /// <summary>
    /// A profile as the shared file holds it. What is personal is left at its default.
    /// </summary>
    private static PackagedProfile Pack(Profile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Configuration = profile.Configuration,
        RemoteHost = profile.RemoteHost,
        RemotePort = profile.RemotePort,
        Protocol = profile.Protocol,
        RequiresCredentials = profile.RequiresCredentials,
        ProtectRoutes = profile.ProtectRoutes,
        Notes = Blank(profile.Notes),
        Colour = profile.Colour,
        Tags = profile.Tags
            .Select(link => link.Tag!.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        UpdatedAt = profile.UpdatedAt,
    };

    private static Dictionary<Guid, PackagedProfile> Index(IReadOnlyList<PackagedProfile>? profiles)
    {
        Dictionary<Guid, PackagedProfile> index = [];

        // A file somebody else's copy wrote is read without assuming what it left out.
        foreach (PackagedProfile profile in profiles ?? [])
        {
            if (profile is not null && profile.Id != Guid.Empty && profile.Configuration is not null)
            {
                index.TryAdd(profile.Id, profile with
                {
                    Name = profile.Name ?? string.Empty,
                    Notes = Blank(profile.Notes),
                    Tags = (profile.Tags ?? []).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                });
            }
        }

        return index;
    }

    private static Dictionary<string, PackagedCredential> CredentialIndex(IReadOnlyList<PackagedCredential>? credentials)
    {
        Dictionary<string, PackagedCredential> index = new(StringComparer.Ordinal);

        foreach (PackagedCredential credential in credentials ?? [])
        {
            if (credential is not null && credential.Password is not null && !string.IsNullOrWhiteSpace(credential.Realm))
            {
                index.TryAdd(SecretReference.ForProfile(credential.ProfileId, credential.Realm), credential);
            }
        }

        return index;
    }

    private static void AddDeletions(Dictionary<Guid, DateTimeOffset> into, IReadOnlyList<PackagedDeletion>? deletions)
    {
        foreach (PackagedDeletion deletion in deletions ?? [])
        {
            if (deletion is null)
            {
                continue;
            }

            into[deletion.ProfileId] = into.TryGetValue(deletion.ProfileId, out DateTimeOffset known)
                ? Max(known, deletion.DeletedAt)
                : deletion.DeletedAt;
        }
    }

    /// <summary>
    /// True when two versions of a profile agree on everything that is shared.
    /// </summary>
    private static bool Same(PackagedProfile left, PackagedProfile right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Configuration, right.Configuration, StringComparison.Ordinal)
        && string.Equals(Blank(left.Notes), Blank(right.Notes), StringComparison.Ordinal)
        && left.ProtectRoutes == right.ProtectRoutes
        && string.Equals(left.Colour, right.Colour, StringComparison.OrdinalIgnoreCase)
        && new HashSet<string>(left.Tags ?? [], StringComparer.OrdinalIgnoreCase).SetEquals(right.Tags ?? []);

    private static bool SameCredential(PackagedCredential? left, PackagedCredential? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(left.Username, right.Username, StringComparison.Ordinal)
                && string.Equals(left.Password, right.Password, StringComparison.Ordinal);

    private static bool SameLibrary(ProfilePackageContent left, ProfilePackageContent right)
    {
        Dictionary<Guid, PackagedProfile> leftProfiles = Index(left.Profiles);
        Dictionary<Guid, PackagedProfile> rightProfiles = Index(right.Profiles);

        if (leftProfiles.Count != rightProfiles.Count
            || leftProfiles.Any(entry => !rightProfiles.TryGetValue(entry.Key, out PackagedProfile? other) || !Same(entry.Value, other)))
        {
            return false;
        }

        Dictionary<string, PackagedCredential> leftCredentials = CredentialIndex(left.Credentials);
        Dictionary<string, PackagedCredential> rightCredentials = CredentialIndex(right.Credentials);

        if (leftCredentials.Count != rightCredentials.Count
            || leftCredentials.Any(entry => !rightCredentials.TryGetValue(entry.Key, out PackagedCredential? other) || !SameCredential(entry.Value, other)))
        {
            return false;
        }

        return new HashSet<Guid>((left.DeletedProfiles ?? []).Select(deletion => deletion.ProfileId))
            .SetEquals((right.DeletedProfiles ?? []).Select(deletion => deletion.ProfileId));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset Stamp(PackagedProfile profile) => profile.UpdatedAt ?? DateTimeOffset.MinValue;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}

/// <summary>
/// What reconciling with the shared library did here, and what the shared file should hold now.
/// </summary>
public sealed record LibraryMergeResult
{
    /// <summary>
    /// Names of the profiles added to this machine.
    /// </summary>
    public IReadOnlyList<string> Added { get; init; } = [];

    public IReadOnlyList<string> Updated { get; init; } = [];

    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>
    /// How many sign ins were written or removed here.
    /// </summary>
    public int CredentialsChanged { get; init; }

    /// <summary>
    /// Profiles deleted elsewhere that stay here until their tunnel ends.
    /// </summary>
    public int DeferredDeletions { get; init; }

    public IReadOnlyList<LibraryConflict> Conflicts { get; init; } = [];

    /// <summary>
    /// What the shared file should hold after this.
    /// </summary>
    public ProfilePackageContent Shared { get; init; } = new();

    /// <summary>
    /// True when that differs from what the shared file holds now, so it has to be written.
    /// </summary>
    public bool SharedChanged { get; init; }

    public bool ChangedHere => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0 || CredentialsChanged > 0;
}

/// <summary>
/// A profile two sides disagreed about.
/// </summary>
/// <param name="ProfileName">The profile, by the name it has now.</param>
/// <param name="Kind">What the disagreement was.</param>
/// <param name="KeptHere">True when this machine's version was kept, false when the shared one was.</param>
public sealed record LibraryConflict(string ProfileName, LibraryConflictKind Kind, bool KeptHere);

public enum LibraryConflictKind
{
    /// <summary>
    /// Both sides changed the same field. The later change was kept.
    /// </summary>
    ChangedOnBothSides,

    /// <summary>
    /// Changed on this machine and deleted in the shared library. The change was kept.
    /// </summary>
    ChangedHereDeletedThere,

    /// <summary>
    /// Deleted on this machine and changed in the shared library. The change was kept.
    /// </summary>
    DeletedHereChangedThere,
}
