using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The import wizard: pick a source, review what would happen, then commit.
/// </summary>
/// <remarks>
/// Nothing is written until the user has seen the outcome for every file. Duplicates and rejects are
/// listed rather than hidden, because a silent skip is indistinguishable from a successful import
/// when the profile does not appear afterwards.
///
/// A package is the other thing that can be imported, and it arrives the same way: picked or dropped
/// on the window. It cannot be previewed until it is decrypted, so the screen changes shape and asks
/// for the passphrase first. Once open, it lists what the package holds: every profile, marked where
/// the store already has it, the sign ins, the shortcuts and the settings, and nothing is taken that
/// is not ticked. Handing someone a set of profiles is worth little if opening it needs a terminal,
/// and a package somebody else assembled rarely holds only what the person opening it wants.
/// </remarks>
public sealed partial class ImportViewModel : ViewModelBase, IDisposable
{
    private readonly IProfileImportService importer;
    private readonly IProfileStore store;
    private readonly IProfilePackageWriter packages;
    private readonly ILocalizer localizer;

    /// <summary>
    /// The extension that marks a portable package rather than a configuration.
    /// </summary>
    public const string PackageExtension = ".ovppkg";

    private ImportSelection? selection;
    private IReadOnlyList<ImportCandidate> candidates = [];
    private bool disposed;

    public ImportViewModel(
        IProfileImportService importer,
        IProfileStore store,
        IProfilePackageWriter packages,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(localizer);

        this.importer = importer;
        this.store = store;
        this.packages = packages;
        this.localizer = localizer;
    }

    /// <summary>
    /// Raised when the wizard is finished with, with true when profiles were stored.
    /// </summary>
    public event EventHandler<bool>? Closed;

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial string TagsInput { get; set; } = string.Empty;

    /// <summary>
    /// Whether a chosen directory contributes what is beneath it as well.
    /// </summary>
    public bool IncludeSubfolders
    {
        get => includeSubfolders;
        set => SetProperty(ref includeSubfolders, value);
    }

    private bool includeSubfolders = true;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial int ImportableCount { get; set; }

    [ObservableProperty]
    public partial int DuplicateCount { get; set; }

    [ObservableProperty]
    public partial int RejectedCount { get; set; }

    /// <summary>
    /// The package waiting to be opened, or null while ordinary configurations are being examined.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPackage))]
    [NotifyPropertyChangedFor(nameof(IsPackageLocked))]
    [NotifyPropertyChangedFor(nameof(IsPackageOpen))]
    [NotifyPropertyChangedFor(nameof(PackageName))]
    [NotifyPropertyChangedFor(nameof(CommitLabel))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    public partial string? PackagePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial string Passphrase { get; set; } = string.Empty;

    /// <summary>
    /// The package once its passphrase has opened it, or null before that.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPackageLocked))]
    [NotifyPropertyChangedFor(nameof(IsPackageOpen))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(CommitLabel))]
    [NotifyPropertyChangedFor(nameof(PackageOrigin))]
    [NotifyPropertyChangedFor(nameof(HasPackageCredentials))]
    [NotifyPropertyChangedFor(nameof(HasPackageHotkeys))]
    [NotifyPropertyChangedFor(nameof(HasPackageSettings))]
    [NotifyPropertyChangedFor(nameof(PackageHotkeysLabel))]
    [NotifyPropertyChangedFor(nameof(PackageHotkeysClashing))]
    [NotifyPropertyChangedFor(nameof(HasPackageHotkeysClashing))]
    public partial OpenedPackage? OpenedPackage { get; set; }

    public bool IsPackage => PackagePath is not null;

    public bool IsPackageLocked => IsPackage && OpenedPackage is null;

    public bool IsPackageOpen => IsPackage && OpenedPackage is not null;

    public string PackageName => PackagePath is { } path ? Path.GetFileName(path) : string.Empty;

    /// <summary>
    /// The profiles an opened package holds, each ticked unless the person unticks it.
    /// </summary>
    public ObservableCollection<ImportPackageProfileViewModel> PackageProfiles { get; } = [];

    /// <summary>
    /// The tags the packaged profiles carry, each ticking the profiles that carry it.
    /// </summary>
    public ObservableCollection<TagChoiceViewModel> PackageTags { get; } = [];

