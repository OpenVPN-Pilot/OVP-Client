using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVpnPilot.Data.Packaging;

/// <summary>
/// What a package carries between machines.
/// </summary>
/// <remarks>
/// The package is a single file so a profile set can be moved without anyone having to know where
/// the database lives. It carries the configurations, their tags, and whichever of the shortcuts,
/// the settings and the saved sign ins the person writing it chose to include, and deliberately
/// nothing about how the profiles were used: the session history belongs to the machine it happened
/// on.
///
/// Credentials are not included unless the caller asks for them and supplies a passphrase. A stored
/// secret is protected by the operating system for one user on one machine, so moving it means
/// re-protecting it with something the recipient can supply, and writing it in the clear because
/// that was easier would be a leak the user did not agree to.
///
/// The same file is what a shared library is kept in. That is why a profile carries when it was
/// last changed and the package carries the profiles that were deleted: two people changing one set
/// can only be reconciled by knowing which change came last and what went away.
/// </remarks>
public sealed record ProfilePackageContent
{
    /// <summary>
    /// The newest layout this build reads and writes.
    /// </summary>
    /// <remarks>
    /// Two added the settings, when a profile last changed and the deletions. A file of one reads as
    /// one of two that has none of them.
    /// </remarks>
    public const int CurrentFormatVersion = 2;

    /// <summary>
    /// The layout this file was written with, so a later version can read an earlier one.
    /// </summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The version of the application that wrote it, for diagnosing a package that will not read.
    /// </summary>
    [JsonPropertyName("writtenBy")]
    public string WrittenBy { get; init; } = string.Empty;

    [JsonPropertyName("profiles")]
    public IReadOnlyList<PackagedProfile> Profiles { get; init; } = [];

    [JsonPropertyName("hotkeys")]
    public IReadOnlyList<PackagedHotkey> Hotkeys { get; init; } = [];

    /// <summary>
    /// Present only when the package was written with credentials and a passphrase.
    /// </summary>
    [JsonPropertyName("credentials")]
    public IReadOnlyList<PackagedCredential> Credentials { get; init; } = [];

    /// <summary>
    /// The settings, in the settings file's own form, when the writer chose to include them.
    /// </summary>
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; init; }

    /// <summary>
    /// Profiles that were deleted from a shared library, so the deletion reaches everyone using it.
    /// </summary>
    [JsonPropertyName("deletedProfiles")]
    public IReadOnlyList<PackagedDeletion> DeletedProfiles { get; init; } = [];

    /// <summary>
    /// The hashes of the shared file's earlier versions this one was merged from, oldest first.
    /// </summary>
    /// <remarks>
    /// How a machine tells a version that continued its own write from one written beside it. A
    /// sync client that saw two writes at once keeps one of them, and the machine whose write was not
    /// kept has to merge against what both started from, or its changes read as taken back. A reader
    /// that does not know the field ignores it.
    /// </remarks>
    [JsonPropertyName("lineage")]
    public IReadOnlyList<string> Lineage { get; init; } = [];
}

/// <summary>
/// A profile with its configuration inline, which is what makes it usable elsewhere.
/// </summary>
public sealed record PackagedProfile
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; init; } = string.Empty;

    [JsonPropertyName("remoteHost")]
    public string? RemoteHost { get; init; }

    [JsonPropertyName("remotePort")]
    public int? RemotePort { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("requiresCredentials")]
    public bool RequiresCredentials { get; init; }

    [JsonPropertyName("isFavourite")]
    public bool IsFavourite { get; init; }

    [JsonPropertyName("favouriteSlot")]
    public int? FavouriteSlot { get; init; }

    [JsonPropertyName("protectRoutes")]
    public bool? ProtectRoutes { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("colour")]
    public string? Colour { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// When what is shared about the profile last changed. Absent from a package of format one.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// A shortcut binding as it travels.
/// </summary>
public sealed record PackagedHotkey(
    [property: JsonPropertyName("actionId")] string ActionId,
    [property: JsonPropertyName("gesture")] string Gesture,
    [property: JsonPropertyName("isEnabled")] bool IsEnabled);

/// <summary>
/// A credential pair, present only in a package written with a passphrase.
/// </summary>
public sealed record PackagedCredential(
    [property: JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonPropertyName("realm")] string Realm,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string Password);

/// <summary>
/// A profile that was deleted, and when.
/// </summary>
public sealed record PackagedDeletion(
    [property: JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonPropertyName("deletedAt")] DateTimeOffset DeletedAt);
