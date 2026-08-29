using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// System wide shortcuts, registered against a window that exists only to receive them.
/// </summary>
/// <remarks>
/// Windows delivers a shortcut to the window that claimed it, and only one application may hold a
/// combination at a time. A refused registration is reported with the reason rather than dropped,
/// because the usual cause is another application already owning the combination and the user is the
/// only one who can resolve that.
///
/// The no repeat flag is set so that holding the keys down produces one action rather than a stream
/// of connection attempts.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Lock gate = new();
    private readonly Dictionary<int, string> registered = [];

    private MessageWindow? window;
    private int nextId = 1;
    private bool disposed;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public event EventHandler<string>? Pressed;

    public HotkeyRegistration Register(string actionId, HotkeyGesture gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(gesture);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!gesture.IsUsable)
        {
            return new HotkeyRegistration(
                actionId,
                gesture,
                Succeeded: false,
                "A system wide shortcut needs at least one modifier key.");
        }

        if (!VirtualKeys.TryResolve(gesture.Key, out uint virtualKey))
        {
            return new HotkeyRegistration(
                actionId,
                gesture,
                Succeeded: false,
                $"'{gesture.Key}' is not a key this platform recognises.");
        }

        lock (gate)
        {
            window ??= new MessageWindow(
                "OpenVpnPilot.HotkeyWindow",
                HandleMessage,
                canBecomeForeground: false);

            int id = nextId++;

            if (NativeMethods.RegisterHotKey(window.Handle, id, ToModifiers(gesture.Modifiers), virtualKey))
            {
                registered[id] = actionId;
                return new HotkeyRegistration(actionId, gesture, Succeeded: true);
            }

            int error = Marshal.GetLastWin32Error();

            // The identifier was never claimed, so it is handed back to the next attempt.
            nextId--;

            return new HotkeyRegistration(
                actionId,
                gesture,
                Succeeded: false,
                error == ErrorHotkeyAlreadyRegistered
                    ? "Another application already uses this combination."
                    : "Windows refused the registration with error "
                        + error.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    public void UnregisterAll()
    {
        lock (gate)
        {
            if (window is null)
            {
                return;
            }

            foreach (int id in registered.Keys)
            {
                NativeMethods.UnregisterHotKey(window.Handle, id);
            }

            registered.Clear();
            nextId = 1;
        }
    }

    private nint? HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message != NativeMethods.WmHotkey)
        {
            return null;
        }

        string? actionId;

        lock (gate)
        {
            registered.TryGetValue((int)wParam, out actionId);
        }

        if (actionId is not null)
        {
            Pressed?.Invoke(this, actionId);
        }

        return 0;
    }

    private static uint ToModifiers(HotkeyModifiers modifiers)
    {
        uint result = NativeMethods.ModNoRepeat;

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= NativeMethods.ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= NativeMethods.ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= NativeMethods.ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= NativeMethods.ModWin;
        }

        return result;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        UnregisterAll();

        lock (gate)
        {
            window?.Dispose();
            window = null;
        }
    }
}

/// <summary>
/// Maps the portable key names used in stored bindings onto Windows virtual key codes.
/// </summary>
/// <remarks>
/// Only keys that make sense in a shortcut are listed. Anything else is refused by name, which is a
/// better answer than registering a code that turns out to be a modifier or a dead key.
/// </remarks>
internal static class VirtualKeys
{
    private static readonly Dictionary<string, uint> ByName = Build();

    public static bool TryResolve(string name, out uint virtualKey) =>
        ByName.TryGetValue(name, out virtualKey);

    private static Dictionary<string, uint> Build()
    {
        Dictionary<string, uint> keys = new(StringComparer.OrdinalIgnoreCase);

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            keys[letter.ToString()] = letter;
        }

        for (int digit = 0; digit <= 9; digit++)
        {
            uint code = (uint)('0' + digit);
            keys[digit.ToString(CultureInfo.InvariantCulture)] = code;
            keys["D" + digit.ToString(CultureInfo.InvariantCulture)] = code;
        }

        for (int function = 1; function <= 24; function++)
        {
            keys["F" + function.ToString(CultureInfo.InvariantCulture)] = (uint)(0x70 + function - 1);
        }

        for (int pad = 0; pad <= 9; pad++)
        {
            keys["NumPad" + pad.ToString(CultureInfo.InvariantCulture)] = (uint)(0x60 + pad);
        }

        keys["Space"] = 0x20;
        keys["Enter"] = 0x0D;
        keys["Return"] = 0x0D;
        keys["Tab"] = 0x09;
        keys["Escape"] = 0x1B;
        keys["Back"] = 0x08;
        keys["Insert"] = 0x2D;
        keys["Delete"] = 0x2E;
        keys["Home"] = 0x24;
        keys["End"] = 0x23;
        keys["PageUp"] = 0x21;
        keys["PageDown"] = 0x22;
        keys["Left"] = 0x25;
        keys["Up"] = 0x26;
        keys["Right"] = 0x27;
        keys["Down"] = 0x28;

        return keys;
    }
}
