using System.Buffers.Binary;
using System.Text;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Tests.Helper;

/// <summary>
/// What a message that went over the socket is when it arrives.
/// </summary>
/// <remarks>
/// The helper is compiled ahead of time and therefore serialises through generated code rather than
/// reflection, and that code does not run the property initialisers of a message type. What a sender
/// leaves out arrives as null or zero, whatever default the record declares, so the value of a
/// message can never be assumed to be there. The first test here pins that down: it is the reason
/// the helper validates every member, and if a runtime ever changes it this test says so.
/// </remarks>
public sealed class HelperFramingTests
{
    [Fact]
    public async Task ReadRequest_AMemberTheSenderLeftOut_ArrivesAbsentAndNotAtItsDeclaredDefault()
    {
        HelperRequest request = await ReadAsync(
            """{"type":"launch","launch":{"configuration":"client\n","managementPort":25340}}""");

        LaunchSpecification launch = Assert.IsType<LaunchSpecification>(request.Launch);

        Assert.Null(launch.ManagementPassword);
        Assert.Null(launch.PullFilters);
        Assert.Equal(0, launch.Verbosity);
    }

    /// <summary>
    /// Not even the type of the message, which decides what is done with it.
    /// </summary>
    [Fact]
    public async Task ReadRequest_AMessageWithoutAType_ArrivesWithoutOne()
    {
        HelperRequest request = await ReadAsync("""{"launch":{"configuration":"client\n"}}""");

        Assert.Null(request.Type);
    }

    /// <summary>
    /// A filter is a record with two positional values, and both of them can be absent too.
    /// </summary>
    [Fact]
    public async Task ReadRequest_APullFilterWithoutItsValues_ArrivesWithoutThem()
    {
        HelperRequest request = await ReadAsync(
            """{"type":"launch","launch":{"pullFilters":[{},{"action":"ignore"}]}}""");

        IReadOnlyList<PullFilterSpecification> filters = Assert.IsType<LaunchSpecification>(request.Launch).PullFilters!;

        Assert.Null(filters[0].Action);
        Assert.Null(filters[0].Text);
        Assert.Null(filters[1].Text);
    }

    private static async Task<HelperRequest> ReadAsync(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        MemoryStream stream = new();
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        stream.Position = 0;

        return Assert.IsType<HelperRequest>(await HelperFraming.ReadRequestAsync(stream, CancellationToken.None));
    }
}
