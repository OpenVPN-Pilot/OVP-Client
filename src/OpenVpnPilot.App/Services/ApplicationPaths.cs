namespace OpenVpnPilot.App.Services;

/// <summary>
/// Resolves the locations the application stores data in.
/// </summary>
/// <remarks>
/// Kept behind an interface so that a portable mode, which keeps everything beside the executable,
/// can be added without touching the components that use these paths.
/// </remarks>
public interface IApplicationPaths
{
    /// <summary>
    /// Directory holding the profile database and the log files.
    /// </summary>
    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string LogDirectory { get; }
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

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "pilot.db");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}
