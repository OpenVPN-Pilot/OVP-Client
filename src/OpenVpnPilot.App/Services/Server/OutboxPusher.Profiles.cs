using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Services.Server;

internal sealed partial class OutboxPusher
{
    // Well under the 64 MiB the server takes, because the size is estimated from the text alone.
    private const long BatchBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Uploads profiles created here, in batches when there are several.
    /// </summary>
    private async Task<SyncStep> CreateAsync(List<PendingChange> markers, SyncCycle cycle, CancellationToken cancellationToken)
    {
        List<Upload> uploads = [];

        foreach (PendingChange marker in markers)
        {
            if (await ReadSnapshotAsync(marker.EntityId!.Value, cancellationToken) is { } snapshot)
            {
                uploads.Add(new Upload(marker, snapshot));
            }
            else
            {
                await DropUnsendableAsync(marker, cancellationToken);
            }
        }

        if (uploads.Count == 1)
        {
            return await CreateOneAsync(uploads[0], cycle, cancellationToken);
        }

        foreach (List<Upload> chunk in Chunk(uploads))
        {
            ServerResult<ProfileBatchResponse> result = await api.CreateProfilesAsync(
                new ProfileBatchRequest([.. chunk.Select(upload => upload.Snapshot.ToCreateRequest())]),
                cancellationToken);

            if (result.IsSuccess)
            {
                await SettleBatchAsync(chunk, result, cycle, cancellationToken);
                continue;
            }

            // A batch refused as a whole, too large for example, says nothing about its items.
            SyncStep step = SyncFailures.Classify(result) == FailureKind.Permanent
                ? await CreateEachAsync(chunk, cycle, cancellationToken)
                : await FailedAsync(chunk[0].Marker, result, cycle, cancellationToken);

            if (step == SyncStep.Stop || cycle.AdministratorChangesDropped)
            {
                return step;
            }
        }

        return SyncStep.Continue;
    }

    /// <summary>
    /// Sends the items of a batch the server would not take as a whole one by one, so each is
    /// judged on its own.
    /// </summary>
    private async Task<SyncStep> CreateEachAsync(List<Upload> uploads, SyncCycle cycle, CancellationToken cancellationToken)
    {
        foreach (Upload upload in uploads)
        {
            if (await CreateOneAsync(upload, cycle, cancellationToken) == SyncStep.Stop)
            {
                return SyncStep.Stop;
            }

            if (cycle.AdministratorChangesDropped)
            {
                break;
            }
        }

        return SyncStep.Continue;
    }

    private async Task<SyncStep> CreateOneAsync(Upload upload, SyncCycle cycle, CancellationToken cancellationToken)
    {
        ServerResult<ProfileResponse> result = await api.CreateProfileAsync(upload.Snapshot.ToCreateRequest(), cancellationToken);

        if (result.IsSuccess)
        {
            await CreatedAsync(upload, result.Value, cycle, cancellationToken);
            return SyncStep.Continue;
        }

        if (SyncFailures.IsProblem(result, 409, ServerErrorCodes.ProfileDuplicate))
        {
            Duplicate(upload, result.RequestId, cycle);
            return SyncStep.Continue;
        }

        if (SyncFailures.Classify(result) == FailureKind.Permanent)
        {
            cycle.Uploads[upload.Marker.EntityId!.Value] = new ProfileUploadOutcome(
                ProfileUploadKind.Rejected,
                Code: result.Code,
                Detail: result.Problem?.Detail);
        }

        return await FailedAsync(upload.Marker, result, cycle, cancellationToken);
    }

    private async Task SettleBatchAsync(
        List<Upload> chunk,
        ServerResult<ProfileBatchResponse> result,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        foreach (ProfileBatchItemResponse item in result.Value.Items ?? [])
        {
            if (item.Index < 0 || item.Index >= chunk.Count)
            {
                continue;
            }

            Upload upload = chunk[item.Index];

            switch (item.Outcome)
            {
                case ProfileBatchOutcomes.Created when item.Profile is { } created:
                    await CreatedAsync(upload, created, cycle, cancellationToken);
                    break;

                case ProfileBatchOutcomes.Duplicate:
                    Duplicate(upload, result.RequestId, cycle);
                    break;

                default:
                    cycle.Uploads[upload.Marker.EntityId!.Value] = new ProfileUploadOutcome(ProfileUploadKind.Rejected, Code: item.Code, Detail: item.Detail);
                    await outbox.DropAsync(upload.Marker.Id, cancellationToken);
                    cycle.Dropped++;
                    cycle.Note(result);
                    SyncEngineLog.ChangeRefused(logger, upload.Marker.Kind, upload.Marker.EntityId, result.Status, item.Code, result.RequestId);
                    break;
            }
        }
    }

