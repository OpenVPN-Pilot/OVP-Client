using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Tagging;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Applies the server's change feed to the copy: profiles, tags and the shared vault.
/// </summary>
/// <remarks>
/// <para>
/// Everything the server sends goes through one transaction, and the cursor is stored in it last.
/// A pull that fails anywhere therefore leaves the old cursor behind and is simply asked again, and
/// the keystore, which cannot take part in the transaction, is written before the commit: a secret
/// written for a pull that is then repeated is written again, never missing.
/// </para>
/// <para>
/// Only the shared columns of a profile are written. When it was last connected, how often, its
/// sessions and its favourite belong to this machine and to the person, and survive even a full
/// synchronisation. A profile, tag or vault entry with a change of this machine still waiting is
/// left as this machine has it until the change is sent. A deletion is the exception: the server
/// no longer has the profile, so a change waiting for it could only ever be refused.
/// </para>
/// </remarks>
internal sealed partial class ChangeFeedPuller
{
    // Enough to keep a first synchronisation of a few hundred profiles from taking minutes, few
    // enough not to look like a client gone wrong.
    private const int ConfigurationFetches = 4;

    private readonly IServerApi api;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly ISecretStore secrets;
    private readonly IServerProfileMaintenance maintenance;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public ChangeFeedPuller(
        IServerApi api,
        IDbContextFactory<PilotDbContext> contextFactory,
        ISecretStore secrets,
        IServerProfileMaintenance maintenance,
        TimeProvider time,
        ILogger logger)
    {
        this.api = api;
        this.contextFactory = contextFactory;
        this.secrets = secrets;
        this.maintenance = maintenance;
        this.time = time;
        this.logger = logger;
    }

    /// <returns>The failure that stopped the pull, or null when everything was applied.</returns>
    public async Task<ServerResult?> PullAsync(SyncCycle cycle, CancellationToken cancellationToken)
    {
        long? stored = await ReadCursorAsync(cancellationToken);
        cycle.CursorFrom = stored;

        ServerResult<SyncChangesResponse> answer = await api.GetChangesAsync(stored ?? 0, cancellationToken);

        if (stored is > 0 && SyncFailures.IsProblem(answer, 410, ServerErrorCodes.SyncCursorExpired))
        {
            SyncEngineLog.CursorExpired(logger, stored.Value, answer.RequestId);
            answer = await api.GetChangesAsync(0, cancellationToken);
        }

        if (!answer.IsSuccess)
        {
            return answer;
        }

        SyncChangesResponse changes = answer.Value;
        PendingSet pending = await ReadPendingAsync(cancellationToken);

        Fetched fetched = await FetchConfigurationsAsync(changes, pending, cancellationToken);

        if (fetched.Failure is not null)
        {
            return fetched.Failure;
        }

        await ApplyAsync(changes, fetched, cycle, cancellationToken);
        return null;
    }

