using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The installation's identity, as the settings file keeps it.
/// </summary>
/// <remarks>
/// Read at the moment a call is made rather than when this is built, because the container is
/// composed before the settings are loaded and the identity is minted while they load.
/// </remarks>
public sealed class SettingsInstallationId : IInstallationIdProvider
{
    private readonly ISettingsService settings;

    public SettingsInstallationId(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <exception cref="InvalidOperationException">
    /// The settings carry no identity, which happens only while a file that exists could not be read.
    /// Inventing one would introduce this installation to the server as a stranger, so nothing is sent.
    /// </exception>
    public Guid InstallationId =>
        settings.Current.Installation.Id is { } id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException(
                "The settings carry no installation identity, so no server can be asked anything that needs one.");

    /// <summary>
    /// True when a call that needs the identity can be made.
    /// </summary>
    public bool IsAvailable => settings.Current.Installation.Id is { } id && id != Guid.Empty;
}
