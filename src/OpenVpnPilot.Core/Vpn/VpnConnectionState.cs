namespace OpenVpnPilot.Core.Vpn;

/// <summary>
/// Where a tunnel is in its lifecycle.
/// </summary>
/// <remarks>
/// This is the client's own state machine, not a copy of the OpenVPN state names. Several OpenVPN
/// states collapse into <see cref="Connecting"/> because the distinction between resolving, waiting
/// and fetching configuration is detail the user does not act on.
/// </remarks>
public enum VpnConnectionState
{
    /// <summary>
    /// No process is running for this profile.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The process is being started and the management interface is not attached yet.
    /// </summary>
    Launching,

    /// <summary>
    /// The server asked for credentials and the client is answering.
    /// </summary>
    Authenticating,

    /// <summary>
    /// The tunnel is being negotiated.
    /// </summary>
    Connecting,

    /// <summary>
    /// The tunnel carries traffic.
    /// </summary>
    Connected,

    /// <summary>
    /// The tunnel dropped and OpenVPN is retrying on its own.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// A disconnect was requested and the process is shutting down.
    /// </summary>
    Disconnecting,

    /// <summary>
    /// The connection ended because of an error. <see cref="VpnConnectionStatus.Message"/> says why.
    /// </summary>
    Failed,
}

/// <summary>
/// A snapshot of one connection, suitable for binding directly to the user interface.
/// </summary>
public sealed record VpnConnectionStatus
{
    public required VpnConnectionState State { get; init; }

    /// <summary>
    /// When the connection entered <see cref="VpnConnectionState.Connected"/>. Null at any other time.
    /// </summary>
    public DateTimeOffset? ConnectedSince { get; init; }

    /// <summary>
    /// Address assigned to the tunnel interface.
    /// </summary>
    public string? LocalAddress { get; init; }

    public string? ServerAddress { get; init; }

    public int? ServerPort { get; init; }

    public long BytesReceived { get; init; }

    public long BytesSent { get; init; }

    /// <summary>
    /// Explanatory text for a failure or a reconnect reason. Empty while everything is normal.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Routes the server pushed, as network and mask. Empty until a server sends any.
    /// </summary>
    /// <remarks>
    /// Shown because a pushed route is what decides which traffic the tunnel actually carries, and
    /// because a route the client refused is otherwise invisible.
    /// </remarks>
    public IReadOnlyList<string> PushedRoutes { get; init; } = [];

    /// <summary>
    /// Name servers the server pushed.
    /// </summary>
    public IReadOnlyList<string> PushedDnsServers { get; init; } = [];

    /// <summary>
    /// The tunnel gateway the server named, which is the address a round trip is measured against.
    /// </summary>
    public string? Gateway { get; init; }

    /// <summary>
    /// True when the server asked to carry all traffic, whether or not the client accepted it.
    /// </summary>
    public bool ServerRequestedDefaultRoute { get; init; }

    /// <summary>
    /// The most recent round trip time, or null when it has not been measured.
    /// </summary>
    /// <remarks>
    /// A measurement that got no reply is reported as <see cref="PingFailed"/> rather than as zero,
    /// because zero would read as an unusually good result.
    /// </remarks>
    public double? PingMilliseconds { get; init; }

    /// <summary>
    /// True when the last measurement got no reply. Many servers do not answer, which is not a fault.
    /// </summary>
    public bool PingFailed { get; init; }

    /// <summary>
    /// Why the connection ended, when it ended for a reason worth acting on.
    /// </summary>
    /// <remarks>
    /// The message alone cannot be acted on, because it is free text from OpenVPN. This is what
    /// tells the history what to record and tells the retry logic whether another attempt could
    /// possibly succeed.
    /// </remarks>
    public VpnFailureKind Failure { get; init; } = VpnFailureKind.None;

    /// <summary>
    /// How long the tunnel has been up, or null when it is not connected.
    /// </summary>
    public TimeSpan? Uptime(DateTimeOffset now) =>
        ConnectedSince is { } since ? now - since : null;

    public static VpnConnectionStatus Disconnected { get; } =
        new() { State = VpnConnectionState.Disconnected };
}

/// <summary>
/// Why a connection stopped, in terms the application can act on.
/// </summary>
public enum VpnFailureKind
{
    /// <summary>
    /// Nothing went wrong, or nothing has gone wrong yet.
    /// </summary>
    None,

    /// <summary>
    /// The user asked for the disconnect.
    /// </summary>
    UserRequested,

    /// <summary>
    /// The process could not be started at all, so nothing was ever negotiated.
    /// </summary>
    LaunchRefused,

    /// <summary>
    /// Credentials were missing or refused. Retrying with the same values cannot help.
    /// </summary>
    Authentication,

    /// <summary>
    /// The tunnel or the management connection dropped. Another attempt may well succeed.
    /// </summary>
    ConnectionLost,

    /// <summary>
    /// OpenVPN reported an unrecoverable error, such as a configuration it cannot parse.
    /// </summary>
    Fatal,
}
