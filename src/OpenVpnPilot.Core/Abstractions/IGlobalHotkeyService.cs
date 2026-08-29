using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Registers shortcuts that work while another application has the focus.
/// </summary>
/// <remarks>
/// A registration can fail because another application already owns the combination, and that has to
/// reach the user: a shortcut that quietly does nothing is worse than one that is reported as taken.
/// The result therefore carries the reason rather than being a plain boolean.
/// </remarks>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>
    /// False when this platform offers no system wide shortcuts.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Claims a combination for an action. Must be called from the user interface thread.
    /// </summary>
    /// <param name="actionId">Reported back when the shortcut is pressed.</param>
    public HotkeyRegistration Register(string actionId, HotkeyGesture gesture);

    /// <summary>
    /// Releases every combination claimed so far.
    /// </summary>
    public void UnregisterAll();

    /// <summary>
    /// Raised with the action identifier when a registered combination is pressed.
    /// </summary>
    public event EventHandler<string>? Pressed;
}

/// <summary>
/// The outcome of one registration attempt.
/// </summary>
/// <param name="ActionId">The action the combination was claimed for.</param>
/// <param name="Gesture">The combination that was attempted.</param>
/// <param name="Succeeded">False when the combination could not be claimed.</param>
/// <param name="Detail">
/// Why it failed, in factual terms. Empty on success. The wording shown to the user is localized in
/// the presentation layer.
/// </param>
public sealed record HotkeyRegistration(
    string ActionId,
    HotkeyGesture Gesture,
    bool Succeeded,
    string Detail = "");

/// <summary>
/// A key combination, in a form that can be stored and read back.
/// </summary>
/// <remarks>
/// The key is a name rather than a platform key code, so a stored binding survives a change of
/// platform and stays readable in the settings file. Mapping the name onto a key code is the
/// platform implementation's job.
/// </remarks>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>
    /// Reads the textual form, for example Control+Alt+V.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        string? key = null;

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;

                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;

                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;

                case "win" or "windows" or "meta" or "cmd":
                    modifiers |= HotkeyModifiers.Windows;
                    break;

                default:
                    // The last part that is not a modifier is the key. A second one is a mistake.
                    if (key is not null)
                    {
                        return false;
                    }

                    key = part;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>
    /// True when the combination has at least one modifier.
    /// </summary>
    /// <remarks>
    /// A bare key registered system wide would take that key away from every other application, so
    /// it is refused rather than offered and regretted.
    /// </remarks>
    public bool IsUsable => Modifiers != HotkeyModifiers.None && Key.Length > 0;

    public override string ToString()
    {
        StringBuilder builder = new();

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            builder.Append("Control+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            builder.Append("Windows+");
        }

        return builder.Append(Key).ToString();
    }
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// The actions a shortcut can be bound to.
/// </summary>
/// <remarks>
/// Identifiers are stored in the database, so the names are part of the persisted format and must
/// not be renamed without a migration.
/// </remarks>
public static class HotkeyActions
{
    public const string ToggleQuickSwitcher = "ToggleQuickSwitcher";
    public const string ShowMainWindow = "ShowMainWindow";
    public const string ConnectLastUsed = "ConnectLastUsed";
    public const string ReconnectActive = "ReconnectActive";
    public const string DisconnectActive = "DisconnectActive";
    public const string DisconnectAll = "DisconnectAll";

    /// <summary>
    /// Identifier for the shortcut that connects the profile in the given favourite slot.
    /// </summary>
    public static string ConnectFavourite(int slot) =>
        slot is < 1 or > 9
            ? throw new ArgumentOutOfRangeException(nameof(slot), slot, "Favourite slots run from one to nine.")
            : $"ConnectFavourite{slot}";

    /// <summary>
    /// The favourite slot an identifier refers to, or null when it is not a favourite shortcut.
    /// </summary>
    public static int? FavouriteSlotOf(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);

        const string prefix = "ConnectFavourite";

        return actionId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(actionId.AsSpan(prefix.Length), out int slot)
            && slot is >= 1 and <= 9
                ? slot
                : null;
    }

    /// <summary>
    /// Every action, in the order a settings screen should list them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ToggleQuickSwitcher,
        ShowMainWindow,
        ConnectLastUsed,
        ReconnectActive,
        DisconnectActive,
        DisconnectAll,
        ConnectFavourite(1),
        ConnectFavourite(2),
        ConnectFavourite(3),
        ConnectFavourite(4),
        ConnectFavourite(5),
        ConnectFavourite(6),
        ConnectFavourite(7),
        ConnectFavourite(8),
        ConnectFavourite(9),
    ];

    /// <summary>
    /// The combinations offered on a fresh installation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        [ToggleQuickSwitcher] = "Control+Alt+V",
        [DisconnectAll] = "Control+Alt+D",
    };
}
