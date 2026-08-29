using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// An invisible window that exists only to receive messages.
/// </summary>
/// <remarks>
/// Several Windows facilities report through window messages rather than callbacks: notification
/// area events and global shortcuts both do. Each needs a window handle, and neither needs anything
/// drawn, so the window is created and never shown.
///
/// It must be created on the thread that runs the message loop, which in this application is the
/// user interface thread. No loop of its own is started, because a second one would deliver messages
/// on a thread the rest of the application does not expect.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MessageWindow : IDisposable
{
    private readonly string className;
    private readonly MessageHandler handler;

    // Held because the window class keeps only an unmanaged pointer to the delegate, and a collected
    // delegate would leave Windows calling into freed memory.
    private readonly NativeMethods.WindowProcedure procedure;

    private nint classNameBuffer;
    private ushort registeredClass;
    private bool disposed;

    /// <summary>
    /// Handles one message, or returns null to let Windows apply its default behaviour.
    /// </summary>
    public delegate nint? MessageHandler(uint message, nint wParam, nint lParam);

    /// <param name="className">Unique per window class in this process.</param>
    /// <param name="handler">Called for every message the window receives.</param>
    /// <param name="canBecomeForeground">
    /// True creates a top level window that is never shown, which is required for anything that
    /// tracks a popup menu. False creates a message only window, which is cheaper but cannot be
    /// brought to the foreground.
    /// </param>
    public MessageWindow(string className, MessageHandler handler, bool canBecomeForeground)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(handler);

        this.className = className;
        this.handler = handler;
        procedure = Dispatch;

        nint module = NativeMethods.GetModuleHandle(null);
        classNameBuffer = Marshal.StringToHGlobalUni(className);

        NativeMethods.WindowClassEx description = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = module,
            lpszClassName = classNameBuffer,
        };

        registeredClass = NativeMethods.RegisterClassEx(ref description);

        Handle = NativeMethods.CreateWindowEx(
            canBecomeForeground ? NativeMethods.WsExToolWindow : 0,
            className,
            className,
            canBecomeForeground ? NativeMethods.WsPopup : 0,
            0,
            0,
            0,
            0,
            canBecomeForeground ? nint.Zero : NativeMethods.HwndMessage,
            nint.Zero,
            module,
            nint.Zero);

        if (Handle == nint.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Dispose();

            throw new InvalidOperationException(
                $"The window '{className}' could not be created. Windows reported error "
                + error.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    public nint Handle { get; }

    private nint Dispatch(nint window, uint message, nint wParam, nint lParam) =>
        handler(message, wParam, lParam)
        ?? NativeMethods.DefWindowProc(window, message, wParam, lParam);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (Handle != nint.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
        }

        if (registeredClass != 0)
        {
            NativeMethods.UnregisterClass(className, NativeMethods.GetModuleHandle(null));
            registeredClass = 0;
        }

        if (classNameBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(classNameBuffer);
            classNameBuffer = nint.Zero;
        }
    }
}
