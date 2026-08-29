namespace OpenVpnPilot.OpenVpn.Management;

/// <summary>
/// A single line received from the OpenVPN management interface.
/// </summary>
public abstract record ManagementMessage
{
    /// <summary>
    /// The unparsed line, kept for diagnostics and for logging unknown notifications.
    /// </summary>
    public required string RawLine { get; init; }
}

/// <summary>
/// A connection lifecycle notification, sent after the client enables state reporting.
/// </summary>
public sealed record StateMessage : ManagementMessage
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The state name as reported by OpenVPN, for example CONNECTED or RECONNECTING.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Free form detail. Carries SUCCESS on connect and the restart reason on reconnect.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The address assigned to the tunnel interface, present from ASSIGN_IP onwards.
    /// </summary>
    public string? LocalAddress { get; init; }

    public string? RemoteAddress { get; init; }

    public int? RemotePort { get; init; }

    public string? LocalPort { get; init; }

    public string? LocalIpv6Address { get; init; }
}

/// <summary>
/// A periodic throughput notification carrying cumulative counters for the current session.
/// </summary>
public sealed record ByteCountMessage : ManagementMessage
{
    public required long BytesIn { get; init; }

    public required long BytesOut { get; init; }
}

/// <summary>
/// A real time log entry forwarded by OpenVPN.
/// </summary>
public sealed record LogMessage : ManagementMessage
{
    public required DateTimeOffset Timestamp { get; init; }

    public required LogSeverity Severity { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// Sent while OpenVPN waits for the client to release the startup hold.
/// </summary>
public sealed record HoldMessage : ManagementMessage
{
    public required string Text { get; init; }

    /// <summary>
    /// Seconds OpenVPN will wait before releasing the hold itself. Zero means it waits indefinitely.
    /// </summary>
    public required int TimeoutSeconds { get; init; }
}

/// <summary>
/// A request for credentials. OpenVPN blocks until the client answers.
/// </summary>
public sealed record PasswordRequestMessage : ManagementMessage
{
    /// <summary>
    /// The realm the credentials belong to, for example Auth or a private key name.
    /// </summary>
    public required string Realm { get; init; }

    /// <summary>
    /// True when both a username and a password are expected, false when only a password is.
    /// </summary>
    public required bool NeedsUsername { get; init; }

    /// <summary>
    /// Present when the server appended a static challenge, written as SC:echo,text.
    /// </summary>
    public StaticChallenge? Challenge { get; init; }
}

/// <summary>
/// A one time code the server asks for together with the password.
/// </summary>
/// <param name="Text">The prompt the server wants shown.</param>
/// <param name="Echo">
/// True when the response may be displayed while it is typed. The server sets this for a code read
/// off a token, and clears it for anything it considers secret.
/// </param>
public sealed record StaticChallenge(string Text, bool Echo);

/// <summary>
/// A challenge raised after an attempt was refused, carried in the verification failure.
/// </summary>
/// <remarks>
/// The wire form is CRV1:flags:state,username:text. Answering it means connecting again with the
/// same user name and a password of CRV1::state::response, which is why the state identifier has to
/// survive the failed attempt.
/// </remarks>
/// <param name="StateId">Opaque token the server uses to match the answer to the challenge.</param>
/// <param name="Username">The user name the server echoed back, already decoded.</param>
/// <param name="Text">The prompt the server wants shown.</param>
/// <param name="Echo">True when the response may be displayed while it is typed.</param>
/// <param name="ResponseRequired">
/// False when the server only wants the attempt repeated, for example after a push notification was
/// approved out of band.
/// </param>
public sealed record DynamicChallenge(
    string StateId,
    string? Username,
    string Text,
    bool Echo,
    bool ResponseRequired);

/// <summary>
/// Reports that supplied credentials were rejected.
/// </summary>
public sealed record PasswordVerificationFailedMessage : ManagementMessage
{
    public required string Realm { get; init; }

    public required string Reason { get; init; }

    /// <summary>
    /// Present when the refusal is really a request for a one time code rather than a wrong password.
    /// </summary>
    public DynamicChallenge? Challenge { get; init; }
}

/// <summary>
/// An unrecoverable error. OpenVPN exits immediately afterwards.
/// </summary>
public sealed record FatalMessage : ManagementMessage
{
    public required string Text { get; init; }
}

/// <summary>
/// An informational banner, sent once when the management session opens.
/// </summary>
public sealed record InfoMessage : ManagementMessage
{
    public required string Text { get; init; }
}

/// <summary>
/// A message pushed by the server for display to the user.
/// </summary>
public sealed record EchoMessage : ManagementMessage
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// A notification type this client does not model yet. Retained so nothing is lost silently.
/// </summary>
public sealed record UnknownNotificationMessage : ManagementMessage
{
    public required string Kind { get; init; }

    public required string Payload { get; init; }
}

/// <summary>
/// A reply to a command rather than an asynchronous notification.
/// </summary>
public sealed record CommandResponseMessage : ManagementMessage
{
    public required CommandResponseKind Kind { get; init; }

    /// <summary>
    /// The text after the SUCCESS or ERROR prefix. Empty for END and for continuation lines.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// True when this line completes the response and the next queued command may be sent.
    /// </summary>
    public bool IsTerminal => Kind is CommandResponseKind.Success
        or CommandResponseKind.Error
        or CommandResponseKind.End;
}

public enum CommandResponseKind
{
    /// <summary>
    /// Part of a multi line reply, such as an entry in a log or status dump.
    /// </summary>
    Continuation,
    Success,
    Error,
    End,
}

public enum LogSeverity
{
    /// <summary>
    /// OpenVPN emits an empty flag field for ordinary progress output.
    /// </summary>
    Verbose,
    Informational,
    Warning,
    NonFatalError,
    Fatal,
    Debug,
}
