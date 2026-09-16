using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Interop;

/// <summary>
/// The Objective-C runtime calls needed to reach the few AppKit and UserNotifications classes
/// Avalonia does not wrap.
/// </summary>
/// <remarks>
/// objc_msgSend has no fixed signature: it is called with the signature of the method it reaches,
/// and on arm64 a mismatch is not tolerated. Each shape used here therefore has its own function
/// pointer type rather than a declaration that happens to work on one architecture.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjectiveC
{
    private const string Library = "/usr/lib/libobjc.A.dylib";

    private static readonly nint LibraryHandle = NativeLibrary.Load(Library);

    private static readonly nint MessageSend = NativeLibrary.GetExport(LibraryHandle, "objc_msgSend");

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getProtocol(string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport(Library)]
    public static partial void objc_registerClassPair(nint cls);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addIvar(nint cls, string name, nint size, byte alignment, string types);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addProtocol(nint cls, nint protocol);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint class_getInstanceVariable(nint cls, string name);

    [LibraryImport(Library)]
    public static partial nint ivar_getOffset(nint ivar);

    [LibraryImport(Library)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Library)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    public static nint Selector(string name) => sel_registerName(name);

    public static nint Send(nint receiver, nint selector) =>
        ((delegate* unmanaged<nint, nint, nint>)MessageSend)(receiver, selector);

    public static nint Send(nint receiver, nint selector, nint argument) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)MessageSend)(receiver, selector, argument);

    public static nint Send(nint receiver, nint selector, nint first, nint second) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)MessageSend)(receiver, selector, first, second);

    public static nint Send(nint receiver, nint selector, nint first, nint second, nint third) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MessageSend)(receiver, selector, first, second, third);

    public static nint Send(nint receiver, nint selector, nuint first, nint second) =>
        ((delegate* unmanaged<nint, nint, nuint, nint, nint>)MessageSend)(receiver, selector, first, second);

    /// <summary>
    /// Creates an NSString the caller owns and has to release.
    /// </summary>
    public static nint CreateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        nint allocated = Send(objc_getClass("NSString"), Selector("alloc"));

        fixed (byte* pointer = bytes)
        {
            // initWithBytes:length:encoding: with NSUTF8StringEncoding, which is 4.
            return ((delegate* unmanaged<nint, nint, byte*, nuint, nuint, nint>)MessageSend)(
                allocated,
                Selector("initWithBytes:length:encoding:"),
                pointer,
                (nuint)bytes.Length,
                4);
        }
    }

    /// <summary>
    /// Reads an NSString the caller does not own.
    /// </summary>
    public static string? ReadString(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        nint utf8 = Send(value, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static void Release(nint value)
    {
        if (value != 0)
        {
            Send(value, Selector("release"));
        }
    }

    /// <summary>
    /// Runs work inside an autorelease pool, which a thread that is not AppKit's own does not have.
    /// </summary>
    public static T WithPool<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        nint pool = objc_autoreleasePoolPush();

        try
        {
            return work();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Calls a block the system handed over, with no argument.
    /// </summary>
    public static void InvokeBlock(nint block)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, void>)BlockInvoke(block))(block);
        }
    }

    /// <summary>
    /// Calls a block the system handed over, with one integer argument.
    /// </summary>
    public static void InvokeBlock(nint block, nuint argument)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, nuint, void>)BlockInvoke(block))(block, argument);
        }
    }

    /// <summary>
    /// The function a block runs. It follows the block's class pointer and two 32 bit fields.
    /// </summary>
    private static nint BlockInvoke(nint block) => Marshal.ReadIntPtr(block, IntPtr.Size + 8);
}

/// <summary>
/// A block literal that captures nothing and lives for the life of the process.
/// </summary>
/// <remarks>
/// Some system calls insist on a completion block even when the caller has no use for the result.
/// A global block is never copied or freed by the runtime, so one allocated once and never released
/// is the whole of its lifetime management.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class GlobalBlock
{
    private const int IsGlobal = 1 << 28;
    private const int HasSignature = 1 << 30;

    private static readonly nint GlobalBlockClass =
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");

    /// <summary>
    /// Builds a block around an unmanaged function whose first parameter is the block itself.
    /// </summary>
    /// <param name="invoke">The function the block runs.</param>
    /// <param name="signature">The Objective-C type encoding of the block, such as v@?B@.</param>
    public static nint Create(nint invoke, string signature) => Create(invoke, signature, 0, false);

    /// <summary>
    /// Builds a block that carries one pointer, which the invoked function reads back with
    /// <see cref="Captured"/>.
    /// </summary>
    /// <remarks>
    /// It is marked global like the others, and that is what makes it safe to hand out: the runtime
    /// neither copies nor frees a global block, so the pointer it carries stays the one that was put
    /// there. The memory is the caller's to keep for as long as the block may still be called.
    /// </remarks>
    public static nint Create(nint invoke, string signature, nint captured) =>
        Create(invoke, signature, captured, true);

    /// <summary>
    /// The pointer a block built by the capturing overload carries.
    /// </summary>
    public static nint Captured(nint block) => Marshal.ReadIntPtr(block, 3 * IntPtr.Size + 8);

    /// <summary>
    /// Clears what a block carries, so a callback that arrives later finds nothing rather than a
    /// pointer whose target has gone.
    /// </summary>
    /// <remarks>
    /// The block itself is not freed. The system may still be holding it, and a global block is the
    /// size of a handful of pointers, so outliving the object it belonged to costs nothing.
    /// </remarks>
    public static void Disown(nint block)
    {
        if (block != 0)
        {
            Marshal.WriteIntPtr(block, 3 * IntPtr.Size + 8, 0);
        }
    }

    private static nint Create(nint invoke, string signature, nint captured, bool carrying)
    {
        nint encoded = Marshal.StringToCoTaskMemUTF8(signature);
        nint size = 3 * IntPtr.Size + 8 + (carrying ? IntPtr.Size : 0);

        // struct { unsigned long reserved; unsigned long size; const char *signature; }
        nint descriptor = Marshal.AllocHGlobal(3 * IntPtr.Size);
        Marshal.WriteIntPtr(descriptor, 0, 0);
        Marshal.WriteIntPtr(descriptor, IntPtr.Size, size);
        Marshal.WriteIntPtr(descriptor, 2 * IntPtr.Size, encoded);

        // struct { void *isa; int flags; int reserved; void *invoke; void *descriptor; void *captured; }
        nint block = Marshal.AllocHGlobal(size);
        Marshal.WriteIntPtr(block, 0, GlobalBlockClass);
        Marshal.WriteInt32(block, IntPtr.Size, IsGlobal | HasSignature);
        Marshal.WriteInt32(block, IntPtr.Size + 4, 0);
        Marshal.WriteIntPtr(block, IntPtr.Size + 8, invoke);
        Marshal.WriteIntPtr(block, 2 * IntPtr.Size + 8, descriptor);

        if (carrying)
        {
            Marshal.WriteIntPtr(block, 3 * IntPtr.Size + 8, captured);
        }

        return block;
    }
}
