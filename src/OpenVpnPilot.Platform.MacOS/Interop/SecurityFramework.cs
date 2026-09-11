using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenVpnPilot.Platform.MacOS.Interop;

/// <summary>
/// The keychain item calls of the Security framework.
/// </summary>
/// <remarks>
/// These are used directly rather than through the security command line tool. The tool takes a
/// secret as an argument, and every argument of every process is visible to anyone who lists the
/// processes on the machine.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static partial class SecurityFramework
{
    private const string Library = "/System/Library/Frameworks/Security.framework/Security";

    public const int Success = 0;

    /// <summary>
    /// errSecItemNotFound: nothing matched the query.
    /// </summary>
    public const int ItemNotFound = -25300;

    /// <summary>
    /// errSecDuplicateItem: an item with the same identity already exists.
    /// </summary>
    public const int DuplicateItem = -25299;

    /// <summary>
    /// errSecUserCanceled: the person dismissed the keychain's own prompt.
    /// </summary>
    public const int UserCanceled = -128;

    /// <summary>
    /// errSecAuthFailed: the keychain refused, for example because access was denied.
    /// </summary>
    public const int AuthFailed = -25293;

    /// <summary>
    /// errSecInteractionNotAllowed: the keychain would have to ask, and asking is not possible.
    /// </summary>
    public const int InteractionNotAllowed = -25308;

    private static readonly nint LibraryHandle = NativeLibrary.Load(Library);

    public static readonly nint Class = ReadConstant("kSecClass");
    public static readonly nint ClassGenericPassword = ReadConstant("kSecClassGenericPassword");
    public static readonly nint AttributeService = ReadConstant("kSecAttrService");
    public static readonly nint AttributeAccount = ReadConstant("kSecAttrAccount");
    public static readonly nint AttributeLabel = ReadConstant("kSecAttrLabel");
    public static readonly nint AttributeDescription = ReadConstant("kSecAttrDescription");
    public static readonly nint ValueData = ReadConstant("kSecValueData");
    public static readonly nint ReturnData = ReadConstant("kSecReturnData");
    public static readonly nint ReturnAttributes = ReadConstant("kSecReturnAttributes");
    public static readonly nint MatchLimit = ReadConstant("kSecMatchLimit");
    public static readonly nint MatchLimitOne = ReadConstant("kSecMatchLimitOne");
    public static readonly nint MatchLimitAll = ReadConstant("kSecMatchLimitAll");

    [LibraryImport(Library)]
    public static partial int SecItemAdd(nint attributes, out nint result);

    [LibraryImport(Library)]
    public static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(Library)]
    public static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(Library)]
    public static partial int SecItemDelete(nint query);

    [LibraryImport(Library)]
    private static partial nint SecCopyErrorMessageString(int status, nint reserved);

    /// <summary>
    /// The framework's own description of a status, for a log line.
    /// </summary>
    public static string DescribeStatus(int status)
    {
        nint message = SecCopyErrorMessageString(status, 0);

        if (message == 0)
        {
            return $"OSStatus {status}";
        }

        try
        {
            return $"{CoreFoundation.ReadString(message)} (OSStatus {status})";
        }
        finally
        {
            CoreFoundation.CFRelease(message);
        }
    }

    private static nint ReadConstant(string name) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(LibraryHandle, name));
}
