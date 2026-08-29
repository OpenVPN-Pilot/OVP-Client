using System.Text.Json.Serialization;

namespace OpenVpnPilot.Data.Packaging;

/// <summary>
/// What a package carries between machines.
/// </summary>
/// <remarks>
/// The package is a single file so a profile set can be moved without anyone having to know where
/// the database lives. It carries the configurations, the way they were filed and the shortcuts, and
/// deliberately nothing about how they were used: the session history belongs to the machine it
/// happened on.
///
/// Credentials are not included unless the caller asks for them and supplies a passphrase. A stored
/// secret is protected by the operating system for one user on one machine, so moving it means
/// re-protecting it with something the recipient can supply, and writing it in the clear because
/// that was easier would be a leak the user did not agree to.
/// </remarks>
public sealed record ProfilePackageContent
{
    /// <summary>
    /// The layout this file was written with, so a later version can read an earlier one.
    /// </summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The version of the application that wrote it, for diagnosing a package that will not read.
    /// </summary>
    [JsonPropertyName("writtenBy")]
    public string WrittenBy { get; init; } = string.Empty;

    [JsonPropertyName("folders")]
    public IReadOnlyList<PackagedFolder> Folders { get; init; } = [];

    [JsonPropertyName("profiles")]
    public IReadOnlyList<PackagedProfile> Profiles { get; init; } = [];

    [JsonPropertyName("hotkeys")]
    public IReadOnlyList<PackagedHotkey> Hotkeys { get; init; } = [];

    /// <summary>
    /// Present only when the package was written with credentials and a passphrase.
    /// </summary>
    [JsonPropertyName("credentials")]
    public IReadOnlyList<PackagedCredential> Credentials { get; init; } = [];
}

/// <summary>
/// A folder as it travels. Identifiers are kept so the nesting can be rebuilt.
/// </summary>
public sealed record PackagedFolder(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parentId")] Guid? ParentId,
    [property: JsonPropertyName("sortOrder")] int SortOrder);

/// <summary>
/// A profile with its configuration inline, which is what makes it usable elsewhere.
/// </summary>
public sealed record PackagedProfile
{
    [property: JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; init; } = string.Empty;

    [JsonPropertyName("folderId")]
    public Guid? FolderId { get; init; }

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
