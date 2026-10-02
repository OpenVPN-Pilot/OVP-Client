using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Replaces the sign in everybody shares for one profile and realm. Administrators only, and only
/// while the server can be reached.
/// </summary>
/// <remarks>
/// Not an outbox change: replacing what everybody uses is a deliberate act whose answer the
/// administrator waits for, not something to be sent some time later without them. On success the
/// new sign in is stored here too, so this computer uses it at once.
/// </remarks>
public interface ISharedSignInReplacement
{
    public Task<ServerResult> ReplaceAsync(
        Guid profileId,
        string realm,
        string? username,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed class SharedSignInReplacement : ISharedSignInReplacement
{
    private readonly IServerApi api;
    private readonly ISecretStore secrets;
    private readonly ILogger<SharedSignInReplacement> logger;

    public SharedSignInReplacement(IServerApi api, ISecretStore secrets, ILogger<SharedSignInReplacement> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        this.api = api;
        this.secrets = secrets;
        this.logger = logger;
    }

    public async Task<ServerResult> ReplaceAsync(
        Guid profileId,
        string realm,
        string? username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string? name = string.IsNullOrEmpty(username) ? null : username;
        ServerResult<VaultEntryResponse> result = await api.PutVaultEntryAsync(
            profileId,
            realm,
            new VaultEntryRequest(name, password),
            cancellationToken);

        if (!result.IsSuccess)
        {
            VaultShareLog.ReplaceFailed(logger, profileId, realm, result.Outcome, result.Status, result.Code, result.RequestId);
            return result;
        }

        if (secrets.IsAvailable)
        {
            await secrets.WriteAsync(SecretReference.ForProfile(profileId, realm), new StoredSecret(name, password), cancellationToken);
        }

        VaultShareLog.Replaced(logger, profileId, realm);
        return result;
    }
}
