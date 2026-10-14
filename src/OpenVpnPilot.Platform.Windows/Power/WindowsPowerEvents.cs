using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Power;

/// <summary>
/// Reports sleep and wake through the power manager's suspend and resume notification.
/// </summary>
/// <remarks>
/// A broadcast of <c>WM_POWERBROADCAST</c> reaches top level windows only, and the window this
/// application keeps for the notification area is a message only window, which broadcasts skip. The
/// power manager's own registration delivers the same events to a callback instead, with no window
/// involved, which is why it is used here.
///
/// Resuming is reported on <c>PBT_APMRESUMEAUTOMATIC</c>, which is sent on every wake. The
/// <c>PBT_APMRESUMESUSPEND</c> that follows when a person is present would report the same wake twice.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsPowerEvents : ISystemPowerEvents
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PowerBroadcastSuspend = 0x0004;
    private const uint PowerBroadcastResumeAutomatic = 0x0012;

    private readonly ILogger<WindowsPowerEvents> logger;
    private readonly Lock gate = new();

    private GCHandle self;
    private nint parameters;
    private nint registration;
    private bool disposed;

    public WindowsPowerEvents(ILogger<WindowsPowerEvents>? logger = null)
    {
        this.logger = logger ?? NullLogger<WindowsPowerEvents>.Instance;
    }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (registration != 0)
            {
                return;
            }

            self = GCHandle.Alloc(this);

            // struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { PDEVICE_NOTIFY_CALLBACK_ROUTINE Callback; PVOID Context; }
            // Kept in unmanaged memory for as long as the registration lives, because nothing says
            // the power manager copies it.
            parameters = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(parameters, 0, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, uint>)&Notified);
            Marshal.WriteIntPtr(parameters, IntPtr.Size, GCHandle.ToIntPtr(self));

            nint handle;
            uint result = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, parameters, &handle);

            if (result != 0)
            {
                // Reported rather than raised: the clock still shows a sleep, only later.
                PowerEventsLog.RegistrationFailed(logger, (int)result);
                Release();
                return;
            }

            registration = handle;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Notified(nint context, uint type, nint setting)
    {
        WindowsPowerEvents? owner = null;

        try
        {
            owner = GCHandle.FromIntPtr(context).Target as WindowsPowerEvents;

            switch (type)
            {
                case PowerBroadcastSuspend:
                    owner?.Suspending?.Invoke(owner, EventArgs.Empty);
                    break;

                case PowerBroadcastResumeAutomatic:
                    owner?.Resumed?.Invoke(owner, EventArgs.Empty);
                    break;

                default:
                    break;
            }
        }
        catch (Exception exception)
        {
            // Deliberately everything: an exception may not cross back into the power manager, and
            // a sleep is the worst moment to end the process over a handler.
            if (owner is not null)
            {
                PowerEventsLog.HandlerFailed(owner.logger, exception);
            }
        }

        return 0;
    }

    private void Release()
    {
        if (parameters != 0)
        {
            Marshal.FreeHGlobal(parameters);
            parameters = 0;
        }

        if (self.IsAllocated)
        {
            self.Free();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (registration != 0)
            {
                uint result = PowerUnregisterSuspendResumeNotification(registration);
                registration = 0;

                if (result != 0)
                {
                    // The callback may still be called, so what it points at has to stay.
                    PowerEventsLog.UnregistrationFailed(logger, (int)result);
                    return;
                }
            }

            Release();
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, nint recipient, nint* registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);
}

/// <summary>
/// Source generated log messages for the power notifications.
/// </summary>
internal static partial class PowerEventsLog
{
    [LoggerMessage(
        EventId = 5600,
        Level = LogLevel.Warning,
        Message = "Sleep and wake notifications could not be registered (error {Error}). A sleep is noticed from the clock instead, a little later.")]
    public static partial void RegistrationFailed(ILogger logger, int error);

    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Warning,
        Message = "The sleep and wake notifications could not be unregistered (error {Error}).")]
    public static partial void UnregistrationFailed(ILogger logger, int error);

    [LoggerMessage(
        EventId = 5602,
        Level = LogLevel.Error,
        Message = "A handler of a sleep or wake notification failed.")]
    public static partial void HandlerFailed(ILogger logger, Exception exception);
}
