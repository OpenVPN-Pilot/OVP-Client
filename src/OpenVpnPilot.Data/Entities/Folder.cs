namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// A node in the profile tree. Folders may nest to any depth.
/// </summary>
public sealed class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public Guid? ParentId { get; set; }

    public Folder? Parent { get; set; }

    /// <summary>
    /// Position among siblings. Lower values come first.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Optional icon key resolved by the presentation layer.
    /// </summary>
    public string? Icon { get; set; }

    public List<Folder> Children { get; } = [];

    public List<Profile> Profiles { get; } = [];
}

/// <summary>
/// A free form label. Tags cut across the folder tree, so a profile can be filed once and labelled
/// several ways.
/// </summary>
public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Colour { get; set; }

    public List<ProfileTag> Profiles { get; } = [];
}

/// <summary>
/// Join entity between <see cref="Entities.Profile"/> and <see cref="Entities.Tag"/>.
/// </summary>
public sealed class ProfileTag
{
    public Guid ProfileId { get; set; }

    public Profile? Profile { get; set; }

    public Guid TagId { get; set; }

    public Tag? Tag { get; set; }
}

/// <summary>
/// A named set of credentials shared by several profiles.
/// </summary>
/// <remarks>
/// Only the reference is stored here. The secret itself lives in the operating system keystore, so
/// the database can be copied or exported without carrying passwords.
/// </remarks>
public sealed class CredentialSet
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Username { get; set; }

    /// <summary>
    /// Key under which the password is stored in the platform keystore.
    /// </summary>
    public required string SecretReference { get; set; }

    /// <summary>
    /// True when the server additionally asks for a one time code.
    /// </summary>
    public bool RequiresOneTimeCode { get; set; }

    public List<Profile> Profiles { get; } = [];
}

/// <summary>
/// A folder watched for configuration files, so that profiles stay in step with a shared directory.
/// </summary>
public sealed class WatchedFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Path { get; set; }

    public bool IsRecursive { get; set; } = true;

    /// <summary>
    /// True when new files are imported without asking.
    /// </summary>
    public bool AutoImport { get; set; } = true;

    /// <summary>
    /// Folder that imported profiles are filed under.
    /// </summary>
    public Guid? TargetFolderId { get; set; }

    public DateTimeOffset? LastScanAt { get; set; }
}
