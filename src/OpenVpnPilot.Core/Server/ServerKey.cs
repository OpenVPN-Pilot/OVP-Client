using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The one form of a server's address, and the short key its local copy is filed under.
/// </summary>
/// <remarks>
/// The local copy of a server lives in <c>servers/&lt;key&gt;/</c> under the data directory, so two
/// spellings of one address must give one key, or the same server would get a second, empty copy
/// the moment somebody typed its name in capitals. Normalising here, in one place, is what makes the
/// application, the companion command and the wipe that removes the folder agree on it.
///
/// The normal form is <c>https://host</c> or <c>https://host:port</c>:
/// <list type="bullet">
/// <item>Only <c>https</c>. The server refuses anything else, and the contract carries passwords,
/// tokens and private keys.</item>
/// <item>The host in lower case, an international name in its ASCII form.</item>
/// <item>The port only when it is not 443.</item>
/// <item>No path, query, fragment or user information. A trailing slash is dropped; anything else
/// after the host is refused rather than dropped, because every call goes to <c>/api/v1</c> from
/// the root, and an address that names a path promises something the client would not keep.</item>
/// </list>
///
/// The key is the first 16 bytes of the SHA-256 of the normal form's UTF-8 bytes, in lower case hex:
/// 32 characters that are safe as a directory name on every system and say nothing about the server
/// to someone reading the folder names.
/// </remarks>
public static class ServerKey
{
    private const int KeyBytes = 16;

    /// <summary>
    /// Brings an address into its normal form.
    /// </summary>
    /// <param name="address">What a person typed or a file holds.</param>
    /// <param name="normalised">The normal form, when the address is usable.</param>
    /// <param name="problem">Why it is not, otherwise <see cref="ServerAddressProblem.None"/>.</param>
    public static bool TryNormalise(
        string? address,
        [NotNullWhen(true)] out string? normalised,
        out ServerAddressProblem problem)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            problem = ServerAddressProblem.Empty;
            return false;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
        {
            problem = ServerAddressProblem.NotAnAddress;
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            problem = ServerAddressProblem.NotHttps;
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            problem = ServerAddressProblem.CarriesCredentials;
            return false;
        }

        if (uri.AbsolutePath != "/")
        {
            problem = ServerAddressProblem.CarriesPath;
            return false;
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = ServerAddressProblem.CarriesQuery;
            return false;
        }

        // An address literal keeps its brackets, which the port would otherwise run into.
        string host = uri.HostNameType == UriHostNameType.IPv6
            ? uri.Host.ToLowerInvariant()
            : uri.IdnHost.ToLowerInvariant();

        string port = uri.IsDefaultPort
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $":{uri.Port}");

        normalised = $"https://{host}{port}";
        problem = ServerAddressProblem.None;
        return true;
    }

    /// <summary>
    /// The key a server's local copy is filed under.
    /// </summary>
    /// <param name="address">The server's address, in any spelling <see cref="TryNormalise"/> accepts.</param>
    /// <param name="key">32 lower case hexadecimal characters.</param>
    /// <param name="problem">Why the address is not usable, otherwise <see cref="ServerAddressProblem.None"/>.</param>
    public static bool TryCompute(
        string? address,
        [NotNullWhen(true)] out string? key,
        out ServerAddressProblem problem)
    {
        if (!TryNormalise(address, out string? normalised, out problem))
        {
            key = null;
            return false;
        }

        key = OfNormalised(normalised);
        return true;
    }

    /// <summary>
    /// The key of an address that must be usable, such as one this application stored itself.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not usable; the message says why.</exception>
    public static string Compute(string address)
    {
        if (!TryCompute(address, out string? key, out ServerAddressProblem problem))
        {
            throw new ArgumentException($"The server address is not usable: {problem}.", nameof(address));
        }

        return key;
    }

    private static string OfNormalised(string normalised)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash, 0, KeyBytes);
    }
}

/// <summary>
/// Why an address cannot be used for a server.
/// </summary>
public enum ServerAddressProblem
{
    None,

    /// <summary>
    /// Nothing was given.
    /// </summary>
    Empty,

    /// <summary>
    /// Not an absolute address, for example a host name without <c>https://</c> in front.
    /// </summary>
    NotAnAddress,

    /// <summary>
    /// <c>http://</c> or any other scheme. The server accepts nothing but HTTPS.
    /// </summary>
    NotHttps,

    /// <summary>
    /// A user name or password in the address, which would end up in the settings file.
    /// </summary>
    CarriesCredentials,

    /// <summary>
    /// Something after the host other than a single slash.
    /// </summary>
    CarriesPath,

    /// <summary>
    /// A query or a fragment.
    /// </summary>
    CarriesQuery,
}
