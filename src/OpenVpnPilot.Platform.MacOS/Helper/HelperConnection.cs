using System.Net.Sockets;
using System.Runtime.Versioning;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// One connection to the privileged helper, which the helper treats as one session.
/// </summary>
/// <remarks>
/// Requests are answered in order and one at a time, so the connection sends the next only after
/// the previous one was answered. Every wait is bounded: a helper that stops answering must end in a
/// clear refusal rather than a tunnel that shows as launching for as long as the application lives.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class HelperConnection : IAsyncDisposable
{
    /// <summary>
    /// How long connecting to the socket may take. launchd starts the helper on the first
    /// connection, so this covers that start as well.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim exchange = new(1, 1);
    private bool disposed;

    private HelperConnection(Socket socket, HelperResponse greeting)
    {
        this.socket = socket;
        stream = new NetworkStream(socket, ownsSocket: false);
        Greeting = greeting;
    }

    /// <summary>
    /// What the helper said about itself and about the caller when the session began.
    /// </summary>
    public HelperResponse Greeting { get; private set; }

    /// <summary>
    /// False once the helper closed the connection or an exchange failed.
    /// </summary>
    public bool IsOpen => !disposed && socket.Connected;

    /// <summary>
    /// Connects and agrees the protocol version.
    /// </summary>
    /// <exception cref="HelperUnavailableException">Nothing answers on the socket.</exception>
    /// <exception cref="HelperProtocolException">The helper speaks another version or no protocol at all.</exception>
    public static async Task<HelperConnection> OpenAsync(
        string clientName,
        string socketPath = HelperInstallation.SocketPath,
        CancellationToken cancellationToken = default)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), connect.Token);
            }
            catch (SocketException exception)
            {
                throw new HelperUnavailableException(
                    $"The helper socket {socketPath} does not answer: {exception.SocketErrorCode}.",
                    exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HelperUnavailableException(
                    $"The helper socket {socketPath} did not accept a connection within {ConnectTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            HelperConnection connection = new(socket, new HelperResponse());

            HelperResponse greeting = await connection.ExchangeAsync(
                new HelperRequest
                {
                    Type = HelperMessageType.Hello,
                    ProtocolVersion = HelperProtocol.Version,
                    ClientName = clientName,
                },
                cancellationToken);

            if (greeting.Type == HelperMessageType.Refused)
            {
                await connection.DisposeAsync();
                throw new HelperProtocolException(greeting.Message ?? "The helper refused the session.");
            }

            if (greeting.ProtocolVersion != HelperProtocol.Version)
            {
                await connection.DisposeAsync();
                throw new HelperProtocolException(
                    $"The helper speaks protocol {greeting.ProtocolVersion} and this client {HelperProtocol.Version}.");
            }

            connection.Greeting = greeting;
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends one request and waits for its answer, within the given time.
    /// </summary>
    /// <exception cref="HelperUnavailableException">The connection ended or the answer did not come in time.</exception>
    public async Task<HelperResponse> ExchangeAsync(
        HelperRequest request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);

        await exchange.WaitAsync(cancellationToken);

        try
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

            try
            {
                await HelperFraming.WriteRequestAsync(stream, request, bounded.Token);

                return await HelperFraming.ReadResponseAsync(stream, bounded.Token)
                    ?? throw new HelperUnavailableException("The helper closed the session.");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The session is unusable once an answer is overdue: a late one would be read as the
                // answer to the next request.
                socket.Close();
                throw new HelperUnavailableException("The helper did not answer in time.", exception);
            }
            catch (IOException exception)
            {
                throw new HelperUnavailableException("The connection to the helper broke.", exception);
            }
        }
        finally
        {
            exchange.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await stream.DisposeAsync();
        socket.Dispose();
    }
}

/// <summary>
/// The helper cannot be reached, or stopped answering.
/// </summary>
public sealed class HelperUnavailableException : Exception
{
    public HelperUnavailableException()
    {
    }

    public HelperUnavailableException(string message)
        : base(message)
    {
    }

    public HelperUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
