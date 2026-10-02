namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Announces that a server told this client to erase what it received from it.
/// </summary>
/// <remarks>
/// The directive can arrive on any answer, an ordinary call, a refresh or a sign in, and the header
/// decides whatever the status. It is looked for on every answer before anything else reads it, and
/// announced once: several calls in flight can all carry it, and the wipe is one act. Once it has
/// arrived, nothing more is sent to that server, because the contract forbids retrying.
///
/// This only detects and announces. Carrying the wipe out (disconnecting, deleting the cache and the
/// keystore entries, switching to local and restarting) belongs to whoever subscribes.
/// </remarks>
public interface IServerWipeSignal
{
    /// <summary>
    /// True once a directive has arrived. Stays true for the life of the connection.
    /// </summary>
    public bool IsRequested { get; }

    /// <summary>
    /// The first directive, when one arrived.
    /// </summary>
    public ServerWipeDirective? Directive { get; }

    /// <summary>
    /// Raised once, for the first directive, on the thread that received the answer. A handler must
    /// not block: it hands the work to something else and returns.
    /// </summary>
    public event EventHandler<ServerWipeDirectiveEventArgs>? WipeRequested;
}

/// <summary>
/// Where and when the directive arrived. Nothing in it is secret.
/// </summary>
/// <param name="BaseAddress">The server that sent it.</param>
/// <param name="Method">The request's method.</param>
/// <param name="Path">The request's path.</param>
/// <param name="Status">The answer's status.</param>
/// <param name="RequestId">The server's id of the request, for its log.</param>
/// <param name="ReceivedAt">When it arrived.</param>
public sealed record ServerWipeDirective(
    Uri BaseAddress,
    string Method,
    string Path,
    int Status,
    string? RequestId,
    DateTimeOffset ReceivedAt);

public sealed class ServerWipeDirectiveEventArgs(ServerWipeDirective directive) : EventArgs
{
    public ServerWipeDirective Directive { get; } = directive;
}

/// <summary>
/// The signal of one server connection.
/// </summary>
public sealed class ServerWipeSignal : IServerWipeSignal
{
    private ServerWipeDirective? directive;

    public bool IsRequested => Volatile.Read(ref directive) is not null;

    public ServerWipeDirective? Directive => Volatile.Read(ref directive);

    public event EventHandler<ServerWipeDirectiveEventArgs>? WipeRequested;

    /// <summary>
    /// Records a directive and announces it, if it is the first.
    /// </summary>
    /// <returns>True for the first directive, false for every later one.</returns>
    public bool Report(ServerWipeDirective received)
    {
        ArgumentNullException.ThrowIfNull(received);

        if (Interlocked.CompareExchange(ref directive, received, null) is not null)
        {
            return false;
        }

        WipeRequested?.Invoke(this, new ServerWipeDirectiveEventArgs(received));
        return true;
    }
}
