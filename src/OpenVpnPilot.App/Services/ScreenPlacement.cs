using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// What is known about one display, in a form that is not the framework's own type.
/// </summary>
/// <remarks>
/// Avalonia's screen type cannot be constructed outside Avalonia, which would make every decision
/// taken from one untestable. The decisions are the part worth covering, so they are taken from
/// this and the framework type is read once, at the edge.
/// </remarks>
/// <param name="Name">The device name the platform reports, when it reports one.</param>
/// <param name="Bounds">The whole display.</param>
/// <param name="WorkingArea">What is left after the task bar.</param>
/// <param name="Scaling">Physical pixels per device independent pixel.</param>
public readonly record struct ScreenInfo(
    string? Name,
    PixelRect Bounds,
    PixelRect WorkingArea,
    double Scaling)
{
    public static ScreenInfo From(Screen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return new ScreenInfo(screen.DisplayName, screen.Bounds, screen.WorkingArea, screen.Scaling);
    }

    public static IReadOnlyList<ScreenInfo> From(IReadOnlyList<Screen> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return [.. screens.Select(From)];
    }
}

/// <summary>
/// Decides which display a window opens on, and remembers the answer.
/// </summary>
/// <remarks>
/// A palette opened by a shortcut has no window to be relative to, so it lands wherever the platform
/// decides, and on a multi head machine that is regularly the wrong screen. This lets the user move
/// it once and have it stay there.
///
/// A display is identified by its name together with the corner it occupies, never by its index. An
/// index is the order the system happens to enumerate them in: unplugging a monitor, or docking,
/// renumbers the rest and the palette would open somewhere nobody chose. The corner is included
/// because two identical monitors report the same name.
///
/// Everything here degrades to doing nothing. A remembered display that is not present, a placement
/// that is off every screen, a platform that reports no displays at all: each of those means the
/// window opens where it would have anyway.
/// </remarks>
public static class ScreenPlacement
{
    /// <summary>
    /// The identity stored in the settings for one display.
    /// </summary>
    public static string Identify(ScreenInfo screen) => string.Create(
        CultureInfo.InvariantCulture,
        $"{screen.Name}|{screen.Bounds.X},{screen.Bounds.Y}");

    /// <summary>
    /// Finds the display an identity refers to, preferring an exact match over the name alone.
    /// </summary>
    /// <remarks>
    /// The corner can change without the display changing, because rearranging the monitors moves
    /// every origin but the primary one. Matching the name alone afterwards is what keeps the choice
    /// alive across a rearrangement.
    /// </remarks>
    public static ScreenInfo? Match(IReadOnlyList<ScreenInfo> screens, string? identity)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (string.IsNullOrEmpty(identity))
        {
            return null;
        }

        foreach (ScreenInfo screen in screens)
        {
            if (Identify(screen) == identity)
            {
                return screen;
            }
        }

        string name = identity.Split('|')[0];

        if (name.Length == 0)
        {
            return null;
        }

        foreach (ScreenInfo screen in screens)
        {
            if (screen.Name == name)
            {
                return screen;
            }
        }

        return null;
    }

    /// <summary>
    /// The display before or after the given one, wrapping around.
    /// </summary>
    /// <remarks>
    /// Ordered left to right by where the displays actually are rather than by the order the system
    /// enumerates them, so that a shortcut meaning "the next one to the right" moves right.
    /// </remarks>
    public static ScreenInfo? Step(IReadOnlyList<ScreenInfo> screens, ScreenInfo? current, int direction)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (screens.Count == 0)
        {
            return null;
        }

        List<ScreenInfo> ordered = [.. screens
            .OrderBy(screen => screen.Bounds.X)
            .ThenBy(screen => screen.Bounds.Y)];

        int index = current is { } from
            ? ordered.FindIndex(screen => screen.Bounds == from.Bounds)
            : 0;

        if (index < 0)
        {
            index = 0;
        }

        int next = (((index + direction) % ordered.Count) + ordered.Count) % ordered.Count;
        return ordered[next];
    }

    /// <summary>
    /// Centres a window on a display, in that display's own scaling.
    /// </summary>
    /// <remarks>
    /// A window's size is in device independent pixels and a screen's bounds are in physical ones,
    /// so the size has to be scaled before it can be centred. Without that a window is placed
    /// noticeably off centre on any display that is not at one hundred percent.
    /// </remarks>
    public static PixelPoint CentreOf(ScreenInfo screen, double width, double height)
    {
        PixelRect area = screen.WorkingArea;
        double scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        return new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)(width * scaling)) / 2),
            area.Y + Math.Max(0, (area.Height - (int)(height * scaling)) / 2));
    }

    /// <summary>
    /// Places a window in the middle of a display.
    /// </summary>
    public static void CentreOn(Window window, ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Position = CentreOf(screen, window.Width, window.Height);
    }

    /// <summary>
    /// True when a recorded placement can still be applied, which means its corner is on a display.
    /// </summary>
    /// <remarks>
    /// The check matters. A window restored onto a monitor that has been unplugged is off screen,
    /// cannot be moved back with the mouse, and looks exactly like an application that failed to
    /// start.
    /// </remarks>
    public static bool IsOnScreen(WindowPlacementSettings placement, IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(screens);

        if (!placement.HasPosition)
        {
            return false;
        }

        PixelPoint position = new(placement.X!.Value, placement.Y!.Value);

        return screens.Any(screen => screen.Bounds.Contains(position));
    }

    /// <summary>
    /// Restores a recorded placement, provided it still lands on a display that exists.
    /// </summary>
    /// <returns>True when the placement was applied.</returns>
    public static bool Restore(
        Window window,
        WindowPlacementSettings placement,
        IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsOnScreen(placement, screens))
        {
            return false;
        }

        if (placement.Width is > 0 && placement.Height is > 0)
        {
            window.Width = placement.Width.Value;
            window.Height = placement.Height.Value;
        }

        window.Position = new PixelPoint(placement.X!.Value, placement.Y!.Value);
        window.WindowState = placement.Maximised ? WindowState.Maximized : WindowState.Normal;

        return true;
    }

    /// <summary>
    /// Reads a window's current placement, ignoring a size taken while it is maximised.
    /// </summary>
    /// <remarks>
    /// A maximised window reports the size of the screen. Recording that as the restored size means
    /// that un-maximising it after the next start does nothing visible, so only the flag is kept and
    /// whatever size was recorded before stays.
    /// </remarks>
    public static WindowPlacementSettings Capture(Window window, WindowPlacementSettings previous)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(previous);

        return Capture(
            window.WindowState,
            window.Position,
            (int)window.Width,
            (int)window.Height,
            previous);
    }

    /// <summary>
    /// The same decision, without a window, so that it can be covered.
    /// </summary>
    public static WindowPlacementSettings Capture(
        WindowState state,
        PixelPoint position,
        int width,
        int height,
        WindowPlacementSettings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        if (state == WindowState.Maximized)
        {
            WindowPlacementSettings kept = previous.Clone();
            kept.Maximised = true;
            return kept;
        }

        if (state == WindowState.Minimized)
        {
            return previous;
        }

        return new WindowPlacementSettings
        {
            X = position.X,
            Y = position.Y,
            Width = width,
            Height = height,
            Maximised = false,
        };
    }
}
