using System.Diagnostics;
using OpenVpnPilot.Platform.MacOS.Shell;

namespace OpenVpnPilot.Platform.MacOS.Tests.Client;

/// <summary>
/// How the next copy is started when the application restarts itself on macOS.
/// </summary>
public sealed class RestartTests
{
    private const string Bundle = "/Applications/OpenVPN Pilot.app";
    private const string Executable = Bundle + "/Contents/MacOS/OpenVpnPilot";

    [Fact]
    public void Successor_FromABundle_IsOpenedAsANewInstanceWithTheOptions()
    {
        ProcessStartInfo start = SuccessorStart.For(Executable, Bundle, ["--after-restart", "4242"]);

        Assert.Equal("/usr/bin/open", start.FileName);

        // -n, or Launch Services brings the copy that is ending forward and starts nothing.
        Assert.Equal(["-n", "-a", Bundle, "--args", "--after-restart", "4242"], start.ArgumentList);
    }

    [Fact]
    public void Successor_FromABundleWithoutOptions_PassesNoArgumentsMarker()
    {
        ProcessStartInfo start = SuccessorStart.For(Executable, Bundle, []);

        Assert.Equal(["-n", "-a", Bundle], start.ArgumentList);
    }

    [Fact]
    public void Successor_WithoutABundle_IsTheExecutableItself()
    {
        const string built = "/Users/example/OpenVpnPilot/bin/Debug/net10.0/OpenVpnPilot";

        ProcessStartInfo start = SuccessorStart.For(built, bundle: null, ["--after-restart", "4242"]);

        Assert.Equal(built, start.FileName);
        Assert.Equal(["--after-restart", "4242"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }
}
