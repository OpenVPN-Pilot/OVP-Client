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
    /// <summary>
    /// The newest layout this build knows how to write.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Which layout the file was written by. Zero for a file written before this existed.
    /// </summary>
    /// <remarks>
    /// Adding a setting needs nothing here: a key that is missing takes the property's default. This
    /// is for the other case, where the default of an existing setting changes. Without a stamp
    /// there is no way to tell a value somebody chose from one that was merely serialized, and
    /// nothing may quietly overrule the first kind.
    /// </remarks>
    public int SchemaVersion { get; set; }

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
    /// <summary>
    /// Brings a file written by an older build up to the current layout.
    /// </summary>
    /// <returns>True when something was changed and the file should be written again.</returns>
    /// <remarks>
    /// One step so far. The update check and the repository it asks about were both stored and
    /// neither was ever reachable: there was no switch, no field and no caller. Every value in an
    /// existing file is therefore a serialized default rather than an answer anybody gave, which is
    /// what makes adopting the new ones legitimate here and would not make it legitimate again.
    /// </remarks>
    public bool Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion)
        {
            return false;
        }

        if (SchemaVersion < 1)
        {
            PilotSettings defaults = new();
            Advanced.CheckForUpdates = defaults.Advanced.CheckForUpdates;
            Advanced.UpdateRepository = defaults.Advanced.UpdateRepository;
        }

        SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    public PilotSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
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
    /// Which display the quick menus open on. Null uses the one the pointer is on.
    /// </summary>
    /// <remarks>
    /// Stored as the display's own name and the corner it occupies rather than as an index, because
    /// an index changes the moment a monitor is unplugged or the arrangement is altered, and the
    /// palette would then open somewhere the user never chose.
    /// </remarks>
    public string? QuickMenuScreen { get; set; }

    /// <summary>
    /// Where the main window was left. Restored on the next start.
    /// </summary>
    public WindowPlacementSettings MainWindow { get; set; } = new();

    /// <summary>
    /// What the profile editor shows first: the form, or the configuration as OpenVPN reads it.
    /// </summary>
    /// <remarks>
    /// Both are one button apart in the editor. This is for the person who reaches for the text every
    /// time and would otherwise press that button every time.
    /// </remarks>
    public ProfileEditorView ProfileEditor { get; set; } = ProfileEditorView.Form;

    public GeneralSettings Clone()
    {
        GeneralSettings copy = (GeneralSettings)MemberwiseClone();

        // A memberwise copy shares the nested object, which would let a settings screen editing a
        // draft write the window placement straight into the live settings.
        copy.MainWindow = MainWindow.Clone();
        return copy;
    }
}

/// <summary>
/// Where a window was and how large it was.
/// </summary>
/// <remarks>
/// Kept as plain numbers rather than as a platform rectangle so the settings file stays readable and
/// the type stays free of a windowing framework. Every value is optional: a placement that was never
/// recorded, or one that belongs to a monitor that is no longer there, means the window opens where
/// it would have without this.
/// </remarks>
public sealed class WindowPlacementSettings
{
    public int? X { get; set; }

    public int? Y { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool Maximised { get; set; }

    /// <summary>
    /// True when there is enough here to restore anything.
    /// </summary>
    public bool HasPosition => X is not null && Y is not null;

    public WindowPlacementSettings Clone() => (WindowPlacementSettings)MemberwiseClone();
}

/// <summary>
/// The two ways the profile editor can present a profile.
/// </summary>
public enum ProfileEditorView
{
    /// <summary>
    /// Named fields for what is changed by hand: the server, the port, the protocol and the keys.
    /// </summary>
    Form,

    /// <summary>
    /// The whole configuration as text.
    /// </summary>
    PlainText,
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
    /// How long a tunnel is given to come up before the attempt is abandoned. Zero waits forever.
    /// </summary>
    /// <remarks>
    /// OpenVPN retries by itself for as long as it is left running, so without this a tunnel that
    /// cannot come up reports that it is connecting until the application closes, and holds a
    /// process the whole time. A minute is long enough for a slow server and short enough to be
    /// believed.
    /// </remarks>
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

    /// <summary>
    /// Announce that a connection has started. Useful when the tunnel was asked for from the quick
    /// switcher or a shortcut, where there is no window to watch.
    /// </summary>
    public bool OnConnecting { get; set; } = true;

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
    /// How many days of log files to keep. Zero keeps them until somebody removes them.
    /// </summary>
    /// <remarks>
    /// A week is enough to look into something that happened over a weekend and short enough that
    /// the directory does not grow without anybody deciding that it should. The logs name profiles
    /// and hosts, so keeping them forever by default would be a decision made on the user's behalf.
    /// </remarks>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>
    /// How large the log files may be together, in megabytes. Zero sets no limit.
    /// </summary>
    /// <remarks>
    /// The retention alone does not bound the directory: a tunnel that logs the same failure for
    /// every packet wrote more than a gigabyte a day. The oldest files go first when this is reached.
    /// </remarks>
    public int LogMaximumMegabytes { get; set; } = 1024;

    /// <summary>
    /// Keep the database, logs and settings beside the executable instead of under the user profile.
    /// </summary>
    public bool PortableMode { get; set; }

    /// <summary>
    /// Look for a newer release on start.
    /// </summary>
    /// <remarks>
    /// On, and switchable. Checking contacts GitHub, which is a third party, so what is contacted is
    /// named in the settings screen and in the README rather than being left for someone to discover
    /// in a packet capture. Turning it off stops every request; nothing else here talks to a network
    /// the user did not ask for.
    /// </remarks>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// The repository to check, in the form owner/name. Empty means no check is made.
    /// </summary>
    /// <remarks>
    /// The project's own repository by default, so turning the check on is a switch rather than an
    /// invitation to type a name correctly. A fork changes it to its own and the check follows.
    /// </remarks>
    public string? UpdateRepository { get; set; } = "Schecher1/OpenVpnPilot";

    public AdvancedSettings Clone() => (AdvancedSettings)MemberwiseClone();
}
