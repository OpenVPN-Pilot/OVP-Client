using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

internal sealed partial class ChangeFeedPuller
{
    /// <summary>
    /// Takes the server's tags by id.
    /// </summary>
    /// <remarks>
    /// A tag this copy holds under a name the server's tag now has, with an id of its own, is one an
    /// administrator gave a profile here before the server had it. It is replaced by the server's,
    /// and its profiles carry the server's instead, because names are unique on both sides.
    /// </remarks>
    private static async Task<bool> ApplyTagsAsync(
        PilotDbContext context,
        SyncChangesResponse changes,
        PendingSet pending,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        List<TagResponse> incoming = [.. (changes.Tags ?? [])
            .Where(tag => !pending.Tags.Contains(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .DistinctBy(tag => tag.Id)];

        if (incoming.Count == 0)
        {
            return false;
        }

        bool changed = false;
        List<Tag> held = await context.Tags.ToListAsync(cancellationToken);

        foreach (TagResponse source in incoming)
        {
            cycle.PulledTags++;

            Tag? clash = held.FirstOrDefault(tag => tag.Id != source.Id
                && string.Equals(tag.Name, source.Name, StringComparison.OrdinalIgnoreCase));

            if (clash is not null && pending.Tags.Contains(clash.Id))
            {
                // Its change is still to be sent; the server's tag waits until it has been.
                continue;
            }

            List<Guid> carriers = [];

            if (clash is not null)
            {
                // The name is unique, so the clashing row goes before the server's takes the name,
                // its links with it through the cascade; they are put back onto the server's tag below.
                carriers = await RemoveForAsync(context, clash, cancellationToken);
                held.Remove(clash);
            }

            Tag? tag = held.FirstOrDefault(candidate => candidate.Id == source.Id);

            if (tag is null)
            {
                tag = new Tag { Id = source.Id, Name = source.Name, Colour = source.Colour };
                context.Tags.Add(tag);
                held.Add(tag);
            }
            else
            {
                tag.Name = source.Name;
                tag.Colour = source.Colour;
            }

            if (carriers.Count > 0)
            {
                HashSet<Guid> linked = await context.ProfileTags
                    .Where(link => link.TagId == source.Id)
                    .Select(link => link.ProfileId)
                    .ToHashSetAsync(cancellationToken);

                foreach (Guid profileId in carriers.Where(profileId => !linked.Contains(profileId)))
                {
                    context.ProfileTags.Add(new ProfileTag { ProfileId = profileId, TagId = source.Id });
                }
            }

            changed |= clash is not null || context.ChangeTracker.HasChanges();
            await context.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    /// <summary>
    /// Removes a tag that stands in the way of the server's, and answers which profiles carried it.
    /// </summary>
    private static async Task<List<Guid>> RemoveForAsync(PilotDbContext context, Tag clash, CancellationToken cancellationToken)
    {
        List<Guid> carriers = await context.ProfileTags
            .Where(link => link.TagId == clash.Id)
            .Select(link => link.ProfileId)
            .ToListAsync(cancellationToken);

        context.Tags.Remove(clash);
        await context.SaveChangesAsync(cancellationToken);

        return carriers;
    }

    /// <summary>
    /// Removes deleted tags, tags a complete answer no longer has, and tags nothing carries any more.
    /// </summary>
    /// <remarks>
    /// A tag that a profile not uploaded yet carries stays, even when the server has no such tag:
    /// uploading that profile creates it there.
    /// </remarks>
    private static async Task<bool> RemoveTagsAsync(
        PilotDbContext context,
        SyncChangesResponse changes,
        PendingSet pending,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> present = [.. (changes.Tags ?? []).Select(tag => tag.Id)];
        HashSet<Guid> deleted = [.. changes.DeletedTags ?? []];

        List<TagUse> uses = await context.Tags
            .Select(tag => new TagUse(
                tag.Id,
                tag.Profiles.Any(),
                tag.Profiles.Any(link => link.Profile!.Source != ProfileSource.Server)))
            .ToListAsync(cancellationToken);

        List<Guid> doomed = [.. uses
            .Where(use => !pending.Tags.Contains(use.Id))
            .Where(use => deleted.Contains(use.Id)
                || (changes.Full && !present.Contains(use.Id) && !use.CarriedByLocal)
                || (!use.Carried && !present.Contains(use.Id)))
            .Select(use => use.Id)];

        if (doomed.Count == 0)
        {
            return false;
        }

        await context.Tags.Where(tag => doomed.Contains(tag.Id)).ExecuteDeleteAsync(cancellationToken);
        cycle.Deletions += doomed.Count;
        return true;
    }

    /// <summary>
    /// Writes the shared sign ins into the keystore. Runs before any deletion of the answer.
    /// </summary>
    private async Task<bool> WriteVaultAsync(
        SyncChangesResponse changes,
        PendingSet pending,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        bool changed = false;
        HashSet<Guid> deletedProfiles = [.. changes.DeletedProfiles ?? []];

        foreach (VaultEntryResponse entry in changes.VaultEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Realm) || entry.Password is null
                || pending.Vault.Contains((entry.ProfileId, entry.Realm)) || deletedProfiles.Contains(entry.ProfileId))
            {
                continue;
            }

            string reference = SecretReference.ForProfile(entry.ProfileId, entry.Realm);
            StoredSecret secret = new(string.IsNullOrEmpty(entry.Username) ? null : entry.Username, entry.Password);
            cycle.PulledVaultEntries++;

            if (await secrets.TryReadAsync(reference, cancellationToken) != secret)
            {
                await secrets.WriteAsync(reference, secret, cancellationToken);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Removes the shared sign ins the server no longer has: those it deleted, and on a complete
    /// answer those of its profiles that the answer does not carry.
    /// </summary>
    private async Task<bool> RemoveVaultAsync(
        PilotDbContext context,
        SyncChangesResponse changes,
        PendingSet pending,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        bool changed = false;
        HashSet<(Guid ProfileId, string Realm)> present = [.. (changes.VaultEntries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Realm))
            .Select(entry => (entry.ProfileId, entry.Realm))];

        HashSet<(Guid ProfileId, string Realm)> gone = [];

        foreach (VaultKeyResponse key in changes.DeletedVaultEntries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key.Realm) && !present.Contains((key.ProfileId, key.Realm)))
            {
                gone.Add((key.ProfileId, key.Realm));
            }
        }

        if (changes.Full)
        {
            HashSet<Guid> serverProfiles = await context.Profiles
                .Where(profile => profile.Source == ProfileSource.Server)
                .Select(profile => profile.Id)
                .ToHashSetAsync(cancellationToken);

            foreach (string reference in await secrets.ListAsync(cancellationToken))
            {
                if (SecretReference.TryParse(reference, out Guid profileId, out string realm)
                    && serverProfiles.Contains(profileId)
                    && !present.Contains((profileId, realm)))
                {
                    gone.Add((profileId, realm));
                }
            }
        }

        foreach ((Guid profileId, string realm) in gone.Where(key => !pending.Vault.Contains(key)))
        {
            string reference = SecretReference.ForProfile(profileId, realm);

            if (await secrets.TryReadAsync(reference, cancellationToken) is not null)
            {
                await secrets.DeleteAsync(reference, cancellationToken);
                cycle.Deletions++;
                changed = true;
            }
        }

        return changed;
    }

    private sealed record TagUse(Guid Id, bool Carried, bool CarriedByLocal);

    /// <summary>
    /// What this copy still has to send, which the pull leaves as it is.
    /// </summary>
    private sealed record PendingSet(
        HashSet<Guid> Profiles,
        HashSet<Guid> Tags,
        HashSet<(Guid ProfileId, string Realm)> Vault)
    {
        public PendingChangeKind[] ProfileKinds { get; } =
        [
            PendingChangeKind.ProfileCreate,
            PendingChangeKind.ProfileUpdate,
            PendingChangeKind.ProfileDelete,
        ];

        public static async Task<PendingSet> ReadAsync(PilotDbContext context, CancellationToken cancellationToken)
        {
            List<PendingChange> markers = await context.PendingChanges
                .AsNoTracking()
                .Where(change => change.EntityId != null)
                .ToListAsync(cancellationToken);

            PendingSet set = new([], [], []);

            foreach (PendingChange marker in markers)
            {
                Guid id = marker.EntityId!.Value;

                switch (marker.Kind)
                {
                    case PendingChangeKind.ProfileCreate:
                    case PendingChangeKind.ProfileUpdate:
                    case PendingChangeKind.ProfileDelete:
                        set.Profiles.Add(id);
                        break;

                    case PendingChangeKind.TagDelete:
                        set.Tags.Add(id);
                        break;

                    case PendingChangeKind.VaultAdd when marker.Realm is { } realm:
                        set.Vault.Add((id, realm));
                        break;

                    default:
                        break;
                }
            }

            return set;
        }
    }
}
