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
    /// <param name="cancellationToken">Cancels before anything has been written.</param>
    public Task<StorageSwitchResult> SwitchToServerAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
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
        SwitchAsync(StorageMode.Local, null, cancellationToken);

    public Task<StorageSwitchResult> SwitchToServerAsync(
        string serverAddress,
        CancellationToken cancellationToken = default)
    {
        if (!ServerKey.TryNormalise(serverAddress, out string? normalised, out ServerAddressProblem problem))
        {
            return Task.FromResult(new StorageSwitchResult(StorageSwitchOutcome.AddressUnusable, problem));
        }

        return SwitchAsync(StorageMode.Server, normalised, cancellationToken);
    }

    private async Task<StorageSwitchResult> SwitchAsync(
        StorageMode mode,
        string? serverAddress,
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

            string[] arguments = [AfterRestartOption, processId.ToString(CultureInfo.InvariantCulture)];

            if (!restart.TryStartSuccessor(arguments))
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
