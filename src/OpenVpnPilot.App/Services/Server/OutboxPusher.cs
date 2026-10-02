using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Sends the outbox to the server, oldest marker first, each as the entity's current state.
/// </summary>
/// <remarks>
/// <para>
/// Dumb on purpose: no comparison with what the server holds, no question who changed what last.
/// A profile goes up with <c>If-Match: *</c>, the lists as a whole, the settings without a tag, and
/// whatever another person changed meanwhile is overwritten.
/// </para>
/// <para>
/// A marker is removed only once the server took the change, or refused it for good. When the
/// entity changed again while it was on its way, that later change collapsed into the same marker,
/// so the marker stays and goes again. Anything that means nothing can be sent now, an unreachable
/// server above all, keeps the marker and ends the push, and the pull is not attempted either.
/// </para>
/// </remarks>
internal sealed partial class OutboxPusher
{
    private readonly IServerApi api;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IOutbox outbox;
    private readonly IServerProfileMaintenance maintenance;
    private readonly ISecretStore secrets;
    private readonly IHeldVaultSecrets held;
    private readonly IPortableSettings settings;
    private readonly PersonalDataSync personal;
    private readonly ILogger logger;

    public OutboxPusher(
        IServerApi api,
        IDbContextFactory<PilotDbContext> contextFactory,
        IOutbox outbox,
        IServerProfileMaintenance maintenance,
        ISecretStore secrets,
        IHeldVaultSecrets held,
        IPortableSettings settings,
        PersonalDataSync personal,
        ILogger logger)
    {
        this.api = api;
        this.contextFactory = contextFactory;
        this.outbox = outbox;
        this.maintenance = maintenance;
        this.secrets = secrets;
        this.held = held;
        this.settings = settings;
        this.personal = personal;
        this.logger = logger;
    }

    /// <returns>The failure that ended the push early, or null when every marker was dealt with.</returns>
    public async Task<ServerResult?> PushAsync(SyncCycle cycle, CancellationToken cancellationToken)
    {
        IReadOnlyList<PendingChange> pending = await outbox.GetPendingAsync(cancellationToken);
        int index = 0;

        while (index < pending.Count)
        {
            PendingChange marker = pending[index];

            if (cycle.AdministratorChangesDropped && SyncFailures.IsAdministratorKind(marker.Kind))
            {
                index++;
                continue;
            }

            SyncStep step;

            if (marker.Kind == PendingChangeKind.ProfileCreate)
            {
                // Creates that follow each other go up together, which is what an import made
                // while the server could not be reached becomes.
                List<PendingChange> run = [.. pending.Skip(index).TakeWhile(change => change.Kind == PendingChangeKind.ProfileCreate)];
                index += run.Count;
                step = await CreateAsync(run, cycle, cancellationToken);
            }
            else
            {
                index++;
                step = await PushOneAsync(marker, cycle, cancellationToken);
            }

            if (step == SyncStep.Stop)
            {
                return cycle.Failure;
            }
        }

        return null;
    }

