using System.Text.Json.Serialization;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Tests.Server;

/// <summary>
/// The serialiser options are shared by the whole process, so nobody may change them.
/// </summary>
public sealed class ServerJsonTests
{
    [Fact]
    public void Options_AreReadOnly()
    {
        Assert.True(ServerJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => ServerJson.Options.DefaultIgnoreCondition = JsonIgnoreCondition.Always);
    }
}
