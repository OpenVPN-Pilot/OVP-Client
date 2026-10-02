namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// A user as an administrator sees it.
/// </summary>
/// <param name="Role">One of <see cref="ServerRoles"/>.</param>
/// <param name="Provider">One of <see cref="ServerAuthModes"/>.</param>
/// <param name="State"><c>active</c>, <c>disabled</c> or <c>deleted</c>.</param>
/// <param name="StateSource"><c>administrator</c>, <c>provider</c> or null.</param>
public sealed record UserResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string Role,
    string Provider,
    string State,
    string? StateSource,
    DateTimeOffset? StateChangedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LastSeenAt);
