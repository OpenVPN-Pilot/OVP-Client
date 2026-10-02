namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// <c>GET /api/v1/sync/changes?since=</c>: everything that changed after a cursor.
/// </summary>
/// <param name="Cursor">Pass as <c>since</c> next time, once everything of this answer is applied.</param>
/// <param name="Full">True for <c>since=0</c>: the complete state, and whatever is missing is gone.</param>
/// <param name="VaultEntries">With their secrets; never print this record's entries anywhere but the keystore.</param>
/// <param name="DeletedVaultEntries">Entries removed while their profile stayed.</param>
public sealed record SyncChangesResponse(
    long Cursor,
    bool Full,
    IReadOnlyList<ProfileResponse> Profiles,
    IReadOnlyList<TagResponse> Tags,
    IReadOnlyList<VaultEntryResponse> VaultEntries,
    IReadOnlyList<Guid> DeletedProfiles,
    IReadOnlyList<Guid> DeletedTags,
    IReadOnlyList<VaultKeyResponse> DeletedVaultEntries);
