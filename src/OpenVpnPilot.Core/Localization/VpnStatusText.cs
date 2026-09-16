using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Turns the reason a connection ended into the words a user reads.
/// </summary>
/// <remarks>
/// One place decides this, because the reason appears in the window, in the notification and in the
/// status line, and three copies of the same decision would drift. What it decides is small: a
/// reason this client wrote is translated, and anything else is the message as it stands, because
/// text from OpenVPN is English wherever it is read and pretending otherwise would only hide it.
/// </remarks>
public static class VpnStatusText
{
    /// <summary>
    /// What to show for a status, translated where that is possible.
    /// </summary>
    public static string Describe(this ILocalizer localizer, VpnConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(status);

        if (status.Reason is not { } reason)
        {
            return status.Message;
        }

        object?[] arguments = [.. reason.Arguments];

        return localizer.Translate(KeyOf(reason.Code), arguments);
    }

    private static string KeyOf(VpnStatusReasonCode code) => code switch
    {
        VpnStatusReasonCode.PushedCompressionRefused => "reason.pushedCompressionRefused",
        VpnStatusReasonCode.ConnectTimedOut => "reason.connectTimedOut",
        VpnStatusReasonCode.CredentialsRejected => "reason.credentialsRejected",
        VpnStatusReasonCode.CredentialsMissing => "reason.credentialsMissing",

        // A code added without a text of its own would otherwise resolve to nothing at all, and a
        // failure that says nothing is worse than one that names itself.
        _ => "reason.unknown",
    };
}
