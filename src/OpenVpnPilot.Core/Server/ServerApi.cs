using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The server's API, one typed method per endpoint the client uses.
/// </summary>
/// <remarks>
/// Nothing here throws for anything the network or the server does; every method answers a
/// <see cref="ServerResult"/>. Calls that need a signed in user carry the session's access token,
/// refresh it a minute before it expires, and on a refusal that a refresh cures refresh once and
/// repeat the call once. Sign in and sign out are <see cref="IServerSignIn"/>'s.
/// </remarks>
public interface IServerApi
{
    /// <summary>
    /// The server this API talks to, ending with a slash.
    /// </summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// <c>GET /api/v1/server/info</c>, anonymous and without <c>X-Pilot-*</c> headers.
    /// </summary>
    public Task<ServerResult<ServerInfoResponse>> GetServerInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET /health/ready</c>: success when the server can serve, a problem with status 503 when it
    /// is reachable but degraded.
    /// </summary>
    public Task<ServerResult> GetReadinessAsync(CancellationToken cancellationToken = default);

    public Task<ServerResult<CurrentUserResponse>> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET /api/v1/sync/changes?since=</c>; 0 asks for the complete state.
    /// </summary>
    public Task<ServerResult<SyncChangesResponse>> GetChangesAsync(long since, CancellationToken cancellationToken = default);

    public Task<ServerResult<IReadOnlyList<ProfileResponse>>> GetProfilesAsync(
        string? tag = null,
        string? search = null,
        CancellationToken cancellationToken = default);

    public Task<ServerResult<ProfileResponse>> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    public Task<ServerResult<ProfileConfigurationResponse>> GetConfigurationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    public Task<ServerResult<ProfileResponse>> CreateProfileAsync(
        ProfileCreateRequest profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /api/v1/profiles/batch</c>, with the longer time limit an upload of up to 64 MiB needs.
    /// Splitting into batches is the caller's job.
    /// </summary>
    public Task<ServerResult<ProfileBatchResponse>> CreateProfilesAsync(
        ProfileBatchRequest batch,
        CancellationToken cancellationToken = default);

    /// <param name="ifMatch">An ETag, or <see cref="PilotHeaders.MatchAny"/> to overwrite whatever is stored.</param>
    public Task<ServerResult<ProfileResponse>> UpdateProfileAsync(
        Guid profileId,
        ProfileUpdateRequest profile,
        string ifMatch,
        CancellationToken cancellationToken = default);

    public Task<ServerResult> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    public Task<ServerResult<IReadOnlyList<TagResponse>>> GetTagsAsync(CancellationToken cancellationToken = default);

    public Task<ServerResult<TagResponse>> CreateTagAsync(TagRequest tag, CancellationToken cancellationToken = default);

    public Task<ServerResult<TagResponse>> UpdateTagAsync(Guid tagId, TagRequest tag, CancellationToken cancellationToken = default);

    public Task<ServerResult> DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default);

    public Task<ServerResult<IReadOnlyList<VaultEntryResponse>>> GetVaultAsync(CancellationToken cancellationToken = default);

