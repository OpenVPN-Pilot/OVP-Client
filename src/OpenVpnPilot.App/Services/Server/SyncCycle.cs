using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Whether a cycle goes on after a step.
/// </summary>
internal enum SyncStep
{
    Continue,
    Stop,
}

/// <summary>
/// What a failed call means for the change that made it.
/// </summary>
internal enum FailureKind
{
    /// <summary>
    /// The server refused this change for good. It is dropped and counted; the rest goes on.
    /// </summary>
    Permanent,

    /// <summary>
    /// Nothing more can be sent now: the server cannot be reached, is failing, asks to wait, or the
    /// session or the machine needs attention. The change is kept and the cycle ends.
    /// </summary>
    Stop,

    /// <summary>
    /// The signed in person is not an administrator (any more).
    /// </summary>
    Forbidden,
}

/// <summary>
/// The bookkeeping of one cycle: what it did, and why it stopped if it did.
/// </summary>
internal sealed class SyncCycle
{
    public int Pushed { get; set; }

    public int Dropped { get; set; }

    public int PulledProfiles { get; set; }

    public int PulledTags { get; set; }

    public int PulledVaultEntries { get; set; }

    public int Deletions { get; set; }

    public long? CursorFrom { get; set; }

    public long? CursorTo { get; set; }

    /// <summary>
    /// What the screens have to reload.
    /// </summary>
    public LibraryChanges Changes { get; set; }

    /// <summary>
    /// Profiles the server called duplicates, matched to its own once the pull has run.
    /// </summary>
    public List<Guid> Duplicates { get; } = [];

    /// <summary>
    /// True once the administrator changes were dropped because the role no longer allows them.
    /// </summary>
    public bool AdministratorChangesDropped { get; set; }

    /// <summary>
    /// True when a change stayed pending although it was sent, because it changed again meanwhile,
    /// so another cycle should follow.
    /// </summary>
    public bool FollowUp { get; set; }

    /// <summary>
    /// The failure that ended the cycle early, if one did.
    /// </summary>
    public ServerResult? Failure { get; private set; }

    /// <summary>
    /// The last refusal or failure seen, for the status and the tooltip.
    /// </summary>
    public ServerResult? LastProblem { get; private set; }

    /// <summary>
    /// What became of each profile uploaded in this cycle, by the id it had here before.
    /// </summary>
    public Dictionary<Guid, ProfileUploadOutcome> Uploads { get; } = [];

    public void Stop(ServerResult failure)
    {
        Failure = failure;
        LastProblem = failure;
    }

    public void Note(ServerResult refusal) => LastProblem = refusal;
}

/// <summary>
/// Reads a failed call the same way wherever it happens in a cycle.
/// </summary>
internal static class SyncFailures
{
    public static readonly PendingChangeKind[] AdministratorKinds =
    [
        PendingChangeKind.ProfileCreate,
        PendingChangeKind.ProfileUpdate,
        PendingChangeKind.ProfileDelete,
        PendingChangeKind.TagUpdate,
        PendingChangeKind.TagDelete,
    ];

    public static bool IsAdministratorKind(PendingChangeKind kind) => AdministratorKinds.Contains(kind);

    public static bool IsProblem(ServerResult result, int status, string code) =>
        result.Outcome == ServerOutcome.Problem
        && result.Status == status
        && string.Equals(result.Code, code, StringComparison.Ordinal);

    public static bool IsNotFound(ServerResult result) =>
        result.Outcome == ServerOutcome.Problem && result.Status == 404;

    public static FailureKind Classify(ServerResult result)
    {
        if (result.Outcome != ServerOutcome.Problem)
        {
            return FailureKind.Stop;
        }

        int status = result.Status ?? 0;
        string? code = result.Code;

        if (status == 403 && code == ServerErrorCodes.Forbidden)
        {
            return FailureKind.Forbidden;
        }

        // A refusal about this client or this machine, not about the change: dropping one change
        // after another for it would lose them all.
        bool aboutTheClient = code is ServerErrorCodes.HeaderMissing or ServerErrorCodes.HeaderInvalid
            or ServerErrorCodes.ApiVersionUnsupported or ServerErrorCodes.ClockSkew
            or ServerErrorCodes.HttpsRequired;

        bool worthRetrying = status is 401 or 408 or 412 or 426 or 429 or >= 500
            || code == ServerErrorCodes.Conflict;

        return aboutTheClient || worthRetrying
            ? FailureKind.Stop
            : FailureKind.Permanent;
    }

    /// <summary>
    /// The state a failure that stopped the cycle leaves the synchronisation in.
    /// </summary>
    /// <remarks>
    /// A server error counts as degraded here. Whether the server is really degraded or simply gone
    /// is asked of its readiness check afterwards.
    /// </remarks>
    public static SyncState StateOf(ServerResult result) => result.Outcome switch
    {
        ServerOutcome.Offline => SyncState.Offline,
        ServerOutcome.TlsRefused => SyncState.CertificateUntrusted,
        ServerOutcome.NotSignedIn => SyncState.SignInRequired,
        ServerOutcome.Wiped => SyncState.Idle,
        ServerOutcome.InvalidResponse => SyncState.Degraded,
        ServerOutcome.IdentityUnavailable => SyncState.SettingsUnreadable,
        _ => result switch
        {
            { Code: ServerErrorCodes.ClientOutdated } or { Status: 426 } => SyncState.ClientOutdated,
            { Code: ServerErrorCodes.ClockSkew } => SyncState.ClockWrong,
            { Status: 401 } => SyncState.SignInRequired,
            { Code: { } code } when ServerErrorCodes.SignInRequired.Contains(code) => SyncState.SignInRequired,
            { Status: >= 500 } => SyncState.Degraded,
            _ => SyncState.ChangesWaiting,
        },
    };

    /// <summary>
    /// True for failures that the schedule answers by trying again sooner and then less often.
    /// </summary>
    public static bool BacksOff(SyncState state) => state is SyncState.Offline or SyncState.Degraded;

    /// <summary>
    /// True when the change itself was tried and should carry the attempt.
    /// </summary>
    public static bool CountsAsAttempt(ServerResult result) =>
        (result.Outcome is ServerOutcome.Offline or ServerOutcome.InvalidResponse or ServerOutcome.Problem)
        && StateOf(result) is not (SyncState.SignInRequired or SyncState.ClientOutdated or SyncState.ClockWrong);
}
