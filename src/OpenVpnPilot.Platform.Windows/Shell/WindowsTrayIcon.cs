using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Shell;

/// <summary>
/// The notification area entry, implemented directly on the shell interface.
/// </summary>
/// <remarks>
/// This owns both the icon and the notifications on purpose. Windows attaches a balloon to an
/// existing notification area entry, so a separate notification component would have to add a second
/// icon that exists only to carry messages, and the user would see two.
///
/// The owner window is a top level window that is never shown rather than a message only one,
/// because a popup menu has to be tracked against a window that can be brought to the foreground.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIcon : ISystemTrayIcon, INotificationPresenter
{
    private const uint IconCallbackMessage = NativeMethods.WmUser + 1;
    private const uint IconId = 1;

    private readonly Lock gate = new();
    private readonly List<TrayMenuEntry> menu = [];

    private MessageWindow? owner;
    private nint icon;
    private string? pendingNotificationTag;
    private bool visible;
    private bool disposed;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public event EventHandler? Activated;

    public event EventHandler<string>? MenuItemInvoked;

    /// <summary>
    /// Raised with the tag of the notification the user clicked.
    /// </summary>
    public event EventHandler<string>? NotificationActivated;

    event EventHandler<string>? INotificationPresenter.Activated
    {
        add => NotificationActivated += value;
        remove => NotificationActivated -= value;
    }

    public void Show(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (gate)
        {
            owner ??= new MessageWindow("OpenVpnPilot.TrayWindow", HandleMessage, canBecomeForeground: true);
            EnsureIcon();

            NativeMethods.NotifyIconData data = CreateData(
                NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip);

            WriteTooltip(ref data, tooltip);

            visible = NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);

            if (visible)
            {
                // Version 4 delivers the cursor position with the message, which is what a popup
                // menu needs in order to appear where the user clicked.
                NativeMethods.NotifyIconData version = CreateData(0);
                version.uVersion = NativeMethods.NotifyIconVersion4;
                NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref version);
            }
        }
    }

    public void SetTooltip(string tooltip)
    {
        ArgumentNullException.ThrowIfNull(tooltip);

        lock (gate)
        {
            if (!visible)
            {
                return;
            }

            NativeMethods.NotifyIconData data = CreateData(NativeMethods.NifTip);
            WriteTooltip(ref data, tooltip);
            NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
        }
    }

    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (gate)
        {
            menu.Clear();
            menu.AddRange(entries);
        }
    }

    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (gate)
        {
            if (!visible)
            {
                // There is no icon to attach a balloon to, so nothing can be shown.
                return Task.CompletedTask;
            }

            pendingNotificationTag = request.Tag;

            NativeMethods.NotifyIconData data = CreateData(NativeMethods.NifInfo);
            WriteInfo(ref data, request.Title, request.Message);

            data.dwInfoFlags = NativeMethods.NiifNone;

            NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
        }

        return Task.CompletedTask;
    }

    private void EnsureIcon()
    {
        if (icon != nint.Zero)
        {
            return;
        }

        // The executable carries the application icon, so it is taken from there rather than from a
        // separate file that could be missing next to a published binary.
        using Process current = Process.GetCurrentProcess();
        string? executable = current.MainModule?.FileName;

        if (executable is { Length: > 0 })
        {
            nint[] large = new nint[1];
            nint[] small = new nint[1];

            if (NativeMethods.ExtractIconEx(executable, 0, large, small, 1) > 0)
            {
                icon = small[0] != nint.Zero ? small[0] : large[0];

                if (small[0] != nint.Zero && large[0] != nint.Zero && icon != large[0])
                {
                    NativeMethods.DestroyIcon(large[0]);
                }
            }
        }

        if (icon == nint.Zero)
        {
            // A generic icon is better than no presence in the notification area at all.
            icon = NativeMethods.LoadIcon(nint.Zero, NativeMethods.IdiApplication);
        }
    }

    private NativeMethods.NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = owner?.Handle ?? nint.Zero,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = IconCallbackMessage,
        hIcon = icon,
    };

    private static void WriteTooltip(ref NativeMethods.NotifyIconData data, string value)
    {
        unsafe
        {
            fixed (char* target = data.szTip)
            {
                Copy(value, target, 128);
            }
        }
    }

    private static void WriteInfo(ref NativeMethods.NotifyIconData data, string title, string message)
    {
        unsafe
        {
            fixed (char* target = data.szInfoTitle)
            {
                Copy(title, target, 64);
            }

            fixed (char* target = data.szInfo)
            {
                Copy(message, target, 256);
            }
        }
    }

    /// <summary>
    /// Copies text into a fixed shell buffer, truncating rather than overrunning it.
    /// </summary>
    private static unsafe void Copy(string value, char* target, int capacity)
    {
        int length = Math.Min(value.Length, capacity - 1);

        for (int index = 0; index < length; index++)
        {
            target[index] = value[index];
        }

        target[length] = '\0';
    }

    private nint? HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message != IconCallbackMessage)
        {
            return null;
        }

        // With version 4 the event is in the low word of lParam and the cursor is in wParam.
        uint notification = (uint)(lParam & 0xFFFF);

        switch (notification)
        {
            case NativeMethods.WmLButtonUp:
                Activated?.Invoke(this, EventArgs.Empty);
                return 0;

            case NativeMethods.WmContextMenu:
            case NativeMethods.WmRButtonUp:
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return 0;

            case NativeMethods.NinBalloonUserClick:
                RaiseNotificationActivated();
                return 0;

            default:
                return 0;
        }
    }

    private void RaiseNotificationActivated()
    {
        string? tag;

        lock (gate)
        {
            tag = pendingNotificationTag;
            pendingNotificationTag = null;
        }

        NotificationActivated?.Invoke(this, tag ?? string.Empty);
    }

    private void ShowMenu(int x, int y)
    {
        List<TrayMenuEntry> entries;
        nint window;

        lock (gate)
        {
            entries = [.. menu];
            window = owner?.Handle ?? nint.Zero;
        }

        if (entries.Count == 0 || window == nint.Zero)
        {
            return;
        }

        nint handle = NativeMethods.CreatePopupMenu();

        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                TrayMenuEntry entry = entries[index];

                if (entry.IsSeparator)
                {
                    NativeMethods.AppendMenu(handle, NativeMethods.MfSeparator, 0, null);
                    continue;
                }

                uint flags = NativeMethods.MfString
                    | (entry.IsEnabled ? 0u : NativeMethods.MfGrayed)
                    | (entry.IsChecked ? NativeMethods.MfChecked : 0u);

                // Command identifiers are one based, because zero means nothing was chosen.
                NativeMethods.AppendMenu(handle, flags, index + 1, entry.Label);
            }

            // Without this the menu stays on screen after the user clicks elsewhere, because the
            // owner window is not in the foreground.
            NativeMethods.SetForegroundWindow(window);

            int chosen = NativeMethods.TrackPopupMenuEx(
                handle,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton | NativeMethods.TpmNonNotify,
                x,
                y,
                window,
                nint.Zero);

            NativeMethods.PostMessage(window, NativeMethods.WmNull, nint.Zero, nint.Zero);

            if (chosen > 0 && chosen <= entries.Count)
            {
                MenuItemInvoked?.Invoke(this, entries[chosen - 1].Id);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(handle);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        lock (gate)
        {
            if (visible)
            {
                NativeMethods.NotifyIconData data = CreateData(0);
                NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
                visible = false;
            }

            if (icon != nint.Zero)
            {
                NativeMethods.DestroyIcon(icon);
                icon = nint.Zero;
            }

            owner?.Dispose();
            owner = null;
        }
    }
}