    public bool HasPackageTags => PackageTags.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(PackageSelectionSummary))]
    [NotifyPropertyChangedFor(nameof(PackageCredentialsLabel))]
    public partial int PackageSelectedCount { get; set; }

    [ObservableProperty]
    public partial bool IncludePackageCredentials { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool IncludePackageHotkeys { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool IncludePackageSettings { get; set; }

    public string PackageOrigin => OpenedPackage is { } opened
        ? localizer.Translate(
            "import.packageOrigin",
            opened.CreatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            opened.WrittenBy)
        : string.Empty;

    public string PackageSelectionSummary =>
        localizer.Translate("import.packageSelected", PackageSelectedCount, PackageProfiles.Count);

    public bool HasPackageCredentials => OpenedPackage?.Preview.Profiles.Any(profile => profile.Credentials > 0) == true;

    public string PackageCredentialsLabel => localizer.Translate(
        "import.packageCredentials",
        PackageProfiles.Where(profile => profile.IsSelected).Sum(profile => profile.Credentials));

    public bool HasPackageHotkeys => OpenedPackage?.Preview.HotkeysToAdd > 0;

    public string PackageHotkeysLabel =>
        localizer.Translate("import.packageHotkeys", OpenedPackage?.Preview.HotkeysToAdd ?? 0);

    public bool HasPackageHotkeysClashing => OpenedPackage?.Preview.HotkeysClashing > 0;

    public string PackageHotkeysClashing =>
        localizer.Translate("import.packageHotkeysClashing", OpenedPackage?.Preview.HotkeysClashing ?? 0);

    public bool HasPackageSettings => OpenedPackage?.Preview.HasSettings == true;

    public string CommitLabel => localizer[IsPackageLocked ? "import.open" : "import.commit"];

    /// <summary>
    /// Set while ticks are being changed on behalf of a tag, so they are not counted one by one.
    /// </summary>
    private bool applyingTag;

    public bool CanCommit => IsPackage
        ? !IsBusy && (OpenedPackage is null
            ? Passphrase.Length > 0
            : PackageSelectedCount > 0
                || (IncludePackageHotkeys && HasPackageHotkeys)
                || (IncludePackageSettings && HasPackageSettings))
        : ImportableCount > 0 && !IsBusy;

    public bool HasRows => !IsPackage && Rows.Count > 0;

    public string DropHint => localizer["import.dropHint"];

    public string TagsHelp => localizer["tags.help"];

    /// <summary>
    /// Examines the given files, directories or archives without writing anything.
    /// </summary>
    public async Task ExamineAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.FirstOrDefault(IsPackagePath) is { } package)
        {
            OfferPackage(package);
            return;
        }

        IsBusy = true;

        try
        {
            PackagePath = null;

            // A previous run may have unpacked archives that are no longer part of the selection.
            selection?.Dispose();

            selection = await importer.ExpandAsync(paths, IncludeSubfolders, cancellationToken);

            if (selection.Files.Count == 0)
            {
                Rows.Clear();
                candidates = [];
                ImportableCount = 0;
                DuplicateCount = 0;
                RejectedCount = 0;
                StatusMessage = localizer["import.nothingFound"];
                OnPropertyChanged(nameof(HasRows));
                return;
            }

            candidates = await importer.PrepareAsync(selection.Files, cancellationToken);

            Rows.Clear();
            foreach (ImportCandidate candidate in candidates)
            {
                Rows.Add(new ImportRowViewModel(candidate, localizer));
            }

            ImportableCount = candidates.Count(c => c.Outcome == ImportOutcome.Importable);
            DuplicateCount = candidates.Count(c =>
                c.Outcome is ImportOutcome.DuplicateInSelection or ImportOutcome.DuplicateInStore);
            RejectedCount = candidates.Count(c =>
                c.Outcome is ImportOutcome.Rejected or ImportOutcome.Unreadable);

            StatusMessage = localizer.Translate(
                "import.summary",
                candidates.Count,
                ImportableCount,
                DuplicateCount,
                RejectedCount);

            OnPropertyChanged(nameof(HasRows));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCommit));
        }
    }

    /// <summary>
    /// True for a file that is a package rather than something the parser can read.
    /// </summary>
    private static bool IsPackagePath(string path) =>
        path.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the screen to opening a package.
    /// </summary>
    /// <remarks>
    /// The contents cannot be previewed the way configurations are: the file is encrypted, and
    /// decrypting it to show a list would mean asking for the passphrase first anyway.
    /// </remarks>
    private void OfferPackage(string path)
    {
        selection?.Dispose();
        selection = null;
        candidates = [];
        Rows.Clear();

        ImportableCount = 0;
        DuplicateCount = 0;
        RejectedCount = 0;

        ClosePackage();

        PackagePath = path;
        Passphrase = string.Empty;
        StatusMessage = localizer.Translate("import.packageChosen", Path.GetFileName(path));
    }

    /// <summary>
    /// Forgets an opened package, which holds decrypted keys and sign ins for as long as it is kept.
    /// </summary>
    private void ClosePackage()
    {
        OpenedPackage = null;
        PackageProfiles.Clear();
        PackageTags.Clear();
        PackageSelectedCount = 0;
        OnPropertyChanged(nameof(HasPackageTags));
    }

    /// <summary>
    /// Decrypts the package and lists what it holds.
    /// </summary>
    private async Task OpenPackageAsync()
    {
        IsBusy = true;

        try
        {
            OpenedPackage opened = await packages.OpenAsync(PackagePath!, Passphrase);

            // The passphrase has done its job and has no reason to stay in memory.
            Passphrase = string.Empty;

            foreach (PackagePreviewProfile preview in opened.Preview.Profiles)
            {
                ImportPackageProfileViewModel row = new(preview, localizer);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ImportPackageProfileViewModel.IsSelected) && !applyingTag)
                    {
                        RecountPackageSelection();
                    }
                };

                PackageProfiles.Add(row);
            }

            foreach (string tag in PackageProfiles
                .SelectMany(profile => profile.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                TagChoiceViewModel choice = new(tag, PackageProfiles.Count(profile => profile.Carries(tag)));
                choice.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TagChoiceViewModel.IsSelected) && !applyingTag)
                    {
                        ApplyPackageTag(choice);
                    }
                };

                PackageTags.Add(choice);
            }

            IncludePackageCredentials = opened.Preview.Profiles.Any(profile => profile.Credentials > 0);
            IncludePackageHotkeys = opened.Preview.HotkeysToAdd > 0;
            IncludePackageSettings = false;

            OpenedPackage = opened;
            OnPropertyChanged(nameof(HasPackageTags));
            RecountPackageSelection();

            StatusMessage = localizer["import.packageChoose"];
        }
        catch (CryptographicException)
        {
            // The mode is authenticated, so this is a wrong passphrase or a file that was altered.
            StatusMessage = localizer["import.packageRefused"];
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file was picked a moment ago and can have been moved or locked since.
            StatusMessage = localizer.Translate("import.packageUnreadable", exception.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCommit));
        }
    }

    private void RecountPackageSelection() =>
        PackageSelectedCount = PackageProfiles.Count(profile => profile.IsSelected);

    /// <summary>
    /// Ticks the profiles a tag covers, or unticks the ones no other ticked tag still covers.
    /// </summary>
    private void ApplyPackageTag(TagChoiceViewModel tag)
    {
        applyingTag = true;

        try
        {
            foreach (ImportPackageProfileViewModel profile in PackageProfiles.Where(profile => profile.Carries(tag.Name)))
            {
                profile.IsSelected = tag.IsSelected
                    || PackageTags.Any(other => other.IsSelected && profile.Carries(other.Name));
            }
        }
        finally
        {
            applyingTag = false;
        }

        RecountPackageSelection();
    }

    [RelayCommand]
    private void SelectAllPackaged() => SetAllPackaged(true);

    [RelayCommand]
    private void SelectNonePackaged() => SetAllPackaged(false);

    private void SetAllPackaged(bool selected)
    {
        applyingTag = true;

        try
        {
            foreach (TagChoiceViewModel tag in PackageTags)
            {
                tag.IsSelected = selected;
            }

            foreach (ImportPackageProfileViewModel profile in PackageProfiles)
            {
                profile.IsSelected = selected;
            }
        }
        finally
        {
            applyingTag = false;
        }

        RecountPackageSelection();
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!CanCommit)
        {
            return;
        }

        if (IsPackage)
        {
            if (OpenedPackage is null)
            {
                await OpenPackageAsync();
            }
            else
            {
                await ApplyPackageAsync(OpenedPackage);
            }

            return;
        }

        IsBusy = true;

        try
        {
            IReadOnlyList<string> tags = TagsInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            int created = await importer.CommitAsync(candidates, tags);

            StatusMessage = localizer.Translate("import.stored", created);

            selection?.Dispose();
            selection = null;

            Closed?.Invoke(this, true);
        }
        catch (DbUpdateException exception)
        {
            // Anything that escapes a command ends the application, and an import is exactly where a
            // store written by another version, or a set somebody else assembled, meets this one.
            StatusMessage = localizer.Translate("import.storeRefused", Innermost(exception).Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Writes what was ticked from the opened package into the store.
    /// </summary>
    private async Task ApplyPackageAsync(OpenedPackage opened)
    {
        IsBusy = true;

        try
        {
            PackageImportResult result = await packages.ApplyAsync(
                opened,
                new PackageImportChoice(PackageProfiles.Where(profile => profile.IsSelected).Select(profile => profile.Id).ToList())
                {
                    IncludeCredentials = IncludePackageCredentials,
                    IncludeHotkeys = IncludePackageHotkeys,
                    IncludeSettings = IncludePackageSettings,
                });

            StatusMessage = localizer.Translate(
                "import.packageAppliedParts",
                result.Added,
                result.Skipped,
                result.Credentials,
                result.Hotkeys,
                localizer[result.Settings ? "common.yes" : "common.no"]);

            ClosePackage();
            Closed?.Invoke(this, true);
        }
        catch (DbUpdateException exception)
        {
            // What ended the application when a package with shared tags was opened. That cause is
            // fixed; this is here so the next thing a store refuses is reported on this screen.
            StatusMessage = localizer.Translate("import.storeRefused", Innermost(exception).Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file was picked a moment ago and can have been moved or locked since.
            StatusMessage = localizer.Translate("import.packageUnreadable", exception.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCommit));
        }
    }

    /// <summary>
    /// The exception that says what actually went wrong.
    /// </summary>
    /// <remarks>
    /// Entity Framework wraps the database's refusal in a message that only says to look inside it.
    /// </remarks>
    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    [RelayCommand]
    private void Cancel()
    {
        selection?.Dispose();
        selection = null;
        ClosePackage();
        Closed?.Invoke(this, false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        selection?.Dispose();
        selection = null;
        ClosePackage();
    }
}

/// <summary>
/// One profile an opened package holds.
/// </summary>
public sealed partial class ImportPackageProfileViewModel : ViewModelBase
{
    public ImportPackageProfileViewModel(PackagePreviewProfile preview, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);

        Id = preview.Profile.Id;
        Name = preview.Profile.Name ?? string.Empty;
        Tags = preview.Profile.Tags ?? [];
        Credentials = preview.Credentials;

        Endpoint = preview.Profile.RemoteHost is { Length: > 0 } host
            ? $"{host}:{preview.Profile.RemotePort}/{preview.Profile.Protocol}"
            : string.Empty;

        // Ticked either way. A profile the store already has is skipped when applied, and its sign
        // ins still arrive, which is what a package sent only to fill those in is for.
        StateDisplay = preview.StoredAs is { } stored
            ? localizer.Translate("import.packageStoredAs", stored)
            : localizer["import.packageNew"];

        IsStored = preview.IsStored;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public IReadOnlyList<string> Tags { get; }

    public string TagsDisplay => string.Join(", ", Tags);

    public bool HasTags => Tags.Count > 0;

    public int Credentials { get; }

    public bool IsStored { get; }

    public string StateDisplay { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public bool Carries(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One examined file in the preview list.
/// </summary>
public sealed class ImportRowViewModel
{
    private readonly ImportCandidate candidate;
    private readonly ILocalizer localizer;

    public ImportRowViewModel(ImportCandidate candidate, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(localizer);

        this.candidate = candidate;
        this.localizer = localizer;
    }

    public string Name => candidate.SuggestedName;

    public string SourcePath => candidate.SourcePath;

    public string Endpoint => candidate.RemoteHost is { Length: > 0 }
        ? $"{candidate.RemoteHost}:{candidate.RemotePort}/{candidate.Protocol}"
        : string.Empty;

    public string OutcomeDisplay => localizer["import.outcome." + candidate.Outcome];

    public bool IsImportable => candidate.Outcome == ImportOutcome.Importable;

    public bool IsDuplicate => candidate.Outcome
        is ImportOutcome.DuplicateInSelection or ImportOutcome.DuplicateInStore;

    public bool IsRejected => candidate.Outcome
        is ImportOutcome.Rejected or ImportOutcome.Unreadable;

    public string? Detail => BuildDetail();

    public bool HasDetail => Detail is { Length: > 0 };

    private string? BuildDetail()
    {
        List<string> parts = [];

        if (candidate.Detail is { Length: > 0 })
        {
            parts.Add(candidate.Detail);
        }

        if (candidate.UnsupportedOptions.Count > 0)
        {
            parts.Add(localizer.Translate(
                "import.unsupported",
                string.Join(", ", candidate.UnsupportedOptions)));
        }

        if (candidate.MissingFiles.Count > 0)
        {
            parts.Add(localizer.Translate(
                "import.missingFiles",
                string.Join(", ", candidate.MissingFiles)));
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }
}

