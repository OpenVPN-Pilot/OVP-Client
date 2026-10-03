using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.App.Services.Storage;

/// <summary>
/// Switches this machine between its own library and a server's, which is a restart.
/// </summary>
/// <remarks>
/// Every store was composed against one database, so another one means another process: the new
/// mode is written to the settings, a new copy is started that waits for this one to end, and this
/// one ends. Nothing is deleted on the way. The library on this computer and every server's copy
/// stay where they are, and switching back opens them again exactly as they were left.
///
/// Refused while a tunnel is up. The tunnels shown belong to the store being left, and ending
/// somebody's connection as a side effect of a setting is not something to do quietly.
/// </remarks>
public interface IStorageModeSwitcher
{
    /// <summary>
    /// Raised once a switch has been written and the new copy started; this copy must now end.
    /// </summary>
    /// <remarks>
    /// Raised on whatever thread the switch ran on. Ending the application is the user interface's
    /// business, so the handler posts the shutdown there.
    /// </remarks>
    public event EventHandler? ShutdownRequested;

    /// <summary>
    /// Switches to the library on this computer.
    /// </summary>
    public Task<StorageSwitchResult> SwitchToLocalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches to a server, or to another server when one is already in use.
    /// </summary>
    /// <param name="serverAddress">The address as typed; it is stored in its normal form.</param>
    /// <param name="followUp">What the next copy does before its main window, see <see cref="StorageSwitchFollowUp"/>.</param>
    /// <param name="cancellationToken">Cancels before anything has been written.</param>
    public Task<StorageSwitchResult> SwitchToServerAsync(
        string serverAddress,
        StorageSwitchFollowUp followUp = StorageSwitchFollowUp.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves a server that told this client to wipe: back to this computer, the server forgotten,
    /// the person told, then the restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one switch that is not a person's choice, so nothing is asked and the tunnel check is not
    /// made: the wipe has ended every tunnel before it comes here. The server's address is removed
    /// rather than kept for the way back, because the account it was used with no longer exists.
    /// </para>
    /// <para>
    /// The mode is written first and the person told second, so the next start is local even when
    /// the application is ended while the message is still on screen. This copy is ended in every
    /// case: it works on a copy that no longer exists. When the settings did not take the change or
    /// the new copy could not be started, the outcome says so and nobody is started. Neither a
    /// settings file that cannot be written nor a message that cannot be shown keeps it from ending;
    /// each is logged with the request id.
    /// </para>
    /// </remarks>
    /// <param name="announce">Tells the person; awaited before the restart.</param>
    /// <param name="requestId">The request that carried the wipe directive, for the log.</param>
    public Task<StorageSwitchResult> LeaveRevokedServerAsync(
        Func<CancellationToken, Task> announce,
        string? requestId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the copy started by a switch does before it shows its main window.
/// </summary>
public enum StorageSwitchFollowUp
{
    /// <summary>
    /// Starts as usual.
    /// </summary>
    None,

    /// <summary>
    /// Continues a setup whose sign in this copy made: confirms the session it stored and runs the
    /// first synchronisation with its progress on screen.
    /// </summary>
    FirstSynchronisation,
}

/// <summary>
/// What became of a request to switch.
/// </summary>
public enum StorageSwitchOutcome
{
    /// <summary>
    /// Written and the new copy started. This one ends next.
    /// </summary>
    Restarting,

    /// <summary>
    /// The store asked for is the one in use already. Nothing was changed.
    /// </summary>
    AlreadyActive,

    /// <summary>
    /// At least one tunnel is up or coming up. Nothing was changed.
    /// </summary>
    TunnelsUp,

    /// <summary>
    /// The server address cannot be used; <see cref="StorageSwitchResult.AddressProblem"/> says why.
    /// </summary>
    AddressUnusable,

    /// <summary>
    /// The settings file did not take the change, so a new copy would have come back unchanged.
    /// </summary>
    SettingsNotSaved,

    /// <summary>
    /// The new copy could not be started. The previous mode was put back.
    /// </summary>
    RestartFailed,

    /// <summary>
    /// Another switch is under way.
    /// </summary>
    InProgress,
}

/// <summary>
/// The outcome of a switch and, for an address that cannot be used, the reason.
/// </summary>
public sealed record StorageSwitchResult(
    StorageSwitchOutcome Outcome,
    ServerAddressProblem AddressProblem = ServerAddressProblem.None);

/// <inheritdoc cref="IStorageModeSwitcher"/>
public sealed class StorageModeSwitcher : IStorageModeSwitcher
{
    /// <summary>
    /// The option that makes the new copy wait for this one.
    /// </summary>
    public const string AfterRestartOption = "--after-restart";

    /// <summary>
    /// The option that makes the new copy continue a setup with the first synchronisation.
    /// </summary>
    public const string FirstSynchronisationOption = "--first-sync";

    private readonly ISettingsService settings;
    private readonly IActiveStorage active;
    private readonly IApplicationPaths paths;
    private readonly IActiveTunnels tunnels;
    private readonly IApplicationRestart restart;
    private readonly ILogger<StorageModeSwitcher> logger;
    private readonly int processId;

    private int switching;

    public StorageModeSwitcher(
        ISettingsService settings,
        IActiveStorage active,
        IApplicationPaths paths,
        IActiveTunnels tunnels,
        IApplicationRestart restart,
        ILogger<StorageModeSwitcher> logger)
        : this(settings, active, paths, tunnels, restart, logger, Environment.ProcessId)
    {
    }

    internal StorageModeSwitcher(
        ISettingsService settings,
        IActiveStorage active,
        IApplicationPaths paths,
        IActiveTunnels tunnels,
        IApplicationRestart restart,
        ILogger<StorageModeSwitcher> logger,
        int processId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(tunnels);
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentNullException.ThrowIfNull(logger);

        this.settings = settings;
        this.active = active;
        this.paths = paths;
        this.tunnels = tunnels;
        this.restart = restart;
        this.logger = logger;
        this.processId = processId;
    }

    public event EventHandler? ShutdownRequested;

    public Task<StorageSwitchResult> SwitchToLocalAsync(CancellationToken cancellationToken = default) =>
        SwitchAsync(StorageMode.Local, null, StorageSwitchFollowUp.None, cancellationToken);

    public Task<StorageSwitchResult> SwitchToServerAsync(
        string serverAddress,
        StorageSwitchFollowUp followUp = StorageSwitchFollowUp.None,
        CancellationToken cancellationToken = default)
    {
        if (!ServerKey.TryNormalise(serverAddress, out string? normalised, out ServerAddressProblem problem))
        {
            return Task.FromResult(new StorageSwitchResult(StorageSwitchOutcome.AddressUnusable, problem));
        }

        return SwitchAsync(StorageMode.Server, normalised, followUp, cancellationToken);
    }

    public async Task<StorageSwitchResult> LeaveRevokedServerAsync(
        Func<CancellationToken, Task> announce,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0)
        {
            return new StorageSwitchResult(StorageSwitchOutcome.InProgress);
        }

        // From here on this copy is on its way out whatever happens, so it stays switching, and
        // neither step below may keep it from ending: it works on a copy that no longer exists.
        StorageSwitchOutcome outcome = StorageSwitchOutcome.Restarting;
        Exception? saveFailure = null;

        try
        {
            await settings.UpdateAsync(
                next =>
                {
                    next.Storage.Mode = StorageMode.Local;
                    next.Storage.ServerUrl = null;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Reported below together with a write that went through without taking the change.
            saveFailure = exception;
        }

        bool saved = saveFailure is null
            && FileSelects(StorageMode.Local, null)
            && StorageModeReader.Read(paths.SettingsPath).ServerUrl is null;

        if (!saved)
        {
            StorageLog.LeaveNotSaved(logger, requestId, saveFailure);
            outcome = StorageSwitchOutcome.SettingsNotSaved;
        }

        try
        {
            await announce(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StorageLog.LeaveNotAnnounced(logger, requestId, exception);
        }

        if (saved)
        {
            if (restart.TryStartSuccessor(SuccessorArguments(StorageSwitchFollowUp.None)))
            {
                StorageLog.Switching(logger, active.Mode, StorageMode.Local, null);
            }
            else
            {
                StorageLog.LeaveRestartFailed(logger, requestId);
                outcome = StorageSwitchOutcome.RestartFailed;
            }
        }

        ShutdownRequested?.Invoke(this, EventArgs.Empty);
        return new StorageSwitchResult(outcome);
    }

    private string[] SuccessorArguments(StorageSwitchFollowUp followUp)
    {
        string[] arguments = [AfterRestartOption, processId.ToString(CultureInfo.InvariantCulture)];

        return followUp == StorageSwitchFollowUp.FirstSynchronisation
            ? [.. arguments, FirstSynchronisationOption]
            : arguments;
    }

    private async Task<StorageSwitchResult> SwitchAsync(
        StorageMode mode,
        string? serverAddress,
        StorageSwitchFollowUp followUp,
        CancellationToken cancellationToken)
    {
        // A second click while the first is writing, or after it has already started the new copy,
        // must not start a second one.
        if (Interlocked.CompareExchange(ref switching, 1, 0) != 0)
        {
            return new StorageSwitchResult(StorageSwitchOutcome.InProgress);
        }

        bool restarting = false;

        try
        {
            int up = tunnels.Count;

            if (up > 0)
            {
                StorageLog.SwitchRefusedTunnelsUp(logger, mode, up);
                return new StorageSwitchResult(StorageSwitchOutcome.TunnelsUp);
            }

            if (IsInUse(mode, serverAddress))
            {
                return new StorageSwitchResult(StorageSwitchOutcome.AlreadyActive);
            }

            StorageSettings previous = settings.Current.Storage.Clone();

            await settings.UpdateAsync(
                next =>
                {
                    next.Storage.Mode = mode;

                    // Switching to Local keeps the address, so switching back offers the same server.
                    if (serverAddress is not null)
                    {
                        next.Storage.ServerUrl = serverAddress;
                    }
                },
                cancellationToken);

            // The new copy decides its store by reading the file, not by asking this process, so the
            // file is read back the same way. A save that failed would otherwise restart into the
            // store that was just left, and look like a switch that did nothing.
            if (!FileSelects(mode, serverAddress))
            {
                StorageLog.SwitchNotSaved(logger, mode);
                await settings.UpdateAsync(next => next.Storage = previous, CancellationToken.None);
                return new StorageSwitchResult(StorageSwitchOutcome.SettingsNotSaved);
            }

            if (!restart.TryStartSuccessor(SuccessorArguments(followUp)))
            {
                StorageLog.SwitchRestartFailed(logger, mode);
                await settings.UpdateAsync(next => next.Storage = previous, CancellationToken.None);
                return new StorageSwitchResult(StorageSwitchOutcome.RestartFailed);
            }

            StorageLog.Switching(logger, active.Mode, mode, serverAddress);
            restarting = true;
        }
        finally
        {
            // Once the new copy has been started this one is on its way out, and stays switching.
            if (!restarting)
            {
                Interlocked.Exchange(ref switching, 0);
            }
        }

        ShutdownRequested?.Invoke(this, EventArgs.Empty);
        return new StorageSwitchResult(StorageSwitchOutcome.Restarting);
    }

    /// <summary>
    /// True when this process works on that store and the settings already name it.
    /// </summary>
    /// <remarks>
    /// Both, because they can differ: a settings file that names a server with an unusable address
    /// runs locally, and switching to Local then still has something to put right in the file.
    /// </remarks>
    private bool IsInUse(StorageMode mode, string? serverAddress)
    {
        StorageSettings stored = settings.Current.Storage;

        if (mode == StorageMode.Local)
        {
            return active.Mode == StorageMode.Local && stored.Mode == StorageMode.Local;
        }

        return active.Mode == StorageMode.Server
            && string.Equals(active.ServerAddress, serverAddress, StringComparison.Ordinal)
            && stored.Mode == StorageMode.Server
            && ServerKey.TryNormalise(stored.ServerUrl, out string? storedAddress, out _)
            && string.Equals(storedAddress, serverAddress, StringComparison.Ordinal);
    }

    private bool FileSelects(StorageMode mode, string? serverAddress)
    {
        StorageSelection written = StorageModeReader.Read(paths.SettingsPath);

        return written.Mode == mode
            && (serverAddress is null || string.Equals(written.ServerUrl, serverAddress, StringComparison.Ordinal));
    }
}
