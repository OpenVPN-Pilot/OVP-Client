using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One profile in the list, carrying its live connection status.
/// </summary>
public sealed partial class ProfileItemViewModel : ViewModelBase
{
    public ProfileItemViewModel(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Id = profile.Id;
        Name = profile.Name;
        FolderId = profile.FolderId;
        Endpoint = FormatEndpoint(profile);
        RequiresCredentials = profile.RequiresCredentials;
        HasUnsupportedOptions = profile.HasUnsupportedOptions;
        LastConnectedAt = profile.LastConnectedAt;
        IsFavourite = profile.IsFavourite;

        // Matching happens on a prepared lower case string so filtering does not allocate per keystroke.
        SearchText = string.Join(
            ' ',
            profile.Name,
            profile.RemoteHost ?? string.Empty,
            profile.Protocol ?? string.Empty).ToLowerInvariant();
    }

    public Guid Id { get; }

    public string Name { get; }

    public Guid? FolderId { get; }

    public string Endpoint { get; }

    public bool RequiresCredentials { get; }

    /// <summary>
    /// True when the configuration uses directives the interactive service refuses for callers that
    /// are not authorised.
    /// </summary>
    public bool HasUnsupportedOptions { get; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    internal string SearchText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteActionLabel))]
    public partial bool IsFavourite { get; set; }

    public string FavouriteActionLabel => IsFavourite ? "Unfavourite" : "Favourite";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(HasFailureMessage))]
    [NotifyPropertyChangedFor(nameof(LocalAddressDisplay))]
    [NotifyPropertyChangedFor(nameof(ServerDisplay))]
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
        VpnConnectionState.Connected => "Connected",
        VpnConnectionState.Connecting => "Connecting",
        VpnConnectionState.Launching => "Starting",
        VpnConnectionState.Authenticating => "Authenticating",
        VpnConnectionState.Reconnecting => "Reconnecting",
        VpnConnectionState.Disconnecting => "Disconnecting",
        VpnConnectionState.Failed => "Failed",
        _ => string.Empty,
    };

    /// <summary>
    /// Status text that is never empty, for places that always show a label.
    /// </summary>
    public string StatusLabel => StatusText.Length == 0 ? "Not connected" : StatusText;

    public bool HasFailureMessage =>
        Status.State == VpnConnectionState.Failed && Status.Message.Length > 0;

    public string LocalAddressDisplay => Status.LocalAddress ?? "-";

    public string ServerDisplay => Status.ServerAddress is { } address
        ? $"{address}:{Status.ServerPort}"
        : Endpoint;

    public string AuthenticationDisplay =>
        RequiresCredentials ? "User name and password" : "Certificate";

    public string LastConnectedDisplay => LastConnectedAt is { } when
        ? when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "Never";

    public void MarkConnected(DateTimeOffset when)
    {
        LastConnectedAt = when;
        OnPropertyChanged(nameof(LastConnectedDisplay));
    }

    /// <summary>
    /// True when the profile matches a search term. An empty term matches everything.
    /// </summary>
    public bool Matches(string lowerCaseTerm) =>
        lowerCaseTerm.Length == 0
        || SearchText.Contains(lowerCaseTerm, StringComparison.Ordinal);

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
