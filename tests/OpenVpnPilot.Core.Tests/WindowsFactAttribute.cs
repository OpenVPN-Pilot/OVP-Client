namespace OpenVpnPilot.Core.Tests;

/// <summary>
/// A test that depends on how Windows shares an open file and therefore only means something there.
/// </summary>
/// <remarks>
/// Reported as skipped elsewhere rather than passing silently, so a run on macOS says plainly what it
/// did not cover.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Depends on Windows file sharing.";
        }
    }
}
