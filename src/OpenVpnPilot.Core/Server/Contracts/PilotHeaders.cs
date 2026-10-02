namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// The header names and fixed values of the contract.
/// </summary>
public static class PilotHeaders
{
    public const string ClientVersion = "X-Pilot-Client-Version";
    public const string ApiVersion = "X-Pilot-Api-Version";
    public const string ClientId = "X-Pilot-Client-Id";
    public const string Platform = "X-Pilot-Platform";
    public const string Timestamp = "X-Pilot-Timestamp";
    public const string RequestId = "X-Pilot-Request-Id";
    public const string ServerVersion = "X-Pilot-Server-Version";

    /// <summary>
    /// Present with the value <see cref="WipeDirective"/> when the account is gone.
    /// </summary>
    public const string Directive = "X-Pilot-Directive";

    public const string WipeDirective = "wipe";

    /// <summary>
    /// The one API version this client speaks.
    /// </summary>
    public const string CurrentApiVersion = "1";

    /// <summary>
    /// <c>If-Match</c> that matches whatever the server holds, which is how an offline change is
    /// pushed without asking.
    /// </summary>
    public const string MatchAny = "*";
}
