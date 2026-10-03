using System.Text.Json;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// The signed in person's favourites, <c>GET</c> and <c>PUT /api/v1/me/favourites</c>. The list is
/// replaced as a whole.
/// </summary>
/// <param name="Items">Up to 1000; slotted ones first in slot order when the server answers.</param>
public sealed record FavouritesDocument(IReadOnlyList<FavouriteItem> Items);

/// <summary>
/// One favourite.
/// </summary>
/// <param name="Slot">1 to 10, or null for a favourite without a slot. Each slot at most once.</param>
public sealed record FavouriteItem(Guid ProfileId, int? Slot);

/// <summary>
/// The signed in person's shortcuts, <c>GET</c> and <c>PUT /api/v1/me/hotkeys</c>. Replaced as a whole.
/// </summary>
public sealed record HotkeysDocument(IReadOnlyList<HotkeyItem> Items);

/// <summary>
/// One shortcut, written as the client stores it; the server interprets neither field.
/// </summary>
/// <param name="ProfileId">Null when the action takes no profile, or the profile was deleted.</param>
public sealed record HotkeyItem(string ActionId, string Gesture, Guid? ProfileId, bool IsEnabled);

/// <summary>
/// <c>PUT /api/v1/me/settings</c>: the portable part of the settings.
/// </summary>
/// <param name="SchemaVersion">The settings' schema version.</param>
/// <param name="Document">A JSON object of at most 64 KiB, as the settings transfer exports it.</param>
public sealed record SettingsRequest(int SchemaVersion, JsonElement Document);

/// <summary>
/// <c>GET</c> and the answer of <c>PUT /api/v1/me/settings</c>.
/// </summary>
/// <param name="SchemaVersion">0 when nothing is stored yet.</param>
/// <param name="Document">An empty object when nothing is stored yet.</param>
/// <param name="ETag">Null when nothing is stored yet.</param>
public sealed record SettingsResponse(
    int SchemaVersion,
    JsonElement Document,
    string? ETag,
    DateTimeOffset? UpdatedAt);
