using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// The palette is judged by what it puts first, because return connects whatever is selected.
/// </summary>
public sealed class QuickSwitcherViewModelTests
{
    [Fact]
    public void Reset_WithNoQuery_PutsFavouritesAndRecentProfilesFirst()
    {
        QuickSwitcherViewModel switcher = Build(
            Profile("alpha"),
            Profile("beta", slot: 1),
            Profile("gamma", lastConnected: DateTimeOffset.UtcNow));

        Assert.Equal("beta", switcher.Results[0].Name);
        Assert.Equal("gamma", switcher.Results[1].Name);
    }

    [Fact]
    public void Query_PrefersAPrefixMatchOverAMatchInTheMiddle()
    {
        QuickSwitcherViewModel switcher = Build(
            Profile("office-berlin"),
            Profile("berlin-backup"));

        switcher.Query = "berlin";

        Assert.Equal("berlin-backup", switcher.Results[0].Name);
    }

    [Fact]
    public void Query_MatchesLettersSpreadThroughTheName()
    {
        QuickSwitcherViewModel switcher = Build(Profile("japan-tokyo-tcp"), Profile("berlin"));

        switcher.Query = "jtt";

        QuickSwitcherEntry match = Assert.Single(switcher.Results);
        Assert.Equal("japan-tokyo-tcp", match.Name);
    }

    [Fact]
    public void Query_MatchesTheEndpointAsWellAsTheName()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha", host: "203.0.113.10"));

        switcher.Query = "203.0.113";

        Assert.Single(switcher.Results);
    }

    [Fact]
    public void Query_ThatMatchesNothing_LeavesNoSelection()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"));

        switcher.Query = "zzzz";

        Assert.Empty(switcher.Results);
        Assert.Null(switcher.SelectedResult);
        Assert.False(switcher.HasResults);
    }

    [Fact]
    public void Query_AlwaysSelectsTheTopResult()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"), Profile("alps"));

        switcher.Query = "al";

        Assert.Same(switcher.Results[0], switcher.SelectedResult);
    }

    [Fact]
    public void MoveSelection_StopsAtTheEndsOfTheList()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"), Profile("beta"));

        switcher.MoveSelection(-1);
        Assert.Same(switcher.Results[0], switcher.SelectedResult);

        switcher.MoveSelection(5);
        Assert.Same(switcher.Results[^1], switcher.SelectedResult);
    }

    [Fact]
    public void Accept_RaisesTheSelectedEntry()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"));

        QuickSwitcherEntry? accepted = null;
        switcher.Accepted += (_, entry) => accepted = entry;

        switcher.AcceptCommand.Execute(null);

        Assert.NotNull(accepted);
        Assert.Equal("alpha", accepted.Name);
    }

    [Fact]
    public void Reset_ClearsAQueryLeftFromTheLastTime()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"));
        switcher.Query = "zzz";

        switcher.Reset([Item(Profile("alpha"))]);

        Assert.Equal(string.Empty, switcher.Query);
        Assert.Single(switcher.Results);
    }

    private static QuickSwitcherViewModel Build(params Profile[] profiles)
    {
        QuickSwitcherViewModel switcher = new(new StubLocalizer());
        switcher.Reset(profiles.Select(Item));
        return switcher;
    }

    private static ProfileItemViewModel Item(Profile profile) => new(profile, new StubLocalizer());

    private static Profile Profile(
        string name,
        string? host = null,
        int? slot = null,
        DateTimeOffset? lastConnected = null) => new()
        {
            Name = name,
            Configuration = "client",
            ContentHash = new string('a', 64),
            RemoteHost = host,
            RemotePort = host is null ? null : 1194,
            Protocol = host is null ? null : "udp",
            FavouriteSlot = slot,
            IsFavourite = slot is not null,
            LastConnectedAt = lastConnected,
        };

    /// <summary>
    /// Returns the key itself, so a test asserts on behaviour rather than on wording.
    /// </summary>
    private sealed class StubLocalizer : ILocalizer
    {
        public string CurrentLanguage => "en";

        public IReadOnlyList<LanguageDescriptor> AvailableLanguages { get; } =
            [new LanguageDescriptor("en", "English", "English")];

        public IReadOnlyCollection<string> Keys { get; } = [];

        public event EventHandler? LanguageChanged
        {
            add { }
            remove { }
        }

        public string this[string key] => key;

        public string Translate(string key, params object?[] arguments) => key;

        public bool TrySetLanguage(string languageCode) => languageCode == "en";

        public void Reload()
        {
        }
    }
}
