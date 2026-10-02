using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Data;
using SyncStateRow = OpenVpnPilot.Data.Entities.SyncState;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The part of "forget all stored credentials" that only a server's copy has.
/// </summary>
/// <remarks>
/// The keystore is emptied by the settings screen, which takes the shared sign ins the
/// synchronisation put there with it. A delta would never bring them back, because nothing about
/// them changed on the server, so the cursor is forgotten and the next synchronisation is a complete
/// one. Sign ins held in memory until they could be shared go too.
/// </remarks>
public interface IServerCredentialsReset
{
    public Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class ServerCredentialsReset : IServerCredentialsReset
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IHeldVaultSecrets held;
    private readonly ILogger<ServerCredentialsReset> logger;

    public ServerCredentialsReset(
        IDbContextFactory<PilotDbContext> contextFactory,
        IHeldVaultSecrets held,
        ILogger<ServerCredentialsReset> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(logger);

        this.contextFactory = contextFactory;
        this.held = held;
        this.logger = logger;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        held.Clear();

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.SyncStates
            .Where(row => row.Id == SyncStateRow.SingletonId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Cursor, (long?)null), cancellationToken);

        ServerLibraryLog.CursorReset(logger);
    }
}
