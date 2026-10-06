using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Removes what this computer still keeps of a server that withdrew the account, when the directive
/// reaches a sign in made from another store.
/// </summary>
/// <remarks>
/// <para>
/// A sign in to a server is made before the application switches to it: from the first start, and
/// from the storage settings while the application works on this computer's library or on another
/// server. A server that answers that sign in with the wipe directive has withdrawn the account, and
/// whatever this computer kept from an earlier time with it has to go: the copy's folder, the
/// keystore entries of the profiles in it, and the refresh token.
/// </para>
/// <para>
/// Unlike the wipe of the running copy this does not switch anything and does not restart: the store
/// in use was never that server's, so it stays as it is, and the person is told. Nothing is sent to
/// the server, and a copy this process works on is never touched here; that is the running wipe's.
/// </para>
/// </remarks>
public interface IServerLeftovers
{
    /// <summary>
    /// Removes what is kept of the server and tells the person.
    /// </summary>
    /// <param name="directive">The directive, for the server's address and the request id.</param>
    /// <param name="serverKey">The key the server's copy is filed under.</param>
    /// <param name="cancellationToken">Cancels the remaining steps.</param>
    /// <returns>What was removed, or null when the key names the copy this process works on.</returns>
    public Task<ServerLeftoversReport?> RemoveAsync(
        ServerWipeDirective directive,
        string serverKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What was removed of a server's leftovers. Counts, never values.
/// </summary>
/// <param name="Profiles">Profiles the leftover copy held.</param>
/// <param name="Secrets">Keystore entries of those profiles that were removed.</param>
/// <param name="RefreshTokenRemoved">True when a stored refresh token was removed.</param>
/// <param name="FolderRemoved">True when the copy's folder is gone, or was never there.</param>
public sealed record ServerLeftoversReport(int Profiles, int Secrets, bool RefreshTokenRemoved, bool FolderRemoved);

/// <summary>
/// Tells the person that a server withdrew the account and what this computer kept of it is gone.
/// </summary>
public interface IServerLeftoversNotice
{
    public Task ShowAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServerLeftovers"/>
public sealed class ServerLeftovers : IServerLeftovers
{
    private readonly IApplicationPaths paths;
    private readonly IActiveStorage storage;
    private readonly ISecretStore secrets;
    private readonly IServerProfileMaintenance maintenance;
    private readonly IServerLeftoversNotice notice;
    private readonly ILogger<ServerLeftovers> logger;

    public ServerLeftovers(
        IApplicationPaths paths,
        IActiveStorage storage,
        ISecretStore secrets,
        IServerProfileMaintenance maintenance,
        IServerLeftoversNotice notice,
        ILogger<ServerLeftovers> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(logger);

        this.paths = paths;
        this.storage = storage;
        this.secrets = secrets;
        this.maintenance = maintenance;
        this.notice = notice;
        this.logger = logger;
    }

    public async Task<ServerLeftoversReport?> RemoveAsync(
        ServerWipeDirective directive,
        string serverKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        // Checked like any key before it becomes a path, so nothing outside the servers folder can
        // be named by it.
        _ = SecretReference.ForServerRefreshToken(serverKey);

        if (storage.IsServerMode && string.Equals(storage.ServerKey, serverKey, StringComparison.Ordinal))
        {
            ServerStatusLog.LeftoversAreActive(logger, serverKey, directive.RequestId);
            return null;
        }

        ServerStatusLog.LeftoversWipeStarted(logger, serverKey, directive.Method, directive.Path, directive.Status, directive.RequestId);

        string folder = Path.Combine(paths.ServersDirectory, serverKey);

        // Read before anything is removed: once the folder is gone, nothing says which keystore
        // entries were the server's.
        IReadOnlyList<Guid> profileIds = await StepAsync("profile list", token => ReadProfileIdsAsync(folder, token), cancellationToken) ?? [];

        int removedSecrets = await StepAsync(
            "keystore entries",
            token => maintenance.DeleteSecretsAsync([.. profileIds], token),
            cancellationToken);

        bool refreshTokenRemoved = await StepAsync("refresh token", token => DeleteRefreshTokenAsync(serverKey, token), cancellationToken);
        bool folderRemoved = await StepAsync("cache folder", token => ServerCacheFolder.DeleteAsync(folder, logger, token), cancellationToken);

        ServerLeftoversReport report = new(profileIds.Count, removedSecrets, refreshTokenRemoved, folderRemoved);

        ServerStatusLog.LeftoversRemoved(
            logger,
            serverKey,
            directive.RequestId,
            report.Profiles,
            report.Secrets,
            report.RefreshTokenRemoved,
            report.FolderRemoved);

        await StepAsync("notice", token => notice.ShowAsync(directive, token), cancellationToken);
        return report;
    }

    private static async Task<IReadOnlyList<Guid>> ReadProfileIdsAsync(string folder, CancellationToken cancellationToken)
    {
        string database = Path.Combine(folder, ActiveStorage.DatabaseFileName);

        if (!File.Exists(database))
        {
            return [];
        }

        // Opened read only and never migrated: it is about to be removed, and only its ids are needed.
        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={database};Mode=ReadOnly")
            .Options;

        await using PilotDbContext context = new(options);
        return await context.Profiles.AsNoTracking().Select(profile => profile.Id).ToListAsync(cancellationToken);
    }

    private async Task<bool> DeleteRefreshTokenAsync(string serverKey, CancellationToken cancellationToken)
    {
        if (!secrets.IsAvailable)
        {
            return false;
        }

        string reference = SecretReference.ForServerRefreshToken(serverKey);

        // What a Microsoft sign in left goes with the session it belongs to, whether or not a
        // refresh token is still there to report.
        await secrets.DeleteAsync(SecretReference.ForServerEntraState(serverKey), cancellationToken);

        if (await secrets.TryReadAsync(reference, cancellationToken) is null)
        {
            return false;
        }

        await secrets.DeleteAsync(reference, cancellationToken);
        return await secrets.TryReadAsync(reference, cancellationToken) is null;
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
            ServerStatusLog.LeftoversStepFailed(logger, step, exception);
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
            ServerStatusLog.LeftoversStepFailed(logger, step, exception);
            return default;
        }
    }
}
