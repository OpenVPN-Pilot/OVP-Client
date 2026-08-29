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

    /// <summary>
    /// A search for something that is not there has to come back empty.
    /// </summary>
    /// <remarks>
    /// Every letter of "lab-11" can be found somewhere in "lab-10-cert 127.0.0.1:1210 udp", so
    /// looking for a subsequence across the endpoint and the tags matched the entire set. A palette
    /// that answers a miss with everything is worse than one that answers nothing.
    /// </remarks>
    [Theory]
    [InlineData("lab-11")]
    [InlineData("lab 11")]
    [InlineData("11")]
    public void Query_ForAProfileThatDoesNotExist_MatchesNothing(string query)
    {
        QuickSwitcherViewModel switcher = Build(
            Profile("lab-10-cert", host: "127.0.0.1", port: 1210),
            Profile("lab-01-cert", host: "127.0.0.1", port: 1201));

        switcher.Query = query;

        Assert.Empty(switcher.Results);
    }

    [Fact]
    public void Query_ForOneThatDoesExist_StillFindsIt()
    {
        QuickSwitcherViewModel switcher = Build(
            Profile("lab-10-cert", host: "127.0.0.1", port: 1210),
            Profile("lab-01-cert", host: "127.0.0.1", port: 1201));

        switcher.Query = "lab-10";

        QuickSwitcherEntry match = Assert.Single(switcher.Results);
        Assert.Equal("lab-10-cert", match.Name);
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
    public void Accept_RaisesTheSelectedEntryAndLeavesTheWindowAlone()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"));

        QuickSwitcherChoice? accepted = null;
        switcher.Accepted += (_, choice) => accepted = choice;

        switcher.AcceptCommand.Execute(null);

        Assert.NotNull(accepted);
        Assert.Equal("alpha", accepted.Entry.Name);
        Assert.False(accepted.ShowWindow);
    }

    /// <summary>
    /// Connecting without leaving what you were doing is the point; watching it is the exception.
    /// </summary>
    [Fact]
    public void AcceptAndShow_AsksForTheWindowAsWell()
    {
        QuickSwitcherViewModel switcher = Build(Profile("alpha"));

        QuickSwitcherChoice? accepted = null;
        switcher.Accepted += (_, choice) => accepted = choice;

        switcher.AcceptAndShowCommand.Execute(null);

        Assert.NotNull(accepted);
        Assert.True(accepted.ShowWindow);
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
        int? port = null,
        int? slot = null,
        DateTimeOffset? lastConnected = null) => new()
        {
            Name = name,
            Configuration = "client",
            ContentHash = new string('a', 64),
            RemoteHost = host,
            RemotePort = host is null ? null : port ?? 1194,
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
