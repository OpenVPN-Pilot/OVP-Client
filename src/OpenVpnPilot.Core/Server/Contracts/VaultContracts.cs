using System.Text;

namespace OpenVpnPilot.Core.Server.Contracts;

/// <summary>
/// A shared sign in, as the vault and the synchronisation deliver it.
/// </summary>
/// <param name="Realm">OpenVPN's realm: <c>Auth</c>, or the name of a private key.</param>
/// <param name="Username">Null for a passphrase.</param>
/// <param name="Password">The secret itself. Left out of anything that prints the record.</param>
public sealed record VaultEntryResponse(
    Guid ProfileId,
    string Realm,
    string? Username,
    string Password,
    long ChangeSeq,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ProfileId = ").Append(ProfileId).Append(", Realm = ").Append(Realm)
            .Append(", ChangeSeq = ").Append(ChangeSeq);
        return true;
    }
}

/// <summary>
/// The body of <c>POST</c> and <c>PUT /api/v1/profiles/{id}/vault/{realm}</c>.
/// </summary>
public sealed record VaultEntryRequest(string? Username, string Password)
{
    public override string ToString() => $"{nameof(VaultEntryRequest)} {{ }}";
}

/// <summary>
/// Names one vault entry, as a deletion in the synchronisation does.
/// </summary>
public sealed record VaultKeyResponse(Guid ProfileId, string Realm);
