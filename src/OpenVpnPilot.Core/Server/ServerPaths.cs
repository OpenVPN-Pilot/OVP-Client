using System.Globalization;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The contract's paths, relative to the server's address.
/// </summary>
internal static class ServerPaths
{
    public const string Info = "api/v1/server/info";
    public const string HealthReady = "health/ready";

    public const string Login = "api/v1/auth/login";
    public const string EntraExchange = "api/v1/auth/entra/exchange";
    public const string Refresh = "api/v1/auth/refresh";
    public const string Logout = "api/v1/auth/logout";
    public const string Me = "api/v1/auth/me";

    public const string Profiles = "api/v1/profiles";
    public const string ProfileBatch = "api/v1/profiles/batch";
    public const string Tags = "api/v1/tags";
    public const string Vault = "api/v1/vault";
    public const string Users = "api/v1/users";

    public const string Favourites = "api/v1/me/favourites";
    public const string Hotkeys = "api/v1/me/hotkeys";
    public const string Settings = "api/v1/me/settings";

    public static string Changes(long since) =>
        "api/v1/sync/changes?since=" + since.ToString(CultureInfo.InvariantCulture);

    public static string ProfileList(string? tag, string? search)
    {
        List<string> query = [];

        if (!string.IsNullOrEmpty(tag))
        {
            query.Add("tag=" + Uri.EscapeDataString(tag));
        }

        if (!string.IsNullOrEmpty(search))
        {
            query.Add("search=" + Uri.EscapeDataString(search));
        }

        return query.Count == 0 ? Profiles : Profiles + "?" + string.Join('&', query);
    }

    public static string Profile(Guid id) => $"{Profiles}/{id:D}";

    public static string Configuration(Guid id) => $"{Profiles}/{id:D}/configuration";

    public static string ProfileVault(Guid id) => $"{Profiles}/{id:D}/vault";

    /// <summary>
    /// A realm may contain anything, a slash included, and is sent exactly as it is, escaped.
    /// </summary>
    public static string VaultEntry(Guid id, string realm)
    {
        ArgumentException.ThrowIfNullOrEmpty(realm);
        return $"{Profiles}/{id:D}/vault/{Uri.EscapeDataString(realm)}";
    }

    public static string Tag(Guid id) => $"{Tags}/{id:D}";

    public static string User(Guid id) => $"{Users}/{id:D}";

    public static string DisableUser(Guid id) => $"{Users}/{id:D}/disable";

    public static string EnableUser(Guid id) => $"{Users}/{id:D}/enable";
}
