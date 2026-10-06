namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Keeps the server's copy in this database in step with the server: pushes what changed here, then
/// pulls what changed there.
/// </summary>
/// <remarks>
/// One engine per signed in session, and one cycle at a time. A request that arrives during a cycle
/// schedules one more cycle rather than a parallel one, so a burst of local changes costs at most two
/// round trips.
/// </remarks>
public interface ISyncEngine
{
    /// <summary>
    /// What the synchronisation is doing and how it went last, for the status bar, the tray and the
    /// storage settings.
    /// </summary>
    public SyncStatus Status { get; }

    /// <summary>
    /// Raised after <see cref="Status"/> changed. Raised on a background thread.
    /// </summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Starts the schedule: a cycle now, then periodically, after local changes and when the network
    /// comes back. Returns without waiting for the first cycle.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the schedule and waits for a running cycle to end.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a cycle now and returns how it ended. When a cycle is running, waits for it and then runs
    /// one more, so the answer covers everything changed before the call.
    /// </summary>
    public Task<SyncCycleResult> SynchronizeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks for a cycle soon without waiting for it. Requests close together become one cycle.
    /// </summary>
    public void RequestSync();
}

/// <summary>
/// The state the status bar shows as its dot and its text.
/// </summary>
public enum SyncState
{
    /// <summary>Not started, or stopped.</summary>
    Idle,

    /// <summary>A cycle is running.</summary>
    Synchronising,

    /// <summary>The last cycle reached the server and left nothing waiting.</summary>
    Synchronised,

    /// <summary>The last cycle reached the server, and local changes are still waiting.</summary>
    ChangesWaiting,

    /// <summary>The server could not be reached; work continues from the local copy.</summary>
    Offline,

    /// <summary>The server answered, but its readiness check says it cannot serve properly.</summary>
    Degraded,

    /// <summary>The session ended and needs a new sign in.</summary>
    SignInRequired,

    /// <summary>The server requires a newer client.</summary>
    ClientOutdated,

    /// <summary>The server refused this machine's clock.</summary>
    ClockWrong,

    /// <summary>The server's certificate is not trusted by this machine.</summary>
    CertificateUntrusted,

    /// <summary>
    /// The settings could not be read at start, so there is no installation identity to send and
    /// nothing is asked of the server until the application starts again.
    /// </summary>
    SettingsUnreadable,

    /// <summary>
    /// The session is stored, but the keychain did not hand it over at the start. Nothing is asked
    /// of the server until the application starts again or the person signs in.
    /// </summary>
    KeystoreRefused,
}

/// <summary>
/// A snapshot of the synchronisation. Nothing in it is secret.
/// </summary>
/// <param name="State">What the synchronisation is doing or why it stopped.</param>
/// <param name="LastPullAt">When a pull last completed, if ever.</param>
/// <param name="LastPushAt">When a push last completed, if ever.</param>
/// <param name="PendingChanges">Local changes not yet pushed.</param>
/// <param name="DroppedChanges">Changes the server refused for good since the last successful cycle.</param>
/// <param name="Cursor">The server's cursor the last pull ended at, if any.</param>
/// <param name="LastErrorCode">The problem code of the last failure, if any.</param>
/// <param name="LastRequestId">The request id of the last failure, if any.</param>
/// <param name="Detail">Text the server gave with the last failure, such as both times of a clock problem.</param>
public sealed record SyncStatus(
    SyncState State,
    DateTimeOffset? LastPullAt,
    DateTimeOffset? LastPushAt,
    int PendingChanges,
    int DroppedChanges,
    long? Cursor,
    string? LastErrorCode,
    string? LastRequestId,
    string? Detail)
{
    /// <summary>
    /// The status before anything happened.
    /// </summary>
    public static SyncStatus Initial { get; } = new(SyncState.Idle, null, null, 0, 0, null, null, null, null);
}

/// <summary>
/// How one cycle ended.
/// </summary>
/// <param name="Completed">True when push and pull both completed.</param>
/// <param name="State">The state the cycle left the synchronisation in.</param>
/// <param name="Pushed">Changes the server accepted.</param>
/// <param name="Dropped">Changes the server refused for good.</param>
/// <param name="Changed">True when the pull changed anything in the local copy.</param>
public sealed record SyncCycleResult(bool Completed, SyncState State, int Pushed, int Dropped, bool Changed)
{
    /// <summary>
    /// What became of each profile created here that the cycle uploaded, by the id it had before.
    /// A profile the cycle did not get to is not in it.
    /// </summary>
    public IReadOnlyDictionary<Guid, ProfileUploadOutcome> Uploads { get; init; } = new Dictionary<Guid, ProfileUploadOutcome>();
}

/// <summary>
/// What the server made of one uploaded profile.
/// </summary>
public enum ProfileUploadKind
{
    /// <summary>The server created it; the profile now has the server's id.</summary>
    Created,

    /// <summary>The server already had the same configuration; the profile became the server's.</summary>
    Duplicate,

    /// <summary>The server refused it; the code and the detail say why.</summary>
    Rejected,
}

/// <param name="ServerId">The server's id, when it was created.</param>
/// <param name="Code">The problem code, when it was refused.</param>
/// <param name="Detail">The server's own words about a refusal, for the person who imported it.</param>
public sealed record ProfileUploadOutcome(ProfileUploadKind Kind, Guid? ServerId = null, string? Code = null, string? Detail = null);
