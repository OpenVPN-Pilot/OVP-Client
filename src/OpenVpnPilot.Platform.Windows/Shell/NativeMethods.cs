using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// The shell and window entry points the notification area entry is built on.
/// </summary>
/// <remarks>
/// Kept in one place so the surface that leaves managed code is visible at a glance. The structures
/// use fixed character buffers rather than marshalled strings so they stay blittable, which is what
/// lets the source generated interop be used throughout.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint WmUser = 0x0400;
    public const uint WmNull = 0x0000;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmContextMenu = 0x007B;

    /// <summary>
    /// Sent when the user clicks a balloon rather than dismissing it.
    /// </summary>
    public const uint NinBalloonUserClick = WmUser + 5;

    public const uint NimAdd = 0x00000000;
    public const uint NimModify = 0x00000001;
    public const uint NimDelete = 0x00000002;
    public const uint NimSetVersion = 0x00000004;

    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const uint NifInfo = 0x00000010;

    /// <summary>
    /// No icon at all beside the message. Both alternatives read worse: the severity flags
    /// (<c>NIIF_INFO</c>/<c>WARNING</c>/<c>ERROR</c>) draw one of Windows' own stock icons, a blue
    /// circle, an orange triangle or a red circle, in place of the application's own; and
    /// <c>NIIF_USER</c>, which draws <c>hIcon</c> instead, stretches whatever small icon the tray
    /// carries into a much larger image and shows every one of its pixels doing it.
    /// </summary>
    public const uint NiifNone = 0x00000000;

    public const uint NotifyIconVersion4 = 4;

    public const uint MfString = 0x00000000;
    public const uint MfGrayed = 0x00000001;
    public const uint MfChecked = 0x00000008;
    public const uint MfSeparator = 0x00000800;
    public const uint MiimBitmap = 0x00000080;
    public const int SmCxSmIcon = 49;

    public const uint TpmLeftButton = 0x0000;
    public const uint TpmRightButton = 0x0002;
    public const uint TpmReturnCmd = 0x0100;
    public const uint TpmNonNotify = 0x0080;

    /// <summary>
    /// Parent that makes a window message only: it never appears and costs nothing to draw.
    /// </summary>
    public static readonly nint HwndMessage = -3;

    /// <summary>
    /// A window with no frame. Combined with never showing it, this keeps the owner invisible.
    /// </summary>
    public const uint WsPopup = 0x80000000;

    /// <summary>
    /// Keeps the owner window out of the task bar and out of the window switcher.
    /// </summary>
    public const uint WsExToolWindow = 0x00000080;

    public static readonly nint IdiApplication = 32512;

    public delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;

        // Pointers rather than strings, so the structure stays blittable and can be passed by the
        // source generated interop. The caller owns the memory for as long as the class is registered.
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    /// <summary>
    /// Declares the identity the shell files this process's notifications under. Returns an HRESULT.
    /// </summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nint item, string? text);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, int item, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconEx(string file, int index, nint[] large, nint[] small, uint count);

    public const uint DibRgbColors = 0;
    public const uint BiRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        // A BOOL, which is four bytes; a managed bool would be one and shift every field after it.
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapObject
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    /// <summary>
    /// A <c>BITMAPINFO</c> with room for the three masks a 32 bit format may carry, so nothing is
    /// written past the end whatever the system decides to fill in.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        public uint mask0;
        public uint mask1;
        public uint mask2;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint handle, int size, out BitmapObject bitmap);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startLine,
        uint lines,
        byte[] bits,
        ref BitmapInfo info,
        uint usage);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo info,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct MenuItemInfo
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public nint hSubMenu;
        public nint hbmpChecked;
        public nint hbmpUnchecked;
        public nint dwItemData;
        public nint dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenuItemInfo(
        nint menu,
        uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition,
        ref MenuItemInfo info);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint window, int id);

    public const uint WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// Stops Windows repeating the shortcut while the keys are held down.
    /// </summary>
    public const uint ModNoRepeat = 0x4000;
}
