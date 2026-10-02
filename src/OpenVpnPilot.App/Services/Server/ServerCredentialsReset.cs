using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// "Forget all stored credentials" as it applies to a server's copy.
/// </summary>
/// <remarks>
/// <para>
/// Only what belongs to this copy goes: the stored sign ins of the profiles it holds, and those held
/// in memory until they could be shared. The keystore is shared with this computer's own library and
/// with the copies of other servers, and switching never deletes the other side's data, so their
/// entries stay. This server's refresh token is the settings screen's to remove, by signing out.
/// </para>
/// <para>
/// The shared sign ins the synchronisation stored go with the rest, and a delta would never bring
/// them back, because nothing about them changed on the server. The cursor is therefore forgotten
/// and the next synchronisation is a complete one.
/// </para>
/// </remarks>
public interface IServerCredentialsReset
{
    /// <summary>
    /// How many sign ins of this copy's profiles the keystore holds.
    /// </summary>
    public Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <returns>How many stored sign ins were removed.</returns>
    public Task<int> ForgetAsync(CancellationToken cancellationToken = default);
}

public sealed class ServerCredentialsReset : IServerCredentialsReset
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly ISecretStore secrets;
    private readonly IHeldVaultSecrets held;
    private readonly ILogger<ServerCredentialsReset> logger;

    public ServerCredentialsReset(
        IDbContextFactory<PilotDbContext> contextFactory,
        ISecretStore secrets,
        IHeldVaultSecrets held,
        ILogger<ServerCredentialsReset> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.secrets = secrets;
        this.held = held;
        this.logger = logger;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        (await ReferencesAsync(cancellationToken)).Count;

    public async Task<int> ForgetAsync(CancellationToken cancellationToken = default)
    {
        held.Clear();

        List<string> references = await ReferencesAsync(cancellationToken);

        foreach (string reference in references)
        {
            await secrets.DeleteAsync(reference, cancellationToken);
        }

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.SyncStates
            .Where(row => row.Id == SyncStateRow.SingletonId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Cursor, (long?)null), cancellationToken);

        ServerLibraryLog.CursorReset(logger);
        return references.Count;
    }

    /// <summary>
    /// The keystore entries of the profiles this copy holds, temporary ones included.
    /// </summary>
    private async Task<List<string>> ReferencesAsync(CancellationToken cancellationToken)
    {
        HashSet<Guid> profiles;

        await using (PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            profiles = [.. await context.Profiles.Select(profile => profile.Id).ToListAsync(cancellationToken)];
        }

        return [.. (await secrets.ListAsync(cancellationToken))
            .Where(reference => SecretReference.TryParse(reference, out Guid owner, out _) && profiles.Contains(owner))];
    }
}
