using System.Net;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Tests.Services.Server;

/// <summary>
/// A cycle that moves every kind of secret there is writes none of them to the log.
/// </summary>
public sealed class SecretLoggingTests
{
    private const string PulledKey = "PULLED-PRIVATE-KEY-MATERIAL";
    private const string UploadedKey = "UPLOADED-PRIVATE-KEY-MATERIAL";
    private const string HeldPassword = "held-password-value";
    private const string StoredPassword = "stored-password-value";
    private const string PulledPassword = "pulled-password-value";
    private const string AdoptedPassword = "adopted-password-value";

    private static readonly Guid ServerProfile = Guid.Parse("5e000000-0000-0000-0000-0000000000a1");
    private static readonly Guid SharedProfile = Guid.Parse("5e000000-0000-0000-0000-0000000000a2");

    [Fact]
    public async Task SynchronisationAndOutbox_NeverWriteASecret()
    {
        RecordingLoggerFactory logs = new();
        await using SyncHarness harness = await SyncHarness.CreateAsync(logs: logs);

        string pulled = "client\nremote vpn.example.com 1194\n<key>\n" + PulledKey + "\n</key>\n";
        harness.Server.Hold(ServerProfile, "example-site", pulled);
        harness.Server.Hold(SharedProfile, "example-site-b", "client\nremote vpn.example.com 1195\n<ca>\nA\n</ca>\n");

        harness.Server.Changes = since => SyncServer.Feed(
            since == 0,
            1,
            profiles: harness.Server.Held(),
            vault:
            [
                SyncServer.VaultEntry(ServerProfile, "Auth", "pulled-user", PulledPassword),
                SyncServer.VaultEntry(SharedProfile, "Auth", "shared-user", AdoptedPassword),
            ]);

        // A profile created offline, with a sign in typed and not remembered, and one remembered.
        string uploaded = "client\nremote vpn.example.com 1196\n<key>\n" + UploadedKey + "\n</key>\n";
        Profile local = await harness.Database.AddProfileAsync("example-site-c", uploaded);
        await harness.Outbox.RecordAsync(PendingChangeKind.ProfileCreate, local.Id);
        harness.Held.Hold(local.Id, "Auth", new StoredSecret("typed-user", HeldPassword));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, local.Id, "Auth");
        await harness.Secrets.WriteAsync(SecretReference.ForProfile(local.Id, "Private Key"), new StoredSecret(null, StoredPassword));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, local.Id, "Private Key");

        // A sign in somebody else shared first, which is taken over here.
        await harness.ChangeAsync(context =>
        {
            context.Profiles.Add(new Profile
            {
                Id = SharedProfile,
                Name = "example-site-b",
                Configuration = harness.Server.Configurations[SharedProfile],
                ContentHash = ProfileImporter.ComputeHash(harness.Server.Configurations[SharedProfile]),
                Source = ProfileSource.Server,
            });

            return Task.CompletedTask;
        });

        await harness.Secrets.WriteAsync(SecretReference.ForProfile(SharedProfile, "Auth"), new StoredSecret("typed-user", StoredPassword));
        await harness.Outbox.RecordAsync(PendingChangeKind.VaultAdd, SharedProfile, "Auth");
        harness.Server.On(HttpMethod.Post, $"/api/v1/profiles/{SharedProfile:D}/vault/Auth", _ =>
            Answers.Problem(HttpStatusCode.Conflict, ServerErrorCodes.VaultEntryExists));
        harness.Server.On(HttpMethod.Get, $"/api/v1/profiles/{SharedProfile:D}/vault", _ =>
            Answers.Json(new[] { SyncServer.VaultEntry(SharedProfile, "Auth", "shared-user", AdoptedPassword) }));

        SyncCycleResult result = await harness.Engine.SynchronizeAsync(CancellationToken.None);

        // The secrets did travel, so the absence below means something.
        Assert.True(result.Completed);
        Assert.Contains(harness.Requests, request => request.Body?.Contains(HeldPassword, StringComparison.Ordinal) == true);
        Assert.Contains(harness.Requests, request => request.Body?.Contains(UploadedKey, StringComparison.Ordinal) == true);
        Assert.Equal(PulledPassword, harness.Secrets.Entries[SecretReference.ForProfile(ServerProfile, "Auth")].Password);
        Assert.Equal(AdoptedPassword, harness.Secrets.Entries[SecretReference.ForProfile(SharedProfile, "Auth")].Password);

        IReadOnlyList<string> lines = [.. logs.Lines, .. harness.Network.Logs.Lines];
        Assert.NotEmpty(logs.Lines);

        foreach (string secret in new[] { PulledKey, UploadedKey, HeldPassword, StoredPassword, PulledPassword, AdoptedPassword })
        {
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.Ordinal));
        }
    }
}