    public Task<ServerResult<IReadOnlyList<VaultEntryResponse>>> GetProfileVaultAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST</c>: adds an entry that does not exist yet, which every user may do; 409
    /// <see cref="ServerErrorCodes.VaultEntryExists"/> when one does.
    /// </summary>
    public Task<ServerResult<VaultEntryResponse>> AddVaultEntryAsync(
        Guid profileId,
        string realm,
        VaultEntryRequest entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>PUT</c>: stores or replaces an entry. Administrators only.
    /// </summary>
    public Task<ServerResult<VaultEntryResponse>> PutVaultEntryAsync(
        Guid profileId,
        string realm,
        VaultEntryRequest entry,
        CancellationToken cancellationToken = default);

    public Task<ServerResult> DeleteVaultEntryAsync(Guid profileId, string realm, CancellationToken cancellationToken = default);

    public Task<ServerResult<FavouritesDocument>> GetFavouritesAsync(CancellationToken cancellationToken = default);

    public Task<ServerResult<FavouritesDocument>> PutFavouritesAsync(
        FavouritesDocument favourites,
        CancellationToken cancellationToken = default);

    public Task<ServerResult<HotkeysDocument>> GetHotkeysAsync(CancellationToken cancellationToken = default);

    public Task<ServerResult<HotkeysDocument>> PutHotkeysAsync(HotkeysDocument hotkeys, CancellationToken cancellationToken = default);

    public Task<ServerResult<SettingsResponse>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <param name="ifMatch">An ETag to refuse overwriting another machine's change, or null to overwrite.</param>
    public Task<ServerResult<SettingsResponse>> PutSettingsAsync(
        SettingsRequest settings,
        string? ifMatch = null,
        CancellationToken cancellationToken = default);

    public Task<ServerResult<IReadOnlyList<UserResponse>>> GetUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables a user, whose clients then receive the wipe directive. Administrators only.
    /// </summary>
    public Task<ServerResult<UserResponse>> DisableUserAsync(Guid userId, CancellationToken cancellationToken = default);

    public Task<ServerResult<UserResponse>> EnableUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The API of one server, over its transport and its session.
/// </summary>
public sealed class ServerApi : IServerApi
{
    private readonly ServerTransport transport;
    private readonly IServerSession session;

    internal ServerApi(Uri baseAddress, ServerTransport transport, IServerSession session)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(session);

        BaseAddress = baseAddress;
        this.transport = transport;
        this.session = session;
    }

    public Uri BaseAddress { get; }

    public Task<ServerResult<ServerInfoResponse>> GetServerInfoAsync(CancellationToken cancellationToken = default) =>
        transport.SendAsync<ServerInfoResponse>(new ServerRequest(HttpMethod.Get, ServerPaths.Info), cancellationToken);

    public Task<ServerResult> GetReadinessAsync(CancellationToken cancellationToken = default) =>
        transport.SendAsync(new ServerRequest(HttpMethod.Get, ServerPaths.HealthReady), cancellationToken);

    public Task<ServerResult<CurrentUserResponse>> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        SendAsync<CurrentUserResponse>(new ServerRequest(HttpMethod.Get, ServerPaths.Me), cancellationToken);

    public Task<ServerResult<SyncChangesResponse>> GetChangesAsync(long since, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(since);
        return SendAsync<SyncChangesResponse>(new ServerRequest(HttpMethod.Get, ServerPaths.Changes(since)), cancellationToken);
    }

    public Task<ServerResult<IReadOnlyList<ProfileResponse>>> GetProfilesAsync(
        string? tag = null,
        string? search = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ProfileResponse>>(
            new ServerRequest(HttpMethod.Get, ServerPaths.ProfileList(tag, search)),
            cancellationToken);

    public Task<ServerResult<ProfileResponse>> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        SendAsync<ProfileResponse>(new ServerRequest(HttpMethod.Get, ServerPaths.Profile(profileId)), cancellationToken);

    public Task<ServerResult<ProfileConfigurationResponse>> GetConfigurationAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ProfileConfigurationResponse>(
            new ServerRequest(HttpMethod.Get, ServerPaths.Configuration(profileId)),
            cancellationToken);

    public Task<ServerResult<ProfileResponse>> CreateProfileAsync(
        ProfileCreateRequest profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SendAsync<ProfileResponse>(new ServerRequest(HttpMethod.Post, ServerPaths.Profiles, profile), cancellationToken);
    }

    public Task<ServerResult<ProfileBatchResponse>> CreateProfilesAsync(
        ProfileBatchRequest batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return SendAsync<ProfileBatchResponse>(
            new ServerRequest(HttpMethod.Post, ServerPaths.ProfileBatch, batch, Timeout: ServerTransport.BatchTimeout),
            cancellationToken);
    }

    public Task<ServerResult<ProfileResponse>> UpdateProfileAsync(
        Guid profileId,
        ProfileUpdateRequest profile,
        string ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(ifMatch);

        return SendAsync<ProfileResponse>(
            new ServerRequest(HttpMethod.Put, ServerPaths.Profile(profileId), profile, IfMatch: ifMatch),
            cancellationToken);
    }

    public Task<ServerResult> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        SendAsync(new ServerRequest(HttpMethod.Delete, ServerPaths.Profile(profileId)), cancellationToken);

    public Task<ServerResult<IReadOnlyList<TagResponse>>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<TagResponse>>(new ServerRequest(HttpMethod.Get, ServerPaths.Tags), cancellationToken);

    public Task<ServerResult<TagResponse>> CreateTagAsync(TagRequest tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return SendAsync<TagResponse>(new ServerRequest(HttpMethod.Post, ServerPaths.Tags, tag), cancellationToken);
    }

    public Task<ServerResult<TagResponse>> UpdateTagAsync(Guid tagId, TagRequest tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return SendAsync<TagResponse>(new ServerRequest(HttpMethod.Put, ServerPaths.Tag(tagId), tag), cancellationToken);
    }

    public Task<ServerResult> DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default) =>
        SendAsync(new ServerRequest(HttpMethod.Delete, ServerPaths.Tag(tagId)), cancellationToken);

    public Task<ServerResult<IReadOnlyList<VaultEntryResponse>>> GetVaultAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<VaultEntryResponse>>(new ServerRequest(HttpMethod.Get, ServerPaths.Vault), cancellationToken);

    public Task<ServerResult<IReadOnlyList<VaultEntryResponse>>> GetProfileVaultAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<VaultEntryResponse>>(
            new ServerRequest(HttpMethod.Get, ServerPaths.ProfileVault(profileId)),
            cancellationToken);

    public Task<ServerResult<VaultEntryResponse>> AddVaultEntryAsync(
        Guid profileId,
        string realm,
        VaultEntryRequest entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return SendAsync<VaultEntryResponse>(
            new ServerRequest(HttpMethod.Post, ServerPaths.VaultEntry(profileId, realm), entry),
            cancellationToken);
    }

    public Task<ServerResult<VaultEntryResponse>> PutVaultEntryAsync(
        Guid profileId,
        string realm,
        VaultEntryRequest entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return SendAsync<VaultEntryResponse>(
            new ServerRequest(HttpMethod.Put, ServerPaths.VaultEntry(profileId, realm), entry),
            cancellationToken);
    }

    public Task<ServerResult> DeleteVaultEntryAsync(Guid profileId, string realm, CancellationToken cancellationToken = default) =>
        SendAsync(new ServerRequest(HttpMethod.Delete, ServerPaths.VaultEntry(profileId, realm)), cancellationToken);

    public Task<ServerResult<FavouritesDocument>> GetFavouritesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<FavouritesDocument>(new ServerRequest(HttpMethod.Get, ServerPaths.Favourites), cancellationToken);

    public Task<ServerResult<FavouritesDocument>> PutFavouritesAsync(
        FavouritesDocument favourites,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(favourites);

        return SendAsync<FavouritesDocument>(
            new ServerRequest(HttpMethod.Put, ServerPaths.Favourites, favourites),
            cancellationToken);
    }

    public Task<ServerResult<HotkeysDocument>> GetHotkeysAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HotkeysDocument>(new ServerRequest(HttpMethod.Get, ServerPaths.Hotkeys), cancellationToken);

    public Task<ServerResult<HotkeysDocument>> PutHotkeysAsync(HotkeysDocument hotkeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        return SendAsync<HotkeysDocument>(new ServerRequest(HttpMethod.Put, ServerPaths.Hotkeys, hotkeys), cancellationToken);
    }

    public Task<ServerResult<SettingsResponse>> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SettingsResponse>(new ServerRequest(HttpMethod.Get, ServerPaths.Settings), cancellationToken);

    public Task<ServerResult<SettingsResponse>> PutSettingsAsync(
        SettingsRequest settings,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return SendAsync<SettingsResponse>(
            new ServerRequest(HttpMethod.Put, ServerPaths.Settings, settings, IfMatch: ifMatch),
            cancellationToken);
    }

    public Task<ServerResult<IReadOnlyList<UserResponse>>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<UserResponse>>(new ServerRequest(HttpMethod.Get, ServerPaths.Users), cancellationToken);

    public Task<ServerResult<UserResponse>> DisableUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<UserResponse>(new ServerRequest(HttpMethod.Post, ServerPaths.DisableUser(userId)), cancellationToken);

    public Task<ServerResult<UserResponse>> EnableUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SendAsync<UserResponse>(new ServerRequest(HttpMethod.Post, ServerPaths.EnableUser(userId)), cancellationToken);

    private async Task<ServerResult> SendAsync(ServerRequest request, CancellationToken cancellationToken) =>
        await SendAuthorisedAsync(request, transport.SendForStatusAsync, cancellationToken);

    private Task<ServerResult<T>> SendAsync<T>(ServerRequest request, CancellationToken cancellationToken) =>
        SendAuthorisedAsync(request, transport.SendAsync<T>, cancellationToken);

    /// <summary>
    /// Sends with the session's access token, and once more after a refresh when the server says
    /// the token is the reason for the refusal.
    /// </summary>
    private async Task<ServerResult<T>> SendAuthorisedAsync<T>(
        ServerRequest request,
        Func<ServerRequest, CancellationToken, Task<ServerResult<T>>> send,
        CancellationToken cancellationToken)
    {
        ServerResult<string> token = await session.GetAccessTokenAsync(cancellationToken);

        if (!token.IsSuccess)
        {
            return token.AsFailure<T>();
        }

        ServerResult<T> result = await send(request with { BearerToken = token.Value }, cancellationToken);

        if (!IsCuredByRefresh(result))
        {
            return result;
        }

        ServerResult<string> refreshed = await session.RefreshAsync(token.Value, cancellationToken);

        if (!refreshed.IsSuccess)
        {
            return refreshed.AsFailure<T>();
        }

        // Once, and only once: a second refusal is the answer.
        return await send(request with { BearerToken = refreshed.Value }, cancellationToken);
    }

    private static bool IsCuredByRefresh(ServerResult result) =>
        result.Outcome == ServerOutcome.Problem
        && result.Status == ServerStatus.Unauthorized
        && result.Code is { } code
        && ServerErrorCodes.RefreshAndRepeat.Contains(code);
}
