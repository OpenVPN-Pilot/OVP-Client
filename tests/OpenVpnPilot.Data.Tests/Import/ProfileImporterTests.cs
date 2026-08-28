using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.Data.Tests.Import;

/// <summary>
/// Runs against a real SQLite database held in memory, so the schema and the indexes are exercised.
/// </summary>
public sealed class ProfileImporterTests : IAsyncLifetime, IDisposable
{
    private const string Certificate = "-----BEGIN CERTIFICATE-----\nMIIDQjCCAiqgAwIB\n-----END CERTIFICATE-----";

    private SqliteConnection connection = null!;
    private PilotDbContext context = null!;
    private string workingDirectory = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite(connection)
            .Options;

        context = new PilotDbContext(options);
        await context.Database.EnsureCreatedAsync();

        workingDirectory = Directory.CreateTempSubdirectory("ovp-import-tests").FullName;
    }

    public async Task DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();

        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_SelfContainedProfile_IsImportable()
    {
        string path = WriteConfig("site-alpha.ovpn", SelfContained("vpn.example.com", 1194, "udp"));

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.Equal(ImportOutcome.Importable, candidate.Outcome);
        Assert.Equal("site-alpha", candidate.SuggestedName);
        Assert.Equal("vpn.example.com", candidate.RemoteHost);
        Assert.Equal(1194, candidate.RemotePort);
        Assert.Equal("udp", candidate.Protocol);
        Assert.True(candidate.IsSelfContained);
        Assert.Empty(candidate.UnsupportedOptions);
    }

    [Fact]
    public async Task PrepareAsync_IdenticalFilesInOneSelection_MarksTheSecondAsADuplicate()
    {
        string content = SelfContained("vpn.example.com", 1194, "udp");
        string first = WriteConfig("site-alpha.ovpn", content);
        string second = WriteConfig("site-alpha copy.ovpn", content);

        IReadOnlyList<ImportCandidate> candidates = await CreateImporter().PrepareAsync([first, second]);

        Assert.Equal(ImportOutcome.Importable, candidates[0].Outcome);
        Assert.Equal(ImportOutcome.DuplicateInSelection, candidates[1].Outcome);
        Assert.Equal(candidates[0].ContentHash, candidates[1].ContentHash);
    }

    [Fact]
    public async Task PrepareAsync_ContentAlreadyStored_IsMarkedAsADuplicate()
    {
        string path = WriteConfig("site-alpha.ovpn", SelfContained("vpn.example.com", 1194, "udp"));
        ProfileImporter importer = CreateImporter();

        await importer.CommitAsync(await importer.PrepareAsync([path]));

        ImportCandidate candidate = Assert.Single(await importer.PrepareAsync([path]));
        Assert.Equal(ImportOutcome.DuplicateInStore, candidate.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WithoutServerVerification_IsRejectedRatherThanImported()
    {
        string path = WriteConfig("broken.ovpn", """
            client
            dev tun
            remote vpn.example.com 1194 udp
            auth-user-pass
            """);

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.Equal(ImportOutcome.Rejected, candidate.Outcome);
        Assert.Contains("peer-fingerprint", candidate.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_WithoutARemote_IsRejected()
    {
        string path = WriteConfig("no-remote.ovpn", $"client\ndev tun\n<ca>\n{Certificate}\n</ca>");

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.Equal(ImportOutcome.Rejected, candidate.Outcome);
        Assert.Contains("no remote", candidate.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_ScriptDirectives_AreReportedWithoutBlockingTheImport()
    {
        string path = WriteConfig("with-scripts.ovpn",
            SelfContained("vpn.example.com", 1194, "udp") + "\nup update-resolv.sh\ndown update-resolv.sh");

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.Equal(ImportOutcome.Importable, candidate.Outcome);
        Assert.Equal(["up", "down"], candidate.UnsupportedOptions);
    }

    [Fact]
    public async Task PrepareAsync_ExternalCertificates_AreInlinedFromDisk()
    {
        await File.WriteAllTextAsync(Path.Combine(workingDirectory, "ca.crt"), Certificate);
        string path = WriteConfig("external.ovpn", """
            client
            dev tun
            remote vpn.example.com 1194 udp
            ca ca.crt
            """);

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.Equal(ImportOutcome.Importable, candidate.Outcome);
        Assert.True(candidate.IsSelfContained);
        Assert.Contains("<ca>", candidate.Configuration, StringComparison.Ordinal);
        Assert.Empty(candidate.MissingFiles);
    }

    [Fact]
    public async Task PrepareAsync_MissingCertificateFile_IsReportedAsNotSelfContained()
    {
        string path = WriteConfig("external.ovpn", """
            client
            dev tun
            remote vpn.example.com 1194 udp
            ca missing.crt
            peer-fingerprint 47:03:71:16
            """);

        ImportCandidate candidate = Assert.Single(await CreateImporter().PrepareAsync([path]));

        Assert.False(candidate.IsSelfContained);
        Assert.Equal("missing.crt", Assert.Single(candidate.MissingFiles));
    }

    [Fact]
    public async Task PrepareAsync_UnreadableFile_DoesNotThrow()
    {
        ImportCandidate candidate = Assert.Single(
            await CreateImporter().PrepareAsync([Path.Combine(workingDirectory, "does-not-exist.ovpn")]));

        Assert.Equal(ImportOutcome.Unreadable, candidate.Outcome);
    }

    [Fact]
    public async Task CommitAsync_StoresOnlyTheImportableCandidates()
    {
        string good = WriteConfig("good.ovpn", SelfContained("vpn.example.com", 1194, "udp"));
        string bad = WriteConfig("bad.ovpn", "client\nremote vpn.example.com 1194 udp");

        ProfileImporter importer = CreateImporter();
        IReadOnlyList<Profile> created = await importer.CommitAsync(await importer.PrepareAsync([good, bad]));

        Profile profile = Assert.Single(created);
        Assert.Equal("good", profile.Name);
        Assert.Equal(1, await context.Profiles.CountAsync());
    }

    [Fact]
    public async Task CommitAsync_FilesProfilesUnderTheChosenFolder()
    {
        Folder folder = new() { Name = "Customers" };
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        string path = WriteConfig("site-alpha.ovpn", SelfContained("vpn.example.com", 1194, "udp"));
        ProfileImporter importer = CreateImporter();

        await importer.CommitAsync(await importer.PrepareAsync([path]), folder.Id);

        Profile stored = await context.Profiles.SingleAsync();
        Assert.Equal(folder.Id, stored.FolderId);
    }

    [Fact]
    public async Task FavouriteSlot_CannotBeClaimedTwice()
    {
        string first = WriteConfig("a.ovpn", SelfContained("a.example.com", 1194, "udp"));
        string second = WriteConfig("b.ovpn", SelfContained("b.example.com", 1194, "udp"));

        ProfileImporter importer = CreateImporter();
        IReadOnlyList<Profile> created = await importer.CommitAsync(await importer.PrepareAsync([first, second]));

        created[0].FavouriteSlot = 1;
        created[1].FavouriteSlot = 1;

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    public void Dispose()
    {
        context?.Dispose();
        connection?.Dispose();
    }

    private ProfileImporter CreateImporter() =>
        new(context, new OvpnConfigInliner(new FileSystemOvpnFileResolver()));

    private string WriteConfig(string fileName, string content)
    {
        string path = Path.Combine(workingDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string SelfContained(string host, int port, string protocol) =>
        $"client\ndev tun\nremote {host} {port} {protocol}\n<ca>\n{Certificate}\n</ca>";
}
