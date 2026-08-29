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
    /// Raised when the user picked an entry, carrying the profile to act on.
    /// </summary>
    public event EventHandler<QuickSwitcherEntry>? Accepted;

    /// <summary>
    /// Raised when the palette should close without doing anything.
    /// </summary>
    public event EventHandler? Dismissed;

    public ObservableCollection<QuickSwitcherEntry> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial QuickSwitcherEntry? SelectedResult { get; set; }

    public string Placeholder => localizer["switcher.placeholder"];

    public string EmptyText => localizer["switcher.noMatch"];

    public bool HasResults => Results.Count > 0;

    /// <summary>
    /// Fills the palette from the profiles currently loaded and resets the query.
    /// </summary>
    public void Reset(IEnumerable<ProfileItemViewModel> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        candidates.Clear();

        foreach (ProfileItemViewModel profile in profiles)
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

    [RelayCommand]
    private void Accept()
    {
        if (SelectedResult is { } entry)
        {
            Accepted?.Invoke(this, entry);
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
        if (subsequence > int.MinValue)
        {
            return 200 + subsequence + bonus;
        }

        subsequence = SubsequenceScore(haystack, term);
        return subsequence > int.MinValue ? subsequence + bonus : int.MinValue;
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
    }
}

/// <summary>
/// One row of the palette.
/// </summary>
public sealed record QuickSwitcherEntry(
    Guid ProfileId,
    string Name,
    string Endpoint,
    string Tags,
    bool IsConnected,
    int? FavouriteSlot,
    DateTimeOffset? LastConnectedAt)
{
    public bool HasTags => Tags is { Length: > 0 };

    public string SlotDisplay => FavouriteSlot?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? string.Empty;

    public bool HasSlot => FavouriteSlot is not null;
}
