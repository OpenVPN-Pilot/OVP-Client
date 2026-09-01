using Avalonia;
using Avalonia.Controls;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Which display the quick menus open on, and what happens when that display is gone.
/// </summary>
/// <remarks>
/// The point of not storing an index is here: unplugging a monitor renumbers the rest, and a palette
/// that opened on "the second one" would then open somewhere nobody chose. Matching by name survives
/// a rearrangement, and matching nothing at all has to be harmless rather than a window placed off
/// every screen.
/// </remarks>
public sealed class ScreenPlacementTests
{
    private static readonly ScreenInfo Left = Display("DISPLAY1", 0);
    private static readonly ScreenInfo Right = Display("DISPLAY2", 1920);
    private static readonly ScreenInfo Far = Display("DISPLAY3", 3840);

    private static readonly ScreenInfo[] All = [Left, Right, Far];

    [Fact]
    public void Match_FindsTheDisplayItWasRecordedFrom() =>
        Assert.Equal(Right, ScreenPlacement.Match(All, ScreenPlacement.Identify(Right)));

    [Fact]
    public void Match_AfterTheDisplaysWereRearranged_StillFindsItByName()
    {
        string recorded = ScreenPlacement.Identify(Right);
        ScreenInfo moved = Display("DISPLAY2", -1920);

        Assert.Equal(moved, ScreenPlacement.Match([Left, moved], recorded));
    }

    [Fact]
    public void Match_WhenTheDisplayIsGone_ChoosesNothing() =>
        Assert.Null(ScreenPlacement.Match([Left], ScreenPlacement.Identify(Right)));

    [Fact]
    public void Match_WithNothingRecorded_ChoosesNothing()
    {
        Assert.Null(ScreenPlacement.Match(All, null));
        Assert.Null(ScreenPlacement.Match(All, string.Empty));
    }

    [Fact]
    public void Step_MovesLeftAndRightInTheOrderTheDisplaysAreArranged()
    {
        Assert.Equal(Far, ScreenPlacement.Step(All, Right, 1));
        Assert.Equal(Left, ScreenPlacement.Step(All, Right, -1));
    }

    [Fact]
    public void Step_WrapsAroundAtEitherEnd()
    {
        Assert.Equal(Left, ScreenPlacement.Step(All, Far, 1));
        Assert.Equal(Far, ScreenPlacement.Step(All, Left, -1));
    }

    [Fact]
    public void Step_WithOneDisplay_StaysWhereItIs() =>
        Assert.Equal(Left, ScreenPlacement.Step([Left], Left, 1));

    [Fact]
    public void Step_WithNoDisplays_ChoosesNothing() =>
        Assert.Null(ScreenPlacement.Step([], null, 1));

    [Fact]
    public void CentreOf_PlacesAWindowInTheMiddleOfTheDisplayItIsOn()
    {
        PixelPoint centre = ScreenPlacement.CentreOf(Right, 620, 420);

        Assert.Equal(1920 + ((1920 - 620) / 2), centre.X);
        Assert.Equal((1080 - 420) / 2, centre.Y);
    }

    [Fact]
    public void CentreOf_OnAScaledDisplay_AccountsForTheScaling()
    {
        ScreenInfo scaled = Left with { Scaling = 2 };

        // The window is 620 wide in device independent pixels, which is 1240 physical ones.
        Assert.Equal((1920 - 1240) / 2, ScreenPlacement.CentreOf(scaled, 620, 420).X);
    }

    [Fact]
    public void IsOnScreen_RefusesAPlacementOnADisplayThatIsGone()
    {
        WindowPlacementSettings placement = new() { X = 2400, Y = 300, Width = 900, Height = 600 };

        Assert.True(ScreenPlacement.IsOnScreen(placement, All));
        Assert.False(ScreenPlacement.IsOnScreen(placement, [Left]));
    }

    [Fact]
    public void IsOnScreen_WithNothingRecorded_RefusesRatherThanGuessing() =>
        Assert.False(ScreenPlacement.IsOnScreen(new WindowPlacementSettings(), All));

    [Fact]
    public void Capture_WhileMaximised_KeepsTheSizeTheWindowWouldReturnTo()
    {
        WindowPlacementSettings previous = new() { X = 100, Y = 80, Width = 1180, Height = 720 };

        WindowPlacementSettings captured = ScreenPlacement.Capture(
            WindowState.Maximized,
            new PixelPoint(0, 0),
            1920,
            1080,
            previous);

        Assert.True(captured.Maximised);
        Assert.Equal(1180, captured.Width);
        Assert.Equal(720, captured.Height);
        Assert.Equal(100, captured.X);
    }

    [Fact]
    public void Capture_WhileMinimised_ChangesNothing()
    {
        WindowPlacementSettings previous = new() { X = 100, Y = 80, Width = 1180, Height = 720 };

        Assert.Same(
            previous,
            ScreenPlacement.Capture(WindowState.Minimized, new PixelPoint(-32000, -32000), 0, 0, previous));
    }

    [Fact]
    public void Capture_WhileOrdinary_RecordsWhereTheWindowIs()
    {
        WindowPlacementSettings captured = ScreenPlacement.Capture(
            WindowState.Normal,
            new PixelPoint(2000, 140),
            1000,
            640,
            new WindowPlacementSettings());

        Assert.Equal(2000, captured.X);
        Assert.Equal(140, captured.Y);
        Assert.Equal(1000, captured.Width);
        Assert.Equal(640, captured.Height);
        Assert.False(captured.Maximised);
    }

    private static ScreenInfo Display(string name, int x) => new(
        name,
        new PixelRect(x, 0, 1920, 1080),
        new PixelRect(x, 0, 1920, 1080),
        1);
}
