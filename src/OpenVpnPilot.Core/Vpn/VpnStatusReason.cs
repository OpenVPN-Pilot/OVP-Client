namespace OpenVpnPilot.Core.Vpn;

/// <summary>
/// A reason this client wrote itself, in a form that can be translated.
/// </summary>
/// <remarks>
/// Most of what reaches <see cref="VpnConnectionStatus.Message"/> is text from OpenVPN or from an
/// exception, which is English wherever it is read and cannot be translated by anyone. The few
/// reasons this client writes are different, and they are the ones a user meets most often, so they
/// travel as a code with its arguments and become words where a localizer exists.
///
/// The English sentence stays in the message beside it. The log, the session history and the command
/// line have no localizer to ask, and a reason that only the window can read would leave all three
/// with nothing.
/// </remarks>
public sealed record VpnStatusReason
{
    public required VpnStatusReasonCode Code { get; init; }

    /// <summary>
    /// What fills the placeholders of the translated text, in the order they appear.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>
/// The reasons this client writes itself, as opposed to those it passes on.
/// </summary>
public enum VpnStatusReasonCode
{
    /// <summary>
    /// The server pushed a compression setting the client refused, taking the whole push reply with it.
    /// </summary>
    PushedCompressionRefused,

    /// <summary>
    /// The tunnel did not come up within the time allowed. The argument is that time in seconds.
    /// </summary>
    ConnectTimedOut,

    /// <summary>
    /// The server rejected the credentials that were offered. The argument is the realm.
    /// </summary>
    CredentialsRejected,

    /// <summary>
    /// Nothing was available to answer the credential request with, which covers a dismissed prompt
    /// and an empty store alike. The argument is the realm.
    /// </summary>
    CredentialsMissing,
}
