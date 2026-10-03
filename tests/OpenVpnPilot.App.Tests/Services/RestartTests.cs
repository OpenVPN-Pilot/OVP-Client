using System.Diagnostics;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Platform.Windows.Shell;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The two halves of a restart: how the next copy is started, and how it waits for this one.
/// </summary>
public sealed class RestartTests
{
    [Fact]
    public void WindowsSuccessor_IsTheExecutableAgainWithTheOptionsAsGiven()
    {
        string executable = Path.Combine(Path.GetTempPath(), "OpenVpnPilot", "OpenVpnPilot.exe");

        ProcessStartInfo start = SuccessorStart.ForExecutable(executable, ["--after-restart", "4242"]);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(["--after-restart", "4242"], start.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);

        // Through the shell, so the new copy does not inherit the handles of one that is ending.
        Assert.True(start.UseShellExecute);
    }

    [Fact]
    public void WaitFor_AProcessThatIsGone_DoesNotWait()
    {
        // Process identifiers are multiples of four on Windows and never this large anywhere.
        Stopwatch watch = Stopwatch.StartNew();

        Assert.True(PredecessorExit.WaitFor(int.MaxValue - 2, "OpenVpnPilot", TimeSpan.FromSeconds(10)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The identifier of an ended process may by now belong to something else entirely.
    /// </summary>
    [Fact]
    public void WaitFor_AProcessOfAnotherName_IsNotWaitedFor()
    {
        using Process current = Process.GetCurrentProcess();
        Stopwatch watch = Stopwatch.StartNew();

        Assert.True(PredecessorExit.WaitFor(current.Id, current.ProcessName + "-other", TimeSpan.FromSeconds(10)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WaitFor_ACopyThatDoesNotEnd_GivesUpWhenTheTimeIsUp()
    {
        using Process current = Process.GetCurrentProcess();

        Assert.False(PredecessorExit.WaitFor(current.Id, current.ProcessName, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void WaitFor_IsBounded()
    {
        Assert.True(PredecessorExit.Timeout > TimeSpan.FromSeconds(4), "Shorter than the teardown it waits for.");
        Assert.True(PredecessorExit.Timeout <= TimeSpan.FromMinutes(1), "Long enough to look like a hang.");
    }
}
