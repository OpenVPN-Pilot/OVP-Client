using System.Collections.Frozen;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// The stable codes of the server's problem details.
/// </summary>
/// <remarks>
/// Everything branches on these and nothing on a detail text or a status alone: the server never
/// renames or reuses a code, while its texts may change. Kept in one place so a branch never rests
/// on a string literal typed somewhere else.
/// </remarks>
public static class ServerErrorCodes
{
    public const string ValidationFailed = "request.validation_failed";
    public const string NotFound = "request.not_found";
    public const string PreconditionRequired = "request.precondition_required";
    public const string PreconditionFailed = "request.precondition_failed";
    public const string Conflict = "request.conflict";
    public const string TooLarge = "request.too_large";
    public const string TooManyRequests = "request.too_many";

    public const string HttpsRequired = "transport.https_required";

    public const string HeaderMissing = "pilot.header_missing";
    public const string HeaderInvalid = "pilot.header_invalid";
    public const string ApiVersionUnsupported = "pilot.api_version_unsupported";
    public const string ClientOutdated = "pilot.client_outdated";
    public const string ClockSkew = "pilot.clock_skew";

    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string ModeMismatch = "auth.mode_mismatch";
    public const string ProviderUnavailable = "auth.provider_unavailable";
    public const string Forbidden = "auth.forbidden";
    public const string IdentityConflict = "auth.identity_conflict";
    public const string TokenMissing = "auth.token_missing";
    public const string TokenInvalid = "auth.token_invalid";
    public const string TokenExpired = "auth.token_expired";
    public const string TokenRevoked = "auth.token_revoked";
    public const string ClientMismatch = "auth.client_mismatch";
    public const string RefreshTokenInvalid = "auth.refresh_token_invalid";
    public const string RefreshTokenReused = "auth.refresh_token_reused";
    public const string ReauthenticationRequired = "auth.reauthentication_required";

    /// <summary>
    /// Arrives together with the wipe directive. The directive decides, not this code.
    /// </summary>
    public const string AccountRevoked = "account.revoked";

    public const string ProfileNotFound = "profile.not_found";
    public const string ProfileDuplicate = "profile.duplicate";
    public const string ProfileInvalidConfiguration = "profile.invalid_configuration";
    public const string ProfileNotSelfContained = "profile.not_self_contained";
    public const string ProfileNoServerVerification = "profile.no_server_verification";

    public const string TagNotFound = "tag.not_found";
    public const string TagDuplicate = "tag.duplicate";

    public const string VaultEntryNotFound = "vault.entry_not_found";
    public const string VaultEntryExists = "vault.entry_exists";

    public const string SyncCursorExpired = "sync.cursor_expired";

    public const string UserNotFound = "user.not_found";
    public const string UserSelfModification = "user.self_modification";

    public const string ServerError = "server.error";
    public const string ServerDataKeyMismatch = "server.data_key_mismatch";

    /// <summary>
    /// Refusals of an ordinary call that a refresh cures: refresh once, then repeat the call once.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenInvalid"/> is here as well as the two the brief names, because the contract
    /// says the same for it: refresh, and sign in only when that fails.
    /// </remarks>
    public static FrozenSet<string> RefreshAndRepeat { get; } =
        FrozenSet.ToFrozenSet([TokenExpired, TokenRevoked, TokenInvalid], StringComparer.Ordinal);

    /// <summary>
    /// Refusals of a refresh that end the session: only signing in again helps. Nothing is erased.
    /// </summary>
    public static FrozenSet<string> SignInRequired { get; } =
        FrozenSet.ToFrozenSet(
            [RefreshTokenInvalid, RefreshTokenReused, ReauthenticationRequired, ClientMismatch],
            StringComparer.Ordinal);
}
