using System.Net;
using System.Net.Sockets;

namespace OpenVpnPilot.OpenVpn.Runtime;

/// <summary>
/// Opens the transport to a running OpenVPN process's management interface.
/// </summary>
/// <remarks>
/// Abstracted so that the connection supervisor can be tested without starting a real process.
/// </remarks>
public interface IManagementChannelFactory
{
    public Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken);
}

/// <summary>
/// Connects over loopback TCP, retrying until the process starts listening.
/// </summary>
/// <remarks>
/// OpenVPN opens the management socket only after it has parsed its options, so a short retry window
/// is normal. Exhausting it almost always means the process exited during option parsing.
/// </remarks>
public sealed class TcpManagementChannelFactory : IManagementChannelFactory
{
    private readonly TimeSpan timeout;
    private readonly TimeSpan retryInterval;

    public TcpManagementChannelFactory(TimeSpan? timeout = null, TimeSpan? retryInterval = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        this.retryInterval = retryInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TcpClient client = new();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return client.GetStream();
            }
            catch (SocketException)
            {
                client.Dispose();

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new ManagementUnavailableException(port);
                }

                await Task.Delay(retryInterval, cancellationToken);
            }
        }
    }
}

/// <summary>
/// Raised when the management interface never started listening.
/// </summary>
public sealed class ManagementUnavailableException : Exception
{
    public ManagementUnavailableException(int port)
        : base($"The management interface on port {port} never started listening. "
            + "OpenVPN usually exits during option parsing when this happens.")
    {
        Port = port;
    }

    public ManagementUnavailableException()
        : base("The management interface never started listening.")
    {
    }

    public ManagementUnavailableException(string message)
        : base(message)
    {
    }

    public ManagementUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Port { get; }
}
