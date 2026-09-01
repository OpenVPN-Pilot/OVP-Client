using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Measures the round trip of each tunnel, continuously, against an address it names.
/// </summary>
/// <remarks>
/// The target is chosen in three steps and never invented. The tunnel gateway the server pushed is
/// the best answer, because it is the far end of the tunnel and the figure then describes the tunnel
/// itself. A server that pushes no gateway still leaves one behind in the routing table, which is
/// the second step. Only when neither exists is the server's public address measured, over the
/// ordinary route, which at least answers the question of how far away the endpoint is.
///
/// What is never measured is this machine's own tunnel address. The local stack answers it without a
/// packet leaving the host, so it reports one or two milliseconds for every tunnel on earth. That is
/// what this used to do, and the figure was believed because it looked like a very good connection.
///
/// Each measurement is three echoes rather than one, and the median is published. A single echo that
/// happens to land during a retransmission is otherwise shown as the connection's quality until the
/// next cycle.
/// </remarks>
public sealed class PingMonitor : IAsyncDisposable
{
    /// <summary>
    /// How long to wait for a reply. A tunnel that is slower than this is unusable anyway.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Echoes per measurement. Three is enough for a median and cheap enough to repeat often.
    /// </summary>
    private const int EchoesPerMeasurement = 3;

    /// <summary>
    /// Gap between the echoes of one measurement, so they do not share a single moment of trouble.
    /// </summary>
    private static readonly TimeSpan EchoSpacing = TimeSpan.FromMilliseconds(200);

    private readonly ConnectionManager connections;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PingMonitor> logger;
    private readonly CancellationTokenSource lifetime = new();

    private Task? loop;
    private bool disposed;

    public PingMonitor(
        ConnectionManager connections,
        TimeProvider timeProvider,
        ILogger<PingMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// How often each active tunnel is measured.
    /// </summary>
    /// <remarks>
    /// The measurements themselves take time, so with many tunnels a cycle lasts longer than this
    /// and the interval becomes a floor rather than a period. That is the right way round: the
    /// figures stay current for one tunnel and cost nothing extra for twenty.
    /// </remarks>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    public void Start() => loop ??= Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, timeProvider, cancellationToken);
                await MeasureAllAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MeasureAllAsync(CancellationToken cancellationToken)
    {
        foreach (Guid profileId in connections.ActiveProfiles)
        {
            if (connections.GetPingCandidates(profileId) is not { } candidates)
            {
                continue;
            }

            (string? target, PingTargetKind kind) = ChooseTarget(candidates);

            if (target is null)
            {
                connections.ReportPing(profileId, null, null, PingTargetKind.None);
                continue;
            }

            double? measured = await MeasureAsync(target, cancellationToken);
            connections.ReportPing(profileId, measured, target, kind);
        }
    }

    /// <summary>
    /// Picks the address worth measuring, and says what that address is.
    /// </summary>
    internal static (string? Target, PingTargetKind Kind) ChooseTarget(PingCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Gateway is { Length: > 0 } pushed)
        {
            return (pushed, PingTargetKind.TunnelGateway);
        }

        if (candidates.LocalAddress is { Length: > 0 } local
            && GatewayOfInterfaceHolding(local) is { } resolved)
        {
            return (resolved, PingTargetKind.TunnelGateway);
        }

        if (candidates.ServerAddress is { Length: > 0 } server)
        {
            return (server, PingTargetKind.ServerEndpoint);
        }

        return (null, PingTargetKind.None);
    }

    /// <summary>
    /// Reads the gateway of the interface that holds the given address.
    /// </summary>
    /// <remarks>
    /// This is how a tunnel whose server pushes no <c>route-gateway</c> still gets measured against
    /// its far end: OpenVPN configures the interface either way, so the address is in the routing
    /// table even when it never appeared in a push reply.
    /// </remarks>
    private static string? GatewayOfInterfaceHolding(string localAddress)
    {
        if (!IPAddress.TryParse(localAddress, out IPAddress? local))
        {
            return null;
        }

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();

                if (!properties.UnicastAddresses.Any(entry => entry.Address.Equals(local)))
                {
                    continue;
                }

                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    // A gateway of 0.0.0.0 is how an unnumbered point to point interface is
                    // reported. It is not an address anything answers.
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork
                        && !gateway.Address.Equals(IPAddress.Any))
                    {
                        return gateway.Address.ToString();
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The interface list could not be read. There is still the server address to fall back
            // on, so this is a step that did not work rather than a failure worth reporting.
            return null;
        }

        return null;
    }

    /// <summary>
    /// Sends several echoes and returns the median of the replies, or null when nothing answered.
    /// </summary>
    private async Task<double?> MeasureAsync(string target, CancellationToken cancellationToken)
    {
        using Ping ping = new();

        List<long> replies = [];

        for (int attempt = 0; attempt < EchoesPerMeasurement; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(EchoSpacing, timeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                PingReply reply = await ping.SendPingAsync(target, Timeout, cancellationToken: cancellationToken);

                if (reply.Status == IPStatus.Success)
                {
                    replies.Add(reply.RoundtripTime);
                }
            }
            catch (PingException exception)
            {
                // An address that cannot be resolved or reached is a normal outcome here, not a
                // fault. Nothing is gained by asking twice, so the measurement ends.
                PingLog.MeasurementFailed(logger, target, exception);
                return null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (replies.Count == 0)
        {
            return null;
        }

        replies.Sort();
        return replies[replies.Count / 2];
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        lifetime.Dispose();
    }
}

/// <summary>
/// Source generated log messages for <see cref="PingMonitor"/>.
/// </summary>
internal static partial class PingLog
{
    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Debug,
        Message = "The round trip to {Target} could not be measured.")]
    public static partial void MeasurementFailed(ILogger logger, string target, Exception exception);
}
