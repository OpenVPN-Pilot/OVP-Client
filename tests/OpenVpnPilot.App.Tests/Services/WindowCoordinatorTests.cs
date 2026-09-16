using Avalonia.Controls;
using OpenVpnPilot.App.Services;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Covers what closing the main window means, which depends on who asked for it.
/// </summary>
/// <remarks>
/// Measured against Avalonia 12.1: ending the Windows session raises ShutdownRequested and then
/// closes every window with <see cref="WindowCloseReason.OSShutdown"/>, and a window that refuses
/// that close answers <c>WM_QUERYENDSESSION</c> with a veto, which stops the machine shutting down.
/// The application shutting itself down closes them with
/// <see cref="WindowCloseReason.ApplicationShutdown"/>, and shutting down in answer to that close
/// re-enters the handler until the stack runs out.
///
/// Both were observed. The preference only applies to the third reason, a person closing the window.
/// </remarks>
public sealed class WindowCoordinatorTests
{
    [Theory]
    [InlineData(WindowCloseReason.OSShutdown, true)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, true)]
    public void CloseThatIsNotAPreferenceProceeds(WindowCloseReason reason, bool closeToTray)
    {
        Assert.Equal(
            WindowCoordinator.CloseIntent.Proceed,
            WindowCoordinator.DecideClose(reason, closeToTray));
    }

    [Fact]
    public void ClosingTheWindowHidesItWhenThatIsTheSetting()
    {
        Assert.Equal(
            WindowCoordinator.CloseIntent.HideToTray,
            WindowCoordinator.DecideClose(WindowCloseReason.WindowClosing, closeToTray: true));
    }

    /// <summary>
    /// Task Manager and <c>taskkill</c> wait for the process to end. Hiding in answer made Task
    /// Manager report the application as not responding.
    /// </summary>
    [Fact]
    public void AnotherProgramAskingToClose_EndsTheApplicationWhateverTheSetting()
    {
        Assert.Equal(
            WindowCoordinator.CloseIntent.Quit,
            WindowCoordinator.DecideClose(WindowCloseReason.WindowClosing, closeToTray: true, askedFromWindow: false));
    }

    [Theory]
    [InlineData(0xF060, true)]
    [InlineData(0xF063, true)]
    [InlineData(0xF020, false)]
    public void TheCloseCommand_IsWhatMarksACloseFromTheWindow(int command, bool expected)
    {
        OpenVpnPilot.Platform.Windows.Shell.WindowsCloseOrigin origin = new();

        origin.Observe(0x0112, command);

        Assert.Equal(expected, origin.TakeAskedFromWindow());
        Assert.False(origin.TakeAskedFromWindow(), "An answer is given once per close.");
    }

    [Fact]
    public void ACloseMessageOnItsOwn_IsNotFromTheWindow()
    {
        OpenVpnPilot.Platform.Windows.Shell.WindowsCloseOrigin origin = new();

        origin.Observe(0x0010, 0);

        Assert.False(origin.TakeAskedFromWindow());
    }

    [Fact]
    public void ClosingTheWindowEndsTheApplicationWhenItIsNot()
    {
        Assert.Equal(
            WindowCoordinator.CloseIntent.Quit,
            WindowCoordinator.DecideClose(WindowCloseReason.WindowClosing, closeToTray: false));
    }
}
