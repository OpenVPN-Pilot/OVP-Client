using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// The answer of <c>GET /api/v1/server/info</c>, asked before anything else.
/// </summary>
/// <param name="Name">Always <see cref="ServerInfoResponse.ExpectedName"/> for an OpenVPN Pilot Server.</param>
/// <param name="Version">The server's own version.</param>
/// <param name="ApiVersion">The API version it speaks.</param>
/// <param name="MinimumClientVersion">Clients older than this are refused.</param>
/// <param name="AuthMode">One of <see cref="ServerAuthModes"/>.</param>
/// <param name="PasswordRequired">False in modes <c>none</c> and <c>entra</c>: no password field.</param>
/// <param name="Entra">Only in mode <c>entra</c>.</param>
public sealed record ServerInfoResponse(
    string Name,
    string Version,
    string ApiVersion,
    string MinimumClientVersion,
    string AuthMode,
    bool PasswordRequired,
    EntraInfoResponse? Entra)
{
    /// <summary>
    /// The name every OpenVPN Pilot Server reports. Anything else is a different service.
    /// </summary>
    public const string ExpectedName = "OpenVPN Pilot Server";
}

/// <summary>
/// What the Microsoft sign in needs, exactly as the server publishes it.
/// </summary>
public sealed record EntraInfoResponse(string TenantId, string ClientId, string Scope, string Authority);

/// <summary>
/// The sign in modes <c>server/info</c> can report.
/// </summary>
public static class ServerAuthModes
{
    public const string None = "none";
    public const string File = "file";
    public const string Ldap = "ldap";
    public const string Entra = "entra";
}

/// <summary>
/// The roles a signed in user can have.
/// </summary>
public static class ServerRoles
{
    public const string Admin = "admin";
    public const string User = "user";
}

/// <summary>
/// <c>POST /api/v1/auth/login</c>. The password is ignored in mode <c>none</c>.
/// </summary>
public sealed record LoginRequest(string Username, string? Password)
{
    // Keeps the password out of anything that prints the record, a log line included.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Username = ").Append(Username);
        return true;
    }
}

/// <summary>
/// <c>POST /api/v1/auth/entra/exchange</c> with the access token Entra issued.
/// </summary>
public sealed record EntraExchangeRequest(string AccessToken)
{
    public override string ToString() => $"{nameof(EntraExchangeRequest)} {{ }}";
}

/// <summary>
/// <c>POST /api/v1/auth/refresh</c>. The token sent is used up.
/// </summary>
public sealed record RefreshRequest(string RefreshToken)
{
    public override string ToString() => $"{nameof(RefreshRequest)} {{ }}";
}

/// <summary>
/// <c>POST /api/v1/auth/logout</c>, ending this installation's session.
/// </summary>
public sealed record LogoutRequest(string RefreshToken)
{
    public override string ToString() => $"{nameof(LogoutRequest)} {{ }}";
}

/// <summary>
/// The answer of every successful sign in, Entra exchange and refresh.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    CurrentUserResponse User)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessTokenExpiresAt = ").Append(AccessTokenExpiresAt.ToString("O", CultureInfo.InvariantCulture))
            .Append(", RefreshTokenExpiresAt = ").Append(RefreshTokenExpiresAt.ToString("O", CultureInfo.InvariantCulture))
            .Append(", User = ").Append(User);
        return true;
    }
}

/// <summary>
/// The signed in user, as sign in, refresh and <c>GET /api/v1/auth/me</c> report it.
/// </summary>
/// <param name="Id">Stable on this server.</param>
/// <param name="Username">The user name.</param>
/// <param name="DisplayName">A name to show, when the identity provider has one.</param>
/// <param name="Role">One of <see cref="ServerRoles"/>.</param>
/// <param name="Provider">One of <see cref="ServerAuthModes"/>: how the user signed in.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string Role,
    string Provider)
{
    /// <summary>
    /// True for the role that may change profiles, tags and the vault.
    /// </summary>
    [JsonIgnore]
    public bool IsAdministrator => string.Equals(Role, ServerRoles.Admin, StringComparison.Ordinal);
}
