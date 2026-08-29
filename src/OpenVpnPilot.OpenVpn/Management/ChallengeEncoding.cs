using System.Text;

namespace OpenVpnPilot.OpenVpn.Management;

/// <summary>
/// Encodes one time code answers into the password field the management interface expects.
/// </summary>
/// <remarks>
/// OpenVPN carries a challenge response inside the password rather than in a field of its own, and
/// the encoding differs between the two kinds. A static challenge is answered in the same attempt
/// that raised it, so the password and the code travel together. A dynamic challenge is answered in
/// a fresh attempt that quotes the state identifier the server issued, and the original password is
/// not repeated.
/// </remarks>
public static class ChallengeEncoding
{
    /// <summary>
    /// Builds the password field for a static challenge: SCRV1:base64(password):base64(response).
    /// </summary>
    public static string ForStaticChallenge(string password, string response)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(response);

        return "SCRV1:" + ToBase64(password) + ":" + ToBase64(response);
    }

    /// <summary>
    /// Builds the password field for a dynamic challenge: CRV1::state::response.
    /// </summary>
    /// <remarks>
    /// The empty flag and user name fields are part of the format. The server matches the answer by
    /// the state identifier it issued with the challenge, so that value must be passed back exactly.
    /// </remarks>
    public static string ForDynamicChallenge(string stateId, string response)
    {
        ArgumentNullException.ThrowIfNull(stateId);
        ArgumentNullException.ThrowIfNull(response);

        return "CRV1::" + stateId + "::" + response;
    }

    private static string ToBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