    /// <summary>
    /// Gives the uploaded profile the server's id, then takes the configuration as the server
    /// stored it, unless the profile was changed here while it was on its way.
    /// </summary>
    private async Task CreatedAsync(Upload upload, ProfileResponse created, SyncCycle cycle, CancellationToken cancellationToken)
    {
        Guid temporaryId = upload.Marker.EntityId!.Value;

        ProfileRekeyOutcome outcome = await maintenance.RekeyAsync(temporaryId, created.Id, cancellationToken);
        cycle.Pushed++;
        cycle.Changes |= LibraryChanges.Profiles;
        cycle.Uploads[temporaryId] = new ProfileUploadOutcome(ProfileUploadKind.Created, created.Id);

        if (outcome == ProfileRekeyOutcome.NotFound)
        {
            // Deleted here before the answer came. The server has it now, so it goes there too.
            await outbox.DropAsync(upload.Marker.Id, cancellationToken);
            await outbox.RecordAsync(PendingChangeKind.ProfileDelete, created.Id, cancellationToken: cancellationToken);
            cycle.FollowUp = true;
            SyncEngineLog.DeletedDuringUpload(logger, temporaryId, created.Id);
            return;
        }

        SyncEngineLog.ProfileUploaded(logger, temporaryId, created.Id);
        await TakeServerCopyAsync(created.Id, upload.Snapshot, created.ContentHash, cycle, cancellationToken);
    }

    private void Duplicate(Upload upload, string? requestId, SyncCycle cycle)
    {
        // The marker stays until the pull of this cycle has shown which profile it is, so a cycle
        // that cannot get that far simply asks again next time.
        cycle.Duplicates.Add(upload.Marker.EntityId!.Value);
        cycle.Uploads[upload.Marker.EntityId!.Value] = new ProfileUploadOutcome(ProfileUploadKind.Duplicate);
        SyncEngineLog.UploadDuplicate(logger, upload.Marker.EntityId!.Value, requestId);
    }

    /// <summary>
    /// Sends a changed profile as it is now. The configuration goes only when it was edited here,
    /// which the marker says; that the server's differs may just as well be somebody else's edit.
    /// </summary>
    private async Task<SyncStep> UpdateAsync(PendingChange marker, SyncCycle cycle, CancellationToken cancellationToken)
    {
        Guid profileId = marker.EntityId!.Value;

        if (await ReadSnapshotAsync(profileId, cancellationToken) is not { } snapshot)
        {
            return await DropUnsendableAsync(marker, cancellationToken);
        }

        // Read after the snapshot, not with the marker: an edit saved in between then either is in
        // the snapshot already or makes the profile differ from it, which sends the marker again.
        if (await ReadConfigurationChangedAsync(marker.Id, cancellationToken) is not { } configurationChanged)
        {
            return await DropUnsendableAsync(marker, cancellationToken);
        }

        ServerResult<ProfileResponse> result = await api.UpdateProfileAsync(
            profileId,
            snapshot.ToUpdateRequest(configurationChanged),
            PilotHeaders.MatchAny,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return await FailedAsync(marker, result, cycle, cancellationToken);
        }

        bool unchanged = await TakeServerCopyAsync(profileId, snapshot, result.Value.ContentHash, cycle, cancellationToken);
        return await SettleAsync(marker, cycle, unchanged, cancellationToken);
    }

