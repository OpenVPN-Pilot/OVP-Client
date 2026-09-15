using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Edits a profile: what the application keeps about it, and the configuration OpenVPN reads.
/// </summary>
/// <remarks>
/// The configuration was not editable at all, on the reasoning that it carries the certificates and a
/// slip would break a working profile with no way back. What that left was re-importing a whole file
/// to change a port, which is the edit people actually needed to make. It is editable now in two
/// ways, and the way back from a slip is that nothing is saved while OpenVPN would refuse it.
///
/// The form names what is changed by hand: the server, the port, the protocol and the certificates
/// and keys. Changing a field replaces the lines that field is about and leaves the rest of the file
/// alone. The plain text is the file itself, for everything the form does not name. Switching carries
/// the edits across in both directions, and whichever is showing is what is saved.
///
/// Every change is checked as it is made, with the same checks for both, so a problem is on screen
/// next to the change that caused it rather than reported when the next connection fails.
/// </remarks>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    private readonly IProfileStore store;
    private readonly ILocalizer localizer;
    private readonly Guid profileId;
    private readonly bool startsInPlainText;

    private readonly Dictionary<string, string?> formBlocks = new(StringComparer.Ordinal);

    /// <summary>
    /// The configuration as it is stored, which is what a save compares against.
    /// </summary>
    private string storedConfiguration = string.Empty;

    /// <summary>
    /// The text the form's fields were read from, and which the form's edits are applied to.
    /// </summary>
    private string formConfiguration = string.Empty;

    private OvpnEndpoint? formEndpoint;

    /// <summary>
    /// Set while fields are being filled from a configuration, when a change is not an edit.
    /// </summary>
    private bool populating;

    /// <summary>
    /// True while this machine uses a shared library, where a saved change reaches every machine.
    /// </summary>
    public bool IsShared { get; init; }

    public ProfileEditorViewModel(
        IProfileStore store,
        ILocalizer localizer,
        ProfileItemViewModel profile,
        bool startsInPlainText = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(profile);

        this.store = store;
        this.localizer = localizer;
        this.startsInPlainText = startsInPlainText;

        profileId = profile.Id;
        Name = profile.Name;
        Notes = profile.Notes ?? string.Empty;
        TagsInput = string.Join(", ", profile.Tags);
        SelectedSlot = profile.FavouriteSlot ?? 0;
        IsFavourite = profile.IsFavourite;
        IsConnected = !profile.IsIdle;

        RouteProtectionChoices =
        [
            new RouteProtectionChoice(null, localizer["editor.routeInherit"]),
            new RouteProtectionChoice(true, localizer["editor.routeProtect"]),
            new RouteProtectionChoice(false, localizer["editor.routeAllow"]),
        ];

        SelectedRouteProtection = RouteProtectionChoices
            .First(choice => choice.Value == profile.ProtectRoutes);

        SelectedProtocol = Protocols[0];
    }

    /// <summary>
    /// Raised when the editor is finished with, with true when something was changed.
    /// </summary>
    public event EventHandler<bool>? Closed;

    /// <summary>
    /// Raised when the user asked for the profile to be removed.
    /// </summary>
    public event EventHandler<Guid>? DeleteRequested;

    public ObservableCollection<RouteProtectionChoice> RouteProtectionChoices { get; }

    /// <summary>
    /// The transports OpenVPN offers, named the way the configuration names them.
    /// </summary>
    public IReadOnlyList<ProtocolChoice> Protocols { get; } =
    [
        new ProtocolChoice(OvpnProtocol.Udp, "UDP"),
        new ProtocolChoice(OvpnProtocol.Tcp, "TCP"),
    ];

    /// <summary>
    /// Zero means no slot. The rest bind the profile to the matching connect shortcut.
    /// </summary>
    public IReadOnlyList<int> Slots { get; } =
        [0, .. Enumerable.Range(1, HotkeyActions.MaximumFavouriteSlot)];

    /// <summary>
    /// What is wrong with the configuration as it stands, errors first.
    /// </summary>
    public ObservableCollection<ProfileEditorIssue> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    /// True when OpenVPN would refuse the configuration as it stands, which is what stops a save.
    /// </summary>
    public bool HasErrors => Issues.Any(issue => issue.IsError);

    /// <summary>
    /// True while the tunnel is up, which a changed configuration does not affect until it reconnects.
    /// </summary>
    public bool IsConnected { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string TagsInput { get; set; }

    [ObservableProperty]
    public partial RouteProtectionChoice? SelectedRouteProtection { get; set; }

    [ObservableProperty]
    public partial int SelectedSlot { get; set; }

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// True while the delete confirmation is showing, so a single click cannot destroy a profile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; set; }

    /// <summary>
    /// False until the configuration has been read, so nothing is edited or saved against an empty one.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    /// <summary>
    /// True while the configuration is shown as text rather than as the form.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewToggleLabel))]
    public partial bool IsPlainText { get; set; }

    [ObservableProperty]
    public partial string ConfigurationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Host { get; set; } = string.Empty;

    /// <summary>
    /// The port, as the numeric field reports it. Null while the field is empty.
    /// </summary>
    [ObservableProperty]
    public partial decimal? Port { get; set; } = 1194;

    [ObservableProperty]
    public partial ProtocolChoice? SelectedProtocol { get; set; }

    [ObservableProperty]
    public partial string CaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CertText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KeyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TlsKeyText { get; set; } = string.Empty;

    /// <summary>
    /// Which of the TLS key blocks the configuration carries, or null when it carries none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTlsKey))]
    [NotifyPropertyChangedFor(nameof(TlsKeyLabel))]
    public partial string? TlsKeyBlock { get; set; }

    public bool HasTlsKey => TlsKeyBlock is not null;

    public string TlsKeyLabel => localizer.Translate("editor.blockTls", TlsKeyBlock ?? string.Empty);

    /// <summary>
    /// How many servers the configuration names. The form changes the first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreRemotes))]
    [NotifyPropertyChangedFor(nameof(MoreRemotesNotice))]
    public partial int RemoteCount { get; set; }

    public bool HasMoreRemotes => RemoteCount > 1;

    public string MoreRemotesNotice => localizer.Translate("editor.moreRemotes", RemoteCount);

    public string ViewToggleLabel => localizer[IsPlainText ? "editor.showForm" : "editor.showPlain"];

    /// <summary>
    /// The label for a favourite slot, which is its number or the word for having none.
    /// </summary>
    /// <remarks>
    /// The reader's own culture, because this is a number shown to a person, and the slots the
    /// shortcuts use are the digits on the keyboard either way.
    /// </remarks>
    public string SlotLabel(int slot) =>
        slot == 0 ? localizer["common.none"] : slot.ToString(CultureInfo.CurrentCulture);

    public string TagsHelp => localizer["tags.help"];

    /// <summary>
    /// Reads the stored configuration into the form and the text.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        storedConfiguration = await store.GetConfigurationAsync(profileId, cancellationToken) ?? string.Empty;

        populating = true;

        try
        {
            ConfigurationText = storedConfiguration;
        }
        finally
        {
            populating = false;
        }

        ReadForm(storedConfiguration);
        IsPlainText = startsInPlainText;
        IsLoaded = true;

        Revalidate();
    }

    partial void OnHostChanged(string value) => Edited();

    partial void OnPortChanged(decimal? value) => Edited();

    partial void OnSelectedProtocolChanged(ProtocolChoice? value) => Edited();

    partial void OnCaTextChanged(string value) => Edited();

    partial void OnCertTextChanged(string value) => Edited();

    partial void OnKeyTextChanged(string value) => Edited();

    partial void OnTlsKeyTextChanged(string value) => Edited();

    partial void OnConfigurationTextChanged(string value) => Edited();

    private void Edited()
    {
        if (!populating && IsLoaded)
        {
            Revalidate();
        }
    }

    /// <summary>
    /// Switches between the form and the text, carrying what was edited in one into the other.
    /// </summary>
    [RelayCommand]
    private void ToggleView()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (IsPlainText)
        {
            ReadForm(ConfigurationText);
            IsPlainText = false;
        }
        else
        {
            string composed = Compose();

            populating = true;

            try
            {
                ConfigurationText = composed;
            }
            finally
            {
                populating = false;
            }

            IsPlainText = true;
        }

        Revalidate();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = localizer["editor.nameRequired"];
            return;
        }

        Revalidate();

        if (HasErrors)
        {
            StatusMessage = localizer["editor.fixIssues"];
            return;
        }

        // A text box on Windows writes its own line ending into what is typed. The file keeps the one
        // it had, so an edit does not turn every line of a configuration into a change.
        string configuration = Compose().ReplaceLineEndings(LineEndingOf(storedConfiguration));

        if (!string.Equals(configuration, storedConfiguration, StringComparison.Ordinal))
        {
            ConfigurationUpdate update = await store.UpdateConfigurationAsync(profileId, configuration);

            if (update.DuplicateOf is { } other)
            {
                StatusMessage = localizer.Translate("editor.duplicate", other);
                return;
            }

            if (!update.Saved)
            {
                StatusMessage = localizer["editor.profileGone"];
                return;
            }

            storedConfiguration = configuration;
        }

        await store.RenameProfileAsync(profileId, Name);
        await store.SetProfileNotesAsync(profileId, Notes);
        await store.SetRouteProtectionAsync(profileId, SelectedRouteProtection?.Value);
        await store.SetFavouriteAsync(profileId, IsFavourite || SelectedSlot > 0);

        await store.SetProfileTagsAsync(
            profileId,
            TagsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // The slot is set last, because assigning one also makes the profile a favourite and would
        // otherwise be undone by the favourite update above.
        await store.SetFavouriteSlotAsync(profileId, SelectedSlot == 0 ? null : SelectedSlot);

        Closed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        await store.DeleteProfileAsync(profileId);
        DeleteRequested?.Invoke(this, profileId);
        Closed?.Invoke(this, true);
    }

    /// <summary>
    /// Fills the form from a configuration, which also becomes what the form's edits apply to.
    /// </summary>
    private void ReadForm(string configuration)
    {
        populating = true;

        try
        {
            formConfiguration = configuration;
            formEndpoint = OvpnConfigEditor.ReadEndpoint(configuration);

            Host = formEndpoint?.Host ?? string.Empty;
            Port = formEndpoint?.Port ?? 1194;
            SelectedProtocol = Protocols.First(choice => choice.Value == (formEndpoint?.Protocol ?? OvpnProtocol.Udp));
            RemoteCount = formEndpoint?.RemoteCount ?? 0;

            formBlocks.Clear();

            foreach (string name in OvpnConfigEditor.KeyBlocks)
            {
                formBlocks[name] = OvpnConfigEditor.ReadBlock(configuration, name);
            }

            CaText = formBlocks["ca"] ?? string.Empty;
            CertText = formBlocks["cert"] ?? string.Empty;
            KeyText = formBlocks["key"] ?? string.Empty;

            TlsKeyBlock = OvpnConfigEditor.TlsKeyBlocks
                .FirstOrDefault(name => OvpnConfigEditor.ReadBlock(configuration, name) is not null);

            if (TlsKeyBlock is { } tls)
            {
                formBlocks[tls] = OvpnConfigEditor.ReadBlock(configuration, tls);
                TlsKeyText = formBlocks[tls] ?? string.Empty;
            }
            else
            {
                TlsKeyText = string.Empty;
            }
        }
        finally
        {
            populating = false;
        }
    }

    /// <summary>
    /// The configuration as it would be saved now.
    /// </summary>
    /// <remarks>
    /// In the form, only a field that differs from what was read is written back, so opening the
    /// editor and saving changes no line of the configuration at all.
    /// </remarks>
    private string Compose()
    {
        if (IsPlainText)
        {
            return ConfigurationText;
        }

        string configuration = formConfiguration;
        string host = Host.Trim();
        OvpnProtocol protocol = SelectedProtocol?.Value ?? OvpnProtocol.Udp;

        if (host.Length > 0 && Port is { } value && value is >= 1 and <= 65535)
        {
            int port = (int)value;

            bool changed = formEndpoint is null
                || !string.Equals(host, formEndpoint.Host, StringComparison.Ordinal)
                || port != formEndpoint.Port
                || protocol != formEndpoint.Protocol;

            if (changed)
            {
                configuration = OvpnConfigEditor.SetEndpoint(configuration, host, port, protocol);
            }
        }

        configuration = ApplyBlock(configuration, "ca", CaText);
        configuration = ApplyBlock(configuration, "cert", CertText);
        configuration = ApplyBlock(configuration, "key", KeyText);

        if (TlsKeyBlock is { } tls)
        {
            configuration = ApplyBlock(configuration, tls, TlsKeyText);
        }

        return configuration;
    }

    private string ApplyBlock(string configuration, string name, string text)
    {
        string read = (formBlocks.GetValueOrDefault(name) ?? string.Empty).ReplaceLineEndings("\n").Trim('\n');
        string edited = text.ReplaceLineEndings("\n").Trim('\n');

        return string.Equals(read, edited, StringComparison.Ordinal)
            ? configuration
            : OvpnConfigEditor.SetBlock(configuration, name, edited);
    }

    private void Revalidate()
    {
        Issues.Clear();

        if (!IsPlainText)
        {
            if (Host.Trim().Length == 0)
            {
                Issues.Add(new ProfileEditorIssue(localizer["editor.issue.HostRequired"], IsError: true));
            }

            if (Port is not { } port || port is < 1 or > 65535)
            {
                Issues.Add(new ProfileEditorIssue(localizer["editor.issue.PortRequired"], IsError: true));
            }
        }

        foreach (OvpnConfigIssue issue in OvpnConfigValidator.Validate(Compose()))
        {
            // The form already says so about its own fields, and in its own words.
            if (!IsPlainText && issue.Code == OvpnConfigIssueCode.NoRemote && Host.Trim().Length == 0)
            {
                continue;
            }

            Issues.Add(new ProfileEditorIssue(Describe(issue), issue.IsError));
        }

        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasErrors));

        if (!HasErrors && StatusMessage == localizer["editor.fixIssues"])
        {
            StatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// The sentence for one finding, with the line it is on when the text is what is showing.
    /// </summary>
    /// <remarks>
    /// A line number means something only beside the lines it counts. In the form it would point at
    /// text that is not on screen.
    /// </remarks>
    private string Describe(OvpnConfigIssue issue)
    {
        string text = localizer.Translate("editor.issue." + issue.Code, [.. issue.Arguments]);

        return IsPlainText && issue.LineNumber > 0
            ? localizer.Translate("editor.issueOnLine", issue.LineNumber, text)
            : text;
    }

    private static string LineEndingOf(string configuration) =>
        configuration.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}

/// <summary>
/// One entry in the route protection picker. A null value follows the application wide setting.
/// </summary>
public sealed record RouteProtectionChoice(bool? Value, string Name);

/// <summary>
/// One entry in the protocol picker.
/// </summary>
public sealed record ProtocolChoice(OvpnProtocol Value, string Name);

/// <summary>
/// One finding about the configuration being edited, worded for the reader.
/// </summary>
public sealed record ProfileEditorIssue(string Text, bool IsError)
{
    public bool IsWarning => !IsError;
}
