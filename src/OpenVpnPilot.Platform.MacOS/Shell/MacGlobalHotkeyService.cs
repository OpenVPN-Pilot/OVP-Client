using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// System wide shortcuts, registered as Carbon hot keys.
/// </summary>
/// <remarks>
/// A hot key reaches this application while another one is in front without the Accessibility or
/// Input Monitoring permission a keyboard monitor needs, which is why it is used rather than an event
/// tap: a shortcut should not require the person to open the privacy settings first.
///
/// Registrations are exclusive, so a combination another application already holds exclusively is
/// refused with a reason rather than shared. The Command modifier is what the stored bindings call
/// Windows; the name is the persisted format and is only translated where it is shown.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacGlobalHotkeyService : IGlobalHotkeyService
{
    /// <summary>
    /// Identifies this application's hot keys among whatever else the process registers.
    /// </summary>
    private const uint Signature = 0x4F56504C; // 'OVPL'

    /// <summary>
    /// eventNotHandledErr: lets the event continue to the next handler.
    /// </summary>
    private const int EventNotHandled = -9874;

    private readonly Lock gate = new();
    private readonly Dictionary<uint, Registration> registered = [];
    private readonly ILogger<MacGlobalHotkeyService> logger;

    private GCHandle self;
    private nint handler;
    private uint nextId = 1;
    private bool disposed;

    public MacGlobalHotkeyService(ILogger<MacGlobalHotkeyService>? logger = null)
    {
        this.logger = logger ?? NullLogger<MacGlobalHotkeyService>.Instance;
    }

    public bool IsAvailable => !disposed;

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

        if (!MacKeyCodes.TryResolve(gesture.Key, out uint keyCode))
        {
            return new HotkeyRegistration(
                actionId,
                gesture,
                Succeeded: false,
                $"'{gesture.Key}' is not a key this platform recognises.");
        }

        lock (gate)
        {
            EnsureHandler();

            uint id = nextId++;
            Carbon.HotKeyId identifier = new() { Signature = Signature, Id = id };

            int status = Carbon.RegisterEventHotKey(
                keyCode,
                ToModifiers(gesture.Modifiers),
                identifier,
                Carbon.GetApplicationEventTarget(),
                Carbon.HotKeyExclusive,
                out nint hotKey);

            if (status == Carbon.NoError)
            {
                registered[id] = new Registration(actionId, hotKey);
                return new HotkeyRegistration(actionId, gesture, Succeeded: true);
            }

            // The identifier was never claimed, so it is handed back to the next attempt.
            nextId--;

            return new HotkeyRegistration(
                actionId,
                gesture,
                Succeeded: false,
                status == Carbon.HotKeyExists
                    ? "Another application already uses this combination."
                    : "macOS refused the registration with error "
                        + status.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    public void UnregisterAll()
    {
        lock (gate)
        {
            foreach (Registration registration in registered.Values)
            {
                int status = Carbon.UnregisterEventHotKey(registration.HotKey);

                if (status != Carbon.NoError)
                {
                    HotkeyLog.ReleaseFailed(logger, registration.ActionId, status);
                }
            }

            registered.Clear();
            nextId = 1;
        }
    }

    /// <summary>
    /// Installs the one event handler every hot key of this service reports to.
    /// </summary>
    /// <remarks>
    /// The handler finds this service through a handle passed as its user data rather than through a
    /// static field, so the handler and the service it serves cannot come apart.
    /// </remarks>
    private void EnsureHandler()
    {
        if (handler != 0)
        {
            return;
        }

        self = GCHandle.Alloc(this, GCHandleType.Normal);

        Carbon.EventTypeSpec pressed = new()
        {
            EventClass = Carbon.EventClassKeyboard,
            EventKind = Carbon.EventHotKeyPressed,
        };

        int status = Carbon.InstallEventHandler(
            Carbon.GetApplicationEventTarget(),
            (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnHotKey,
            1,
            &pressed,
            GCHandle.ToIntPtr(self),
            out handler);

        if (status != Carbon.NoError)
        {
            self.Free();
            handler = 0;

            throw new InvalidOperationException(
                $"The shortcut handler could not be installed: error {status.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    [UnmanagedCallersOnly]
    private static int OnHotKey(nint callReference, nint eventReference, nint userData)
    {
        Carbon.HotKeyId identifier;

        int status = Carbon.GetEventParameter(
            eventReference,
            Carbon.ParameterDirectObject,
            Carbon.TypeEventHotKeyId,
            0,
            (nuint)sizeof(Carbon.HotKeyId),
            0,
            &identifier);

        if (status != Carbon.NoError
            || identifier.Signature != Signature
            || GCHandle.FromIntPtr(userData).Target is not MacGlobalHotkeyService service)
        {
            return EventNotHandled;
        }

        return service.Raise(identifier.Id) ? Carbon.NoError : EventNotHandled;
    }

    private bool Raise(uint id)
    {
        string? actionId;

        lock (gate)
        {
            actionId = registered.TryGetValue(id, out Registration? registration) ? registration.ActionId : null;
        }

        if (actionId is null)
        {
            return false;
        }

        try
        {
            Pressed?.Invoke(this, actionId);
        }
        catch (Exception exception)
        {
            // Deliberately everything: an exception may not travel back through the Carbon
            // dispatcher, which would end the process over a key press. The shortcut stays
            // registered, and the fault is the handler's.
            HotkeyLog.HandlerFailed(logger, actionId, exception);
        }

        return true;
    }

    private static uint ToModifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= Carbon.ControlModifier;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= Carbon.OptionModifier;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= Carbon.ShiftModifier;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= Carbon.CommandModifier;
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
            if (handler != 0)
            {
                int status = Carbon.RemoveEventHandler(handler);

                if (status != Carbon.NoError)
                {
                    HotkeyLog.HandlerRemovalFailed(logger, status);
                }

                handler = 0;
            }

            if (self.IsAllocated)
            {
                self.Free();
            }
        }
    }

    private sealed record Registration(string ActionId, nint HotKey);
}

/// <summary>
/// Maps the portable key names used in stored bindings onto macOS virtual key codes.
/// </summary>
/// <remarks>
/// The names are the ones the Windows implementation understands, so a binding made on one system
/// means the same key on the other. A key code names a position on the keyboard, the one that
/// carries that letter on a US layout.
/// </remarks>
internal static class MacKeyCodes
{
    private static readonly Dictionary<string, uint> ByName = Build();

    public static bool TryResolve(string name, out uint keyCode) => ByName.TryGetValue(name, out keyCode);

    private static Dictionary<string, uint> Build()
    {
        Dictionary<string, uint> keys = new(StringComparer.OrdinalIgnoreCase);

        uint[] letters =
        [
            0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E,
            0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06,
        ];

        for (int letter = 0; letter < letters.Length; letter++)
        {
            keys[((char)('A' + letter)).ToString()] = letters[letter];
        }

        uint[] digits = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];
        uint[] pad = [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C];

        for (int digit = 0; digit <= 9; digit++)
        {
            string text = digit.ToString(CultureInfo.InvariantCulture);
            keys[text] = digits[digit];
            keys["D" + text] = digits[digit];
            keys["NumPad" + text] = pad[digit];
        }

        uint[] functions =
        [
            0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D,
            0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A,
        ];

        for (int function = 0; function < functions.Length; function++)
        {
            keys["F" + (function + 1).ToString(CultureInfo.InvariantCulture)] = functions[function];
        }

        keys["Space"] = 0x31;
        keys["Enter"] = 0x24;
        keys["Return"] = 0x24;
        keys["Tab"] = 0x30;
        keys["Escape"] = 0x35;
        keys["Back"] = 0x33;
        keys["Insert"] = 0x72;
        keys["Delete"] = 0x75;
        keys["Home"] = 0x73;
        keys["End"] = 0x77;
        keys["PageUp"] = 0x74;
        keys["PageDown"] = 0x79;
        keys["Left"] = 0x7B;
        keys["Right"] = 0x7C;
        keys["Down"] = 0x7D;
        keys["Up"] = 0x7E;

        return keys;
    }
}

/// <summary>
/// Source generated log messages for <see cref="MacGlobalHotkeyService"/>.
/// </summary>
internal static partial class HotkeyLog
{
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Error,
        Message = "The shortcut for {ActionId} was pressed and its handler failed.")]
    public static partial void HandlerFailed(ILogger logger, string actionId, Exception exception);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Warning,
        Message = "The shortcut for {ActionId} could not be released: error {Status}.")]
    public static partial void ReleaseFailed(ILogger logger, string actionId, int status);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Warning,
        Message = "The shortcut handler could not be removed: error {Status}.")]
    public static partial void HandlerRemovalFailed(ILogger logger, int status);
}
