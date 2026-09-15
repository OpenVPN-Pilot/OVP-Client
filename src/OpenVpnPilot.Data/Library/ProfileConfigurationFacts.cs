using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.Data.Library;

/// <summary>
/// Sets a profile's configuration together with everything the list reads from it.
/// </summary>
/// <remarks>
/// The server, the port, whether a sign in is needed and whether the profile can run on its own are
/// all read from the configuration, and a profile whose configuration changed without them lists
/// the old server. Editing a profile and taking a colleague's change from a shared library are the
/// two ways a configuration changes after it was imported, and both come through here so that either
/// looks exactly as the same file imported fresh would.
/// </remarks>
public static class ProfileConfigurationFacts
{
    public static void Apply(Profile profile, string configuration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configuration);

        OvpnConfiguration parsed = OvpnConfigParser.Parse(configuration);
        OvpnRemote? remote = parsed.Remotes.Count > 0 ? parsed.Remotes[0] : null;

        profile.Configuration = configuration;
        profile.ContentHash = ProfileImporter.ComputeHash(configuration);
        profile.RemoteHost = remote?.Host;
        profile.RemotePort = remote?.Port;
        profile.Protocol = remote?.Protocol.ToString().ToLowerInvariant();
        profile.RequiresCredentials = parsed.RequiresUserCredentials;
        profile.IsSelfContained = parsed.IsSelfContained;
        profile.HasUnsupportedOptions = parsed.ScriptOptions.Count > 0;
    }
}
