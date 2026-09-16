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
    /// The same reason as <see cref="Message"/>, in a form that can be translated, when this client
    /// wrote it. Null when the message is text from OpenVPN or from an exception, which nobody can
    /// translate, and null when there is no message at all.
    /// </summary>
    public VpnStatusReason? Reason { get; init; }

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
    /// True when the server pushed a compression setting.
    /// </summary>
    /// <remarks>
    /// A current client refuses any pushed compression when data channel offload is active, and
    /// refusing one option makes it abandon the whole push reply. What the user sees is a tunnel
    /// that reconnects forever, reporting a reason that names neither compression nor the server.
    /// This is what lets the interface say which it was.
    /// </remarks>
    public bool ServerRequestedCompression { get; init; }

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
    /// The address the round trip was measured against.
    /// </summary>
    /// <remarks>
    /// Shown alongside the figure. A round trip without its target cannot be checked against
    /// anything, and a number nobody can check is a number nobody should be asked to believe.
    /// </remarks>
    public string? PingTarget { get; init; }

    /// <summary>
    /// What the measured address is, which decides what the figure means.
    /// </summary>
    public PingTargetKind PingTargetKind { get; init; } = PingTargetKind.None;

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
/// What a round trip was measured against.
/// </summary>
/// <remarks>
/// The two are different measurements and the difference is large enough to be noticed: the gateway
/// is reached through the tunnel and carries the encapsulation with it, while the server's public
/// address is reached over the ordinary route and says only how far away the endpoint is. Reporting
/// a figure without saying which one it is invites the user to compare it with a terminal ping and
/// conclude the client is lying.
/// </remarks>
public enum PingTargetKind
{
    /// <summary>
    /// Nothing has been measured, because nothing worth measuring is known yet.
    /// </summary>
    None,

    /// <summary>
    /// The far end of the tunnel. The figure is the round trip through the tunnel.
    /// </summary>
    TunnelGateway,

    /// <summary>
    /// The server's public address, reached outside the tunnel. Used when the server names no
    /// gateway, which is the only case where there would otherwise be nothing to measure at all.
    /// </summary>
    ServerEndpoint,
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

    /// <summary>
    /// The server asked for something this client cannot do, such as a compression setting.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Fatal"/> because it decides whether trying again makes sense. The
    /// server will ask for the same thing on the next attempt and the client will refuse it again,
    /// so retrying only produces one more process to clean up, several times a minute.
    /// </remarks>
    Unsupported,
}
