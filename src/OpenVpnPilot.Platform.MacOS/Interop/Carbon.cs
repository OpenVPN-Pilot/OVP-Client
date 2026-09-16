using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenVpnPilot.Platform.MacOS.Interop;

/// <summary>
/// The Carbon event calls behind system wide shortcuts.
/// </summary>
/// <remarks>
/// RegisterEventHotKey is old, and it is still what macOS offers for a shortcut that works while
/// another application is in front without the privacy permission a keyboard monitor needs. Its
/// events arrive through the main thread's run loop, which AppKit runs, so nothing has to pump them.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class Carbon
{
    private const string Library = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const int NoError = 0;

    /// <summary>
    /// eventHotKeyExistsErr: the combination is already registered, by this or another application.
    /// </summary>
    public const int HotKeyExists = -9878;

    /// <summary>
    /// eventHotKeyInvalidErr: the combination cannot be registered at all.
    /// </summary>
    public const int HotKeyInvalid = -9879;

    /// <summary>
    /// kEventHotKeyExclusive: the registration fails when another application already holds the
    /// combination exclusively, and keeps others from taking it while it is held.
    /// </summary>
    public const uint HotKeyExclusive = 1;

    public const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    public const uint EventHotKeyPressed = 5;
    public const uint ParameterDirectObject = 0x2D2D2D2D; // '----'
    public const uint TypeEventHotKeyId = 0x686B6964;     // 'hkid'

    public const uint CommandModifier = 0x0100;
    public const uint ShiftModifier = 0x0200;
    public const uint OptionModifier = 0x0800;
    public const uint ControlModifier = 0x1000;

    [LibraryImport(Library)]
    public static partial nint GetApplicationEventTarget();

    [LibraryImport(Library)]
    public static partial int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        HotKeyId identifier,
        nint target,
        uint options,
        out nint hotKey);

    [LibraryImport(Library)]
    public static partial int UnregisterEventHotKey(nint hotKey);

    [LibraryImport(Library)]
    public static partial int InstallEventHandler(
        nint target,
        nint handler,
        nuint typeCount,
        EventTypeSpec* types,
        nint userData,
        out nint handlerReference);

    [LibraryImport(Library)]
    public static partial int RemoveEventHandler(nint handlerReference);

    [LibraryImport(Library)]
    public static partial int GetEventParameter(
        nint eventReference,
        uint name,
        uint desiredType,
        nint actualType,
        nuint bufferSize,
        nint actualSize,
        HotKeyId* data);

    [StructLayout(LayoutKind.Sequential)]
    public struct HotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }
}
