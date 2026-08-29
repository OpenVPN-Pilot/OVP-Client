using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Covers the one part of a package that needs a platform facility: the saved sign ins.
/// </summary>
/// <remarks>
/// They live in the operating system keystore rather than in the profile database, which is why
/// collecting them for an export and putting them back after an import happens here. Handing over
/// fifty profiles is most of the work left undone if the recipient still has to be told fifty
/// passwords, and doing it wrong would export somebody else's.
/// </remarks>
public sealed class ProfilePackageWriterTests : IAsyncLifetime
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-writer-").FullName;

    private ServiceProvider services = null!;
    private IDbContextFactory<PilotDbContext> factory = null!;
    private FakeSecretStore secrets = null!;
    private ProfilePackageWriter writer = null!;

    private Guid alpha;
    private Guid beta;

    private string PackagePath => Path.Combine(root, "set.ovppkg");

    public async Task InitializeAsync()
    {
        ServiceCollection collection = new();

        collection.AddDbContextFactory<PilotDbContext>(options => options
            .UseSqlite($"Data Source={Path.Combine(root, "source.db")}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services = collection.BuildServiceProvider();
        factory = services.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        await using PilotDbContext context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        Profile first = new()
        {
            Name = "example-site-alpha",
            Configuration = "client\nremote vpn.example.com 1194\n",
            ContentHash = new string('a', 64),
            RequiresCredentials = true,
        };

        Profile second = new()
        {
            Name = "example-site-beta",
            Configuration = "client\nremote vpn2.example.com 1195\n",
            ContentHash = new string('b', 64),
        };

        context.Profiles.AddRange(first, second);
        await context.SaveChangesAsync();

        alpha = first.Id;
        beta = second.Id;

        secrets = new FakeSecretStore();
        writer = new ProfilePackageWriter(factory, secrets, TimeProvider.System);
    }

    [Fact]
    public async Task WriteAsync_WithoutBeingAsked_CarriesNoCredentials()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));

        PackageWriteResult result = await writer.WriteAsync(PackagePath, [alpha, beta], "a passphrase");

        Assert.Equal(2, result.Profiles);
        Assert.Equal(0, result.Credentials);
    }

    [Fact]
    public async Task WriteAsync_TakesOnlyTheCredentialsOfTheProfilesBeingWritten()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));
        await secrets.WriteAsync(SecretReference.ForProfile(beta, "Auth"), new StoredSecret("other", "other-secret"));

        PackageWriteResult result = await writer.WriteAsync(
            PackagePath,
            [alpha],
            "a passphrase",
            includeCredentials: true);

        Assert.Equal(1, result.Profiles);
        Assert.Equal(1, result.Credentials);
    }

    /// <summary>
    /// One profile can be asked for more than one secret, such as a key passphrase and a sign in.
    /// </summary>
    [Fact]
    public async Task WriteAsync_TakesEveryRealmAProfileHas()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));
        await secrets.WriteAsync(
            SecretReference.ForProfile(alpha, "Private Key"),
            new StoredSecret(null, "key-passphrase"));

        PackageWriteResult result = await writer.WriteAsync(
            PackagePath,
            [alpha],
            "a passphrase",
            includeCredentials: true);

        Assert.Equal(2, result.Credentials);
    }

    [Fact]
    public async Task ApplyAsync_PutsTheCredentialsUnderTheProfilesTheyArrivedWith()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));

        await writer.WriteAsync(PackagePath, [alpha], "a passphrase", includeCredentials: true);

        // A different machine: its own store, and nothing in its keystore.
        (ProfilePackageWriter other, FakeSecretStore otherSecrets, IDbContextFactory<PilotDbContext> otherFactory) =
            await CreateEmptyMachineAsync();

        PackageImportResult result = await other.ApplyAsync(PackagePath, "a passphrase");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Credentials);

        await using PilotDbContext context = await otherFactory.CreateDbContextAsync();
        Guid imported = await context.Profiles
            .Where(profile => profile.Name == "example-site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        StoredSecret? restored = await otherSecrets.TryReadAsync(SecretReference.ForProfile(imported, "Auth"));

        Assert.NotNull(restored);
        Assert.Equal("operator", restored.Username);
        Assert.Equal("secret", restored.Password);
    }

    /// <summary>
    /// A machine that cannot protect a secret must not be given one to keep anywhere else.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WhenNothingCanProtectASecret_StoresNone()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));
        await writer.WriteAsync(PackagePath, [alpha], "a passphrase", includeCredentials: true);

        (ProfilePackageWriter other, FakeSecretStore otherSecrets, _) = await CreateEmptyMachineAsync();
        otherSecrets.IsAvailable = false;

        PackageImportResult result = await other.ApplyAsync(PackagePath, "a passphrase");

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Credentials);
        Assert.Empty(await otherSecrets.ListAsync());
    }

    [Fact]
    public async Task CountCredentialsAsync_ReportsWhatEachProfileHas()
    {
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Auth"), new StoredSecret("operator", "secret"));
        await secrets.WriteAsync(SecretReference.ForProfile(alpha, "Private Key"), new StoredSecret(null, "key"));

        IReadOnlyDictionary<Guid, int> counts = await writer.CountCredentialsAsync();

        Assert.Equal(2, counts[alpha]);
        Assert.False(counts.ContainsKey(beta));
    }

    private async Task<(ProfilePackageWriter Writer, FakeSecretStore Secrets, IDbContextFactory<PilotDbContext> Factory)>
        CreateEmptyMachineAsync()
    {
        ServiceCollection collection = new();

        collection.AddDbContextFactory<PilotDbContext>(options => options
            .UseSqlite($"Data Source={Path.Combine(root, "target.db")}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

        ServiceProvider provider = collection.BuildServiceProvider();
        IDbContextFactory<PilotDbContext> target = provider.GetRequiredService<IDbContextFactory<PilotDbContext>>();

        await using PilotDbContext context = await target.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        FakeSecretStore store = new();
        return (new ProfilePackageWriter(target, store, TimeProvider.System), store, target);
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();
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

    /// <summary>
    /// Stands in for the keystore. Nothing is protected, which is exactly why it is only a test.
    /// </summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, StoredSecret> entries = new(StringComparer.Ordinal);

        public bool IsAvailable { get; set; } = true;

        public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.GetValueOrDefault(reference));

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
}
