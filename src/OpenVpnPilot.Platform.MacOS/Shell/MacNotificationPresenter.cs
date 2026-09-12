using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// Shows notifications through the user notification centre.
/// </summary>
/// <remarks>
/// The notification centre attributes every message to an application bundle and refuses to work at
/// all for a process that has none; asking it anyway raises an Objective-C exception that ends the
/// process. A build run straight from the source tree is such a process, so the presenter reports
/// itself unavailable there and the application stays quiet rather than falling over.
///
/// The centre asks the person once whether this application may notify at all, the first time it is
/// asked to. A refusal is theirs to make and is not worked around; the messages are then simply not
/// shown, and the settings under System Settings, Notifications are where it can be changed.
///
/// A delegate is installed so a message is shown even while the application is in front, which the
/// centre otherwise suppresses, and so that clicking a message brings up the profile it was about.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNotificationPresenter : INotificationPresenter, IDisposable
{
    private const string DelegateClassName = "OpenVpnPilotNotificationDelegate";
    private const string ManagedField = "managed";
    private const string TagKey = "tag";

    // UNAuthorizationOptionSound | UNAuthorizationOptionAlert.
    private const nuint AuthorisationOptions = (1 << 1) | (1 << 2);

    // UNNotificationPresentationOptionSound | List | Banner.
    private const nuint PresentationOptions = (1 << 1) | (1 << 3) | (1 << 4);

    private readonly ILogger<MacNotificationPresenter> logger;
    private readonly Lock gate = new();
    private readonly bool bundled;

    private GCHandle self;
    private nint center;
    private nint centreDelegate;
    private nint authorisation;
    private nint delivery;
    private bool disposed;

    public MacNotificationPresenter(ILogger<MacNotificationPresenter>? logger = null)
    {
        this.logger = logger ?? NullLogger<MacNotificationPresenter>.Instance;
        bundled = HasBundleIdentifier();
    }

    public bool IsAvailable => bundled && !disposed;

    public event EventHandler<string>? Activated;

    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAvailable)
        {
            return Task.CompletedTask;
        }

        try
        {
            ObjectiveC.WithPool(() =>
            {
                Post(request);
                return 0;
            });
        }
        catch (InvalidOperationException exception)
        {
            // Fire and forget by design: a notification is an aside, and one that could not be
            // posted must not become a failure of the connection it was about.
            NotificationLog.PostFailed(logger, exception);
        }

        return Task.CompletedTask;
    }

    private void Post(NotificationRequest request)
    {
        nint notificationCentre = EnsureCentre();

        nint content = ObjectiveC.Send(
            ObjectiveC.Send(ObjectiveC.objc_getClass("UNMutableNotificationContent"), ObjectiveC.Selector("alloc")),
            ObjectiveC.Selector("init"));

        nint title = ObjectiveC.CreateString(request.Title);
        nint body = ObjectiveC.CreateString(request.Message);
        nint identifier = ObjectiveC.CreateString("openvpnpilot-" + Guid.NewGuid().ToString("N"));
        nint tagKey = ObjectiveC.CreateString(TagKey);
        nint tagValue = ObjectiveC.CreateString(request.Tag ?? string.Empty);

        try
        {
            ObjectiveC.Send(content, ObjectiveC.Selector("setTitle:"), title);
            ObjectiveC.Send(content, ObjectiveC.Selector("setBody:"), body);

            // Only what went wrong makes a sound. Twenty tunnels coming up should not ring twenty times.
            if (request.Severity != NotificationSeverity.Information)
            {
                nint sound = ObjectiveC.Send(ObjectiveC.objc_getClass("UNNotificationSound"), ObjectiveC.Selector("defaultSound"));
                ObjectiveC.Send(content, ObjectiveC.Selector("setSound:"), sound);
            }

            nint userInfo = ObjectiveC.Send(
                ObjectiveC.objc_getClass("NSDictionary"),
                ObjectiveC.Selector("dictionaryWithObject:forKey:"),
                tagValue,
                tagKey);

            ObjectiveC.Send(content, ObjectiveC.Selector("setUserInfo:"), userInfo);

            nint notification = ObjectiveC.Send(
                ObjectiveC.objc_getClass("UNNotificationRequest"),
                ObjectiveC.Selector("requestWithIdentifier:content:trigger:"),
                identifier,
                content,
                0);

            ObjectiveC.Send(
                notificationCentre,
                ObjectiveC.Selector("addNotificationRequest:withCompletionHandler:"),
                notification,
                delivery);
        }
        finally
        {
            ObjectiveC.Release(title);
            ObjectiveC.Release(body);
            ObjectiveC.Release(identifier);
            ObjectiveC.Release(tagKey);
            ObjectiveC.Release(tagValue);
            ObjectiveC.Release(content);
        }
    }

    /// <summary>
    /// The notification centre, with this presenter as its delegate and permission asked for.
    /// </summary>
    private nint EnsureCentre()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (center != 0)
            {
                return center;
            }

            if (NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications") == 0)
            {
                throw new InvalidOperationException("The UserNotifications framework could not be loaded.");
            }

            nint centreClass = ObjectiveC.objc_getClass("UNUserNotificationCenter");
            nint current = ObjectiveC.Send(centreClass, ObjectiveC.Selector("currentNotificationCenter"));

            if (current == 0)
            {
                throw new InvalidOperationException("The notification centre is not available to this process.");
            }

            self = GCHandle.Alloc(this, GCHandleType.Normal);
            centreDelegate = CreateDelegate(GCHandle.ToIntPtr(self));
            ObjectiveC.Send(current, ObjectiveC.Selector("setDelegate:"), centreDelegate);

            authorisation = GlobalBlock.Create(
                (nint)(delegate* unmanaged<nint, byte, nint, void>)&Authorised,
                "v@?B@",
                GCHandle.ToIntPtr(self));

            delivery = GlobalBlock.Create(
                (nint)(delegate* unmanaged<nint, nint, void>)&Delivered,
                "v@?@",
                GCHandle.ToIntPtr(self));

            ObjectiveC.Send(
                current,
                ObjectiveC.Selector("requestAuthorizationWithOptions:completionHandler:"),
                AuthorisationOptions,
                authorisation);

            center = current;
            return center;
        }
    }

    /// <summary>
    /// Creates the delegate object, defining its class the first time.
    /// </summary>
    /// <remarks>
    /// The delegate carries a handle to this presenter in an instance variable rather than the
    /// presenter being found through a static field, so a second presenter, as in a test, is its
    /// own delegate and not a replacement for the first.
    /// </remarks>
    private static nint CreateDelegate(nint handle)
    {
        nint delegateClass = ObjectiveC.objc_getClass(DelegateClassName);

        if (delegateClass == 0)
        {
            delegateClass = ObjectiveC.objc_allocateClassPair(ObjectiveC.objc_getClass("NSObject"), DelegateClassName, 0);

            ObjectiveC.class_addIvar(delegateClass, ManagedField, IntPtr.Size, (byte)Math.Log2(IntPtr.Size), "^v");

            ObjectiveC.class_addMethod(
                delegateClass,
                ObjectiveC.Selector("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent,
                "v@:@@@?");

            ObjectiveC.class_addMethod(
                delegateClass,
                ObjectiveC.Selector("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceive,
                "v@:@@@?");

            nint protocol = ObjectiveC.objc_getProtocol("UNUserNotificationCenterDelegate");

            if (protocol != 0)
            {
                ObjectiveC.class_addProtocol(delegateClass, protocol);
            }

            ObjectiveC.objc_registerClassPair(delegateClass);
        }

        nint instance = ObjectiveC.Send(ObjectiveC.Send(delegateClass, ObjectiveC.Selector("alloc")), ObjectiveC.Selector("init"));
        nint field = ObjectiveC.class_getInstanceVariable(delegateClass, ManagedField);
        Marshal.WriteIntPtr(instance, (int)ObjectiveC.ivar_getOffset(field), handle);

        return instance;
    }

    private static MacNotificationPresenter? Owner(nint delegateInstance)
    {
        nint delegateClass = ObjectiveC.objc_getClass(DelegateClassName);
        nint field = ObjectiveC.class_getInstanceVariable(delegateClass, ManagedField);
        nint handle = Marshal.ReadIntPtr(delegateInstance, (int)ObjectiveC.ivar_getOffset(field));

        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as MacNotificationPresenter;
    }

    [UnmanagedCallersOnly]
    private static void WillPresent(nint self, nint selector, nint centre, nint notification, nint completion)
    {
        // Shown as a banner even while this application is in front, which is when the person is
        // most likely to be waiting for a tunnel to come up.
        ObjectiveC.InvokeBlock(completion, PresentationOptions);
    }

    [UnmanagedCallersOnly]
    private static void DidReceive(nint self, nint selector, nint centre, nint response, nint completion)
    {
        MacNotificationPresenter? owner = null;

        try
        {
            owner = Owner(self);
            string? tag = ObjectiveC.WithPool(() => ReadTag(response));

            if (tag is not null && owner is not null)
            {
                owner.Activated?.Invoke(owner, tag);
            }
        }
        catch (Exception exception)
        {
            // Deliberately everything: an exception may not cross back into the notification centre,
            // which would end the process over a click on a message.
            NotificationLog.ActivationFailed((ILogger?)owner?.logger ?? NullLogger.Instance, exception);
        }
        finally
        {
            ObjectiveC.InvokeBlock(completion);
        }
    }

    private static string? ReadTag(nint response)
    {
        nint notification = ObjectiveC.Send(response, ObjectiveC.Selector("notification"));
        nint request = ObjectiveC.Send(notification, ObjectiveC.Selector("request"));
        nint content = ObjectiveC.Send(request, ObjectiveC.Selector("content"));
        nint userInfo = ObjectiveC.Send(content, ObjectiveC.Selector("userInfo"));
        nint key = ObjectiveC.CreateString(TagKey);

        try
        {
            return ObjectiveC.ReadString(ObjectiveC.Send(userInfo, ObjectiveC.Selector("objectForKey:"), key));
        }
        finally
        {
            ObjectiveC.Release(key);
        }
    }

    private static bool HasBundleIdentifier() =>
        ObjectiveC.WithPool(() =>
        {
            nint bundle = ObjectiveC.Send(ObjectiveC.objc_getClass("NSBundle"), ObjectiveC.Selector("mainBundle"));
            nint identifier = bundle == 0 ? 0 : ObjectiveC.Send(bundle, ObjectiveC.Selector("bundleIdentifier"));
            return identifier != 0;
        });

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            if (center != 0)
            {
                ObjectiveC.Send(center, ObjectiveC.Selector("setDelegate:"), 0);
                center = 0;
            }

            ObjectiveC.Release(centreDelegate);
            centreDelegate = 0;

            // The handle the blocks carry is freed below, so they are told to stop carrying it first.
            // A callback that arrives after this finds nothing and logs nowhere, rather than reading
            // a handle that is no longer allocated.
            GlobalBlock.Disown(authorisation);
            GlobalBlock.Disown(delivery);
            authorisation = 0;
            delivery = 0;

            if (self.IsAllocated)
            {
                self.Free();
            }
        }
    }

    /// <summary>
    /// The answer to the permission request.
    /// </summary>
    /// <remarks>
    /// Whether the person allowed notifications is theirs to decide and is not worked around, but a
    /// refusal is the whole explanation for an application that has gone quiet, and discarding it
    /// leaves nothing to read anywhere. It is therefore written to the log, once per run.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static void Authorised(nint block, byte granted, nint error)
    {
        ILogger logger = LoggerOf(block);

        try
        {
            if (granted != 0 && error == 0)
            {
                return;
            }

            ObjectiveC.WithPool(() =>
            {
                NotificationLog.NotPermitted(logger, granted != 0, Describe(error));
                return 0;
            });
        }
        catch (Exception exception)
        {
            // Deliberately everything: an exception may not cross back into the notification centre.
            NotificationLog.PostFailed(logger, exception);
        }
    }

    /// <summary>
    /// The answer to a posted message, which says whether the centre accepted it.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void Delivered(nint block, nint error)
    {
        ILogger logger = LoggerOf(block);

        try
        {
            if (error == 0)
            {
                return;
            }

            ObjectiveC.WithPool(() =>
            {
                NotificationLog.Refused(logger, Describe(error));
                return 0;
            });
        }
        catch (Exception exception)
        {
            NotificationLog.PostFailed(logger, exception);
        }
    }

    /// <summary>
    /// The log of the presenter a block belongs to, or a sink, so a callback always has one.
    /// </summary>
    private static ILogger LoggerOf(nint block)
    {
        nint handle = GlobalBlock.Captured(block);
        MacNotificationPresenter? owner =
            handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as MacNotificationPresenter;

        return (ILogger?)owner?.logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// What an NSError says, as its code and its localized description.
    /// </summary>
    private static string Describe(nint error)
    {
        if (error == 0)
        {
            return "no reason given";
        }

        nint code = ObjectiveC.Send(error, ObjectiveC.Selector("code"));
        string? message = ObjectiveC.ReadString(
            ObjectiveC.Send(error, ObjectiveC.Selector("localizedDescription")));

        return $"{message ?? "no description"} ({code})";
    }
}

/// <summary>
/// Source generated log messages for <see cref="MacNotificationPresenter"/>.
/// </summary>
internal static partial class NotificationLog
{
    [LoggerMessage(
        EventId = 5300,
        Level = LogLevel.Warning,
        Message = "A notification could not be posted.")]
    public static partial void PostFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Warning,
        Message = "The notification centre did not permit notifications. Granted: {Granted}. {Reason}")]
    public static partial void NotPermitted(ILogger logger, bool granted, string reason);

    [LoggerMessage(
        EventId = 5303,
        Level = LogLevel.Warning,
        Message = "The notification centre refused a message. {Reason}")]
    public static partial void Refused(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Warning,
        Message = "A click on a notification could not be handled.")]
    public static partial void ActivationFailed(ILogger logger, Exception exception);
}
