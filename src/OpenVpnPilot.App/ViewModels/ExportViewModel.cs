using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Writes profiles back out, either as ordinary configurations or as one portable package.
/// </summary>
/// <remarks>
/// Both outputs carry private keys, and the two differ in what can be done about that. A package is
/// one file, so it is always encrypted and the passphrase is required rather than offered. Plain
/// configurations have to stay readable by OpenVPN, so they cannot be protected at all, and the
/// screen says so instead of leaving the user to work it out.
///
/// A package can also carry the saved sign ins, which is what turns handing over fifty profiles into
/// something the recipient can use rather than fifty passwords they have to be told separately. It is
/// off unless it is asked for, it is only possible for a package because only a package is
/// encrypted, and the screen says plainly what it means: whoever has the file and the passphrase can
/// connect as the person who wrote it.
/// </remarks>
public sealed partial class ExportViewModel : ViewModelBase
{
    private readonly IProfileStore store;
    private readonly IProfilePackageWriter packages;
    private readonly ILocalizer localizer;

    /// <summary>
    /// How many sign ins are stored per profile, read once when the screen opens.
    /// </summary>
    private IReadOnlyDictionary<Guid, int> credentialCounts = new Dictionary<Guid, int>();

    public ExportViewModel(IProfileStore store, IProfilePackageWriter packages, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(localizer);

        this.store = store;
        this.packages = packages;
        this.localizer = localizer;
    }

    /// <summary>
    /// Raised when the screen is finished with.
    /// </summary>
    public event EventHandler? Closed;

