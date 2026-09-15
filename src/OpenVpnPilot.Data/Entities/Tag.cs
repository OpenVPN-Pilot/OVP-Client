namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// A free form label. Tags are how a profile set is organised: a profile can carry as many as it
/// needs, and searching across them is faster than walking a tree.
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
