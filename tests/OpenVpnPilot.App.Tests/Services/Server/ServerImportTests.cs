using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// Importing into a server's copy: uploaded at once with an answer per profile, or kept and sent
/// later when the server cannot be reached.
/// </summary>
public sealed class ServerImportTests : IAsyncLifetime
{
    private SyncHarness harness = null!;
    private string folder = null!;

    public async Task InitializeAsync()
    {
        harness = await SyncHarness.CreateAsync();
        harness.Server.Changes = since => SyncServer.Feed(false, 1, profiles: harness.Server.Held());
        folder = Directory.CreateTempSubdirectory("ovp-import-test-").FullName;
    }

    public async Task DisposeAsync()
    {
        await harness.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task Online_UploadsInOneBatchAndSaysWhatBecameOfEach()
    {
        harness.Server.On(HttpMethod.Post, "/api/v1/profiles/batch", request =>
        {
            ProfileBatchRequest batch = request.Json.Deserialize<ProfileBatchRequest>(ServerJson.Options)!;
            ProfileResponse created = harness.Server.Hold(Guid.NewGuid(), batch.Items[0].Name, batch.Items[0].Configuration);

            return Answers.Json(new ProfileBatchResponse(1, 0, 1, [
                new ProfileBatchItemResponse(0, ProfileBatchOutcomes.Created, created, null, null),
                new ProfileBatchItemResponse(1, ProfileBatchOutcomes.Rejected, null, "request.validation_failed", "The name is too long.")]));
        });

        ProfileImportService importer = Importer();
        IReadOnlyList<ImportCandidate> candidates = await importer.PrepareAsync([Write("example-site-a", 1194), Write("example-site-b", 1195)]);

        ImportCommitResult result = await importer.CommitAsync(candidates, []);

        Assert.True(result.WentToServer);
        Assert.Equal(1, harness.Count(HttpMethod.Post, "/api/v1/profiles/batch"));
        Assert.Equal(ProfileUploadKind.Created, result.Uploads[candidates[0].SourcePath].Upload!.Kind);

        ProfileUploadOutcome refused = result.Uploads[candidates[1].SourcePath].Upload!;
        Assert.Equal(ProfileUploadKind.Rejected, refused.Kind);
        Assert.Equal("The name is too long.", refused.Detail);

        // The created one lives under the server's id; the refused one is not kept.
        Profile kept = Assert.Single(await harness.QueryAsync(context => context.Profiles.AsNoTracking().ToListAsync()));
        Assert.Equal(result.Uploads[candidates[0].SourcePath].Upload!.ServerId, kept.Id);
        Assert.DoesNotContain(await harness.Database.MarkersAsync(), marker => marker.Kind == PendingChangeKind.ProfileCreate);
    }

    [Fact]
    public async Task Offline_KeepsTheProfilesAndRecordsThemForLater()
    {
        harness.Server.Offline = true;

        ProfileImportService importer = Importer();
        IReadOnlyList<ImportCandidate> candidates = await importer.PrepareAsync([Write("example-site-a", 1194), Write("example-site-b", 1195)]);

        ImportCommitResult result = await importer.CommitAsync(candidates, ["Office"]);

        Assert.All(result.Uploads.Values, outcome => Assert.True(outcome.IsWaiting));
        Assert.Equal(2, await harness.QueryAsync(context => context.Profiles.CountAsync()));
        Assert.Equal(
            [PendingChangeKind.ProfileCreate, PendingChangeKind.ProfileCreate],
            (await harness.Database.MarkersAsync()).Select(marker => marker.Kind));
    }

    [Fact]
    public async Task OnTheLocalLibrary_StoresWithoutRecordingOrSending()
    {
        ProfileImportService importer = new(harness.Database.Factory, new OvpnConfigInliner(new FileSystemOvpnFileResolver()), TimeProvider.System);
        IReadOnlyList<ImportCandidate> candidates = await importer.PrepareAsync([Write("example-site-a", 1194)]);

        ImportCommitResult result = await importer.CommitAsync(candidates, []);

        Assert.False(result.WentToServer);
        Assert.Equal(1, result.Created);
        Assert.Empty(await harness.Database.MarkersAsync());
        Assert.DoesNotContain(harness.Requests, request => request.Path.StartsWith("/api/v1/profiles", StringComparison.Ordinal));
    }

    private ProfileImportService Importer() => new(
        harness.Database.Factory,
        new OvpnConfigInliner(new FileSystemOvpnFileResolver()),
        TimeProvider.System,
        harness.Engine,
        harness.Outbox,
        harness.Maintenance);

    private string Write(string name, int port)
    {
        string path = Path.Combine(folder, name + ".ovpn");
        File.WriteAllText(path, $"client\ndev tun\nremote vpn.example.com {port} udp\n<ca>\nA\n</ca>\n");
        return path;
    }
}