    private async Task<long?> ReadCursorAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.SyncStates
            .AsNoTracking()
            .Where(state => state.Id == SyncStateRow.SingletonId)
            .Select(state => state.Cursor)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<PendingSet> ReadPendingAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await PendingSet.ReadAsync(context, cancellationToken);
    }

    /// <summary>
    /// Fetches the configuration of every profile whose hash is not the one held, before anything
    /// is written, so a failure halfway leaves nothing half applied.
    /// </summary>
    private async Task<Fetched> FetchConfigurationsAsync(
        SyncChangesResponse changes,
        PendingSet pending,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> held;

        await using (PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            held = await context.Profiles
                .AsNoTracking()
                .ToDictionaryAsync(profile => profile.Id, profile => profile.ContentHash, cancellationToken);
        }

        List<Guid> wanted = [.. (changes.Profiles ?? [])
            .Where(profile => !pending.Profiles.Contains(profile.Id)
                && (!held.TryGetValue(profile.Id, out string? hash) || !string.Equals(hash, profile.ContentHash, StringComparison.Ordinal)))
            .Select(profile => profile.Id)
            .Distinct()];

        ConcurrentDictionary<Guid, ProfileConfigurationResponse> configurations = new();
        ConcurrentBag<Guid> vanished = [];
        ServerResult? failure = null;

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                wanted,
                new ParallelOptions { MaxDegreeOfParallelism = ConfigurationFetches, CancellationToken = stop.Token },
                async (profileId, token) =>
                {
                    ServerResult<ProfileConfigurationResponse> result = await api.GetConfigurationAsync(profileId, token);

                    if (result.IsSuccess)
                    {
                        configurations[profileId] = result.Value;
                        SyncEngineLog.ConfigurationFetched(logger, profileId, result.Value.ContentHash);
                    }
                    else if (SyncFailures.IsNotFound(result))
                    {
                        // Deleted between the feed and this call; the next feed says so.
                        vanished.Add(profileId);
                        SyncEngineLog.ProfileVanished(logger, profileId);
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref failure, result, null);
                        await stop.CancelAsync();
                    }
                });
        }
        catch (OperationCanceledException) when (failure is not null && !cancellationToken.IsCancellationRequested)
        {
            // The fetches were stopped because one failed; that failure is the answer.
        }

        return new Fetched(configurations, [.. vanished], failure);
    }

    private async Task ApplyAsync(
        SyncChangesResponse changes,
        Fetched fetched,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Read again inside the transaction: a change recorded since the fetch is seen here.
        PendingSet pending = await PendingSet.ReadAsync(context, cancellationToken);
        bool changed = false;

        changed |= await ApplyTagsAsync(context, changes, pending, cycle, cancellationToken);
        changed |= await ApplyProfilesAsync(context, changes, fetched, pending, cycle, cancellationToken);

        // The keystore cannot join the transaction, so it is written before the commit that makes
        // the cursor final: a repeated pull writes it again rather than finding it missing.
        changed |= await WriteVaultAsync(changes, pending, cycle, cancellationToken);

        // Deletions only after every entry of the answer.
        List<Guid> removed = await RemoveProfilesAsync(context, changes, pending, cancellationToken);
        cycle.Deletions += removed.Count;
        changed |= removed.Count > 0;

        changed |= await RemoveTagsAsync(context, changes, pending, cycle, cancellationToken);
        changed |= await RemoveVaultAsync(context, changes, pending, cycle, cancellationToken);

        SyncStateRow state = await context.SyncStates.FindAsync([SyncStateRow.SingletonId], cancellationToken)
            ?? context.SyncStates.Add(new SyncStateRow()).Entity;

        state.Cursor = changes.Cursor;
        state.LastSuccessfulPullAt = time.GetUtcNow();

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        cycle.CursorTo = changes.Cursor;

        if (changed)
        {
            cycle.Changes |= LibraryChanges.Profiles;
        }
    }

    private async Task<bool> ApplyProfilesAsync(
        PilotDbContext context,
        SyncChangesResponse changes,
        Fetched fetched,
        PendingSet pending,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        List<ProfileResponse> incoming = [.. (changes.Profiles ?? [])
            .Where(profile => !pending.Profiles.Contains(profile.Id) && !fetched.Vanished.Contains(profile.Id))
            .DistinctBy(profile => profile.Id)];

        if (incoming.Count == 0)
        {
            return false;
        }

        List<Guid> ids = [.. incoming.Select(profile => profile.Id)];

        Dictionary<Guid, Profile> held = await context.Profiles
            .Include(profile => profile.Tags)
            .ThenInclude(link => link.Tag)
            .Where(profile => ids.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, cancellationToken);

        TagCatalogue catalogue = await TagCatalogue.LoadAsync(context, cancellationToken);

        foreach (ProfileResponse source in incoming)
        {
            fetched.Configurations.TryGetValue(source.Id, out ProfileConfigurationResponse? configuration);

            if (!held.TryGetValue(source.Id, out Profile? profile))
            {
                if (configuration is null)
                {
                    continue;
                }

                profile = new Profile
                {
                    Id = source.Id,
                    Name = source.Name,
                    Configuration = configuration.Configuration,
                    ContentHash = configuration.ContentHash,
                    CreatedAt = source.CreatedAt,
                };

                context.Profiles.Add(profile);
            }

            ApplySharedFields(profile, source, api.BaseAddress);

            if (configuration is not null)
            {
                profile.Configuration = configuration.Configuration;
                profile.ContentHash = configuration.ContentHash;
            }

            ApplyTags(context, profile, source.Tags ?? [], catalogue);
            cycle.PulledProfiles++;
        }

        bool changed = context.ChangeTracker.HasChanges();
        await context.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// The columns the server shares. What is personal or describes this machine is not among them.
    /// </summary>
    private static void ApplySharedFields(Profile profile, ProfileResponse source, Uri server)
    {
        profile.Name = source.Name;
        profile.Notes = source.Notes;
        profile.Colour = source.Colour;
        profile.ProtectRoutes = source.ProtectRoutes;
        profile.RemoteHost = source.RemoteHost;
        profile.RemotePort = source.RemotePort;
        profile.Protocol = source.Protocol;
        profile.RequiresCredentials = source.RequiresCredentials;
        profile.HasUnsupportedOptions = source.HasUnsupportedOptions;
        profile.IsSelfContained = true;
        profile.Source = ProfileSource.Server;
        profile.SourcePath = server.ToString();

        if (profile.UpdatedAt != source.UpdatedAt)
        {
            profile.UpdatedAt = source.UpdatedAt;
        }
    }

    private static void ApplyTags(PilotDbContext context, Profile profile, IReadOnlyList<string> names, TagCatalogue catalogue)
    {
        Dictionary<Guid, Tag> wanted = [];

        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            Tag tag = catalogue.Resolve(name);
            wanted.TryAdd(tag.Id, tag);
        }

        foreach (ProfileTag link in profile.Tags.Where(link => !wanted.ContainsKey(link.TagId)).ToList())
        {
            profile.Tags.Remove(link);
            context.ProfileTags.Remove(link);
        }

        foreach ((Guid tagId, Tag tag) in wanted)
        {
            if (profile.Tags.All(link => link.TagId != tagId))
            {
                profile.Tags.Add(new ProfileTag { ProfileId = profile.Id, TagId = tagId, Tag = tag });
            }
        }
    }

    private async Task<List<Guid>> RemoveProfilesAsync(
        PilotDbContext context,
        SyncChangesResponse changes,
        PendingSet pending,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> gone = [.. changes.DeletedProfiles ?? []];

        if (changes.Full)
        {
            HashSet<Guid> present = [.. (changes.Profiles ?? []).Select(profile => profile.Id)];

            List<Guid> missing = await context.Profiles
                .Where(profile => profile.Source == ProfileSource.Server)
                .Select(profile => profile.Id)
                .ToListAsync(cancellationToken);

            gone.UnionWith(missing.Where(id => !present.Contains(id)));
        }

        List<Profile> doomed = await context.Profiles
            .Where(profile => gone.Contains(profile.Id))
            .ToListAsync(cancellationToken);

        if (doomed.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. doomed.Select(profile => profile.Id)];

        // Whatever was waiting to be sent about these can only be refused now.
        List<PendingChange> markers = await context.PendingChanges
            .Where(change => change.EntityId != null && ids.Contains(change.EntityId.Value)
                && (pending.ProfileKinds.Contains(change.Kind) || change.Kind == PendingChangeKind.VaultAdd))
            .ToListAsync(cancellationToken);

        context.PendingChanges.RemoveRange(markers);
        context.Profiles.RemoveRange(doomed);
        await context.SaveChangesAsync(cancellationToken);

        // Their stored sign ins go with them, unlike a local profile's.
        await maintenance.DeleteSecretsAsync(ids, cancellationToken);

        return ids;
    }

    private sealed record Fetched(
        IReadOnlyDictionary<Guid, ProfileConfigurationResponse> Configurations,
        HashSet<Guid> Vanished,
        ServerResult? Failure);
}
