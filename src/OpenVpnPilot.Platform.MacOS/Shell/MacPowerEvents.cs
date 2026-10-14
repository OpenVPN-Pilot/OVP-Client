using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Reports sleep and wake through the workspace notifications AppKit posts for them.
/// </summary>
/// <remarks>
/// <c>NSWorkspaceWillSleepNotification</c> and <c>NSWorkspaceDidWakeNotification</c> are posted to
/// the workspace's own notification centre, not the default one, and on the main thread, which the
/// application's run loop serves. An observer object is defined at runtime with a method for each,
/// carrying a handle to this instance in an instance variable, as the notification delegate does.
///
/// The notification names are the constants' values, which AppKit defines as their own names.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPowerEvents : ISystemPowerEvents
{
    private const string ObserverClassName = "OpenVpnPilotPowerObserver";
    private const string ManagedField = "managed";
    private const string WillSleep = "NSWorkspaceWillSleepNotification";
    private const string DidWake = "NSWorkspaceDidWakeNotification";

    private readonly ILogger<MacPowerEvents> logger;
    private readonly Lock gate = new();

    private GCHandle self;
    private nint center;
    private nint observer;
    private bool disposed;

    public MacPowerEvents(ILogger<MacPowerEvents>? logger = null)
    {
        this.logger = logger ?? NullLogger<MacPowerEvents>.Instance;
    }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (observer != 0)
            {
                return;
            }

            self = GCHandle.Alloc(this);

            ObjectiveC.WithPool(() =>
            {
                nint workspace = ObjectiveC.Send(ObjectiveC.objc_getClass("NSWorkspace"), ObjectiveC.Selector("sharedWorkspace"));
                center = ObjectiveC.Send(workspace, ObjectiveC.Selector("notificationCenter"));

                // Retained, because the workspace's centre outlives every autorelease pool here and
                // this instance has to be able to remove its observer from it later.
                ObjectiveC.Send(center, ObjectiveC.Selector("retain"));

                observer = CreateObserver(GCHandle.ToIntPtr(self));
                Observe(WillSleep, "willSleep:");
                Observe(DidWake, "didWake:");
                return 0;
            });
        }
    }

    private void Observe(string notification, string selector)
    {
        nint name = ObjectiveC.CreateString(notification);

        try
        {
            // The centre copies the name, so the string is released straight away.
            ObjectiveC.Send(
                center,
                ObjectiveC.Selector("addObserver:selector:name:object:"),
                observer,
                ObjectiveC.Selector(selector),
                name,
                0);
        }
        finally
        {
            ObjectiveC.Release(name);
        }
    }

    private static nint CreateObserver(nint handle)
    {
        nint observerClass = ObjectiveC.objc_getClass(ObserverClassName);

        if (observerClass == 0)
        {
            observerClass = ObjectiveC.objc_allocateClassPair(ObjectiveC.objc_getClass("NSObject"), ObserverClassName, 0);

            ObjectiveC.class_addIvar(observerClass, ManagedField, IntPtr.Size, (byte)Math.Log2(IntPtr.Size), "^v");

            ObjectiveC.class_addMethod(
                observerClass,
                ObjectiveC.Selector("willSleep:"),
                (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnWillSleep,
                "v@:@");

            ObjectiveC.class_addMethod(
                observerClass,
                ObjectiveC.Selector("didWake:"),
                (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnDidWake,
                "v@:@");

            ObjectiveC.objc_registerClassPair(observerClass);
        }

        nint instance = ObjectiveC.Send(ObjectiveC.Send(observerClass, ObjectiveC.Selector("alloc")), ObjectiveC.Selector("init"));
        SetOwner(instance, handle);
        return instance;
    }

    private static void SetOwner(nint instance, nint handle)
    {
        nint observerClass = ObjectiveC.objc_getClass(ObserverClassName);
        nint field = ObjectiveC.class_getInstanceVariable(observerClass, ManagedField);
        Marshal.WriteIntPtr(instance, (int)ObjectiveC.ivar_getOffset(field), handle);
    }

    private static MacPowerEvents? Owner(nint instance)
    {
        nint observerClass = ObjectiveC.objc_getClass(ObserverClassName);
        nint field = ObjectiveC.class_getInstanceVariable(observerClass, ManagedField);
        nint handle = Marshal.ReadIntPtr(instance, (int)ObjectiveC.ivar_getOffset(field));

        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as MacPowerEvents;
    }

    [UnmanagedCallersOnly]
    private static void OnWillSleep(nint instance, nint selector, nint notification) =>
        Raise(instance, sleeping: true);

    [UnmanagedCallersOnly]
    private static void OnDidWake(nint instance, nint selector, nint notification) =>
        Raise(instance, sleeping: false);

    private static void Raise(nint instance, bool sleeping)
    {
        MacPowerEvents? owner = null;

        try
        {
            owner = Owner(instance);

            if (owner is null)
            {
                return;
            }

            if (sleeping)
            {
                owner.Suspending?.Invoke(owner, EventArgs.Empty);
            }
            else
            {
                owner.Resumed?.Invoke(owner, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            // Deliberately everything: an exception may not cross back into AppKit, which would end
            // the process on the way into or out of a sleep.
            MacPowerEventsLog.HandlerFailed((ILogger?)owner?.logger ?? NullLogger.Instance, exception);
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

            if (observer != 0)
            {
                ObjectiveC.Send(center, ObjectiveC.Selector("removeObserver:"), observer);

                // Cleared before release, so a notification already on its way finds no owner
                // rather than a handle that has been freed.
                SetOwner(observer, 0);
                ObjectiveC.Release(observer);
                observer = 0;
            }

            if (center != 0)
            {
                ObjectiveC.Release(center);
                center = 0;
            }

            if (self.IsAllocated)
            {
                self.Free();
            }
        }
    }
}

/// <summary>
/// Source generated log messages for <see cref="MacPowerEvents"/>.
/// </summary>
internal static partial class MacPowerEventsLog
{
    [LoggerMessage(
        EventId = 5610,
        Level = LogLevel.Error,
        Message = "A handler of a sleep or wake notification failed.")]
    public static partial void HandlerFailed(ILogger logger, Exception exception);
}
