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
    /// How long the tunnel has been up, or null when it is not connected.
    /// </summary>
    public TimeSpan? Uptime(DateTimeOffset now) =>
        ConnectedSince is { } since ? now - since : null;

    public static VpnConnectionStatus Disconnected { get; } =
        new() { State = VpnConnectionState.Disconnected };
}
