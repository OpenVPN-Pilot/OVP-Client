namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// Where synchronisation with the configured server stands. A database holds at most one row.
/// </summary>
/// <remarks>
/// It lives in the database rather than in the settings because the server's copy of the profiles
/// is a database of its own, one per server, and the cursor is only valid for the server that issued
/// it. Keeping both in one file means they can never disagree. In a local library the table simply
/// stays empty.
///
/// The signed in person is kept here as well, so the role can still be shown while the server cannot
/// be reached. Nothing in this row is secret: tokens live in the keystore only.
/// </remarks>
public sealed class SyncState
{
    /// <summary>
    /// The key of the one row there is.
    /// </summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// What the server answered as its cursor on the last synchronisation that was applied
    /// completely. Null until the first one, which then asks for everything.
    /// </summary>
    public long? Cursor { get; set; }

    public DateTimeOffset? LastSuccessfulPullAt { get; set; }

    public DateTimeOffset? LastSuccessfulPushAt { get; set; }

    /// <summary>
    /// The problem code of the last call that failed, as the server named it.
    /// </summary>
    public string? LastErrorCode { get; set; }

    /// <summary>
    /// The request id the server echoed for that failure, so the person reporting it and the
    /// operator reading the server log can find the same request.
    /// </summary>
    public string? LastRequestId { get; set; }

    public Guid? UserId { get; set; }

    public string? Username { get; set; }

    public string? UserDisplayName { get; set; }

    /// <summary>
    /// The role exactly as the server wrote it, for example admin or user.
    /// </summary>
    public string? UserRole { get; set; }

    /// <summary>
    /// How the person signed in, exactly as the server wrote it, for example file or entra.
    /// </summary>
    public string? UserProvider { get; set; }
}