    public ObservableCollection<ExportProfileViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPackage))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool ExportAsPackage { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial string Passphrase { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial string PassphraseConfirmation { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Carries the saved user names and passwords for the selected profiles into the package.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeCredentials { get; set; }

    /// <summary>
    /// How many stored sign ins the current selection would carry.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialSummary))]
    [NotifyPropertyChangedFor(nameof(HasCredentials))]
    public partial int SelectedCredentialCount { get; set; }

    public bool HasCredentials => SelectedCredentialCount > 0;

    /// <summary>
    /// Says how many sign ins the selection would carry, or why it would carry none.
    /// </summary>
    /// <remarks>
    /// Shown even when the answer is none. An option that disappears when it has nothing to do is
    /// indistinguishable from one that does not exist, and someone looking for it would conclude
    /// that packages cannot carry credentials at all.
    /// </remarks>
    public string CredentialSummary => HasCredentials
        ? localizer.Translate("export.credentialsAvailable", SelectedCredentialCount)
        : localizer["export.credentialsNone"];

    /// <summary>
    /// True when the credential option is worth showing: only a package is encrypted, and offering
    /// to carry credentials in a file that is not would be offering to leak them.
    /// </summary>
    public bool CanIncludeCredentials => ExportAsPackage;

    public bool IsPackage => ExportAsPackage;

    public string SelectionSummary => localizer.Translate("export.selected", SelectedCount);

    /// <summary>
    /// A package is encrypted or it is not written, so the passphrase is part of being ready.
    /// </summary>
    public bool CanExport => SelectedCount > 0 && !IsBusy && (!ExportAsPackage || PassphraseIsUsable);

    public bool PassphraseIsUsable =>
        Passphrase.Length >= MinimumPassphraseLength
        && string.Equals(Passphrase, PassphraseConfirmation, StringComparison.Ordinal);

    /// <summary>
    /// Short enough not to be a nuisance, long enough that the work factor has something to protect.
    /// </summary>
    public const int MinimumPassphraseLength = 8;

    /// <summary>
    /// Why the passphrase is not accepted yet, or null when it is.
    /// </summary>
    public string? PassphraseProblem
    {
        get
        {
            if (!ExportAsPackage)
            {
                return null;
            }

            if (Passphrase.Length == 0)
            {
                return localizer["export.passphraseRequired"];
            }

            if (Passphrase.Length < MinimumPassphraseLength)
            {
                return localizer.Translate("export.passphraseTooShort", MinimumPassphraseLength);
            }

            return string.Equals(Passphrase, PassphraseConfirmation, StringComparison.Ordinal)
                ? null
                : localizer["export.passphraseMismatch"];
        }
    }

    public bool HasPassphraseProblem => PassphraseProblem is { Length: > 0 };

    public string SuggestedFileName => ExportAsPackage ? "openvpnpilot.ovppkg" : string.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Profiles.Clear();
        credentialCounts = await packages.CountCredentialsAsync(cancellationToken);

        foreach (Profile profile in await store.GetProfilesAsync(cancellationToken))
        {
            ExportProfileViewModel row = new(profile.Id, profile.Name);
            row.PropertyChanged += (_, _) => RecountSelection();
            Profiles.Add(row);
        }

        RecountSelection();
    }

    partial void OnExportAsPackageChanged(bool value)
    {
        OnPropertyChanged(nameof(PassphraseProblem));
        OnPropertyChanged(nameof(HasPassphraseProblem));
        OnPropertyChanged(nameof(SuggestedFileName));
        OnPropertyChanged(nameof(CanIncludeCredentials));

        if (!value)
        {
            // Plain configurations cannot be protected, so the option is not merely hidden.
            IncludeCredentials = false;
        }
    }

    partial void OnPassphraseChanged(string value) => RaisePassphraseState();

    partial void OnPassphraseConfirmationChanged(string value) => RaisePassphraseState();

    private void RaisePassphraseState()
    {
        OnPropertyChanged(nameof(PassphraseIsUsable));
        OnPropertyChanged(nameof(PassphraseProblem));
        OnPropertyChanged(nameof(HasPassphraseProblem));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (ExportProfileViewModel profile in Profiles)
        {
            profile.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (ExportProfileViewModel profile in Profiles)
        {
            profile.IsSelected = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Writes the package. The destination comes from the view, which owns the picker.
    /// </summary>
    public async Task<bool> WritePackageAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!CanExport)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            PackageWriteResult written = await packages.WriteAsync(
                path,
                SelectedIds,
                Passphrase,
                IncludeCredentials,
                cancellationToken);

            StatusMessage = written.Credentials > 0
                ? localizer.Translate(
                    "export.packageWrittenWithCredentials",
                    written.Profiles,
                    written.Credentials,
                    path)
                : localizer.Translate("export.packageWritten", written.Profiles, path);

            // The passphrase has done its job and has no reason to stay in memory.
            Passphrase = string.Empty;
            PassphraseConfirmation = string.Empty;

            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Writes one configuration file per selected profile into a directory.
    /// </summary>
    public async Task<bool> WriteConfigurationsAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!CanExport)
        {
            return false;
        }

        IsBusy = true;

        try
        {
            Directory.CreateDirectory(directory);
            int written = 0;

            foreach (ExportProfileViewModel profile in Profiles.Where(item => item.IsSelected))
            {
                string? configuration = await store.GetConfigurationAsync(profile.Id, cancellationToken);

                if (configuration is null)
                {
                    continue;
                }

                string path = Path.Combine(directory, SafeFileName(profile.Name) + ".ovpn");
                await File.WriteAllTextAsync(path, configuration, new UTF8Encoding(false), cancellationToken);
                written++;
            }

            StatusMessage = localizer.Translate("export.configurationsWritten", written, directory);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyCollection<Guid> SelectedIds =>
        Profiles.Where(profile => profile.IsSelected).Select(profile => profile.Id).ToList();

    private void RecountSelection()
    {
        SelectedCount = Profiles.Count(profile => profile.IsSelected);

        SelectedCredentialCount = Profiles
            .Where(profile => profile.IsSelected)
            .Sum(profile => credentialCounts.TryGetValue(profile.Id, out int count) ? count : 0);

        OnPropertyChanged(nameof(CanIncludeCredentials));

        if (!HasCredentials)
        {
            IncludeCredentials = false;
        }
    }

    /// <summary>
    /// Turns a profile name into a file name, because a name is free text and a path is not.
    /// </summary>
    private static string SafeFileName(string name)
    {
        StringBuilder builder = new(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (char character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// One profile in the export list.
/// </summary>
public sealed partial class ExportProfileViewModel : ViewModelBase
{
    public ExportProfileViewModel(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}
