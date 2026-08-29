using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The borderless palette: type a few letters, press return, connect.
/// </summary>
/// <remarks>
/// Matching is a subsequence match rather than a substring one, so "jpt" finds "japan-tcp" the way a
/// command palette does. Results are ranked so that the closest match is already selected when the
/// user stops typing, because the whole point is that return connects without another decision.
///
/// A subsequence is only ever looked for in the name. Spread across the endpoint and the tags as
/// well it matches almost anything: every letter of "lab-11" is found somewhere in
/// "lab-10-cert 127.0.0.1:1210 udp", so a search for a profile that does not exist returned the
/// whole set. A palette that answers a miss with everything is worse than one that answers nothing.
/// </remarks>
public sealed partial class QuickSwitcherViewModel : ViewModelBase
{
    private readonly ILocalizer localizer;
    private readonly List<QuickSwitcherEntry> candidates = [];

    public QuickSwitcherViewModel(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        this.localizer = localizer;
    }

    /// <summary>
    /// Raised when the user picked an entry, carrying the profile and what to do about the window.
    /// </summary>
    public event EventHandler<QuickSwitcherChoice>? Accepted;

    /// <summary>
    /// Raised when the palette should close without doing anything.
    /// </summary>
    public event EventHandler? Dismissed;

    public ObservableCollection<QuickSwitcherEntry> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial QuickSwitcherEntry? SelectedResult { get; set; }

    /// <summary>
    /// Whether the palette is picking something to connect or something to stop.
    /// </summary>
    /// <remarks>
    /// One surface, two jobs. Stopping a tunnel is the same act as starting one, done to a shorter
    /// list, and a second window with its own search box and its own key handling would be the same
    /// code twice with two places to get it wrong.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyPropertyChangedFor(nameof(IsDisconnecting))]
    public partial QuickSwitcherMode Mode { get; set; } = QuickSwitcherMode.Connect;

    public bool IsDisconnecting => Mode == QuickSwitcherMode.Disconnect;

    public string Placeholder => localizer[
        IsDisconnecting ? "switcher.disconnectPlaceholder" : "switcher.placeholder"];

    public string EmptyText => localizer[
        IsDisconnecting ? "switcher.nothingConnected" : "switcher.noMatch"];

    public string Hint => localizer[IsDisconnecting ? "switcher.disconnectHint" : "switcher.hint"];

    /// <summary>
    /// True when at least one row has been ticked, which is what makes return act on several.
    /// </summary>
    public bool HasTicked => Results.Any(entry => entry.IsTicked);

    public bool HasResults => Results.Count > 0;

    /// <summary>
    /// Fills the palette from the profiles currently loaded and resets the query.
    /// </summary>
    public void Reset(
        IEnumerable<ProfileItemViewModel> profiles,
        QuickSwitcherMode mode = QuickSwitcherMode.Connect)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        Mode = mode;
        candidates.Clear();

        // Stopping only ever applies to what is running, so the list is the answer to the question
        // before anything is typed.
        foreach (ProfileItemViewModel profile in profiles.Where(
            profile => mode == QuickSwitcherMode.Connect || !profile.IsIdle))
        {
            candidates.Add(new QuickSwitcherEntry(
                profile.Id,
                profile.Name,
                profile.Endpoint,
                profile.TagsDisplay,
                profile.IsConnected,
                profile.FavouriteSlot,
                profile.LastConnectedAt));
        }

