using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Where the person said the profiles should live, the first time the application started.
/// </summary>
public enum FirstRunChoice
{
    /// <summary>
    /// On this computer, as every version before server mode kept them.
    /// </summary>
    ThisComputer,

    /// <summary>
    /// On a server. The application restarts into it once the person has signed in.
    /// </summary>
    Server,

    /// <summary>
    /// Not decided; the same as this computer until the person chooses in the settings.
    /// </summary>
    DecideLater,
}

/// <summary>
/// Decides whether the application asks where the profiles should live before its main window.
/// </summary>
public static class FirstRun
{
    /// <summary>
    /// True on a true first start of a copy that shows windows.
    /// </summary>
    /// <param name="settingsFileExistedAtLoad">
    /// Whether the settings file was there before the settings were loaded; the one signal for a
    /// first start. An installation that updates to this version has a file and is never asked.
    /// </param>
    /// <param name="storage">The store this process works on.</param>
    /// <param name="headless">True for a copy started without any window, which has nobody to ask.</param>
    public static bool ShouldAsk(bool? settingsFileExistedAtLoad, IActiveStorage storage, bool headless)
    {
        ArgumentNullException.ThrowIfNull(storage);

        return settingsFileExistedAtLoad == false && !storage.IsServerMode && !headless;
    }
}