    private Task<SyncStep> PushOneAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken) =>
        marker.Kind switch
        {
            PendingChangeKind.ProfileUpdate => UpdateAsync(marker, cycle, cancellationToken),
            PendingChangeKind.ProfileDelete => SendAsync(marker, cycle, api.DeleteProfileAsync(marker.EntityId!.Value, cancellationToken), notFoundIsDone: true, cancellationToken),
            PendingChangeKind.TagUpdate => UpdateTagAsync(marker, cycle, cancellationToken),
            PendingChangeKind.TagDelete => SendAsync(marker, cycle, api.DeleteTagAsync(marker.EntityId!.Value, cancellationToken), notFoundIsDone: true, cancellationToken),
            PendingChangeKind.VaultAdd => AddVaultEntryAsync(marker, cycle, cancellationToken),
            PendingChangeKind.Favourites => PushFavouritesAsync(marker, cycle, cancellationToken),
            PendingChangeKind.Hotkeys => PushHotkeysAsync(marker, cycle, cancellationToken),
            PendingChangeKind.Settings => PushSettingsAsync(marker, cycle, cancellationToken),
            _ => DropUnsendableAsync(marker, cancellationToken),
        };

    /// <summary>
    /// Waits for a call whose answer carries nothing more than success, and settles its marker.
    /// </summary>
    private async Task<SyncStep> SendAsync(
        PendingChange marker,
        SyncCycle cycle,
        Task<ServerResult> call,
        bool notFoundIsDone,
        CancellationToken cancellationToken)
    {
        ServerResult result = await call;

        if (!result.IsSuccess && !(notFoundIsDone && SyncFailures.IsNotFound(result)))
        {
            return await FailedAsync(marker, result, cycle, cancellationToken);
        }

        await outbox.DropAsync(marker.Id, cancellationToken);
        cycle.Pushed++;
        return SyncStep.Continue;
    }

    private async Task<SyncStep> UpdateTagAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        Tag? tag;

        await using (PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            tag = await context.Tags.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == marker.EntityId, cancellationToken);
        }

        if (tag is null)
        {
            return await DropUnsendableAsync(marker, cancellationToken);
        }

        Task<ServerResult> call = Widen(api.UpdateTagAsync(tag.Id, new TagRequest(tag.Name, tag.Colour), cancellationToken));
        return await SendAsync(marker, cycle, call, notFoundIsDone: true, cancellationToken);
    }

    private async Task<SyncStep> AddVaultEntryAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        Guid profileId = marker.EntityId!.Value;
        string realm = marker.Realm!;

        // Read now, never kept in the outbox: the database must not hold a secret even briefly. One
        // the person chose not to store is held in memory until this moment, and is what worked.
        if ((held.Peek(profileId, realm)
                ?? await secrets.TryReadAsync(SecretReference.ForProfile(profileId, realm), cancellationToken)) is not { } secret)
        {
            SyncEngineLog.VaultSecretMissing(logger, profileId, realm);
            return await DropUnsendableAsync(marker, cancellationToken);
        }

        SyncStep step = await ShareAsync(marker, secret, cycle, cancellationToken);

        // Kept only while the marker can still be sent again, which a stopped push leaves it for.
        if (step == SyncStep.Continue)
        {
            held.Release(profileId, realm);
        }

        return step;
    }

    private async Task<SyncStep> ShareAsync(PendingChange marker, StoredSecret secret, SyncCycle cycle, CancellationToken cancellationToken)
    {
        Guid profileId = marker.EntityId!.Value;
        string realm = marker.Realm!;

        ServerResult<VaultEntryResponse> added = await api.AddVaultEntryAsync(
            profileId,
            realm,
            new VaultEntryRequest(secret.Username, secret.Password),
            cancellationToken);

        if (SyncFailures.IsProblem(added, 409, ServerErrorCodes.VaultEntryExists))
        {
            // Somebody shared one first. That one is what everybody uses, so it is used here too.
            ServerResult<IReadOnlyList<VaultEntryResponse>> shared = await api.GetProfileVaultAsync(profileId, cancellationToken);

            if (!shared.IsSuccess)
            {
                return await FailedAsync(marker, shared, cycle, cancellationToken);
            }

            if ((shared.Value ?? []).FirstOrDefault(entry => string.Equals(entry.Realm, realm, StringComparison.Ordinal)) is { } entry)
            {
                await secrets.WriteAsync(
                    SecretReference.ForProfile(profileId, realm),
                    new StoredSecret(string.IsNullOrEmpty(entry.Username) ? null : entry.Username, entry.Password),
                    cancellationToken);

                cycle.Changes |= LibraryChanges.Profiles;
                SyncEngineLog.VaultEntryAdopted(logger, profileId, realm);
            }

            await outbox.DropAsync(marker.Id, cancellationToken);
            cycle.Pushed++;
            return SyncStep.Continue;
        }

        return await SendAsync(marker, cycle, Task.FromResult<ServerResult>(added), notFoundIsDone: false, cancellationToken);
    }

    private async Task<SyncStep> PushFavouritesAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        FavouritesDocument sent = await personal.ReadFavouritesAsync(cancellationToken);
        ServerResult<FavouritesDocument> result = await api.PutFavouritesAsync(sent, cancellationToken);

        if (SyncFailures.IsProblem(result, 404, ServerErrorCodes.ProfileNotFound))
        {
            // A profile was deleted on the server meanwhile. Without it the list is sent once more.
            ServerResult<IReadOnlyList<ProfileResponse>> known = await api.GetProfilesAsync(cancellationToken: cancellationToken);

            if (!known.IsSuccess)
            {
                return await FailedAsync(marker, known, cycle, cancellationToken);
            }

            HashSet<Guid> ids = IdsOf(known.Value);
            FavouritesDocument kept = new([.. sent.Items.Where(item => ids.Contains(item.ProfileId))]);
            result = await api.PutFavouritesAsync(kept, cancellationToken);
        }

        if (!result.IsSuccess)
        {
            return await FailedAsync(marker, result, cycle, cancellationToken);
        }

        FavouritesDocument now = await personal.ReadFavouritesAsync(cancellationToken);
        return await SettleAsync(marker, cycle, PersonalDataSync.SameItems(sent.Items, now.Items), cancellationToken);
    }

    private async Task<SyncStep> PushHotkeysAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        HotkeysDocument sent = await personal.ReadHotkeysAsync(cancellationToken);
        ServerResult<HotkeysDocument> result = await api.PutHotkeysAsync(sent, cancellationToken);

        if (SyncFailures.IsProblem(result, 404, ServerErrorCodes.ProfileNotFound))
        {
            // As for the favourites; a shortcut that named the deleted profile names none.
            ServerResult<IReadOnlyList<ProfileResponse>> known = await api.GetProfilesAsync(cancellationToken: cancellationToken);

            if (!known.IsSuccess)
            {
                return await FailedAsync(marker, known, cycle, cancellationToken);
            }

            HashSet<Guid> ids = IdsOf(known.Value);
            HotkeysDocument kept = new([.. sent.Items.Select(item =>
                item.ProfileId is { } id && !ids.Contains(id) ? item with { ProfileId = null } : item)]);
            result = await api.PutHotkeysAsync(kept, cancellationToken);
        }

        if (!result.IsSuccess)
        {
            return await FailedAsync(marker, result, cycle, cancellationToken);
        }

        HotkeysDocument now = await personal.ReadHotkeysAsync(cancellationToken);
        return await SettleAsync(marker, cycle, PersonalDataSync.SameItems(sent.Items, now.Items), cancellationToken);
    }

    private async Task<SyncStep> PushSettingsAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        string sent = settings.Export().GetRawText();

        // No tag: whatever another machine stored meanwhile is overwritten.
        ServerResult<SettingsResponse> result = await api.PutSettingsAsync(
            new SettingsRequest(PilotSettings.CurrentSchemaVersion, settings.Export()),
            ifMatch: null,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return await FailedAsync(marker, result, cycle, cancellationToken);
        }

        return await SettleAsync(marker, cycle, settings.Export().GetRawText() == sent, cancellationToken);
    }

    private static HashSet<Guid> IdsOf(IReadOnlyList<ProfileResponse>? profiles) =>
        [.. (profiles ?? []).Select(profile => profile.Id)];

    /// <summary>
    /// Ends a change the server took: the marker goes, unless the entity changed again meanwhile.
    /// </summary>
    private async Task<SyncStep> SettleAsync(PendingChange marker, SyncCycle cycle, bool unchanged, CancellationToken cancellationToken)
    {
        cycle.Pushed++;

        if (unchanged)
        {
            await outbox.DropAsync(marker.Id, cancellationToken);
        }
        else
        {
            cycle.FollowUp = true;
        }

        return SyncStep.Continue;
    }

    /// <summary>
    /// Removes a marker whose entity is gone or has nothing to send. Not a refusal, so not counted.
    /// </summary>
    private async Task<SyncStep> DropUnsendableAsync(PendingChange marker, CancellationToken cancellationToken)
    {
        await outbox.DropAsync(marker.Id, cancellationToken);
        return SyncStep.Continue;
    }

    /// <summary>
    /// Decides what a failed call means for its marker and for the rest of the push.
    /// </summary>
    private async Task<SyncStep> FailedAsync(PendingChange marker, ServerResult result, SyncCycle cycle, CancellationToken cancellationToken)
    {
        FailureKind kind = SyncFailures.Classify(result);

        if (kind == FailureKind.Forbidden && SyncFailures.IsAdministratorKind(marker.Kind))
        {
            await DropAdministratorChangesAsync(result, cycle, cancellationToken);
            return SyncStep.Continue;
        }

        if (kind == FailureKind.Stop)
        {
            if (SyncFailures.CountsAsAttempt(result))
            {
                await outbox.RecordFailedAttemptAsync(marker.Id, result.Code, cancellationToken);
            }

            SyncEngineLog.ChangeDeferred(logger, marker.Kind, marker.EntityId, result.Outcome, result.Status, result.Code, result.RequestId);
            cycle.Stop(result);
            return SyncStep.Stop;
        }

        await outbox.DropAsync(marker.Id, cancellationToken);
        cycle.Dropped++;
        cycle.Note(result);
        SyncEngineLog.ChangeRefused(logger, marker.Kind, marker.EntityId, result.Status, result.Code, result.RequestId);
        return SyncStep.Continue;
    }

    /// <summary>
    /// The role no longer allows changing profiles and tags, so none of those changes can ever be sent.
    /// </summary>
    private async Task DropAdministratorChangesAsync(ServerResult result, SyncCycle cycle, CancellationToken cancellationToken)
    {
        int dropped = 0;

        foreach (PendingChange marker in await outbox.GetPendingAsync(cancellationToken))
        {
            if (SyncFailures.IsAdministratorKind(marker.Kind))
            {
                await outbox.DropAsync(marker.Id, cancellationToken);
                dropped++;
            }
        }

        cycle.Dropped += dropped;
        cycle.AdministratorChangesDropped = true;
        cycle.Note(result);
        SyncEngineLog.AdministratorChangesDropped(logger, result.RequestId, dropped);
    }

    private static async Task<ServerResult> Widen<T>(Task<ServerResult<T>> call) => await call;
}
