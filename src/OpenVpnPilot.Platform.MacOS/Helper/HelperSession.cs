using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// The one session this process holds with the privileged helper.
/// </summary>
/// <remarks>
/// The helper ties every tunnel to the session that started it and ends them when that session
/// closes. Holding one session for the life of the process is therefore what makes a crash of the
/// application end its tunnels instead of leaving them running with nobody who knows their
/// management password, which is exactly the orphan the Windows client cannot prevent.
///
/// The session is opened on first use rather than at startup, so a machine without the helper still
/// starts and explains what is missing, and it is opened again after the helper went away, which
/// took the tunnels of the old session with it.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class HelperSession : IAsyncDisposable
{
    private readonly string socketPath;
    private readonly ILogger<HelperSession> logger;
    private readonly SemaphoreSlim opening = new(1, 1);
    private HelperConnection? connection;
    private bool disposed;

    public HelperSession(ILogger<HelperSession>? logger = null, string socketPath = HelperInstallation.SocketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        this.socketPath = socketPath;
        this.logger = logger ?? NullLogger<HelperSession>.Instance;
    }

    /// <summary>
    /// The name this process gives itself, for the helper's record.
    /// </summary>
    public static string ClientName
    {
        get
        {
            Assembly entry = Assembly.GetEntryAssembly() ?? typeof(HelperSession).Assembly;
            AssemblyName name = entry.GetName();
            return $"{name.Name} {name.Version}";
        }
    }

    /// <summary>
    /// Sends a request over the session, opening it first when needed.
    /// </summary>
    /// <exception cref="HelperUnavailableException">The helper cannot be reached.</exception>
    /// <exception cref="HelperProtocolException">The helper speaks another version.</exception>
    public async Task<HelperResponse> SendAsync(
        HelperRequest request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        HelperConnection open = await EnsureOpenAsync(cancellationToken);
        return await open.ExchangeAsync(request, cancellationToken, timeout);
    }

    /// <summary>
    /// What the helper said when the current session began, opening one when there is none.
    /// </summary>
    public async Task<HelperResponse> GreetingAsync(CancellationToken cancellationToken) =>
        (await EnsureOpenAsync(cancellationToken)).Greeting;

    private async Task<HelperConnection> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await opening.WaitAsync(cancellationToken);

        try
        {
            if (connection is { IsOpen: true } current)
            {
                return current;
            }

            if (connection is not null)
            {
                // The helper went away, and the tunnels of that session went with it.
                HelperSessionLog.SessionLost(logger);
                await connection.DisposeAsync();
                connection = null;
            }

            connection = await HelperConnection.OpenAsync(ClientName, socketPath, cancellationToken);

            HelperSessionLog.SessionOpened(
                logger,
                connection.Greeting.HelperVersion ?? "unknown",
                connection.Greeting.OpenVpnVersion ?? "missing",
                connection.Greeting.Authorisation ?? "unknown");

            return connection;
        }
        finally
        {
            opening.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }
    }
}

/// <summary>
/// Source generated log messages for the helper session.
/// </summary>
internal static partial class HelperSessionLog
{
    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Information,
        Message = "Session with the helper opened: helper {HelperVersion}, OpenVPN {OpenVpnVersion}, authorised as {Authorisation}.")]
    public static partial void SessionOpened(ILogger logger, string helperVersion, string openVpnVersion, string authorisation);

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Warning,
        Message = "The session with the helper was lost. Tunnels it had started have ended with it.")]
    public static partial void SessionLost(ILogger logger);
}
