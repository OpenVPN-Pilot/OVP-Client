using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One profile in the list, carrying its live connection status.
/// </summary>
public sealed partial class ProfileItemViewModel : ViewModelBase
{
    private readonly ILocalizer localizer;

    public ProfileItemViewModel(Profile profile, ILocalizer localizer, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(localizer);

        this.localizer = localizer;

        Id = profile.Id;
        Name = profile.Name;
        Endpoint = FormatEndpoint(profile);
        RequiresCredentials = profile.RequiresCredentials;
        HasUnsupportedOptions = profile.HasUnsupportedOptions;
        LastConnectedAt = profile.LastConnectedAt;
        ConnectCount = profile.ConnectCount;
        Notes = profile.Notes;
        ProtectRoutes = profile.ProtectRoutes;
        Tags = tags ?? [];

        IsFavourite = profile.IsFavourite;
        FavouriteSlot = profile.FavouriteSlot;

        // Matching happens on a prepared lower case string so filtering does not allocate per keystroke.
        SearchText = string.Join(
            ' ',
            profile.Name,
            profile.RemoteHost ?? string.Empty,
            profile.Protocol ?? string.Empty,
            string.Join(' ', Tags)).ToLowerInvariant();
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public bool RequiresCredentials { get; }

    /// <summary>
    /// True when the configuration uses directives the interactive service refuses for callers that
    /// are not authorised.
    /// </summary>
    public bool HasUnsupportedOptions { get; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    public int ConnectCount { get; }

    public string? Notes { get; }

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>
    /// Per profile override for the route protection. Null follows the application wide setting.
    /// </summary>
    public bool? ProtectRoutes { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    public string TagsDisplay => string.Join(", ", Tags);

    internal string SearchText { get; }

    /// <summary>
    /// True while the list is offering a checkbox on every row.
    /// </summary>
    /// <remarks>
    /// Carried by the row rather than read from the list it sits in. A template that reaches up to
    /// its ancestor's data context for one boolean is a binding nobody can follow later.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsSelecting { get; set; }

    /// <summary>
    /// True when this row is ticked. Meaningless while <see cref="IsSelecting"/> is false.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteActionLabel))]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFavouriteSlot))]
    [NotifyPropertyChangedFor(nameof(FavouriteSlotDisplay))]
    public partial int? FavouriteSlot { get; set; }

    public bool HasFavouriteSlot => FavouriteSlot is not null;

    public string FavouriteSlotDisplay =>
        FavouriteSlot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string FavouriteActionLabel => IsFavourite
        ? localizer["profile.favouriteRemove"]
        : localizer["profile.favouriteAdd"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(HasFailureMessage))]
    [NotifyPropertyChangedFor(nameof(FailureMessage))]
    [NotifyPropertyChangedFor(nameof(LocalAddressDisplay))]
    [NotifyPropertyChangedFor(nameof(ServerDisplay))]
    [NotifyPropertyChangedFor(nameof(UptimeDisplay))]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    [NotifyPropertyChangedFor(nameof(PingTooltip))]
    [NotifyPropertyChangedFor(nameof(RoutesDisplay))]
    [NotifyPropertyChangedFor(nameof(DnsDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPushedOptions))]
    [NotifyPropertyChangedFor(nameof(HasRefusedDefaultRoute))]
    [NotifyPropertyChangedFor(nameof(HasRefusedCompression))]
    public partial VpnConnectionStatus Status { get; set; } = VpnConnectionStatus.Disconnected;

    public bool IsConnected => Status.State == VpnConnectionState.Connected;

    /// <summary>
    /// True while a connection is being established or torn down, so the row can show progress.
    /// </summary>
    public bool IsBusy => Status.State
        is VpnConnectionState.Launching
        or VpnConnectionState.Connecting
        or VpnConnectionState.Authenticating
        or VpnConnectionState.Reconnecting
        or VpnConnectionState.Disconnecting;

    public bool IsIdle => !IsConnected && !IsBusy;

    public string StatusText => Status.State switch
    {
        VpnConnectionState.Connected => localizer["state.connected"],
        VpnConnectionState.Connecting => localizer["state.connecting"],
        VpnConnectionState.Launching => localizer["state.launching"],
        VpnConnectionState.Authenticating => localizer["state.authenticating"],
        VpnConnectionState.Reconnecting => localizer["state.reconnecting"],
        VpnConnectionState.Disconnecting => localizer["state.disconnecting"],
        VpnConnectionState.Failed => localizer["state.failed"],
        _ => string.Empty,
    };

    /// <summary>
    /// Status text that is never empty, for places that always show a label.
    /// </summary>
    public string StatusLabel => StatusText.Length == 0 ? localizer["state.disconnected"] : StatusText;

    public bool HasFailureMessage =>
        Status.State == VpnConnectionState.Failed && FailureMessage.Length > 0;

    /// <summary>
    /// Why the connection failed, translated where this client wrote the reason itself.
    /// </summary>
    public string FailureMessage => localizer.Describe(Status);

    public string LocalAddressDisplay => Status.LocalAddress ?? "-";

    public string ServerDisplay => Status.ServerAddress is { } address
        ? $"{address}:{Status.ServerPort}"
        : Endpoint;

    public string UptimeDisplay => Status.ConnectedSince is { } since
        ? FormatDuration(DateTimeOffset.UtcNow - since)
        : "-";

    /// <summary>
    /// The round trip, or an honest statement that nothing answered.
    /// </summary>
    public string PingDisplay => Status.PingMilliseconds is { } milliseconds
        ? string.Create(CultureInfo.InvariantCulture, $"{milliseconds:0} ms")
        : Status.PingFailed ? localizer["profile.noReply"] : "-";

    /// <summary>
    /// Says which address the round trip was measured against and what that makes the figure.
    /// </summary>
    /// <remarks>
    /// Without this the number cannot be checked. The two targets differ by more than measurement
    /// noise, so someone comparing the figure with a terminal ping has to be able to see which
    /// question was answered.
    /// </remarks>
    public string PingTooltip => Status.PingTargetKind switch
    {
        PingTargetKind.TunnelGateway =>
            localizer.Translate("profile.pingViaTunnel", Status.PingTarget ?? string.Empty),
        PingTargetKind.ServerEndpoint =>
            localizer.Translate("profile.pingViaServer", Status.PingTarget ?? string.Empty),
        _ => localizer["profile.pingNoTarget"],
    };

    public bool HasPushedOptions =>
        Status.PushedRoutes.Count > 0 || Status.PushedDnsServers.Count > 0;

    public string RoutesDisplay => Status.PushedRoutes.Count > 0
        ? string.Join(Environment.NewLine, Status.PushedRoutes)
        : "-";

    public string DnsDisplay => Status.PushedDnsServers.Count > 0
        ? string.Join(", ", Status.PushedDnsServers)
        : "-";

    /// <summary>
    /// True when the server asked to carry all traffic and this profile does not allow it.
    /// </summary>
    /// <remarks>
    /// Worth saying plainly: the tunnel is up and looks normal, but it is not carrying what the
    /// server intended, and nothing else in the interface would reveal that.
    /// </remarks>
    public bool HasRefusedDefaultRoute =>
        Status.ServerRequestedDefaultRoute && ProtectRoutes != false;

    /// <summary>
    /// True when the server pushed a compression setting this client cannot apply.
    /// </summary>
    /// <remarks>
    /// Worth saying plainly, because the failure that follows names neither compression nor the
    /// server: the tunnel reconnects forever reporting that it could not process the push message,
    /// and nothing else in the interface would reveal which option it was.
    /// </remarks>
    public bool HasRefusedCompression => Status.ServerRequestedCompression;

    public string AuthenticationDisplay => RequiresCredentials
        ? localizer["profile.authPassword"]
        : localizer["profile.authCertificate"];

    public string LastConnectedDisplay => LastConnectedAt is { } when
        ? when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : localizer["common.never"];

    public void MarkConnected(DateTimeOffset when)
    {
        LastConnectedAt = when;
        OnPropertyChanged(nameof(LastConnectedDisplay));
    }

    /// <summary>
    /// Re-reads every computed label, which is what a language change requires.
    /// </summary>
    public void RefreshLocalizedText() => OnPropertyChanged(propertyName: null);

    /// <summary>
    /// Raises the uptime so a connected row keeps counting without the status itself changing.
    /// </summary>
    public void RefreshUptime()
    {
        if (IsConnected)
        {
            OnPropertyChanged(nameof(UptimeDisplay));
        }
    }

    /// <summary>
    /// True when the profile matches a search term. An empty term matches everything.
    /// </summary>
    public bool Matches(string lowerCaseTerm) =>
        lowerCaseTerm.Length == 0
        || SearchText.Contains(lowerCaseTerm, StringComparison.Ordinal);

    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1
        ? value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatEndpoint(Profile profile)
    {
        if (profile.RemoteHost is null)
        {
            return string.Empty;
        }

        string protocol = profile.Protocol is { Length: > 0 } value ? $" {value}" : string.Empty;
        return $"{profile.RemoteHost}:{profile.RemotePort}{protocol}";
    }
}
