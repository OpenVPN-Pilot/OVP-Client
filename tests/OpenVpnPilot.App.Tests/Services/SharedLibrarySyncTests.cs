using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Library;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Library;
using OpenVpnPilot.Data.Packaging;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Two machines sharing one file in one folder, the way two people share a synchronised library.
/// </summary>
/// <remarks>
/// The folder is an ordinary directory. What a sync client adds, delay and conflict copies, cannot be
/// reproduced here; what can is everything this machine does about the file: what it reads, what it
/// writes, when it refuses to, and what it keeps for later.
/// </remarks>
public sealed class SharedLibrarySyncTests : IAsyncLifetime
{
    private const string Passphrase = "team passphrase";

    private readonly string root = Directory.CreateTempSubdirectory("ovp-shared-").FullName;
    private readonly List<Machine> machines = [];

    private string Folder => Path.Combine(root, "share");

    private string LibraryPath => Path.Combine(Folder, "team.ovppkg");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreatingAndJoining_BringsTheLibraryAndItsSignInsToTheSecondMachine()
    {
        Machine first = await CreateMachineAsync("first");
        Profile alpha = await first.AddAsync("site-alpha", 1194);
        await first.AddAsync("site-beta", 1195);
        await first.Secrets.WriteAsync(SecretReference.ForProfile(alpha.Id, "Auth"), new StoredSecret("operator", "secret"));

        SharedLibraryStatus created = await first.Sync.CreateAsync(LibraryPath, Passphrase);

        Assert.Equal(SharedLibraryCondition.Synchronised, created.Condition);
        Assert.True(File.Exists(LibraryPath));

        Machine second = await CreateMachineAsync("second");
        SharedLibraryStatus joined = await second.Sync.JoinAsync(LibraryPath, Passphrase);

        Assert.Equal(SharedLibraryCondition.Synchronised, joined.Condition);
        Assert.Equal(["site-alpha", "site-beta"], await second.NamesAsync());
        Assert.Equal("secret", (await second.Secrets.TryReadAsync(SecretReference.ForProfile(alpha.Id, "Auth")))?.Password);
        Assert.Equal(Passphrase, (await second.Secrets.TryReadAsync(SecretReference.LibraryPassphrase))?.Password);
    }

    [Fact]
    public async Task JoiningWithTheWrongPassphrase_ChangesNothing()
    {
        Machine first = await CreateMachineAsync("first");
        await first.AddAsync("site-alpha", 1194);
        await first.Sync.CreateAsync(LibraryPath, Passphrase);

        Machine second = await CreateMachineAsync("second");

        await Assert.ThrowsAnyAsync<CryptographicException>(() => second.Sync.JoinAsync(LibraryPath, "not it"));

        Assert.Null(second.Settings.Current.Library.SharedPath);
        Assert.Null(await second.Secrets.TryReadAsync(SecretReference.LibraryPassphrase));
    }

    [Fact]
    public async Task ChangesOnBothMachines_ReachBothMachines()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();

        await first.ChangeAsync(id, profile => profile.Notes = "jump host 198.51.100.20");
        await second.ChangeAsync(id, profile => profile.Name = "site-alpha-renamed");

