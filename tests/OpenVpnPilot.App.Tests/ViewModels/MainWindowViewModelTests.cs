using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Library;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Data.Library;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// What the profile list shows, what is ticked, and what an action applies to.
/// </summary>
/// <remarks>
/// None of this was covered, and all of it is the kind that comes back: the sidebar filters fought
/// each other for one selection until each was given its own, and an action that applies to "the
/// selection" has to keep meaning the same thing whether one row is highlighted or twenty are
/// ticked.
/// </remarks>
public sealed class MainWindowViewModelTests : IAsyncLifetime
{
    private readonly FakeProfileStore store = new();

    // Properties rather than fields: the analyser reads a disposable field as a promise that the
    // class implements IDisposable, and this one is torn down through IAsyncLifetime instead.
    private ConnectionManager connections { get; set; } = null!;

    private MainWindowViewModel model { get; set; } = null!;

    public async Task InitializeAsync()
    {
        store.Add("alpha", "production");
        store.Add("beta", "production", "berlin");
        store.Add("gamma");

        connections = IdleConnections.Create();

        model = new MainWindowViewModel(
            store,
            connections,
            new ProfileNameCache(),
            new FakeSettingsService(),
            new FakeSecrets(),
            ReadyEnvironmentProbe.Gate(),
            SilentUpdates.Coordinator(),
            new StubLocalizer(),
            TimeProvider.System)
        {
            MinimumSyncingDisplay = TimeSpan.FromMilliseconds(150),
        };

        await model.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        model.Dispose();
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Loading_ShowsEveryProfileAndTheTagsItFound()
    {
        Assert.Equal(3, model.VisibleProfiles.Count);
        Assert.Equal(["berlin", "production"], model.TagFilters.Select(filter => filter.TagName));
        Assert.True(model.HasTags);

        await Task.CompletedTask;
    }

    /// <summary>
    /// The sidebar is two lists and one selection between them.
    /// </summary>
    /// <remarks>
    /// Choosing a tag used to leave the built in list holding an item it did not contain, which it
    /// answered by writing its own selection back. Both looked chosen and the filter was neither.
    /// </remarks>
    [Fact]
    public void ChoosingATag_ClearsTheBuiltInSelectionAndFiltersTheList()
    {
        model.SelectedBuiltIn = model.Filters[0];
        Assert.Equal(3, model.VisibleProfiles.Count);

        model.SelectedTag = model.TagFilters.Single(filter => filter.TagName == "berlin");

        Assert.Null(model.SelectedBuiltIn);
        Assert.Equal(SidebarFilterKind.Tag, model.SelectedFilter?.Kind);
        Assert.Equal("beta", Assert.Single(model.VisibleProfiles).Name);
    }

    [Fact]
    public void ChoosingABuiltInFilter_ClearsTheTagSelection()
    {
        model.SelectedTag = model.TagFilters.Single(filter => filter.TagName == "berlin");

        model.SelectedBuiltIn = model.Filters[0];

        Assert.Null(model.SelectedTag);
        Assert.Equal(SidebarFilterKind.All, model.SelectedFilter?.Kind);
        Assert.Equal(3, model.VisibleProfiles.Count);
    }

    [Fact]
    public void TheSearchTermAndTheFilterApplyTogether()
    {
        model.SelectedTag = model.TagFilters.Single(filter => filter.TagName == "production");
        model.SearchTerm = "alp";

        Assert.Equal("alpha", Assert.Single(model.VisibleProfiles).Name);
    }

    [Fact]
    public void SelectingMode_OffersACheckboxOnEveryRowAndClearsUpAfterItself()
    {
        model.ToggleSelectingCommand.Execute(null);

        Assert.True(model.IsSelecting);
        Assert.All(model.VisibleProfiles, profile => Assert.True(profile.IsSelecting));

        model.VisibleProfiles[0].IsSelected = true;
        Assert.Equal(1, model.TickedCount);
        Assert.True(model.HasTicked);

        model.ToggleSelectingCommand.Execute(null);

        Assert.False(model.IsSelecting);
        Assert.Equal(0, model.TickedCount);
        Assert.All(model.VisibleProfiles, profile => Assert.False(profile.IsSelected));
    }

    /// <summary>
    /// Ticking everything means everything on screen, not everything there is.
    /// </summary>
    [Fact]
    public void TickAllShown_IgnoresWhatTheFilterIsHiding()
    {
        model.ToggleSelectingCommand.Execute(null);
        model.SelectedTag = model.TagFilters.Single(filter => filter.TagName == "berlin");

        model.TickAllShownCommand.Execute(null);

        Assert.Equal(1, model.TickedCount);
        Assert.Equal("beta", model.VisibleProfiles.Single(profile => profile.IsSelected).Name);
    }

    [Fact]
    public void TickNone_ClearsEvenWhatTheFilterIsHiding()
    {
        model.ToggleSelectingCommand.Execute(null);
        model.TickAllShownCommand.Execute(null);

        model.SelectedTag = model.TagFilters.Single(filter => filter.TagName == "berlin");
        model.TickNoneCommand.Execute(null);

        Assert.Equal(0, model.TickedCount);
    }

    /// <summary>
    /// Dragging a ticked row takes everything ticked; dragging anything else takes only itself.
    /// </summary>
    [Fact]
    public void ProfilesToDrag_FollowsWhatIsTicked()
    {
        ProfileItemViewModel first = model.VisibleProfiles[0];
        ProfileItemViewModel second = model.VisibleProfiles[1];

        Assert.Equal([first.Id], model.ProfilesToDrag(first.Id));

        model.ToggleSelectingCommand.Execute(null);
        first.IsSelected = true;
        second.IsSelected = true;

        Assert.Equal(2, model.ProfilesToDrag(first.Id).Count);

        // A row nobody ticked is dragged on its own even while others are ticked.
        Assert.Equal([model.VisibleProfiles[2].Id], model.ProfilesToDrag(model.VisibleProfiles[2].Id));
    }

    [Fact]
    public async Task AssignTag_AddsItWithoutReplacingTheOnesAlreadyThere()
    {
        ProfileItemViewModel beta = model.VisibleProfiles.Single(profile => profile.Name == "beta");

        await model.AssignTagAsync([beta.Id], "staging");

        Assert.Equal(
            ["berlin", "production", "staging"],
            store.TagsOf(beta.Id).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AssignTag_ToSomethingThatAlreadyHasIt_ChangesNothing()
    {
        ProfileItemViewModel alpha = model.VisibleProfiles.Single(profile => profile.Name == "alpha");

        await model.AssignTagAsync([alpha.Id], "production");

        Assert.Equal(["production"], store.TagsOf(alpha.Id));
        Assert.Equal("tags.alreadyThere", model.StatusMessage);
    }

    [Fact]
    public async Task AssignTag_AppliesToEveryProfileGiven()
    {
        List<Guid> everything = model.VisibleProfiles.Select(profile => profile.Id).ToList();

        await model.AssignTagAsync(everything, "everywhere");

        Assert.All(everything, id => Assert.Contains("everywhere", store.TagsOf(id)));
        Assert.Contains(model.TagFilters, filter => filter.TagName == "everywhere");
    }

    /// <summary>
    /// Deleting takes two decisions, because it is the one action here that cannot be undone.
    /// </summary>
    [Fact]
    public async Task DeletingTicked_AsksFirst()
    {
        model.ToggleSelectingCommand.Execute(null);
        model.VisibleProfiles[0].IsSelected = true;

        model.AskToDeleteTickedCommand.Execute(null);

        Assert.True(model.IsConfirmingDelete);
        Assert.Empty(store.Deleted);

        model.CancelDeleteCommand.Execute(null);

        Assert.False(model.IsConfirmingDelete);
        Assert.Empty(store.Deleted);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeletingTicked_RemovesThemOnceItIsConfirmed()
    {
        model.ToggleSelectingCommand.Execute(null);

        Guid doomed = model.VisibleProfiles[0].Id;
        model.VisibleProfiles[0].IsSelected = true;

        model.AskToDeleteTickedCommand.Execute(null);
        await model.DeleteTickedCommand.ExecuteAsync(null);

        Assert.Equal([doomed], store.Deleted);
        Assert.Equal(2, model.VisibleProfiles.Count);
        Assert.False(model.IsConfirmingDelete);
    }

    /// <summary>
    /// With nothing ticked, an action applies to the row that is highlighted.
    /// </summary>
    [Fact]
    public async Task WithNothingTicked_AnActionTakesTheHighlightedRow()
    {
        model.ToggleSelectingCommand.Execute(null);

        Guid doomed = model.VisibleProfiles[1].Id;
        model.SelectedProfile = model.VisibleProfiles[1];

        model.AskToDeleteTickedCommand.Execute(null);
        Assert.False(model.IsConfirmingDelete, "Nothing is ticked, so there is nothing to confirm.");

        await model.DeleteTickedCommand.ExecuteAsync(null);

        Assert.Equal([doomed], store.Deleted);
    }

    /// <summary>
    /// A missing passphrase offers the prompt, anything else offers another attempt, and the banner
    /// goes once the library is in step again.
    /// </summary>
    [Fact]
    public void LibraryBanner_OffersWhatTheProblemNeedsAndGoesWhenItIsSolved()
    {
        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.PassphraseNeeded));

        Assert.True(model.IsLibraryBannerVisible);
        Assert.True(model.IsLibraryProblem);
        Assert.True(model.LibraryBannerOffersPassphrase);
        Assert.False(model.LibraryBannerOffersRetry);

        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Unreachable));

        Assert.False(model.LibraryBannerOffersPassphrase);
        Assert.True(model.LibraryBannerOffersRetry);

        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Synchronised, DateTimeOffset.UtcNow));

        Assert.False(model.IsLibraryBannerVisible);
        Assert.False(model.IsLibraryProblem);
    }

    /// <summary>
    /// A conflict that was resolved is worth a notice, and one that is dismissed is not brought back
    /// by the library merely staying in step.
    /// </summary>
    [Fact]
    public void LibraryBanner_TellsOfResolvedConflictsUntilDismissed()
    {
        SharedLibraryReport report = new(
            [], ["alpha"], [], 0,
            [new LibraryConflict("alpha", LibraryConflictKind.ChangedOnBothSides, KeptHere: false)],
            0, []);

        model.ShowLibraryReport(report);

        Assert.True(model.IsLibraryBannerVisible);
        Assert.False(model.IsLibraryProblem);
        Assert.False(model.LibraryBannerOffersRetry);
        Assert.Equal("library.reconciled", model.StatusMessage);

        model.DismissLibraryBannerCommand.Execute(null);
        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Synchronised, DateTimeOffset.UtcNow));

        Assert.False(model.IsLibraryBannerVisible);
    }

    /// <summary>
    /// A problem is what somebody has to act on, so a conflict notice does not cover it up.
    /// </summary>
    [Fact]
    public void LibraryBanner_KeepsAProblemInViewOverAConflictNotice()
    {
        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.PassphraseRejected));

        model.ShowLibraryReport(new SharedLibraryReport(
            [], [], [], 0,
            [new LibraryConflict("beta", LibraryConflictKind.DeletedHereChangedThere, KeptHere: false)],
            0, []));

        Assert.True(model.IsLibraryProblem);
        Assert.True(model.LibraryBannerOffersPassphrase);
    }

    [Fact]
    public void StatusBar_SaysWhereTheLibraryStandsAndHowItIsGoing()
    {
        Assert.False(model.IsLibraryShared);

        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Synchronised, DateTimeOffset.UtcNow));

        Assert.True(model.IsLibraryShared);
        Assert.Equal("library.state.Synchronised", model.LibraryStateText);
        Assert.False(model.IsLibraryRetrying || model.IsLibraryStuck);

        model.ShowLibraryStatus(new SharedLibraryStatus(
            SharedLibraryCondition.Unreachable,
            WaitingSince: DateTimeOffset.UtcNow,
            RetryAt: DateTimeOffset.UtcNow.AddSeconds(30)));

        Assert.True(model.IsLibraryRetrying);
        Assert.Equal("library.state.Unreachable · library.state.retry · library.state.waiting", model.LibraryStateText);

        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.PassphraseNeeded));

        Assert.True(model.IsLibraryStuck);
        Assert.False(model.IsLibraryRetrying);

        model.ShowLibraryStatus(SharedLibraryStatus.NotShared);

        Assert.False(model.IsLibraryShared);
    }

    /// <summary>
    /// A synchronisation that is over in a moment still shows as one, long enough to be read.
    /// </summary>
    [Fact]
    public async Task StatusBar_KeepsSynchronisingInViewForAMoment()
    {
        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Synchronising));
        model.ShowLibraryStatus(new SharedLibraryStatus(SharedLibraryCondition.Synchronised, DateTimeOffset.UtcNow));

        Assert.True(model.IsLibrarySyncing);

        await Task.Delay(600);

        Assert.False(model.IsLibrarySyncing);
        Assert.Equal("library.state.Synchronised", model.LibraryStateText);
    }

    [Fact]
    public void LibraryActivity_ShowsTheNewestStepFirst()
    {
        model.AddLibraryActivity(new SharedLibraryActivity(DateTimeOffset.UtcNow, SharedLibraryActivityKind.ChangedHere, []));
        model.AddLibraryActivity(new SharedLibraryActivity(DateTimeOffset.UtcNow, SharedLibraryActivityKind.Written, [2048L]));

        Assert.True(model.HasLibraryActivity);
        Assert.Equal(["library.activity.Written", "library.activity.ChangedHere"], model.LibraryActivity.Select(entry => entry.Text));
    }

    [Fact]
    public void LibraryBanner_Commands_AskTheOwnerToAct()
    {
        int retries = 0;
        int prompts = 0;
        model.LibraryRetryRequested += (_, _) => retries++;
        model.LibraryPassphraseRequested += (_, _) => prompts++;

        model.RetryLibraryCommand.Execute(null);
        model.EnterLibraryPassphraseCommand.Execute(null);

        Assert.Equal(1, retries);
        Assert.Equal(1, prompts);
    }
}
