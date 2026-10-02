using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A server that withdraws the account during a sign in made from this computer's library: what is
/// kept of it goes, and nothing else does.
/// </summary>
public sealed class ServerLeftoversTests : IAsyncDisposable
{
    private const string Address = "https://pilot.example.com";

    private static readonly ServerWipeDirective Directive = new(
        new Uri(Address + "/"),
        "POST",
        "/api/v1/auth/login",
        403,
        "req-wipe",
        DateTimeOffset.UtcNow);

    private readonly string root = Directory.CreateTempSubdirectory("ovp-leftovers-").FullName;
    private DatabaseAt? copy;

    [Fact]
    public async Task Remove_FromTheLocalLibrary_RemovesTheCopyItsSignInsAndTheTokenOnly()
    {
        TemporaryPaths paths = new(root);
        string key = ServerKey.Compute(Address);
        string folder = Path.Combine(paths.ServersDirectory, key);
        Guid serverProfile = Guid.NewGuid();
        Guid localProfile = Guid.NewGuid();

        copy = await DatabaseAt.CreateAsync(Path.Combine(folder, ActiveStorage.DatabaseFileName));

        await using (PilotDbContext context = await copy.Factory.CreateDbContextAsync())
        {
            context.Profiles.Add(new Profile
            {
                Id = serverProfile,
                Name = "example-site",
                Configuration = "client\nremote vpn.example.com 1194\n",
                ContentHash = new string('a', 64),
                Source = ProfileSource.Server,
            });
            await context.SaveChangesAsync();
        }

        FakeSecrets secrets = new();
        await secrets.WriteAsync(SecretReference.ForProfile(serverProfile, "Auth"), new StoredSecret("u", "p"));
        await secrets.WriteAsync(SecretReference.ForProfile(localProfile, "Auth"), new StoredSecret("u", "p"));
        await secrets.WriteAsync(SecretReference.ForServerRefreshToken(key), new StoredSecret(null, "refresh"));

        RecordingNotice notice = new();
        ActiveStorage local = ActiveStorage.Resolve(paths, StorageSelection.Local);
        ServerLeftovers leftovers = new(
            paths,
            local,
            secrets,
            new ServerProfileMaintenance(
                copy.Factory,
                secrets,
                new Outbox(copy.Factory, TimeProvider.System, NullLogger<Outbox>.Instance),
                NullLogger<ServerProfileMaintenance>.Instance),
            notice,
            NullLogger<ServerLeftovers>.Instance);

        ServerLeftoversReport? report = await leftovers.RemoveAsync(Directive, key);

        Assert.NotNull(report);
        Assert.Equal(1, report.Profiles);
        Assert.Equal(1, report.Secrets);
        Assert.True(report.RefreshTokenRemoved);
        Assert.True(report.FolderRemoved);
        Assert.False(Directory.Exists(folder));
        Assert.Equal([SecretReference.ForProfile(localProfile, "Auth")], await secrets.ListAsync());
        Assert.Equal(1, notice.Shown);
        Assert.Equal(StorageMode.Local, local.Mode);
    }

    public async ValueTask DisposeAsync()
    {
        if (copy is not null)
        {
            await copy.DisposeAsync();
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    private sealed class RecordingNotice : IServerLeftoversNotice
    {
        public int Shown { get; private set; }

        public Task ShowAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default)
        {
            Shown++;
            return Task.CompletedTask;
        }
    }
}