        await first.Sync.SyncNowAsync();
        SharedLibraryStatus second_ = await second.Sync.SyncNowAsync();
        await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.Synchronised, second_.Condition);

        foreach (Machine machine in new[] { first, second })
        {
            Profile profile = await machine.SingleAsync();
            Assert.Equal("site-alpha-renamed", profile.Name);
            Assert.Equal("jump host 198.51.100.20", profile.Notes);
        }
    }

    [Fact]
    public async Task ADeletionOnOneMachine_ReachesTheOther()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();

        await using (PilotDbContext context = await first.Factory.CreateDbContextAsync())
        {
            await context.Profiles.Where(profile => profile.Id == id).ExecuteDeleteAsync();
        }

        await first.Sync.SyncNowAsync();

        List<SharedLibraryReport> reports = [];
        second.Sync.Reconciled += (_, report) => reports.Add(report);

        await second.Sync.SyncNowAsync();

        Assert.Empty(await second.NamesAsync());
        Assert.Equal(["site-alpha"], Assert.Single(reports).Removed);
    }

    /// <summary>
    /// What was changed while the folder could not be reached is written when it can be again.
    /// </summary>
    [Fact]
    public async Task WhileTheFolderIsGone_ChangesWaitAndAreWrittenLater()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();

        string away = Folder + "-away";
        Directory.Move(Folder, away);

        await first.ChangeAsync(id, profile => profile.Name = "renamed-offline");
        SharedLibraryStatus offline = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.Unreachable, offline.Condition);
        Assert.NotNull(offline.WaitingSince);

        Directory.Move(away, Folder);

        SharedLibraryStatus back = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.Synchronised, back.Condition);
        Assert.Null(back.WaitingSince);

        await second.Sync.SyncNowAsync();
        Assert.Equal(["renamed-offline"], await second.NamesAsync());
    }

    /// <summary>
    /// A file that disappeared may be on its way from the sync client, or removed on purpose.
    /// </summary>
    [Fact]
    public async Task AMissingFile_IsReportedAndNotWrittenAgainOnItsOwn()
    {
        (Machine first, _, Guid id) = await TwoJoinedMachinesAsync();

        File.Delete(LibraryPath);
        await first.ChangeAsync(id, profile => profile.Name = "renamed");

        SharedLibraryStatus status = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.FileMissing, status.Condition);
        Assert.False(File.Exists(LibraryPath));
    }

    [Fact]
    public async Task AChangedPassphrase_IsAskedForOnTheOtherMachine()
    {
        (Machine first, Machine second, _) = await TwoJoinedMachinesAsync();

        SharedLibraryStatus changed = await first.Sync.ChangePassphraseAsync("the new passphrase");
        Assert.Equal(SharedLibraryCondition.Synchronised, changed.Condition);

        SharedLibraryStatus rejected = await second.Sync.SyncNowAsync();
        Assert.Equal(SharedLibraryCondition.PassphraseRejected, rejected.Condition);
        Assert.True(rejected.NeedsPassphrase);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => second.Sync.ProvidePassphraseAsync(Passphrase));

        SharedLibraryStatus accepted = await second.Sync.ProvidePassphraseAsync("the new passphrase");
        Assert.Equal(SharedLibraryCondition.Synchronised, accepted.Condition);
    }

    [Fact]
    public async Task ALockAnotherMachineHolds_IsWaitedForAndThenReported()
    {
        (Machine first, _, Guid id) = await TwoJoinedMachinesAsync(lockWait: TimeSpan.FromMilliseconds(600));

        await WriteLockAsync(DateTimeOffset.UtcNow);
        await first.ChangeAsync(id, profile => profile.Name = "renamed");

        byte[] before = await File.ReadAllBytesAsync(LibraryPath);
        SharedLibraryStatus status = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.Locked, status.Condition);
        Assert.Equal("another-machine", status.Detail);
        Assert.NotNull(status.WaitingSince);
        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));
    }

    /// <summary>
    /// A machine that crashed while writing must not block everyone else for good.
    /// </summary>
    [Fact]
    public async Task ALockLeftBehind_IsTakenOver()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync(lockWait: TimeSpan.FromMilliseconds(600));

        await WriteLockAsync(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        await first.ChangeAsync(id, profile => profile.Name = "renamed");

        SharedLibraryStatus status = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.Synchronised, status.Condition);
        Assert.False(File.Exists(LibraryPath + ".lock"));

        await second.Sync.SyncNowAsync();
        Assert.Equal(["renamed"], await second.NamesAsync());
    }

    [Fact]
    public async Task AFileANewerVersionWrote_IsLeftUntouched()
    {
        (Machine first, _, _) = await TwoJoinedMachinesAsync();

        byte[] newer = ProfilePackageFile.Encode(
            new ProfilePackageContent { FormatVersion = ProfilePackageContent.CurrentFormatVersion + 1, WrittenBy = "9.0.0" },
            Passphrase);

        await File.WriteAllBytesAsync(LibraryPath, newer);

        SharedLibraryStatus status = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.TooNew, status.Condition);
        Assert.Equal(newer, await File.ReadAllBytesAsync(LibraryPath));
        Assert.Single(await first.NamesAsync());
    }

    [Fact]
    public async Task NothingChanged_TheFileIsNotWrittenAgain()
    {
        (Machine first, _, _) = await TwoJoinedMachinesAsync();

        DateTime written = File.GetLastWriteTimeUtc(LibraryPath);
        await Task.Delay(50);

        await first.Sync.SyncNowAsync();

        Assert.Equal(written, File.GetLastWriteTimeUtc(LibraryPath));
    }

    [Fact]
    public async Task CreatingWhereAFileAlreadyIs_IsRefused()
    {
        await File.WriteAllTextAsync(LibraryPath, "somebody's file");

        Machine machine = await CreateMachineAsync("machine");

        await Assert.ThrowsAsync<SharedLibraryExistsException>(() => machine.Sync.CreateAsync(LibraryPath, Passphrase));
        Assert.Equal("somebody's file", await File.ReadAllTextAsync(LibraryPath));
    }

    [Fact]
    public async Task Leaving_KeepsTheProfilesAndForgetsThePassphrase()
    {
        (Machine first, _, _) = await TwoJoinedMachinesAsync();

        await first.Sync.LeaveAsync(keepProfiles: true);

        Assert.Single(await first.NamesAsync());
        Assert.Null(first.Settings.Current.Library.SharedPath);
        Assert.Null(await first.Secrets.TryReadAsync(SecretReference.LibraryPassphrase));
        Assert.Equal(SharedLibraryCondition.NotShared, first.Sync.Status.Condition);
    }

    /// <summary>
    /// Starting again with an empty library removes the profiles and their sign ins here, and
    /// nothing in the shared file.
    /// </summary>
    [Fact]
    public async Task LeavingToStartAgain_RemovesTheProfilesHereAndLeavesTheFile()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();
        await second.Observed.WriteAsync(SecretReference.ForProfile(id, "Auth"), new StoredSecret("operator", "secret"));
        await second.Sync.SyncNowAsync();
        byte[] before = await File.ReadAllBytesAsync(LibraryPath);

        await second.Sync.LeaveAsync(keepProfiles: false);

        Assert.Empty(await second.NamesAsync());
        Assert.Null(await second.Secrets.TryReadAsync(SecretReference.ForProfile(id, "Auth")));
        Assert.Null(second.Settings.Current.Library.SharedPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));
        Assert.Contains(second.Sync.RecentActivity, entry => entry.Kind == SharedLibraryActivityKind.LeftRemovingProfiles);

        await first.Sync.SyncNowAsync();
        Assert.Equal(["site-alpha"], await first.NamesAsync());
    }

    [Fact]
    public async Task LeavingToStartAgainWhileATunnelRuns_IsRefused()
    {
        (_, Machine second, Guid id) = await TwoJoinedMachinesAsync();
        second.Running.Add(id);

        await Assert.ThrowsAsync<SharedLibraryInUseException>(() => second.Sync.LeaveAsync(keepProfiles: false));

        Assert.Single(await second.NamesAsync());
        Assert.NotNull(second.Settings.Current.Library.SharedPath);
    }

    /// <summary>
    /// A machine joins with profiles of its own. They are replaced, with their sign ins, and none of
    /// them reaches the shared file or the other machine.
    /// </summary>
    [Fact]
    public async Task Joining_ReplacesTheProfilesHereAndLeavesTheFileAlone()
    {
        Machine first = await CreateMachineAsync("first");
        await first.AddAsync("site-alpha", 1194);
        await first.Sync.CreateAsync(LibraryPath, Passphrase);
        byte[] before = await File.ReadAllBytesAsync(LibraryPath);

        Machine second = await CreateMachineAsync("second");
        Profile own = await second.AddAsync("kept-to-myself", 1300);
        await second.Secrets.WriteAsync(SecretReference.ForProfile(own.Id, "Auth"), new StoredSecret("me", "mine"));

        List<SharedLibraryReport> reports = [];
        second.Sync.Reconciled += (_, report) => reports.Add(report);

        SharedLibraryStatus joined = await second.Sync.JoinAsync(LibraryPath, Passphrase);

        Assert.Equal(SharedLibraryCondition.Synchronised, joined.Condition);
        Assert.Equal(["site-alpha"], await second.NamesAsync());
        Assert.Null(await second.Secrets.TryReadAsync(SecretReference.ForProfile(own.Id, "Auth")));
        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));
        Assert.Equal(["kept-to-myself"], Assert.Single(reports).Removed);

        // The next synchronisation finds nothing to write: the replaced profile is not a deletion.
        await second.Sync.SyncNowAsync();
        await first.Sync.SyncNowAsync();

        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));
        Assert.Equal(["site-alpha"], await first.NamesAsync());
    }

    [Fact]
    public async Task JoiningWhileATunnelRuns_IsRefusedAndChangesNothing()
    {
        Machine first = await CreateMachineAsync("first");
        await first.AddAsync("site-alpha", 1194);
        await first.Sync.CreateAsync(LibraryPath, Passphrase);

        Machine second = await CreateMachineAsync("second");
        Profile own = await second.AddAsync("in-use-here", 1300);
        second.Running.Add(own.Id);

        await Assert.ThrowsAsync<SharedLibraryInUseException>(() => second.Sync.JoinAsync(LibraryPath, Passphrase));

        Assert.Equal(["in-use-here"], await second.NamesAsync());
        Assert.Null(second.Settings.Current.Library.SharedPath);
    }

    /// <summary>
    /// The record a person opens says what was done, and a failure says when it is tried again.
    /// </summary>
    [Fact]
    public async Task TheStepsTaken_AreRecordedWithTheNextAttempt()
    {
        (Machine first, _, Guid id) = await TwoJoinedMachinesAsync();

        await first.ChangeAsync(id, profile => profile.Name = "site-alpha-renamed");
        await first.Sync.SyncNowAsync();

        Assert.Contains(first.Sync.RecentActivity, entry => entry.Kind == SharedLibraryActivityKind.Read);
        Assert.Contains(first.Sync.RecentActivity, entry => entry.Kind == SharedLibraryActivityKind.Written);

        File.Delete(LibraryPath);
        SharedLibraryStatus failed = await first.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.FileMissing, failed.Condition);
        Assert.NotNull(failed.RetryAt);
        Assert.Contains(first.Sync.RecentActivity, entry => entry.Kind == SharedLibraryActivityKind.Failed && entry.Status == failed);
        Assert.Equal(SharedLibraryActivityKind.RetryScheduled, first.Sync.RecentActivity[^1].Kind);
    }

    /// <summary>
    /// A sign in stored after a prompt is a change to the library like any other.
    /// </summary>
    [Fact]
    public async Task ASignInStoredOnOneMachine_ReachesTheOther()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();

        await first.Observed.WriteAsync(SecretReference.ForProfile(id, "Auth"), new StoredSecret("operator", "typed at the prompt"));
        await first.Sync.SyncNowAsync();
        await second.Sync.SyncNowAsync();

        Assert.Equal("typed at the prompt", (await second.Secrets.TryReadAsync(SecretReference.ForProfile(id, "Auth")))?.Password);
    }

    /// <summary>
    /// A profile deleted elsewhere while its tunnel runs here stays, and goes once the tunnel has
    /// ended, although neither the file nor anything on this machine changed in the meantime.
    /// </summary>
    [Fact]
    public async Task ADeletionThatWaitedForATunnel_IsCarriedOutWhenTheTunnelEnds()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();
        second.Running.Add(id);

        await using (PilotDbContext context = await first.Factory.CreateDbContextAsync())
        {
            await context.Profiles.Where(profile => profile.Id == id).ExecuteDeleteAsync();
        }

        await first.Sync.SyncNowAsync();
        await second.Sync.SyncNowAsync();

        Assert.Equal(["site-alpha"], await second.NamesAsync());
        Assert.False(await second.Sync.IsDueHereAsync(), "Nothing is due while the tunnel runs.");

        second.Running.Clear();

        Assert.True(await second.Sync.IsDueHereAsync(), "The tunnel has ended, so the deletion is due.");

        await second.Sync.SyncNowAsync();

        Assert.Empty(await second.NamesAsync());
        Assert.False(await second.Sync.IsDueHereAsync());
    }

    /// <summary>
    /// Two machines write while neither can see the other's lock, as when one of them is offline and
    /// the sync client keeps only one of the two files. The machine whose file was not kept still has
    /// its changes, and they must reach the library rather than be taken back.
    /// </summary>
    [Fact]
    public async Task AWriteTheSyncClientDidNotKeep_IsNotTakenBack()
    {
        (Machine first, Machine second, Guid id) = await TwoJoinedMachinesAsync();
        byte[] common = await File.ReadAllBytesAsync(LibraryPath);

        // The first machine adds a profile and writes, but the sync client later keeps the other file.
        await first.AddAsync("added-while-offline", 1300);
        await first.Sync.SyncNowAsync();
        await File.WriteAllBytesAsync(LibraryPath, common);
        File.SetLastWriteTimeUtc(LibraryPath, DateTime.UtcNow.AddSeconds(5));

        // The second machine renames a profile on the version both started from and writes.
        await second.ChangeAsync(id, profile => profile.Name = "renamed-meanwhile");
        await second.Sync.SyncNowAsync();

        await first.Sync.SyncNowAsync();
        await second.Sync.SyncNowAsync();

        Assert.Equal(["added-while-offline", "renamed-meanwhile"], await first.NamesAsync());
        Assert.Equal(["added-while-offline", "renamed-meanwhile"], await second.NamesAsync());
    }

    /// <summary>
    /// A machine whose database was started again empty would delete the whole library for everyone.
    /// It holds that back, and restoring puts the profiles back without touching the file.
    /// </summary>
    [Fact]
    public async Task AnEmptiedStore_HoldsTheDeletionsBackUntilRestored()
    {
        (Machine first, Machine second) = await ThreeProfilesJoinedAsync();
        byte[] before = await File.ReadAllBytesAsync(LibraryPath);

        await using (PilotDbContext context = await second.Factory.CreateDbContextAsync())
        {
            await context.Profiles.ExecuteDeleteAsync();
        }

        SharedLibraryStatus held = await second.Sync.SyncNowAsync();

        Assert.Equal(SharedLibraryCondition.DeletionHeld, held.Condition);
        Assert.Equal("3", held.Detail);
        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));

        SharedLibraryStatus restored = await second.Sync.RestoreHeldDeletionsAsync();

        Assert.Equal(SharedLibraryCondition.Synchronised, restored.Condition);
        Assert.Equal(["site-alpha", "site-beta", "site-gamma"], await second.NamesAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(LibraryPath));
    }

    [Fact]
    public async Task ConfirmedDeletions_AreWrittenForEveryone()
    {
        (Machine first, Machine second) = await ThreeProfilesJoinedAsync();

        await using (PilotDbContext context = await second.Factory.CreateDbContextAsync())
        {
            await context.Profiles.Where(profile => profile.Name != "site-gamma").ExecuteDeleteAsync();
        }

        Assert.Equal(SharedLibraryCondition.DeletionHeld, (await second.Sync.SyncNowAsync()).Condition);
        Assert.Equal(SharedLibraryCondition.Synchronised, (await second.Sync.ConfirmDeletionsAsync()).Condition);

        await first.Sync.SyncNowAsync();

        Assert.Equal(["site-gamma"], await first.NamesAsync());
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 3, false)]
    [InlineData(2, 3, true)]
    [InlineData(4, 40, false)]
    [InlineData(10, 400, true)]
    public void WhatCountsAsUnusuallyMany(int deleted, int held, bool expected) =>
        Assert.Equal(expected, SharedLibrarySync.IsUnusuallyMany(deleted, held));

    private async Task<(Machine First, Machine Second)> ThreeProfilesJoinedAsync()
    {
        Machine first = await CreateMachineAsync("first");
        await first.AddAsync("site-alpha", 1194);
        await first.AddAsync("site-beta", 1195);
        await first.AddAsync("site-gamma", 1196);
        await first.Sync.CreateAsync(LibraryPath, Passphrase);

        Machine second = await CreateMachineAsync("second");
        await second.Sync.JoinAsync(LibraryPath, Passphrase);

        return (first, second);
    }

    private async Task<(Machine First, Machine Second, Guid Id)> TwoJoinedMachinesAsync(TimeSpan? lockWait = null)
    {
        Machine first = await CreateMachineAsync("first", lockWait);
        Profile alpha = await first.AddAsync("site-alpha", 1194);
        await first.Sync.CreateAsync(LibraryPath, Passphrase);

        Machine second = await CreateMachineAsync("second", lockWait);
        await second.Sync.JoinAsync(LibraryPath, Passphrase);

        return (first, second, alpha.Id);
    }

    private async Task WriteLockAsync(DateTimeOffset since)
    {
        await File.WriteAllTextAsync(
            LibraryPath + ".lock",
            JsonSerializer.Serialize(new { Token = "someone else", Holder = "another-machine", Since = since }));

        File.SetLastWriteTimeUtc(LibraryPath + ".lock", since.UtcDateTime);
    }

    private async Task<Machine> CreateMachineAsync(string name, TimeSpan? lockWait = null)
    {
        string data = Path.Combine(root, name);
        Directory.CreateDirectory(data);

        ServiceCollection collection = new();
        collection.AddDbContextFactory<PilotDbContext>(options => options
            .UseSqlite($"Data Source={Path.Combine(data, "pilot.db")}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        ServiceProvider services = collection.BuildServiceProvider();
        IDbContextFactory<PilotDbContext> factory = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        await using (PilotDbContext context = await factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
        }

        FakeSecrets secrets = new();
        ObservedSecretStore observed = new(secrets);
        FakeSettingsService settings = new();
        ConnectionManager connections = IdleConnections.Create();
        HashSet<Guid> running = [];

        SharedLibrarySync sync = new(
            settings,
            factory,
            observed,
            connections,
            new TestPaths(data),
            TimeProvider.System,
            NullLogger<SharedLibrarySync>.Instance)
        {
            LockWait = lockWait ?? TimeSpan.FromSeconds(5),
            ActiveProfiles = () => running,
        };

        Machine machine = new(services, factory, secrets, observed, settings, connections, sync, running);
        machines.Add(machine);
        return machine;
    }

    public async Task DisposeAsync()
    {
        foreach (Machine machine in machines)
        {
            await machine.Sync.DisposeAsync();
            await machine.Connections.DisposeAsync();
            await machine.Services.DisposeAsync();
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

    private sealed record Machine(
        ServiceProvider Services,
        IDbContextFactory<PilotDbContext> Factory,
        FakeSecrets Secrets,
        ObservedSecretStore Observed,
        FakeSettingsService Settings,
        ConnectionManager Connections,
        SharedLibrarySync Sync,
        HashSet<Guid> Running)
    {
        public async Task<Profile> AddAsync(string name, int port)
        {
            await using PilotDbContext context = await Factory.CreateDbContextAsync();

            Profile profile = new()
            {
                Name = name,
                Configuration = string.Empty,
                ContentHash = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            ProfileConfigurationFacts.Apply(
                profile,
                $"client\nremote vpn.example.com {port} udp\n<ca>\n-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n</ca>\n");

            context.Profiles.Add(profile);
            await context.SaveChangesAsync();
            return profile;
        }

        public async Task ChangeAsync(Guid id, Action<Profile> change)
        {
            await using PilotDbContext context = await Factory.CreateDbContextAsync();
            Profile profile = await context.Profiles.SingleAsync(candidate => candidate.Id == id);
            change(profile);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        public async Task<List<string>> NamesAsync()
        {
            await using PilotDbContext context = await Factory.CreateDbContextAsync();
            return await context.Profiles.OrderBy(profile => profile.Name).Select(profile => profile.Name).ToListAsync();
        }

        public async Task<Profile> SingleAsync()
        {
            await using PilotDbContext context = await Factory.CreateDbContextAsync();
            return await context.Profiles.AsNoTracking().SingleAsync();
        }
    }

    private sealed class TestPaths(string data) : IApplicationPaths
    {
        public string DataDirectory => data;

        public string DatabasePath => Path.Combine(data, "pilot.db");

        public string LogDirectory => Path.Combine(data, "logs");

        public string SettingsPath => Path.Combine(data, "settings.json");

        public string SecretsDirectory => Path.Combine(data, "secrets");

        public string InstalledLanguageDirectory => Path.Combine(data, "lang");

        public string UserLanguageDirectory => Path.Combine(data, "lang");
    }
}