        Query = string.Empty;
        Rank();
    }

    partial void OnQueryChanged(string value) => Rank();

    /// <summary>
    /// Moves the selection, so the arrow keys work while the focus stays in the text box.
    /// </summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        int index = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        index = Math.Clamp(index + delta, 0, Results.Count - 1);

        SelectedResult = Results[index];
    }

    /// <summary>
    /// Connects the selected profile and leaves the window where it was.
    /// </summary>
    /// <remarks>
    /// The palette exists to connect without stopping what you were doing, so the ordinary case
    /// brings nothing to the front. Someone who wants to watch the connection asks for that, and
    /// gets the window as well.
    /// </remarks>
    [RelayCommand]
    private void Accept() => Choose(showWindow: false);

    [RelayCommand]
    private void AcceptAndShow() => Choose(showWindow: true);

    /// <summary>
    /// Ticks or unticks the highlighted row, which is how several are chosen at once.
    /// </summary>
    public void ToggleTick()
    {
        if (SelectedResult is { } entry)
        {
            entry.IsTicked = !entry.IsTicked;
            OnPropertyChanged(nameof(HasTicked));
        }
    }

    /// <summary>
    /// Everything ticked, or the highlighted row when nothing is.
    /// </summary>
    /// <remarks>
    /// Ticking nothing and pressing return is the common case and has to keep working, so an empty
    /// selection means the row under the cursor rather than nothing at all.
    /// </remarks>
    private IReadOnlyList<QuickSwitcherEntry> Chosen
    {
        get
        {
            List<QuickSwitcherEntry> ticked = Results.Where(entry => entry.IsTicked).ToList();

            return ticked.Count > 0
                ? ticked
                : SelectedResult is { } entry ? [entry] : [];
        }
    }

    private void Choose(bool showWindow)
    {
        IReadOnlyList<QuickSwitcherEntry> chosen = Chosen;

        if (chosen.Count > 0)
        {
            Accepted?.Invoke(this, new QuickSwitcherChoice(chosen, showWindow, Mode));
        }
    }

    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke(this, EventArgs.Empty);

    private void Rank()
    {
        string term = Query.Trim();

        List<QuickSwitcherEntry> ranked = candidates
            .Select(entry => (Entry: entry, Score: Score(entry, term)))
            .Where(match => match.Score > int.MinValue)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Entry.LastConnectedAt ?? DateTimeOffset.MinValue)
            .ThenBy(match => match.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(match => match.Entry)
            .ToList();

        Results.Clear();
        foreach (QuickSwitcherEntry entry in ranked)
        {
            Results.Add(entry);
        }

        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Ranks one entry against the term. Returns <see cref="int.MinValue"/> when it does not match.
    /// </summary>
    /// <remarks>
    /// A prefix match on the name beats a match anywhere in it, which in turn beats a subsequence
    /// spread across the searchable text. Favourites and recently used profiles are nudged upwards
    /// so the common case is already selected.
    /// </remarks>
    private static int Score(QuickSwitcherEntry entry, string term)
    {
        int bonus = (entry.FavouriteSlot is not null ? 40 : 0)
            + (entry.LastConnectedAt is not null ? 20 : 0);

        if (term.Length == 0)
        {
            return bonus;
        }

        string name = entry.Name;

        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + bonus;
        }

        if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 700 + bonus;
        }

        string haystack = string.Join(' ', name, entry.Endpoint, entry.Tags);

        if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 400 + bonus;
        }

        int subsequence = SubsequenceScore(name, term);
        return subsequence > int.MinValue ? 200 + subsequence + bonus : int.MinValue;
    }

    /// <summary>
    /// Scores a subsequence match, rewarding letters that land close together.
    /// </summary>
    private static int SubsequenceScore(string text, string term)
    {
        int position = 0;
        int score = 0;
        int lastMatch = -1;

        foreach (char wanted in term)
        {
            int found = -1;

            for (int index = position; index < text.Length; index++)
            {
                if (char.ToLowerInvariant(text[index]) == char.ToLowerInvariant(wanted))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                return int.MinValue;
            }

            // Consecutive letters are worth more than letters scattered through the text.
            score += lastMatch >= 0 && found == lastMatch + 1 ? 10 : 1;

            lastMatch = found;
            position = found + 1;
        }

        return score;
    }

    /// <summary>
    /// Re-reads the labels after a language change.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Hint));
    }
}

/// <summary>
/// What the user asked the palette to do.
/// </summary>
/// <param name="Entries">The profiles that were picked, never empty.</param>
/// <param name="ShowWindow">
/// True when the main window should come forward as well, which is what someone asks for when they
/// want to watch a connection rather than only start one.
/// </param>
public sealed record QuickSwitcherChoice(
    IReadOnlyList<QuickSwitcherEntry> Entries,
    bool ShowWindow,
    QuickSwitcherMode Mode);

/// <summary>
/// What the palette was opened for.
/// </summary>
public enum QuickSwitcherMode
{
    Connect,
    Disconnect,
}

/// <summary>
/// One row of the palette.
/// </summary>
public sealed partial class QuickSwitcherEntry : ViewModelBase
{
    public QuickSwitcherEntry(
        Guid profileId,
        string name,
        string endpoint,
        string tags,
        bool isConnected,
        int? favouriteSlot,
        DateTimeOffset? lastConnectedAt)
    {
        ProfileId = profileId;
        Name = name;
        Endpoint = endpoint;
        Tags = tags;
        IsConnected = isConnected;
        FavouriteSlot = favouriteSlot;
        LastConnectedAt = lastConnectedAt;
    }

    public Guid ProfileId { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public string Tags { get; }

    public bool IsConnected { get; }

    public int? FavouriteSlot { get; }

    public DateTimeOffset? LastConnectedAt { get; }

    /// <summary>
    /// True when the row has been ticked, so that return acts on it along with the others.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTicked { get; set; }

    public bool HasTags => Tags is { Length: > 0 };

    public string SlotDisplay => FavouriteSlot?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? string.Empty;

    public bool HasSlot => FavouriteSlot is not null;
}
