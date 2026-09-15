using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Import;

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
/// on the window. It cannot be previewed file by file because it is encrypted, so the screen changes
/// shape and asks for the passphrase instead. Handing someone a set of profiles is worth little if
/// opening it needs a terminal.
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
    [NotifyPropertyChangedFor(nameof(PackageName))]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    public partial string? PackagePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial string Passphrase { get; set; } = string.Empty;

    public bool IsPackage => PackagePath is not null;

    public string PackageName => PackagePath is { } path ? Path.GetFileName(path) : string.Empty;

    public bool CanCommit => IsPackage
        ? !IsBusy && Passphrase.Length > 0
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

        PackagePath = path;
        Passphrase = string.Empty;
        StatusMessage = localizer.Translate("import.packageChosen", Path.GetFileName(path));
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
            await ApplyPackageAsync();
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
    /// Opens the package and writes what it holds into the store.
    /// </summary>
    private async Task ApplyPackageAsync()
    {
        IsBusy = true;

        try
        {
            PackageImportResult result = await packages.ApplyAsync(PackagePath!, Passphrase);

            StatusMessage = result.Credentials > 0
                ? localizer.Translate(
                    "import.packageAppliedWithCredentials",
                    result.Added,
                    result.Skipped,
                    result.Credentials)
                : localizer.Translate("import.packageApplied", result.Added, result.Skipped);

            // The passphrase has done its job and has no reason to stay in memory.
            Passphrase = string.Empty;

            Closed?.Invoke(this, true);
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
    }
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

