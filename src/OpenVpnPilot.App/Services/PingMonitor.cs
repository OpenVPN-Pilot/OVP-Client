using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Measures the round trip to the far end of each tunnel.
/// </summary>
/// <remarks>
/// A measurement is only meaningful against the other end of the tunnel, so the pushed gateway is
/// the target and the server's public address is not: that one would be reached over the ordinary
/// route and would say nothing about the tunnel at all.
///
/// Plenty of servers do not answer an echo request. That is reported as no reply rather than as a
/// round trip of zero, because zero reads as an unusually good result and would be believed.
/// </remarks>
public sealed class PingMonitor : IAsyncDisposable
{
    /// <summary>
    /// How long to wait for a reply. A tunnel that is slower than this is unusable anyway.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

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
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);

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
            string? target = connections.GetPingTarget(profileId);

            if (target is null)
            {
                continue;
            }

            connections.ReportPing(profileId, await MeasureAsync(target, cancellationToken));
        }
    }

    private async Task<double?> MeasureAsync(string target, CancellationToken cancellationToken)
    {
        using Ping ping = new();

        try
        {
            PingReply reply = await ping.SendPingAsync(target, Timeout, cancellationToken: cancellationToken);

            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException exception)
        {
            // An address that cannot be resolved or reached is a normal outcome here, not a fault.
            PingLog.MeasurementFailed(logger, target, exception);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
