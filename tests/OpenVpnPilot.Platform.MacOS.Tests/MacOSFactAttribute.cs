namespace OpenVpnPilot.Platform.MacOS.Tests;

/// <summary>
/// A test that exercises the operating system itself and therefore only means something on macOS.
/// </summary>
/// <remarks>
/// Reported as skipped elsewhere rather than passing silently, so a run on Windows says plainly what
/// it did not cover.
/// </remarks>
public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Exercises macOS itself.";
        }
    }
}
