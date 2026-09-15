using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.Data.Library;
using OpenVpnPilot.Data.Packaging;
using OpenVpnPilot.Data.Tagging;

namespace OpenVpnPilot.Data.Tests.Library;

/// <summary>
/// Two people changing one library: what survives, what goes, and what is reported.
/// </summary>
/// <remarks>
/// Each test is a machine with its own store and keystore, and a shared file represented by the
/// package content it would hold. The ancestor is what a machine's previous synchronisation left the
/// shared file holding, which is exactly what the coordinator keeps a copy of.
/// </remarks>
public sealed class LibraryMergerTests : IDisposable
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Directory.CreateTempSubdirectory("ovp-library-").FullName;
    private readonly List<PilotDbContext> contexts = [];

    [Fact]
    public async Task JoiningWithAnEmptyStore_TakesTheWholeLibrary()
    {
        Machine source = await CreateMachineAsync("source");
        Profile alpha = await source.AddAsync("site-alpha", Config(1194), "production");
        await source.Secrets.WriteAsync(SecretReference.ForProfile(alpha.Id, "Auth"), new StoredSecret("operator", "secret"));

        ProfilePackageContent shared = await source.Merger.ReadLocalAsync();

        Machine joining = await CreateMachineAsync("joining");
        LibraryMergeResult result = await joining.Merger.MergeAsync(shared, ancestor: null);

        Assert.Equal(["site-alpha"], result.Added);
        Assert.False(result.SharedChanged);

        Profile arrived = await joining.Context.Profiles.Include(profile => profile.Tags).ThenInclude(link => link.Tag).SingleAsync();
        Assert.Equal(alpha.Id, arrived.Id);
        Assert.Equal("vpn.example.com", arrived.RemoteHost);
        Assert.Equal(["production"], arrived.Tags.Select(link => link.Tag!.Name));

        StoredSecret? secret = await joining.Secrets.TryReadAsync(SecretReference.ForProfile(alpha.Id, "Auth"));
        Assert.Equal("secret", secret?.Password);
    }

    [Fact]
    public async Task JoiningWithProfilesOfYourOwn_AddsThemToTheLibrary()
    {
        Machine source = await CreateMachineAsync("source");
        await source.AddAsync("site-alpha", Config(1194));
        ProfilePackageContent shared = await source.Merger.ReadLocalAsync();

        Machine joining = await CreateMachineAsync("joining");
        await joining.AddAsync("site-beta", Config(1195));

        LibraryMergeResult result = await joining.Merger.MergeAsync(shared, ancestor: null);

        Assert.True(result.SharedChanged);
        Assert.Equal(["site-alpha", "site-beta"], result.Shared.Profiles.Select(profile => profile.Name));
    }

    /// <summary>
    /// A colleague renaming a profile and this machine changing its port are not in conflict.
    /// </summary>
    [Fact]
    public async Task DifferentFieldsChangedOnEachSide_BothSurvive()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        await ChangeAsync(machine, id, profile => ProfileConfigurationFacts.Apply(profile, Config(443)));

        ProfilePackageContent remote = Modify(ancestor, id, profile => profile with { Name = "site-alpha-renamed", UpdatedAt = Later });

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        Assert.Empty(result.Conflicts);
        Assert.True(result.SharedChanged);

        PackagedProfile shared = Assert.Single(result.Shared.Profiles);
        Assert.Equal("site-alpha-renamed", shared.Name);
        Assert.Contains("remote vpn.example.com 443", shared.Configuration, StringComparison.Ordinal);
        Assert.Equal(443, shared.RemotePort);

        Profile stored = await machine.Context.Profiles.AsNoTracking().SingleAsync();
        Assert.Equal("site-alpha-renamed", stored.Name);
        Assert.Equal(443, stored.RemotePort);
    }

    [Fact]
    public async Task TheSameFieldChangedOnBothSides_TheLaterChangeWinsAndIsReported()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        await ChangeAsync(machine, id, profile =>
        {
            profile.Name = "renamed-here";
            profile.UpdatedAt = Earlier.AddHours(30);
        });

        ProfilePackageContent remote = Modify(ancestor, id, profile => profile with { Name = "renamed-there", UpdatedAt = Earlier.AddHours(40) });

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        LibraryConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal(LibraryConflictKind.ChangedOnBothSides, conflict.Kind);
        Assert.False(conflict.KeptHere);
        Assert.Equal("renamed-there", (await machine.Context.Profiles.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task DeletedElsewhereAndUntouchedHere_IsRemovedHere()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        ProfilePackageContent remote = ancestor with
        {
            Profiles = [],
            DeletedProfiles = [new PackagedDeletion(id, Later)],
        };

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        Assert.Equal(["site-alpha"], result.Removed);
        Assert.False(await machine.Context.Profiles.AnyAsync());
        Assert.False(result.SharedChanged);
    }

    /// <summary>
    /// Losing a colleague's edit to a deletion that crossed it would be worse than deleting twice.
    /// </summary>
    [Fact]
    public async Task DeletedElsewhereButChangedHere_IsKeptAndReported()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        await ChangeAsync(machine, id, profile => profile.Notes = "still in use");

        ProfilePackageContent remote = ancestor with { Profiles = [], DeletedProfiles = [new PackagedDeletion(id, Later)] };

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        Assert.Equal(LibraryConflictKind.ChangedHereDeletedThere, Assert.Single(result.Conflicts).Kind);
        Assert.True(await machine.Context.Profiles.AnyAsync());
        Assert.Single(result.Shared.Profiles);
        Assert.Empty(result.Shared.DeletedProfiles);
    }

    [Fact]
    public async Task DeletedHere_TheDeletionReachesTheLibrary()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        await machine.Context.Profiles.Where(profile => profile.Id == id).ExecuteDeleteAsync();

        LibraryMergeResult result = await machine.Merger.MergeAsync(ancestor, ancestor);

        Assert.True(result.SharedChanged);
        Assert.Empty(result.Shared.Profiles);
        Assert.Equal(id, Assert.Single(result.Shared.DeletedProfiles).ProfileId);
    }

    /// <summary>
    /// A machine that joins late must not bring back what everyone else deleted.
    /// </summary>
    [Fact]
    public async Task ADeletionTheLibraryRemembers_IsNotUndoneByAMachineJoiningLate()
    {
        Machine late = await CreateMachineAsync("late");
        Profile old = await late.AddAsync("site-retired", Config(1300), updatedAt: Earlier);

        ProfilePackageContent remote = new() { DeletedProfiles = [new PackagedDeletion(old.Id, Later)] };

        LibraryMergeResult result = await late.Merger.MergeAsync(remote, ancestor: null);

        Assert.Equal(["site-retired"], result.Removed);
        Assert.False(result.SharedChanged);
    }

    [Fact]
    public async Task AProfileWhoseTunnelRuns_IsKeptUntilItEnds()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();
        await machine.Secrets.WriteAsync(SecretReference.ForProfile(id, "Auth"), new StoredSecret("operator", "secret"));

        ProfilePackageContent remote = ancestor with { Profiles = [], DeletedProfiles = [new PackagedDeletion(id, Later)] };

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor, new HashSet<Guid> { id });

        Assert.Equal(1, result.DeferredDeletions);
        Assert.True(await machine.Context.Profiles.AnyAsync());
        Assert.Empty(result.Shared.Profiles);
        Assert.NotNull(await machine.Secrets.TryReadAsync(SecretReference.ForProfile(id, "Auth")));
    }

    /// <summary>
    /// Two people importing the same file before either synchronised end up with one profile.
    /// </summary>
    [Fact]
    public async Task TheSameConfigurationUnderTwoIdentifiers_ConvergesOnOne()
    {
        Machine first = await CreateMachineAsync("first");
        Profile one = await first.AddAsync("site-alpha", Config(1194));

        Machine second = await CreateMachineAsync("second");
        Profile two = await second.AddAsync("site-alpha", Config(1194));

        await ChangeAsync(second, two.Id, profile =>
        {
            profile.IsFavourite = true;
            profile.FavouriteSlot = 3;
        });

        ProfilePackageContent afterFirst = (await first.Merger.MergeAsync(new ProfilePackageContent(), ancestor: null)).Shared;
        LibraryMergeResult merged = await second.Merger.MergeAsync(afterFirst, ancestor: null);

        Guid survivor = new[] { one.Id, two.Id }.OrderBy(value => value.ToString("N")).First();

        Assert.Equal(survivor, Assert.Single(merged.Shared.Profiles).Id);

        Profile kept = await second.Context.Profiles.AsNoTracking().SingleAsync();
        Assert.Equal(survivor, kept.Id);
        Assert.True(kept.IsFavourite);
        Assert.Equal(3, kept.FavouriteSlot);
    }

    [Fact]
    public async Task TagsAddedOnEachSide_AreBothKept()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync("production");

        await SetTagsAsync(machine, id, ["production", "berlin"]);
        ProfilePackageContent remote = Modify(ancestor, id, profile => profile with { Tags = ["production", "night-shift"], UpdatedAt = Later });

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        Assert.Empty(result.Conflicts);
        Assert.Equal(["berlin", "night-shift", "production"], Assert.Single(result.Shared.Profiles).Tags);
    }

    [Fact]
    public async Task ASignInChangedElsewhere_ArrivesHere_AndOneDeletedHere_LeavesTheLibrary()
    {
        (Machine machine, ProfilePackageContent ancestor, Guid id) = await SynchronisedAsync();

        ancestor = ancestor with
        {
            Credentials =
            [
                new PackagedCredential(id, "Auth", "operator", "old"),
                new PackagedCredential(id, "Private Key", null, "key"),
            ],
        };

        await machine.Secrets.WriteAsync(SecretReference.ForProfile(id, "Auth"), new StoredSecret("operator", "old"));

        ProfilePackageContent remote = ancestor with
        {
            Credentials =
            [
                new PackagedCredential(id, "Auth", "operator", "new"),
                new PackagedCredential(id, "Private Key", null, "key"),
            ],
        };

        LibraryMergeResult result = await machine.Merger.MergeAsync(remote, ancestor);

        Assert.Equal("new", (await machine.Secrets.TryReadAsync(SecretReference.ForProfile(id, "Auth")))?.Password);
        Assert.Equal(["Auth"], result.Shared.Credentials.Select(credential => credential.Realm));
        Assert.True(result.SharedChanged);
    }

    [Fact]
    public async Task NothingChangedAnywhere_ChangesNothing()
    {
        (Machine machine, ProfilePackageContent ancestor, _) = await SynchronisedAsync("production");

        LibraryMergeResult result = await machine.Merger.MergeAsync(ancestor, ancestor);

        Assert.False(result.SharedChanged);
        Assert.False(result.ChangedHere);
        Assert.Empty(result.Conflicts);
    }

    /// <summary>
    /// A machine holding one profile, already synchronised with a library holding the same.
    /// </summary>
    private async Task<(Machine Machine, ProfilePackageContent Ancestor, Guid Id)> SynchronisedAsync(params string[] tags)
    {
        Machine machine = await CreateMachineAsync("machine");
        Profile profile = await machine.AddAsync("site-alpha", Config(1194), tags);
        await ChangeAsync(machine, profile.Id, stored => stored.UpdatedAt = Earlier);

        LibraryMergeResult first = await machine.Merger.MergeAsync(new ProfilePackageContent(), ancestor: null);
        return (machine, first.Shared, profile.Id);
    }

    private static ProfilePackageContent Modify(
        ProfilePackageContent content,
        Guid id,
        Func<PackagedProfile, PackagedProfile> change) =>
        content with
        {
            Profiles = content.Profiles.Select(profile => profile.Id == id ? change(profile) : profile).ToList(),
        };

    private static async Task ChangeAsync(Machine machine, Guid id, Action<Profile> change)
    {
        Profile profile = await machine.Context.Profiles.SingleAsync(candidate => candidate.Id == id);
        change(profile);
        await machine.Context.SaveChangesAsync();
        machine.Context.ChangeTracker.Clear();
    }

    private static async Task SetTagsAsync(Machine machine, Guid id, IReadOnlyList<string> names)
    {
        await machine.Context.ProfileTags.Where(link => link.ProfileId == id).ExecuteDeleteAsync();

        TagCatalogue catalogue = await TagCatalogue.LoadAsync(machine.Context);

        foreach (string name in names)
        {
            Tag tag = catalogue.Resolve(name);
            machine.Context.ProfileTags.Add(new ProfileTag { ProfileId = id, TagId = tag.Id, Tag = tag });
        }

        await machine.Context.SaveChangesAsync();
        machine.Context.ChangeTracker.Clear();
    }

    private static string Config(int port) =>
        $"client\nremote vpn.example.com {port} udp\n<ca>\n-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n</ca>\n";

    private async Task<Machine> CreateMachineAsync(string name)
    {
        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, name + ".db")}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        PilotDbContext context = new(options);
        await context.Database.MigrateAsync();
        contexts.Add(context);

        MemorySecretStore secrets = new();
        return new Machine(context, secrets, new LibraryMerger(context, secrets));
    }

    public void Dispose()
    {
        foreach (PilotDbContext context in contexts)
        {
            context.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private sealed record Machine(PilotDbContext Context, MemorySecretStore Secrets, LibraryMerger Merger)
    {
        public async Task<Profile> AddAsync(string name, string configuration, params string[] tags) =>
            await AddAsync(name, configuration, Earlier, tags);

        public async Task<Profile> AddAsync(string name, string configuration, DateTimeOffset updatedAt, params string[] tags)
        {
            Profile profile = new()
            {
                Name = name,
                Configuration = string.Empty,
                ContentHash = string.Empty,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            };

            ProfileConfigurationFacts.Apply(profile, configuration);
            Context.Profiles.Add(profile);
            await Context.SaveChangesAsync();

            if (tags.Length > 0)
            {
                await SetTagsAsync(this, profile.Id, tags);
            }

            Context.ChangeTracker.Clear();
            return profile;
        }
    }
}

/// <summary>
/// A keystore that keeps secrets in memory, which is all a test of the merge needs.
/// </summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, StoredSecret> entries = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.TryGetValue(reference, out StoredSecret? secret) ? secret : null);

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        entries[reference] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        entries.Remove(reference);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. entries.Keys]);

    public Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        int count = entries.Count;
        entries.Clear();
        return Task.FromResult(count);
    }
}
