namespace OpenVpnPilot.Data.Entities;

/// <summary>
/// A marker in the outbox: something changed locally that the server has not been told about yet.
/// </summary>
/// <remarks>
/// The marker names what changed, never what it changed to. When it is pushed, the entity's current
/// state is read and sent, so several edits become one call and a secret never has to be written
/// into the database to survive until the server can be reached.
/// </remarks>
public sealed class PendingChange
{
    /// <summary>
    /// Increases with every marker and is the order in which markers are pushed.
    /// </summary>
    public long Id { get; set; }

    public PendingChangeKind Kind { get; set; }

    /// <summary>
    /// The profile or tag the marker is about. Null for the kinds that cover a whole list.
    /// </summary>
    public Guid? EntityId { get; set; }

    /// <summary>
    /// The credential realm of a vault addition, null for every other kind.
    /// </summary>
    public string? Realm { get; set; }

    /// <summary>
    /// True when a profile update includes an edit of the configuration made here.
    /// </summary>
    /// <remarks>
    /// The update sends the configuration only then. Whether the server's copy differs says nothing
    /// about who changed it, and an administrator's edit there must not be reset by a rename here.
    /// </remarks>
    public bool ConfigurationChanged { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How often pushing it failed in a way that is worth trying again.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The problem code of the last failed attempt, as the server named it.
    /// </summary>
    public string? LastErrorCode { get; set; }
}

/// <summary>
/// What a <see cref="PendingChange"/> is about.
/// </summary>
/// <remarks>
/// Stored as its number, so the values are fixed and a new kind is added at the end.
/// </remarks>
public enum PendingChangeKind
{
    /// <summary>
    /// A profile created while the server could not be reached, still under its temporary id.
    /// </summary>
    ProfileCreate = 0,

    ProfileUpdate = 1,

    ProfileDelete = 2,

    TagUpdate = 3,

    TagDelete = 4,

    /// <summary>
    /// A sign in that worked and that the shared vault does not have yet, named by profile and realm.
    /// </summary>
    VaultAdd = 5,

    /// <summary>
    /// The favourites as a whole, which the server replaces as one list.
    /// </summary>
    Favourites = 6,

    /// <summary>
    /// The shortcuts as a whole, which the server replaces as one list.
    /// </summary>
    Hotkeys = 7,

    /// <summary>
    /// The portable part of the settings, which the server replaces as one document.
    /// </summary>
    Settings = 8,
}
