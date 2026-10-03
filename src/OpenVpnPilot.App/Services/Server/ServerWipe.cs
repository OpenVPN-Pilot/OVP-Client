using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.App.Services.Storage;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Carries out the wipe directive: everything that came from the server goes, then the application
/// starts again on this computer's own library.
/// </summary>
/// <remarks>
/// <para>
/// The steps are the contract's, in its order: every tunnel ends (every profile shown belongs to the
/// server), the keystore entries of every profile in the copy go with the refresh token, the copy's
/// folder goes, the settings that followed the server go back to their defaults, the mode becomes
/// Local, the person is told in plain words, and the application restarts.
/// </para>
/// <para>
/// Nothing is sent to the server on the way and nothing is tried again: the directive is final. A
/// step that fails is logged and the next one still runs, because leaving the rest of the server's
/// data behind would be worse than a partial report. Nothing that did not come from the server is
/// touched: the local library is another file, and only the keystore entries of the copy's own
/// profiles are removed.
/// </para>
/// </remarks>
public interface IServerWipe
{
    /// <summary>
    /// Wipes once. A second call, from a second answer carrying the directive, does nothing.
    /// </summary>
    public Task<ServerWipeReport?> WipeAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the wipe removed. Counts, never values.
/// </summary>
/// <param name="Tunnels">Tunnels that were up and were ended.</param>
/// <param name="Profiles">Profiles the copy held.</param>
/// <param name="Secrets">Keystore entries of those profiles that were removed.</param>
/// <param name="RefreshTokenRemoved">True when a stored refresh token was removed.</param>
/// <param name="FolderRemoved">True when the copy's folder is gone.</param>
/// <param name="Switch">How leaving the server went.</param>
public sealed record ServerWipeReport(
    int Tunnels,
    int Profiles,
    int Secrets,
    bool RefreshTokenRemoved,
    bool FolderRemoved,
    StorageSwitchOutcome Switch);

/// <summary>
/// Tells the person that the account no longer has access, and waits until they have seen it.
/// </summary>
public interface IAccountRevokedNotice
{
    public Task ShowAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServerWipe"/>
public sealed class ServerWipe : IServerWipe
{
    private readonly IActiveStorage storage;
    private readonly IActiveTunnels tunnels;
    private readonly ISyncEngine engine;
    private readonly IServerSession session;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IServerProfileMaintenance maintenance;
    private readonly ISecretStore secrets;
    private readonly IProfileMaterializer materializer;
    private readonly ISettingsService settings;
    private readonly IStorageModeSwitcher switcher;
    private readonly IAccountRevokedNotice notice;
    private readonly ILogger<ServerWipe> logger;

    private int started;

    public ServerWipe(
        IActiveStorage storage,
        IActiveTunnels tunnels,
        ISyncEngine engine,
        IServerSession session,
        IDbContextFactory<PilotDbContext> contextFactory,
        IServerProfileMaintenance maintenance,
        ISecretStore secrets,
        IProfileMaterializer materializer,
        ISettingsService settings,
        IStorageModeSwitcher switcher,
        IAccountRevokedNotice notice,
        ILogger<ServerWipe> logger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(tunnels);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(materializer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(logger);

        this.storage = storage;
        this.tunnels = tunnels;
        this.engine = engine;
        this.session = session;
        this.contextFactory = contextFactory;
        this.maintenance = maintenance;
        this.secrets = secrets;
        this.materializer = materializer;
        this.settings = settings;
        this.switcher = switcher;
        this.notice = notice;
        this.logger = logger;
    }

    public async Task<ServerWipeReport?> WipeAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return null;
        }

        if (storage is not { IsServerMode: true, ServerKey: { } serverKey, ServerDirectory: { } folder })
        {
            // Only a process working on a server's copy has anything of it to wipe.
            ServerWipeLog.NotInServerMode(logger, directive.RequestId);
            return null;
        }

        ServerWipeLog.Started(logger, serverKey, directive.Method, directive.Path, directive.Status, directive.RequestId);

        // Stopped first, because it holds the database and would otherwise keep writing into what is
        // about to be removed. The signal already keeps it from sending anything.
        await StepAsync("synchronisation", token => engine.StopAsync(token), cancellationToken);

        int tunnelsUp = tunnels.Count;
        await StepAsync("tunnels", token => tunnels.DisconnectAllAsync(token), cancellationToken);
        await StepAsync("runtime configurations", _ => Task.FromResult(materializer.RemoveStaleFiles()), cancellationToken);

        // Read before anything is removed: once the folder is gone, nothing says which keystore
        // entries were the server's.
        IReadOnlyList<Guid> profileIds = await StepAsync("profile list", ReadProfileIdsAsync, cancellationToken) ?? [];

        int removedSecrets = await StepAsync(
            "keystore entries",
            token => maintenance.DeleteSecretsAsync([.. profileIds], token),
            cancellationToken);

        bool refreshTokenRemoved = await StepAsync("refresh token", token => ForgetSessionAsync(serverKey, token), cancellationToken);

        bool folderRemoved = await StepAsync("cache folder", token => ServerCacheFolder.DeleteAsync(folder, logger, token), cancellationToken);

        await StepAsync("settings", token => ResetPortableSettingsAsync(token), cancellationToken);

        ServerWipeLog.Completed(
            logger,
            serverKey,
            directive.RequestId,
            tunnelsUp,
            profileIds.Count,
            removedSecrets,
            refreshTokenRemoved,
            folderRemoved);

        StorageSwitchResult left = await switcher.LeaveRevokedServerAsync(
            token => notice.ShowAsync(directive, token),
            directive.RequestId,
            cancellationToken);

        return new ServerWipeReport(tunnelsUp, profileIds.Count, removedSecrets, refreshTokenRemoved, folderRemoved, left.Outcome);
    }

    private async Task<IReadOnlyList<Guid>> ReadProfileIdsAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Profiles.AsNoTracking().Select(profile => profile.Id).ToListAsync(cancellationToken);
    }

    private async Task<bool> ForgetSessionAsync(string serverKey, CancellationToken cancellationToken)
    {
        string reference = SecretReference.ForServerRefreshToken(serverKey);
        bool stored = secrets.IsAvailable && await secrets.TryReadAsync(reference, cancellationToken) is not null;

        // Discards the tokens held in memory as well, without a word to the server.
        await session.ForgetAsync(cancellationToken);

        return stored && await secrets.TryReadAsync(reference, cancellationToken) is null;
    }

    /// <summary>
    /// Puts the portable settings back to their defaults, keeping what describes this machine.
    /// </summary>
    /// <remarks>
    /// In Server mode the portable part of the settings follows the server, so what is in effect now
    /// is what was received from it, and the contract has those copies removed too.
    /// </remarks>
    private async Task ResetPortableSettingsAsync(CancellationToken cancellationToken)
    {
        PilotSettings defaults = new() { SchemaVersion = PilotSettings.CurrentSchemaVersion };
        PilotSettings? reset = PilotSettingsTransfer.Import(PilotSettingsTransfer.Export(defaults), settings.Current);

        if (reset is not null)
        {
            await settings.ReplaceAsync(reset, cancellationToken);
        }
    }

    private async Task StepAsync(string step, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Each step belongs to a different part of the application, so its failures cannot be
            // listed here. It is written down, and the steps after it still run.
            ServerWipeLog.StepFailed(logger, step, exception);
        }
    }

    private async Task<T?> StepAsync<T>(string step, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ServerWipeLog.StepFailed(logger, step, exception);
            return default;
        }
    }
}
