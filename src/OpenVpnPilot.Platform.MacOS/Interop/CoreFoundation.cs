using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenVpnPilot.Platform.MacOS.Interop;

/// <summary>
/// The Core Foundation calls the keychain needs, and the conversions to and from managed values.
/// </summary>
/// <remarks>
/// Every object returned by a Create or Copy function belongs to the caller and has to be released.
/// <see cref="CoreFoundationHandle"/> does that, so no path through a call can leak one.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe partial class CoreFoundation
{
    private const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly nint LibraryHandle = NativeLibrary.Load(Library);

    /// <summary>
    /// The address of the dictionary key callbacks that retain and release Core Foundation objects.
    /// </summary>
    public static readonly nint TypeDictionaryKeyCallBacks =
        NativeLibrary.GetExport(LibraryHandle, "kCFTypeDictionaryKeyCallBacks");

    public static readonly nint TypeDictionaryValueCallBacks =
        NativeLibrary.GetExport(LibraryHandle, "kCFTypeDictionaryValueCallBacks");

    public static readonly nint BooleanTrue = ReadConstant("kCFBooleanTrue");

    [LibraryImport(Library)]
    public static partial void CFRelease(nint value);

    [LibraryImport(Library)]
    public static partial nint CFGetTypeID(nint value);

    [LibraryImport(Library)]
    public static partial nint CFStringGetTypeID();

    [LibraryImport(Library)]
    public static partial nint CFDataGetTypeID();

    [LibraryImport(Library)]
    public static partial nint CFDictionaryGetTypeID();

    [LibraryImport(Library)]
    public static partial nint CFArrayGetTypeID();

    [LibraryImport(Library)]
    private static partial nint CFStringCreateWithCharacters(nint allocator, char* characters, nint length);

    [LibraryImport(Library)]
    private static partial nint CFStringGetLength(nint value);

    [LibraryImport(Library)]
    private static partial void CFStringGetCharacters(nint value, Range range, char* buffer);

    [LibraryImport(Library)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(Library)]
    private static partial nint CFDataGetLength(nint value);

    [LibraryImport(Library)]
    private static partial byte* CFDataGetBytePtr(nint value);

    [LibraryImport(Library)]
    private static partial nint CFDictionaryCreateMutable(
        nint allocator,
        nint capacity,
        nint keyCallBacks,
        nint valueCallBacks);

    [LibraryImport(Library)]
    public static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);

    [LibraryImport(Library)]
    public static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(Library)]
    public static partial nint CFArrayGetCount(nint array);

    [LibraryImport(Library)]
    public static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    /// <summary>
    /// Reads a constant the framework exports as a global variable holding an object reference.
    /// </summary>
    public static nint ReadConstant(string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(LibraryHandle, name));

    public static CoreFoundationHandle CreateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        fixed (char* characters = value)
        {
            return new CoreFoundationHandle(CFStringCreateWithCharacters(0, characters, value.Length));
        }
    }

    /// <summary>
    /// Copies the bytes into a new data object. The caller clears its own copy.
    /// </summary>
    public static CoreFoundationHandle CreateData(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return new CoreFoundationHandle(CFDataCreate(0, pointer, bytes.Length));
        }
    }

    public static CoreFoundationHandle CreateMutableDictionary() =>
        new(CFDictionaryCreateMutable(0, 0, TypeDictionaryKeyCallBacks, TypeDictionaryValueCallBacks));

    /// <summary>
    /// Reads a string the caller does not own, or null when the object is not a string.
    /// </summary>
    public static string? ReadString(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return null;
        }

        nint length = CFStringGetLength(value);

        if (length == 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length];

        fixed (char* target = buffer)
        {
            CFStringGetCharacters(value, new Range(0, length), target);
        }

        return new string(buffer);
    }

    /// <summary>
    /// Copies the bytes out of a data object the caller does not own.
    /// </summary>
    public static byte[]? ReadData(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFDataGetTypeID())
        {
            return null;
        }

        int length = checked((int)CFDataGetLength(value));
        byte[] bytes = new byte[length];

        if (length > 0)
        {
            new ReadOnlySpan<byte>(CFDataGetBytePtr(value), length).CopyTo(bytes);
        }

        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Range
    {
        public Range(nint location, nint length)
        {
            Location = location;
            Length = length;
        }

        public nint Location { get; }

        public nint Length { get; }
    }
}

/// <summary>
/// Owns one Core Foundation object and releases it exactly once.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class CoreFoundationHandle : IDisposable
{
    public CoreFoundationHandle(nint value)
    {
        if (value == 0)
        {
            throw new InvalidOperationException("Core Foundation could not create an object.");
        }

        Value = value;
    }

    public nint Value { get; private set; }

    public void Dispose()
    {
        if (Value != 0)
        {
            CoreFoundation.CFRelease(Value);
            Value = 0;
        }
    }
}

/// <summary>
/// Text helpers shared by the framework calls.
/// </summary>
internal static class NativeText
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