    /// <summary>
    /// After the server stored a profile: takes its configuration when the server's hash differs,
    /// because it rewrote the text, or records the profile for sending again when it changed here
    /// meanwhile.
    /// </summary>
    /// <returns>True when the profile was as it was sent.</returns>
    private async Task<bool> TakeServerCopyAsync(
        Guid profileId,
        ProfileSnapshot sent,
        string serverHash,
        SyncCycle cycle,
        CancellationToken cancellationToken)
    {
        ServerResult<ProfileConfigurationResponse>? configuration = null;

        if (!string.Equals(serverHash, sent.ContentHash, StringComparison.Ordinal))
        {
            // When this fails the hashes still differ, and the next pull fetches it.
            configuration = await api.GetConfigurationAsync(profileId, cancellationToken);
        }

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles
            .Include(candidate => candidate.Tags)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(candidate => candidate.Id == profileId, cancellationToken);

        if (profile is null)
        {
            return true;
        }

        if (ProfileSnapshot.Of(profile) != sent)
        {
            // Coordinator's rule for an edit made while the upload was under way: it is not lost,
            // it is sent again as an update of the profile under its new id.
            await outbox.StageAsync(
                context,
                PendingChangeKind.ProfileUpdate,
                profileId,
                configurationChanged: !string.Equals(profile.Configuration, sent.Configuration, StringComparison.Ordinal),
                cancellationToken: cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            cycle.FollowUp = true;
            SyncEngineLog.EditDuringUpload(logger, profileId);
            return false;
        }

        if (configuration is { IsSuccess: true } stored)
        {
            ProfileConfigurationFacts.Apply(profile, stored.Value.Configuration);
            profile.ContentHash = stored.Value.ContentHash;
            await context.SaveChangesAsync(cancellationToken);
            cycle.Changes |= LibraryChanges.Profiles;
        }

        return true;
    }

    private async Task<ProfileSnapshot?> ReadSnapshotAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        Profile? profile = await context.Profiles
            .AsNoTracking()
            .Include(candidate => candidate.Tags)
            .ThenInclude(link => link.Tag)
            .FirstOrDefaultAsync(candidate => candidate.Id == profileId, cancellationToken);

        return profile is null ? null : ProfileSnapshot.Of(profile);
    }

    /// <returns>Null when the marker is gone, superseded by a deletion meanwhile.</returns>
    private async Task<bool?> ReadConfigurationChangedAsync(long markerId, CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PendingChanges
            .AsNoTracking()
            .Where(change => change.Id == markerId)
            .Select(change => (bool?)change.ConfigurationChanged)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IEnumerable<List<Upload>> Chunk(List<Upload> uploads)
    {
        List<Upload> chunk = [];
        long bytes = 0;

        foreach (Upload upload in uploads)
        {
            long size = upload.Snapshot.EstimatedBytes;

            if (chunk.Count > 0 && (chunk.Count == ProfileBatchRequest.MaximumItems || bytes + size > BatchBytes))
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }

            chunk.Add(upload);
            bytes += size;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    private sealed record Upload(PendingChange Marker, ProfileSnapshot Snapshot);
}

/// <summary>
/// The shared state of a profile as it was sent, to tell later whether it changed meanwhile.
/// </summary>
/// <param name="Tags">The tag names, sorted and joined, so two snapshots compare by value.</param>
internal sealed record ProfileSnapshot(
    string Name,
    string Configuration,
    string ContentHash,
    string? Notes,
    string? Colour,
    bool? ProtectRoutes,
    string Tags)
{
    private const char TagSeparator = '\n';

    public long EstimatedBytes =>
        Encoding.UTF8.GetByteCount(Configuration) + Encoding.UTF8.GetByteCount(Name)
        + (Notes is null ? 0 : Encoding.UTF8.GetByteCount(Notes)) + Encoding.UTF8.GetByteCount(Tags) + 1024;

    public static ProfileSnapshot Of(Profile profile) => new(
        profile.Name,
        profile.Configuration,
        profile.ContentHash,
        profile.Notes,
        profile.Colour,
        profile.ProtectRoutes,
        string.Join(TagSeparator, profile.Tags
            .Select(link => link.Tag?.Name)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)));

    public ProfileCreateRequest ToCreateRequest() =>
        new(Name, Configuration, Notes, Colour, ProtectRoutes, TagList());

    public ProfileUpdateRequest ToUpdateRequest(bool withConfiguration) =>
        new(Name, withConfiguration ? Configuration : null, Notes, Colour, ProtectRoutes, TagList());

    private List<string> TagList() => Tags.Length == 0 ? [] : [.. Tags.Split(TagSeparator)];

    // The configuration is left out, because it carries private keys.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name).Append(", ContentHash = ").Append(ContentHash);
        return true;
    }
}
