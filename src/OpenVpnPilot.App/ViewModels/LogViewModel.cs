using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The live log, filtered by where a line came from, how serious it is and what it says.
/// </summary>
/// <remarks>
/// Two streams in one list, in the order they happened. Splitting them into two views would hide
/// exactly what a log is read for: the client said it was connecting, and then OpenVPN said why that
/// did not work. The filter is there for when only one of them is wanted.
///
/// The view holds its own copy rather than binding to the hub's ring, because the filter changes
/// what is shown and re-applying it must not disturb what is being collected.
/// </remarks>
public sealed partial class LogViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// The most lines the window shows at once, matching what the hub keeps.
    /// </summary>
    private const int MaximumRows = 5000;

    private readonly LogHub hub;
    private readonly ILocalizer localizer;
    private bool disposed;

    public LogViewModel(LogHub hub, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(localizer);

        this.hub = hub;
        this.localizer = localizer;

        Sources =
        [
            new LogSourceChoice(null, localizer["log.sourceAll"]),
            new LogSourceChoice(LogSource.Pilot, localizer["log.sourcePilot"]),
            new LogSourceChoice(LogSource.OpenVpn, localizer["log.sourceOpenVpn"]),
        ];

        Levels =
        [
            new LogLevelChoice(LogEntryLevel.Trace, localizer["log.levelAll"]),
            new LogLevelChoice(LogEntryLevel.Debug, localizer["log.levelDebug"]),
            new LogLevelChoice(LogEntryLevel.Information, localizer["log.levelInformation"]),
            new LogLevelChoice(LogEntryLevel.Warning, localizer["log.levelWarning"]),
            new LogLevelChoice(LogEntryLevel.Error, localizer["log.levelError"]),
        ];

        SelectedSource = Sources[0];
        SelectedLevel = Levels[0];
    }

    public ObservableCollection<LogRowViewModel> Rows { get; } = [];

    public ObservableCollection<LogSourceChoice> Sources { get; }

    public ObservableCollection<LogLevelChoice> Levels { get; }

    [ObservableProperty]
    public partial LogSourceChoice? SelectedSource { get; set; }

    [ObservableProperty]
    public partial LogLevelChoice? SelectedLevel { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; } = string.Empty;

    /// <summary>
    /// Whether the view follows the newest line.
    /// </summary>
    /// <remarks>
    /// On by default, because a live log that does not follow is a log nobody sees the end of, and
    /// switchable, because a log that jumps away while something is being read is worse.
    /// </remarks>
    [ObservableProperty]
    public partial bool FollowsTail { get; set; } = true;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasRows => Rows.Count > 0;

    public string EmptyText => localizer["log.empty"];

    /// <summary>
    /// Where the files are written.
    /// </summary>
    public string DirectoryPath => hub.DirectoryPath;

    /// <summary>
    /// Raised when a new line was added and the view is following the tail.
    /// </summary>
    /// <remarks>
    /// Scrolling is a windowing operation, so the window does it. The view model only says when
    /// there is something new at the bottom.
    /// </remarks>
    public event EventHandler? ScrolledToEnd;

    public void Attach()
    {
        Reload();
        hub.Appended += OnAppended;
    }

    private void OnAppended(object? sender, LogEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        if (!Matches(entry))
        {
            return;
        }

        Rows.Add(new LogRowViewModel(entry));
        TrimAndAnnounce();
    });

    partial void OnSelectedSourceChanged(LogSourceChoice? value) => Reload();

    partial void OnSelectedLevelChanged(LogLevelChoice? value) => Reload();

    partial void OnSearchTermChanged(string value) => Reload();

    /// <summary>
    /// Re-applies the filter to everything the hub still holds.
    /// </summary>
    private void Reload()
    {
        Rows.Clear();

        foreach (LogEntry entry in hub.Snapshot())
        {
            if (Matches(entry))
            {
                Rows.Add(new LogRowViewModel(entry));
            }
        }

        TrimAndAnnounce();
        StatusMessage = localizer.Translate("log.showing", Rows.Count);
    }

    private void TrimAndAnnounce()
    {
        while (Rows.Count > MaximumRows)
        {
            Rows.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasRows));

        if (FollowsTail)
        {
            ScrolledToEnd?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Matches(LogEntry entry)
    {
        if (SelectedSource?.Source is { } source && entry.Source != source)
        {
            return false;
        }

        if (SelectedLevel is { } level && entry.Level < level.Minimum)
        {
            return false;
        }

        if (SearchTerm.Length == 0)
        {
            return true;
        }

        return entry.Message.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase)
            || entry.Scope.Contains(SearchTerm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Empties the view. The files are untouched, which is the point of a log.
    /// </summary>
    [RelayCommand]
    private void ClearView()
    {
        Rows.Clear();
        OnPropertyChanged(nameof(HasRows));
        StatusMessage = localizer["log.viewCleared"];
    }

    /// <summary>
    /// Writes what is currently shown to a file.
    /// </summary>
    public async Task<int> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        StringBuilder builder = new();

        foreach (LogRowViewModel row in Rows)
        {
            builder.AppendLine(row.Entry.ToFileLine());
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken);

        StatusMessage = localizer.Translate("log.saved", Rows.Count, path);
        return Rows.Count;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        hub.Appended -= OnAppended;
    }
}

/// <summary>
/// One line, prepared for display.
/// </summary>
public sealed class LogRowViewModel
{
    public LogRowViewModel(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    public LogEntry Entry { get; }

    public string TimeDisplay =>
        Entry.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string SourceDisplay => Entry.Source == LogSource.OpenVpn ? "openvpn" : "pilot";

    public string ScopeDisplay => Entry.Scope;

    public string Message => Entry.Message;

    public LogEntryLevel Level => Entry.Level;
}

/// <summary>
/// One entry in the source filter. A null source means both.
/// </summary>
public sealed record LogSourceChoice(LogSource? Source, string Name);

/// <summary>
/// One entry in the level filter, naming the lowest level still shown.
/// </summary>
public sealed record LogLevelChoice(LogEntryLevel Minimum, string Name);
