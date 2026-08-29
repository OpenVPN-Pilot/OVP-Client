using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The import wizard: pick a source, review what would happen, then commit.
/// </summary>
/// <remarks>
/// Nothing is written until the user has seen the outcome for every file. Duplicates and rejects are
/// listed rather than hidden, because a silent skip is indistinguishable from a successful import
/// when the profile does not appear afterwards.
/// </remarks>
public sealed partial class ImportViewModel : ViewModelBase, IDisposable
{
    private readonly IProfileImportService importer;
    private readonly IProfileStore store;
    private readonly ILocalizer localizer;

    private ImportSelection? selection;
    private IReadOnlyList<ImportCandidate> candidates = [];
    private bool disposed;

    public ImportViewModel(IProfileImportService importer, IProfileStore store, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(localizer);

        this.importer = importer;
        this.store = store;
        this.localizer = localizer;
    }

    /// <summary>
    /// Raised when the wizard is finished with, with true when profiles were stored.
    /// </summary>
    public event EventHandler<bool>? Closed;

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];

    public ObservableCollection<ImportFolderChoice> Folders { get; } = [];

    [ObservableProperty]
    public partial ImportFolderChoice? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial string TagsInput { get; set; } = string.Empty;

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

    public bool CanCommit => ImportableCount > 0 && !IsBusy;

    public bool HasRows => Rows.Count > 0;

    public string DropHint => localizer["import.dropHint"];

    public async Task LoadFoldersAsync(CancellationToken cancellationToken = default)
    {
        Folders.Clear();
        Folders.Add(new ImportFolderChoice(null, localizer["import.noFolder"]));

        foreach (Folder folder in await store.GetFoldersAsync(cancellationToken))
        {
            Folders.Add(new ImportFolderChoice(folder.Id, folder.Name));
        }

        SelectedFolder ??= Folders[0];
    }

    /// <summary>
    /// Examines the given files, directories or archives without writing anything.
    /// </summary>
    public async Task ExamineAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        IsBusy = true;

        try
        {
            // A previous run may have unpacked archives that are no longer part of the selection.
            selection?.Dispose();

            selection = await importer.ExpandAsync(paths, cancellationToken);

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

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!CanCommit)
        {
            return;
        }

        IsBusy = true;

        try
        {
            IReadOnlyList<string> tags = TagsInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            int created = await importer.CommitAsync(candidates, SelectedFolder?.FolderId, tags);

            StatusMessage = localizer.Translate("import.stored", created);

            selection?.Dispose();
            selection = null;

            Closed?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
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

/// <summary>
/// One entry in the target folder picker. A null identifier files the profiles at the top level.
/// </summary>
public sealed record ImportFolderChoice(Guid? FolderId, string Name);
