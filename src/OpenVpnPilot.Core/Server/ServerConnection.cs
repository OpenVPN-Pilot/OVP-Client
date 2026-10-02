using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Everything that talks to one configured server, built together because the parts share one
/// HTTP client, one session and one wipe signal.
/// </summary>
public interface IServerConnection : IDisposable
{
    /// <summary>
    /// The server's address, ending with a slash.
    /// </summary>
    public Uri BaseAddress { get; }

    public string ServerKey { get; }

    public IServerApi Api { get; }

    public IServerSession Session { get; }

    public IServerSignIn SignIn { get; }

    /// <summary>
    /// Announces the wipe directive, whichever call received it.
    /// </summary>
    public IServerWipeSignal Wipe { get; }
}

/// <summary>
/// Builds the connection to a server.
/// </summary>
public interface IServerConnectionFactory
{
    /// <param name="baseAddress">The server's address, <c>https</c> only.</param>
    /// <param name="serverKey">The key computed from that address, lower case hex of 16 bytes.</param>
    /// <exception cref="ArgumentException">The address is not <c>https</c>, or the key is malformed.</exception>
    public IServerConnection Create(Uri baseAddress, string serverKey);
}

public sealed class ServerConnectionFactory : IServerConnectionFactory
{
    private readonly IServerHttpClientFactory clients;
    private readonly IClientVersionProvider version;
    private readonly ISecretStore secrets;
    private readonly TimeProvider time;
    private readonly ILoggerFactory loggers;

    public ServerConnectionFactory(
        IServerHttpClientFactory clients,
        IClientVersionProvider version,
        ISecretStore secrets,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggers);

        this.clients = clients;
        this.version = version;
        this.secrets = secrets;
        this.time = time;
        this.loggers = loggers;
    }

    public IServerConnection Create(Uri baseAddress, string serverKey)
    {
        Uri address = ServerHttpClientFactory.NormaliseBaseAddress(baseAddress);

        // Checked before anything is built, so a malformed key never leaves a client behind.
        _ = SecretReference.ForServerRefreshToken(serverKey);

        ServerWipeSignal wipe = new();
        HttpClient client = clients.Create(address, wipe);

        ServerTransport transport = new(client, wipe, time, loggers.CreateLogger("OpenVpnPilot.Core.Server.ServerHttp"));
        ILogger sessionLogger = loggers.CreateLogger("OpenVpnPilot.Core.Server.ServerSession");
        ServerSession session = new(serverKey, transport, secrets, time, sessionLogger);
        ServerApi api = new(address, transport, session);
        ServerSignIn signIn = new(api, transport, session, version, sessionLogger);

        return new ServerConnection(address, serverKey, client, api, session, signIn, wipe);
    }

    private sealed class ServerConnection(
        Uri baseAddress,
        string serverKey,
        HttpClient client,
        IServerApi api,
        ServerSession session,
        IServerSignIn signIn,
        IServerWipeSignal wipe) : IServerConnection
    {
        public Uri BaseAddress { get; } = baseAddress;

        public string ServerKey { get; } = serverKey;

        public IServerApi Api { get; } = api;

        public IServerSession Session => session;

        public IServerSignIn SignIn { get; } = signIn;

        public IServerWipeSignal Wipe { get; } = wipe;

        public void Dispose()
        {
            session.Dispose();
            client.Dispose();
        }
    }
}
