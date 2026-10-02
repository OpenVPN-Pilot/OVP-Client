using System.Text;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// A profile as the server holds it, without its configuration.
/// </summary>
/// <param name="Id">Stable, and the key of the profile's vault entries.</param>
/// <param name="RemotePort">Null exactly when <paramref name="RemoteHost"/> is.</param>
/// <param name="Protocol"><c>udp</c> or <c>tcp</c>, null exactly when <paramref name="RemoteHost"/> is.</param>
/// <param name="ProtectRoutes">Null follows the client's own setting.</param>
/// <param name="Colour"><c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</param>
/// <param name="Tags">Tag names, sorted regardless of case.</param>
/// <param name="ContentHash">Lower case hex SHA-256 of the configuration as the server stores it.</param>
/// <param name="ChangeSeq">The change number of the last change.</param>
/// <param name="ETag">For <c>If-Match</c>.</param>
public sealed record ProfileResponse(
    Guid Id,
    string Name,
    string? RemoteHost,
    int? RemotePort,
    string? Protocol,
    bool RequiresCredentials,
    bool HasUnsupportedOptions,
    bool? ProtectRoutes,
    string? Notes,
    string? Colour,
    IReadOnlyList<string> Tags,
    string ContentHash,
    long ChangeSeq,
    string ETag,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>
/// <c>GET /api/v1/profiles/{id}/configuration</c>: what the client connects with.
/// </summary>
/// <remarks>
/// The configuration carries private keys, so it is left out of anything that prints the record.
/// </remarks>
public sealed record ProfileConfigurationResponse(Guid ProfileId, string ContentHash, string Configuration)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ProfileId = ").Append(ProfileId).Append(", ContentHash = ").Append(ContentHash);
        return true;
    }
}

/// <summary>
/// The body of <c>POST /api/v1/profiles</c> and of one item of a batch.
/// </summary>
/// <param name="Configuration">Self contained: every file inline, as the importer produces it.</param>
/// <param name="Tags">Names; unknown ones are created.</param>
public sealed record ProfileCreateRequest(
    string Name,
    string Configuration,
    string? Notes,
    string? Colour,
    bool? ProtectRoutes,
    IReadOnlyList<string>? Tags)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name);
        return true;
    }
}

/// <summary>
/// The body of <c>PUT /api/v1/profiles/{id}</c>, which replaces every field it names.
/// </summary>
/// <param name="Configuration">A new configuration, or null to keep the stored one.</param>
/// <param name="Notes">Null clears.</param>
/// <param name="Colour">Null clears.</param>
/// <param name="Tags">The complete list; null or empty removes every tag.</param>
public sealed record ProfileUpdateRequest(
    string Name,
    string? Configuration,
    string? Notes,
    string? Colour,
    bool? ProtectRoutes,
    IReadOnlyList<string>? Tags)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name).Append(", ConfigurationChanged = ").Append(Configuration is not null);
        return true;
    }
}

/// <summary>
/// <c>POST /api/v1/profiles/batch</c>: 1 to <see cref="MaximumItems"/> items, at most
/// <see cref="MaximumBytes"/> in all.
/// </summary>
public sealed record ProfileBatchRequest(IReadOnlyList<ProfileCreateRequest> Items)
{
    public const int MaximumItems = 500;

    public const long MaximumBytes = 64L * 1024 * 1024;
}

/// <summary>
/// What happened to each item of a batch.
/// </summary>
public sealed record ProfileBatchResponse(
    int Created,
    int Duplicates,
    int Rejected,
    IReadOnlyList<ProfileBatchItemResponse> Items);

/// <summary>
/// The outcome of one batch item.
/// </summary>
/// <param name="Index">Position in the request, from zero.</param>
/// <param name="Outcome">One of <see cref="ProfileBatchOutcomes"/>.</param>
/// <param name="Profile">The new profile, when created.</param>
/// <param name="Code">The error code, when not created.</param>
/// <param name="Detail">Why not, in the server's words.</param>
public sealed record ProfileBatchItemResponse(
    int Index,
    string Outcome,
    ProfileResponse? Profile,
    string? Code,
    string? Detail);

/// <summary>
/// The outcomes a batch item can have.
/// </summary>
public static class ProfileBatchOutcomes
{
    public const string Created = "created";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
}

/// <summary>
/// A tag as the server holds it.
/// </summary>
public sealed record TagResponse(Guid Id, string Name, string? Colour, long ChangeSeq);

/// <summary>
/// The body of <c>POST /api/v1/tags</c> and <c>PUT /api/v1/tags/{id}</c>.
/// </summary>
public sealed record TagRequest(string Name, string? Colour);
