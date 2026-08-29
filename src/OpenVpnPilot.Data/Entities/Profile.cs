namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// One VPN profile, stored as a single self contained configuration.
/// </summary>
/// <remarks>
/// The configuration text carries its certificates and keys inline, which is what makes a profile
/// movable between machines without a directory of supporting files.
/// </remarks>
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public Guid? FolderId { get; set; }

    public Folder? Folder { get; set; }

    /// <summary>
    /// The complete configuration. Never contains credentials.
    /// </summary>
    public required string Configuration { get; set; }

    /// <summary>
    /// SHA-256 of <see cref="Configuration"/>, used to recognise duplicates on import.
    /// </summary>
    public required string ContentHash { get; set; }

    public ProfileSource Source { get; set; } = ProfileSource.Manual;

    /// <summary>
    /// Where the profile came from, so a watched folder can match a file back to its profile.
    /// </summary>
    public string? SourcePath { get; set; }

    public string? RemoteHost { get; set; }

    public int? RemotePort { get; set; }

    /// <summary>
    /// Transport as written in the configuration, either udp or tcp.
    /// </summary>
    public string? Protocol { get; set; }

    public bool RequiresCredentials { get; set; }

    public Guid? CredentialSetId { get; set; }

    public CredentialSet? CredentialSet { get; set; }

    public bool IsFavourite { get; set; }

    /// <summary>
    /// Slot one to nine, bound to a hotkey. Null when the profile is a favourite without a slot.
    /// </summary>
    public int? FavouriteSlot { get; set; }

    /// <summary>
    /// True when the configuration uses directives the interactive service refuses for unauthorised
    /// callers, such as script hooks. Surfaced at import time rather than at connection time.
    /// </summary>
    public bool HasUnsupportedOptions { get; set; }

    /// <summary>
    /// True when nothing outside the configuration text is needed to connect.
    /// </summary>
    public bool IsSelfContained { get; set; } = true;

    /// <summary>
    /// Whether pushed routing and DNS options are ignored for this profile. Null follows the
    /// application wide setting.
    /// </summary>
    /// <remarks>
    /// A profile that is meant to carry all traffic needs the pushed default route, while one that
    /// only reaches a single network must not take the host's routing table with it. That is a per
    /// profile decision, not a global one.
    /// </remarks>
    public bool? ProtectRoutes { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Accent colour for the list, stored as a hexadecimal value such as #4C8BF5.
    /// </summary>
    public string? Colour { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }

    public int ConnectCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProfileTag> Tags { get; } = [];

    public List<Session> Sessions { get; } = [];
}

public enum ProfileSource
{
    /// <summary>
    /// Created in the application.
    /// </summary>
    Manual,

    /// <summary>
    /// Imported once from a file or an archive.
    /// </summary>
    Imported,

    /// <summary>
    /// Kept in step with a watched folder.
    /// </summary>
    WatchedFolder,
}
