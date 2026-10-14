using System.Net.Http;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.Tests.Services.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Core.Tests.Server;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// What the diagnostics bundle says about where the profiles live, which leaves the machine.
/// </summary>
public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ovp-diagnostics-" + Guid.NewGuid().ToString("N"));

    public DiagnosticsBundleTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task DescribeStorage_ServerMode_NamesTheStateAndNothingSecret()
    {
        TemporaryPaths paths = new(root);
        ActiveStorage storage = ActiveStorage.Resolve(paths, new StorageSelection(StorageMode.Server, "https://pilot.example.com"));
        await using DatabaseAt database = await DatabaseAt.CreateAsync(storage.DatabasePath);

        await using (PilotDbContext context = await database.Factory.CreateDbContextAsync())
        {
            context.SyncStates.Add(new SyncState
            {
                Cursor = 42,
                LastSuccessfulPullAt = TestServer.Start,
                LastErrorCode = ServerErrorCodes.ProfileDuplicate,
                LastRequestId = "req-0815",
                Username = "example-user",
                UserDisplayName = "Example User",
                UserRole = ServerRoles.Admin,
                UserProvider = "file",
            });
            context.PendingChanges.Add(new PendingChange { Kind = PendingChangeKind.ProfileUpdate, EntityId = Guid.NewGuid(), CreatedAt = TestServer.Start });
            context.PendingChanges.Add(new PendingChange { Kind = PendingChangeKind.VaultAdd, EntityId = Guid.NewGuid(), Realm = "auth", CreatedAt = TestServer.Start, LastErrorCode = "server.unavailable" });
            await context.SaveChangesAsync();
        }

        using TestServer server = new(_ => Answers.Json(Answers.Info()));
        DiagnosticsBundle bundle = new(paths, new ReadyEnvironmentProbe(), new FakeSettingsService(), database.Factory, TimeProvider.System, storage, server.Api);

        string text = await bundle.DescribeStorageAsync(CancellationToken.None);

        Assert.Contains("Storage mode: Server", text, StringComparison.Ordinal);
        Assert.Contains("Server host: pilot.example.com", text, StringComparison.Ordinal);
        Assert.Contains("Server: OpenVPN Pilot Server 1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("API version: 1", text, StringComparison.Ordinal);
        Assert.Contains("Cursor: 42", text, StringComparison.Ordinal);
        Assert.Contains("Last error code: " + ServerErrorCodes.ProfileDuplicate, text, StringComparison.Ordinal);
        Assert.Contains("Last request id: req-0815", text, StringComparison.Ordinal);
        Assert.Contains("Last push: never", text, StringComparison.Ordinal);
        Assert.Contains("Changes waiting: 2", text, StringComparison.Ordinal);
        Assert.Contains("Last met server.unavailable: 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example-user", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Example User", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeStorage_ServerUnreachable_SaysSoAndCarriesOn()
    {
        TemporaryPaths paths = new(root);
        ActiveStorage storage = ActiveStorage.Resolve(paths, new StorageSelection(StorageMode.Server, "https://pilot.example.com"));
        await using DatabaseAt database = await DatabaseAt.CreateAsync(storage.DatabasePath);

        using TestServer server = new(_ => Answers.Fail(new HttpRequestException("refused")));
        DiagnosticsBundle bundle = new(paths, new ReadyEnvironmentProbe(), new FakeSettingsService(), database.Factory, TimeProvider.System, storage, server.Api);

        string text = await bundle.DescribeStorageAsync(CancellationToken.None);

        Assert.Contains("Server: not answered: Offline", text, StringComparison.Ordinal);
        Assert.Contains("Changes waiting: 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeStorage_LocalMode_NamesOnlyTheMode()
    {
        TemporaryPaths paths = new(root);
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageSelection.Local);
        await using DatabaseAt database = await DatabaseAt.CreateAsync(storage.DatabasePath);
        DiagnosticsBundle bundle = new(paths, new ReadyEnvironmentProbe(), new FakeSettingsService(), database.Factory, TimeProvider.System, storage);

        string text = await bundle.DescribeStorageAsync(CancellationToken.None);

        Assert.Contains("Storage mode: Local", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Server", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The extract of problems is the part someone reads first, so it keeps what went wrong, what
    /// the client did about it and nothing else.
    /// </summary>
    [Fact]
    public async Task ExtractProblems_KeepsWarningsErrorsSummariesAndWhatTheClientDid()
    {
        TemporaryPaths paths = new(root);
        Directory.CreateDirectory(paths.LogDirectory);
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageSelection.Local);
        await using DatabaseAt database = await DatabaseAt.CreateAsync(storage.DatabasePath);

        await File.WriteAllLinesAsync(Path.Combine(paths.LogDirectory, "2026-09-15.log"),
        [
            "2026-09-15 10:00:00.000 [INF] [Pilot] App: Started",
            "2026-09-15 10:00:01.000 [INF] [OpenVpn] example-site: Initialization Sequence Completed",
            "2026-09-15 10:05:00.000 [WRN] [Pilot] ConnectionSupervisor: The management connection closed while the tunnel was Connected.",
            "2026-09-15 10:05:01.000 [ERR] [Pilot] SessionRecorder: The history could not be written.",
            "System.IO.IOException: disk full",
            "   at Somewhere()",
            "2026-09-15 10:05:02.000 [INF] [OpenVpn] example-site: Repeated 4312 more times in 2 min, last at 10:07:02: TLS handshake failed",
            "2026-09-15 10:06:00.000 [INF] [Pilot] ConnectionHealthMonitor: Checking 1 tunnel(s) because the computer woke.",
            "2026-09-15 10:06:01.000 [INF] [Pilot] PingMonitor: An ordinary line",
        ]);

        DiagnosticsBundle bundle = new(paths, new ReadyEnvironmentProbe(), new FakeSettingsService(), database.Factory, TimeProvider.System, storage);

        string text = await bundle.ExtractProblemsAsync(CancellationToken.None);

        Assert.Contains("management connection closed", text, StringComparison.Ordinal);
        Assert.Contains("disk full", text, StringComparison.Ordinal);
        Assert.Contains("at Somewhere()", text, StringComparison.Ordinal);
        Assert.Contains("Repeated 4312 more times", text, StringComparison.Ordinal);
        Assert.Contains("Checking 1 tunnel(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Initialization Sequence Completed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An ordinary line", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeConnections_WithoutHistory_SaysSo()
    {
        TemporaryPaths paths = new(root);
        ActiveStorage storage = ActiveStorage.Resolve(paths, StorageSelection.Local);
        await using DatabaseAt database = await DatabaseAt.CreateAsync(storage.DatabasePath);
        DiagnosticsBundle bundle = new(paths, new ReadyEnvironmentProbe(), new FakeSettingsService(), database.Factory, TimeProvider.System, storage);

        string text = await bundle.DescribeConnectionsAsync(CancellationToken.None);

        Assert.Contains("Last wake: none", text, StringComparison.Ordinal);
        Assert.Contains("  none", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }
}
