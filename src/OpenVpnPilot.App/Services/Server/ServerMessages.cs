using System.Globalization;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Puts what a server answered into plain words, one sentence per thing a person can do about it.
/// </summary>
/// <remarks>
/// Branches on the outcome and the problem's code, never on the server's own text, which may change.
/// The one exception is the clock: the server's detail names both times, and that is exactly what the
/// person needs to see. A certificate that is not trusted is described as the configuration problem
/// it is, with nothing offered to get past it.
/// </remarks>
public static class ServerMessages
{
    /// <summary>
    /// Why a sign in did not succeed.
    /// </summary>
    public static string SignInFailure(ILocalizer localizer, ServerResult result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            ServerOutcome.Offline => localizer["signIn.offline"],
            ServerOutcome.TlsRefused => localizer["signIn.certificateUntrusted"],
            ServerOutcome.Wiped => localizer["signIn.accountRevoked"],
            ServerOutcome.InvalidResponse => localizer["signIn.notPilotServer"],
            ServerOutcome.NotSignedIn => localizer["signIn.notSignedIn"],
            ServerOutcome.IdentityUnavailable => localizer["signIn.identityUnavailable"],
            _ => Problem(localizer, result),
        };
    }

    /// <summary>
    /// Why a server cannot be used, after asking it about itself; null when it can.
    /// </summary>
    public static string? CheckFailure(ILocalizer localizer, ServerCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(check);

        return check.Compatibility switch
        {
            ServerCompatibility.Compatible => null,
            ServerCompatibility.NotAPilotServer => localizer["signIn.notPilotServer"],
            ServerCompatibility.ApiVersionUnsupported => localizer["signIn.apiVersionUnsupported"],
            ServerCompatibility.ClientOutdated => localizer.Translate(
                "signIn.clientOutdatedVersion",
                check.Info?.MinimumClientVersion ?? string.Empty,
                check.ClientVersion.ToString(3)),
            _ => SignInFailure(localizer, check.Transport),
        };
    }

    /// <summary>
    /// The line that lets the server's operator find the request in their log, or null without one.
    /// </summary>
    public static string? Reference(ILocalizer localizer, ServerResult result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);

        return string.IsNullOrEmpty(result.RequestId) || result.Outcome is ServerOutcome.Offline or ServerOutcome.TlsRefused
            ? null
            : localizer.Translate("signIn.reference", result.RequestId);
    }

    /// <summary>
    /// How a server lets people sign in, as a person would say it.
    /// </summary>
    public static string AuthMode(ILocalizer localizer, string authMode)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        return authMode switch
        {
            ServerAuthModes.None => localizer["signIn.modeNone"],
            ServerAuthModes.File => localizer["signIn.modeFile"],
            ServerAuthModes.Ldap => localizer["signIn.modeLdap"],
            ServerAuthModes.Entra => localizer["signIn.modeEntra"],
            _ => authMode,
        };
    }

    private static string Problem(ILocalizer localizer, ServerResult result) => result.Code switch
    {
        ServerErrorCodes.InvalidCredentials => localizer["signIn.invalidCredentials"],
        ServerErrorCodes.Forbidden => localizer["signIn.forbidden"],
        ServerErrorCodes.ProviderUnavailable => localizer["signIn.providerUnavailable"],
        ServerErrorCodes.TooManyRequests => result.RetryAfter is { } wait
            ? localizer.Translate("signIn.tooMany", Seconds(wait))
            : localizer["signIn.tooManyNoTime"],
        ServerErrorCodes.ClockSkew => localizer.Translate("signIn.clockSkew", result.Problem?.Detail ?? string.Empty),
        ServerErrorCodes.IdentityConflict => localizer["signIn.identityConflict"],
        ServerErrorCodes.ModeMismatch => localizer["signIn.modeMismatch"],
        ServerErrorCodes.ClientOutdated => localizer["signIn.clientOutdated"],
        ServerErrorCodes.ApiVersionUnsupported => localizer["signIn.apiVersionUnsupported"],
        ServerErrorCodes.HttpsRequired => localizer["signIn.httpsRequired"],
        ServerErrorCodes.AccountRevoked => localizer["signIn.accountRevoked"],
        null => localizer.Translate("signIn.failedStatus", result.Status?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        { } code => localizer.Translate("signIn.failedCode", code),
    };

    // Rounded up, so a person who waits as long as they are told is never refused again.
    private static string Seconds(TimeSpan wait) =>
        ((long)Math.Ceiling(Math.Max(wait.TotalSeconds, 1))).ToString(CultureInfo.CurrentCulture);
}
