namespace OpenVpnPilot.Core.Storage;

/// <summary>
/// Resolves the locations the application stores data in.
/// </summary>
/// <remarks>
/// Kept behind an interface so that a portable mode, which keeps everything beside the executable,
/// can be added without touching the components that use these paths.
///
/// It lives here rather than in the application because the companion command reads and writes the
/// same store, the same settings and the same protected credentials. Two copies of these paths would
/// be two places to change, and one of them would eventually be missed.
/// </remarks>
public interface IApplicationPaths
{
    /// <summary>
    /// Directory holding the profile database and the log files.
    /// </summary>
    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string LogDirectory { get; }

    /// <summary>
    /// The settings file, stored as JSON so it stays editable by hand.
    /// </summary>
    public string SettingsPath { get; }

    /// <summary>
    /// Protected credential storage. Nothing here is readable without the current user's key.
    /// </summary>
    public string SecretsDirectory { get; }

    /// <summary>
    /// Language files that ship with the application.
    /// </summary>
    public string InstalledLanguageDirectory { get; }

    /// <summary>
    /// Language files the user adds, which override the installed ones key by key.
    /// </summary>
    public string UserLanguageDirectory { get; }
}

/// <summary>
/// Stores data under the current user's local application data directory.
/// </summary>
public sealed class UserApplicationPaths : IApplicationPaths
{
    public UserApplicationPaths()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenVpnPilot");

        InstalledLanguageDirectory = Path.Combine(AppContext.BaseDirectory, "lang");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);

        // Created eagerly so a user who wants to add a translation finds the place to put it.
        Directory.CreateDirectory(UserLanguageDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "pilot.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    public string InstalledLanguageDirectory { get; }

    public string UserLanguageDirectory => Path.Combine(DataDirectory, "lang");
}
