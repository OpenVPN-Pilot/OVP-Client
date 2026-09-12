using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Turns connection state changes into notifications, subject to what the user asked to be told.
/// </summary>
/// <remarks>
/// Someone running twenty tunnels does not want twenty messages, so each event type can be silenced
/// on its own and the defaults are deliberately quiet: connected, lost and failed are announced,
/// while an ordinary disconnect the user asked for is not, because they already know.
///
/// The profile identifier travels as the notification tag, so activating a message can bring that
/// profile up rather than only raising the window.
/// </remarks>
public sealed class NotificationService : IDisposable
{
    private readonly ConnectionManager connections;
    private readonly INotificationPresenter presenter;
    private readonly ISettingsService settings;
    private readonly ILocalizer localizer;
    private readonly IProfileNameLookup profileNames;
    private bool disposed;

    public NotificationService(
        ConnectionManager connections,
        INotificationPresenter presenter,
        ISettingsService settings,
        ILocalizer localizer,
        IProfileNameLookup profileNames)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(profileNames);

        this.connections = connections;
        this.presenter = presenter;
        this.settings = settings;
        this.localizer = localizer;
        this.profileNames = profileNames;
    }

    /// <summary>
    /// Raised when the user clicks a notification, carrying the profile it was about.
    /// </summary>
    public event EventHandler<Guid>? ProfileActivated;

    public void Attach()
    {
        connections.StateChanged += OnStateChanged;
        presenter.Activated += OnNotificationActivated;
    }

    private void OnNotificationActivated(object? sender, string tag)
    {
        if (Guid.TryParse(tag, out Guid profileId))
        {
            ProfileActivated?.Invoke(this, profileId);
        }
    }

    private void OnStateChanged(object? sender, ConnectionStatusChanged change)
    {
        NotificationSettings preferences = settings.Current.Notifications;

        if (!preferences.Enabled || !presenter.IsAvailable)
        {
            return;
        }

        Notice? notice = Describe(change.Status, preferences);

        if (notice is null)
        {
            return;
        }

        string profileName = profileNames.GetDisplayName(change.ProfileId);

        // Fire and forget: a notification is an aside, never something a tunnel waits for.
        _ = presenter.ShowAsync(new NotificationRequest(
            localizer[notice.TitleKey],
            localizer.Translate(notice.MessageKey, profileName, localizer.Describe(change.Status)),
            notice.Severity,
            change.ProfileId.ToString()));
    }

    private static Notice? Describe(VpnConnectionStatus status, NotificationSettings preferences) =>
        status.State switch
        {
            VpnConnectionState.Launching when preferences.OnConnecting =>
                new Notice("notify.connectingTitle", "notify.connectingMessage", NotificationSeverity.Information),

            VpnConnectionState.Connected when preferences.OnConnected =>
                new Notice("notify.connectedTitle", "notify.connectedMessage", NotificationSeverity.Information),

            VpnConnectionState.Reconnecting when preferences.OnReconnecting =>
                new Notice("notify.reconnectingTitle", "notify.reconnectingMessage", NotificationSeverity.Warning),

            VpnConnectionState.Failed when preferences.OnFailed =>
                new Notice("notify.failedTitle", "notify.failedMessage", NotificationSeverity.Error),

            // A tunnel that went away on its own is a different event from one the user stopped, and
            // people who silence ordinary disconnects still want to hear about a drop.
            VpnConnectionState.Disconnected when status.Failure == VpnFailureKind.ConnectionLost
                && preferences.OnConnectionLost =>
                new Notice("notify.lostTitle", "notify.lostMessage", NotificationSeverity.Warning),

            VpnConnectionState.Disconnected when status.Failure != VpnFailureKind.ConnectionLost
                && preferences.OnDisconnected =>
                new Notice("notify.disconnectedTitle", "notify.disconnectedMessage", NotificationSeverity.Information),

            _ => null,
        };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connections.StateChanged -= OnStateChanged;
        presenter.Activated -= OnNotificationActivated;
    }

    private sealed record Notice(string TitleKey, string MessageKey, NotificationSeverity Severity);
}
