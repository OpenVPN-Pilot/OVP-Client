namespace OpenVpnPilot.Core.Settings;

/// <summary>
/// Everything the user can configure, held as one object and stored as one JSON file.
/// </summary>
/// <remarks>
/// The file is meant to be readable and editable by hand, which is why the sections are nested
/// objects with spelled out names rather than a flat list of abbreviations. Every property has a
/// working default, so a missing file, a missing section or a missing key all behave the same way.
/// </remarks>
public sealed class PilotSettings
{
    public GeneralSettings General { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public ConnectionSettings Connections { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public CredentialSettings Credentials { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    /// <summary>
    /// Produces an independent copy, so a screen can edit settings without the change taking effect
    /// until it is saved.
    /// </summary>
    public PilotSettings Clone() => new()
    {
        General = General.Clone(),
        Appearance = Appearance.Clone(),
        Connections = Connections.Clone(),
        Notifications = Notifications.Clone(),
        Credentials = Credentials.Clone(),
        Advanced = Advanced.Clone(),
    };
}

public sealed class GeneralSettings
{
    /// <summary>
    /// Language code such as en or de. Null follows the operating system.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Start with the window hidden, leaving only the tray icon.
    /// </summary>
    public bool StartMinimised { get; set; }

    /// <summary>
    /// Start with the operating system. Implemented by the platform layer.
    /// </summary>
    public bool StartWithSystem { get; set; }

    /// <summary>
    /// Closing the window hides it instead of ending the application, so tunnels keep running.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Set once the shipped shortcut defaults have been written.
    /// </summary>
    /// <remarks>
    /// Without this the defaults would return on every start, and a shortcut the user cleared on
    /// purpose would come back with it.
    /// </remarks>
    public bool HotkeyDefaultsApplied { get; set; }

    public GeneralSettings Clone() => (GeneralSettings)MemberwiseClone();
}

public sealed class AppearanceSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public AppearanceSettings Clone() => (AppearanceSettings)MemberwiseClone();
}

public enum ThemePreference
{
    /// <summary>
    /// Follows the operating system setting.
    /// </summary>
    System,

    Light,

    Dark,
}

public sealed class ConnectionSettings
{
    /// <summary>
    /// Ignore pushed routing and DNS options so the host keeps its own default route. Applies to
    /// profiles that do not override it.
    /// </summary>
    public bool ProtectRoutes { get; set; } = true;

    /// <summary>
    /// How long a connection may stay in a pre connected state before it is treated as failed.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Re-establish a tunnel that dropped, rather than leaving it to OpenVPN's own retry alone.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Attempts before the tunnel is given up on. Zero means never give up.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 5;

    /// <summary>
    /// Delay before the first retry. Later attempts back off from this value.
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Reconnect the tunnels that were up when the application last closed.
    /// </summary>
    public bool RestoreOnStart { get; set; }

    public ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    public bool OnConnected { get; set; } = true;

    public bool OnDisconnected { get; set; }

    public bool OnConnectionLost { get; set; } = true;

    public bool OnReconnecting { get; set; }

    public bool OnFailed { get; set; } = true;

    public NotificationSettings Clone() => (NotificationSettings)MemberwiseClone();
}

public sealed class CredentialSettings
{
    /// <summary>
    /// Tick the remember option in the credential prompt by default.
    /// </summary>
    public bool RememberByDefault { get; set; } = true;

    /// <summary>
    /// Try a stored secret before prompting. Turning this off makes every connection ask.
    /// </summary>
    public bool UseStoredSecrets { get; set; } = true;

    public CredentialSettings Clone() => (CredentialSettings)MemberwiseClone();
}

public sealed class AdvancedSettings
{
    /// <summary>
    /// Full path to the openvpn executable. Null uses the one the installation registers.
    /// </summary>
    public string? OpenVpnPath { get; set; }

    /// <summary>
    /// OpenVPN verbosity passed as --verb. Three is enough for the state and log stream.
    /// </summary>
    public int OpenVpnVerbosity { get; set; } = 3;

    /// <summary>
    /// Minimum level written to the application log.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Keep the database, logs and settings beside the executable instead of under the user profile.
    /// </summary>
    public bool PortableMode { get; set; }

    /// <summary>
    /// Look for a newer release on start. Off by default.
    /// </summary>
    /// <remarks>
    /// Checking contacts a third party, so it is something the user turns on rather than something
    /// they have to discover and turn off.
    /// </remarks>
    public bool CheckForUpdates { get; set; }

    /// <summary>
    /// The repository to check, in the form owner/name. Empty means no check is made.
    /// </summary>
    public string? UpdateRepository { get; set; }

    public AdvancedSettings Clone() => (AdvancedSettings)MemberwiseClone();
}
