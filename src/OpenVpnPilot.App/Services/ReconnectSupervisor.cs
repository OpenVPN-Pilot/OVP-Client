using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Re-establishes a tunnel that dropped, with a bounded number of attempts.
/// </summary>
/// <remarks>
/// OpenVPN already retries inside a running process and reports that as reconnecting. What this adds
/// is the case OpenVPN cannot handle: the process itself ended. It also puts a limit on the effort,
/// because a tunnel that has failed the same way five times will fail the sixth, and retrying
/// forever hides the problem instead of reporting it.
///
/// Authentication failures are never retried. The credentials that were refused are the only ones
/// available, so another attempt would produce the same refusal and, with a server that counts them,
/// could lock the account.
/// </remarks>
public sealed class ReconnectSupervisor : IDisposable
{
    private readonly ConnectionManager connections;
    private readonly IProfileStore store;
    private readonly ISettingsService settings;
    private readonly ILocalizer localizer;
    private readonly IProfileNameLookup profileNames;
    private readonly ILogger<ReconnectSupervisor> logger;
    private readonly TimeProvider timeProvider;

    private readonly ConcurrentDictionary<Guid, Attempt> attempts = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public ReconnectSupervisor(
        ConnectionManager connections,
        IProfileStore store,
        ISettingsService settings,
        ILocalizer localizer,
        IProfileNameLookup profileNames,
        TimeProvider timeProvider,
        ILogger<ReconnectSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(profileNames);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.connections = connections;
        this.store = store;
        this.settings = settings;
        this.localizer = localizer;
        this.profileNames = profileNames;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Raised with a message describing what the retry logic decided, for the status bar.
    /// </summary>
    public event EventHandler<string>? Reported;

    public void Attach() => connections.StateChanged += OnStateChanged;

    private void OnStateChanged(object? sender, ConnectionStatusChanged change)
    {
        VpnConnectionStatus status = change.Status;

        if (status.State == VpnConnectionState.Connected)
        {
            // A tunnel that came up has nothing left to recover from.
            attempts.TryRemove(change.ProfileId, out _);
            return;
        }

        if (!ShouldRetry(status))
        {
            attempts.TryRemove(change.ProfileId, out _);
            return;
        }

        _ = RetryAsync(change.ProfileId);
    }

    private bool ShouldRetry(VpnConnectionStatus status)
    {
        ConnectionSettings preferences = settings.Current.Connections;

        if (!preferences.AutoReconnect)
        {
            return false;
        }

        // Authentication and an unsupported configuration are both refusals the next attempt would
        // receive again, word for word. Retrying them produces nothing but another process.
        return status.State is VpnConnectionState.Disconnected or VpnConnectionState.Failed
            && status.Failure is VpnFailureKind.ConnectionLost or VpnFailureKind.Fatal;
    }

    private async Task RetryAsync(Guid profileId)
    {
        ConnectionSettings preferences = settings.Current.Connections;

        Attempt attempt = attempts.AddOrUpdate(
            profileId,
            _ => new Attempt(1),
            (_, existing) => new Attempt(existing.Count + 1));

        if (preferences.MaxReconnectAttempts > 0 && attempt.Count > preferences.MaxReconnectAttempts)
        {
            attempts.TryRemove(profileId, out _);
            ReconnectLog.GaveUp(logger, profileId, preferences.MaxReconnectAttempts);
            Report("status.reconnectGaveUp", profileId, preferences.MaxReconnectAttempts);
            return;
        }

        // Back off so a server that is down is not hammered, but keep the wait bounded so a
        // transient drop recovers quickly.
        TimeSpan delay = TimeSpan.FromSeconds(
            Math.Min(preferences.ReconnectDelaySeconds * Math.Pow(2, attempt.Count - 1), 300));

        Report("status.reconnectScheduled", profileId, (int)delay.TotalSeconds, attempt.Count);

        try
        {
            await Task.Delay(delay, timeProvider, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The user may have connected it again, or disconnected it on purpose, while we waited.
        if (connections.IsActive(profileId) || !attempts.ContainsKey(profileId))
        {
            return;
        }

        string? configuration = await store.GetConfigurationAsync(profileId, lifetime.Token);

        if (configuration is null)
        {
            attempts.TryRemove(profileId, out _);
            return;
        }

        try
        {
            ReconnectLog.Retrying(logger, profileId, attempt.Count);

            await connections.ConnectAsync(
                profileId,
                configuration,
                RouteProtectionFor(profileId),
                preferences.ConnectTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(preferences.ConnectTimeoutSeconds)
                    : null,
                lifetime.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // The next state change decides whether to try again, so this only needs recording.
            ReconnectLog.AttemptFailed(logger, profileId, exception);
        }
    }

    private string[] RouteProtectionFor(Guid profileId) =>
        settings.Current.Connections.ProtectRoutes ? RouteProtection : [];

    private static readonly string[] RouteProtection =
    [
        "--pull-filter ignore \"redirect-gateway\"",
        "--pull-filter ignore \"dhcp-option\"",
        "--pull-filter ignore \"block-outside-dns\"",
    ];

    private void Report(string key, Guid profileId, params object?[] arguments)
    {
        object?[] all = [profileNames.GetDisplayName(profileId), .. arguments];
        Reported?.Invoke(this, localizer.Translate(key, all));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StateChanged -= OnStateChanged;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>
    /// How many times a profile has been retried since it last connected.
    /// </summary>
    private sealed record Attempt(int Count);
}

/// <summary>
/// Source generated log messages for <see cref="ReconnectSupervisor"/>.
/// </summary>
internal static partial class ReconnectLog
{
    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "Reconnecting profile {ProfileId}, attempt {Attempt}.")]
    public static partial void Retrying(ILogger logger, Guid profileId, int attempt);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Warning,
        Message = "Giving up on profile {ProfileId} after {Attempts} attempt(s).")]
    public static partial void GaveUp(ILogger logger, Guid profileId, int attempts);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Warning,
        Message = "A reconnect attempt for profile {ProfileId} could not be started.")]
    public static partial void AttemptFailed(ILogger logger, Guid profileId, Exception exception);
}
