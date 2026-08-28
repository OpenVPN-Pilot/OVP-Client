namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// One connection, from the moment it came up to the moment it ended.
/// </summary>
/// <remarks>
/// Recorded so the history view can answer when a profile was used, for how long and how much it
/// transferred.
/// </remarks>
public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Null while the session is still running.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    public long BytesReceived { get; set; }

    public long BytesSent { get; set; }

    public string? LocalAddress { get; set; }

    public string? ServerAddress { get; set; }

    public int? ServerPort { get; set; }

    /// <summary>
    /// Mean round trip time over the session, when it could be measured.
    /// </summary>
    public double? AveragePingMilliseconds { get; set; }

    public SessionEndReason EndReason { get; set; } = SessionEndReason.Running;

    /// <summary>
    /// Explanatory text for a failed or dropped session.
    /// </summary>
    public string? EndDetail { get; set; }

    public List<SessionEvent> Events { get; } = [];

    public TimeSpan? Duration => EndedAt is { } ended ? ended - StartedAt : null;
}

public enum SessionEndReason
{
    /// <summary>
    /// The session has not ended yet.
    /// </summary>
    Running,

    /// <summary>
    /// The user disconnected.
    /// </summary>
    UserRequested,

    /// <summary>
    /// The tunnel dropped and was not restored.
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// Authentication was refused.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// OpenVPN reported a fatal error.
    /// </summary>
    Error,

    /// <summary>
    /// The application exited while the tunnel was up.
    /// </summary>
    ApplicationClosed,
}

/// <summary>
/// A notable moment within a session, kept for the per connection log.
/// </summary>
public sealed class SessionEvent
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public Session? Session { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public required string Level { get; set; }

    public required string Message { get; set; }
}

/// <summary>
/// A user assignable global shortcut.
/// </summary>
public sealed class HotkeyBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Identifies the action, for example ConnectLastUsed or ConnectFavourite1.
    /// </summary>
    public required string ActionId { get; set; }

    /// <summary>
    /// The key combination in a portable textual form, for example Control+Alt+V.
    /// </summary>
    public required string Gesture { get; set; }

    /// <summary>
    /// Profile the action applies to, when the action targets one.
    /// </summary>
    public Guid? ProfileId { get; set; }

    public bool IsEnabled { get; set; } = true;
}
