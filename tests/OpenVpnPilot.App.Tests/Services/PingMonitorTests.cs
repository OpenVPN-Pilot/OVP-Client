using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Which address a round trip is measured against.
/// </summary>
/// <remarks>
/// This decides whether the figure means anything. Falling back to the machine's own tunnel address
/// is what made every tunnel report one or two milliseconds: the local stack answers it without a
/// packet leaving, so the measurement was of nothing at all, and it looked like an excellent
/// connection while being it.
/// </remarks>
public sealed class PingMonitorTests
{
    [Fact]
    public void ChooseTarget_PrefersTheGatewayTheServerPushed()
    {
        (string? target, PingTargetKind kind) = PingMonitor.ChooseTarget(
            new PingCandidates("10.8.0.1", "10.8.0.6", "198.51.100.10"));

        Assert.Equal("10.8.0.1", target);
        Assert.Equal(PingTargetKind.TunnelGateway, kind);
    }

    [Fact]
    public void ChooseTarget_WithoutAGateway_MeasuresTheServerRatherThanThisMachine()
    {
        // The local address is deliberately one that no interface on the machine running this test
        // holds, so the routing table cannot answer and the server is what is left.
        (string? target, PingTargetKind kind) = PingMonitor.ChooseTarget(
            new PingCandidates(null, "203.0.113.6", "198.51.100.10"));

        Assert.Equal("198.51.100.10", target);
        Assert.Equal(PingTargetKind.ServerEndpoint, kind);
    }

    [Fact]
    public void ChooseTarget_WithNothingReachable_MeasuresNothing()
    {
        (string? target, PingTargetKind kind) = PingMonitor.ChooseTarget(
            new PingCandidates(null, null, null));

        Assert.Null(target);
        Assert.Equal(PingTargetKind.None, kind);
    }

    [Fact]
    public void ChooseTarget_NeverReturnsTheLocalTunnelAddress()
    {
        (string? target, _) = PingMonitor.ChooseTarget(
            new PingCandidates(null, "203.0.113.6", null));

        Assert.NotEqual("203.0.113.6", target);
    }
}
