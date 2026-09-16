using System.Globalization;

namespace OpenVpnPilot.App.Views;

/// <summary>
/// What a drag of profiles carries, written and read as text.
/// </summary>
/// <remarks>
/// Text rather than the objects themselves, because a drag that does not reach the platform is not a
/// drag on macOS. Avalonia documents an in process format as never being serialized to a platform
/// drag-and-drop operation, and the macOS backend builds the dragging session out of exactly what was
/// serialized: with nothing to serialize the pasteboard is empty, AppKit refuses to begin a session
/// with no items by raising, and an Objective-C exception raised under the run loop ends the process.
/// Press and drag on a profile did that every time. Windows keeps its data object inside the process
/// and never noticed.
///
/// It is its own type so the two halves can be tested without a window.
/// </remarks>
internal static class DraggedProfiles
{
    /// <summary>
    /// The name the drag format goes by.
    /// </summary>
    /// <remarks>
    /// Avalonia namespaces this rather than passing it to the platform, so it cannot collide with a
    /// system type, and it accepts letters, digits, dots and hyphens and nothing else. Anything else
    /// is refused by throwing, and the format is built in a static constructor, so the window cannot
    /// be created at all: the first attempt used a slash and the application would not start.
    /// </remarks>
    public const string FormatIdentifier = "org.openvpnpilot.profiles";

    /// <summary>
    /// The separator. A space cannot appear inside the identifiers, which carry no punctuation in
    /// this form.
    /// </summary>
    private const char Separator = ' ';

    public static string Format(IReadOnlyList<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        return string.Join(
            Separator,
            profileIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads the identifiers back, keeping only what is one.
    /// </summary>
    /// <remarks>
    /// This comes off the platform and therefore from outside this process. Anything that is not an
    /// identifier is dropped rather than being allowed to fail the whole drop, and a drop that turns
    /// out to name nothing does nothing.
    /// </remarks>
    public static List<Guid> Parse(string? dragged)
    {
        if (string.IsNullOrWhiteSpace(dragged))
        {
            return [];
        }

        return dragged
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Guid.TryParseExact(part, "N", out Guid id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToList();
    }
}
