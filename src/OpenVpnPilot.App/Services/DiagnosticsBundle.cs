using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Collects what someone would need to diagnose a problem, and nothing more.
/// </summary>
/// <remarks>
/// The bundle is meant to be attached to a support request, which means it will leave the machine.
/// It therefore carries the environment report, the logs, a profile listing and where the profiles
/// live, and never the configurations, the credentials, the tokens or the database itself: a stored
/// profile contains a private key, and a bundle that quietly included one would be a leak the user
/// did not know they were making.
/// </remarks>
public sealed class DiagnosticsBundle
{
    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// How long the bundle waits for the server to say what it is.
    /// </summary>
    private static readonly TimeSpan ServerQuestionTimeout = TimeSpan.FromSeconds(5);

    private readonly IApplicationPaths paths;
    private readonly IOpenVpnEnvironmentProbe probe;
    private readonly ISettingsService settings;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly IActiveStorage storage;
    private readonly IServerApi? serverApi;

    /// <param name="serverApi">The connection to the server in Server mode, or null on the local library.</param>
    public DiagnosticsBundle(
        IApplicationPaths paths,
        IOpenVpnEnvironmentProbe probe,
        ISettingsService settings,
        IDbContextFactory<PilotDbContext> contextFactory,
        TimeProvider timeProvider,
        IActiveStorage storage,
        IServerApi? serverApi = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(storage);

        this.paths = paths;
        this.probe = probe;
        this.settings = settings;
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.storage = storage;
        this.serverApi = serverApi;
    }

    /// <summary>
    /// Writes a bundle to the given archive path.
    /// </summary>
    /// <returns>How many entries were written.</returns>
    public async Task<int> WriteAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string? directory = Path.GetDirectoryName(archivePath);

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(archivePath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);

        int entries = 0;

        await WriteEntryAsync(archive, "environment.txt", await DescribeEnvironmentAsync(cancellationToken), cancellationToken);
        entries++;

        await WriteEntryAsync(archive, "settings.json", DescribeSettings(), cancellationToken);
        entries++;

        await WriteEntryAsync(archive, "profiles.json", await DescribeProfilesAsync(cancellationToken), cancellationToken);
        entries++;

        await WriteEntryAsync(archive, "storage.txt", await DescribeStorageAsync(cancellationToken), cancellationToken);
        entries++;

        entries += await CopyLogsAsync(archive, cancellationToken);

        return entries;
    }

    /// <summary>
    /// A name that sorts by when it was made, so several bundles can sit in one directory.
    /// </summary>
    public string SuggestedFileName =>
        "openvpnpilot-diagnostics-"
        + timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + ".zip";

    private async Task<string> DescribeEnvironmentAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();

        builder.AppendLine("OpenVpnPilot diagnostics");
        builder.Append(CultureInfo.InvariantCulture, $"Collected: {timeProvider.GetUtcNow():O}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"Version: {typeof(DiagnosticsBundle).Assembly.GetName().Version}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"Operating system: {Environment.OSVersion}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"Runtime: {Environment.Version}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"64 bit process: {Environment.Is64BitProcess}");
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Environment checks");

        OpenVpnEnvironmentReport report = await probe.ProbeAsync(cancellationToken);

        foreach (EnvironmentCheck check in report.Checks)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  [{check.Status}] {check.Id}: {check.Detail}");
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"Can connect: {report.CanConnect}");
        builder.AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// The settings as they stand. They contain no secrets by design, only a language, a theme and
    /// a set of timeouts.
    /// </summary>
    private string DescribeSettings() => JsonSerializer.Serialize(settings.Current, JsonOutput);

    /// <summary>
    /// The profile listing without the configurations, so no key can travel with the bundle.
    /// </summary>
    private async Task<string> DescribeProfilesAsync(CancellationToken cancellationToken)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        List<ProfileSummary> profiles = await context.Profiles
            .AsNoTracking()
            .OrderBy(profile => profile.Name)
            .Select(profile => new ProfileSummary(
                profile.Name,
                profile.RemoteHost,
                profile.RemotePort,
                profile.Protocol,
                profile.RequiresCredentials,
                profile.HasUnsupportedOptions,
                profile.IsSelfContained,
                profile.ProtectRoutes,
                profile.ConnectCount,
                profile.LastConnectedAt))
            .ToListAsync(cancellationToken);

        return JsonSerializer.Serialize(profiles, JsonOutput);
    }

    /// <summary>
    /// Where the profiles live and, for a server, how the synchronisation with it stands.
    /// </summary>
    /// <remarks>
    /// The server is named by its host only, never by the address as written, which could carry a
    /// path or a user name. What the copy knows is counted, not listed: how many changes wait and
    /// which problem codes they met, the last error code and the request id the operator can find
    /// in the server's log. Nobody's name, token or shared sign in is part of it.
    ///
    /// The server's version is asked from the server itself, anonymously, because the copy does not
    /// keep it. A bundle is often made because the server cannot be reached, so the question is
    /// bounded and its failure is written down as what happened rather than delaying the bundle.
    /// </remarks>
    internal async Task<string> DescribeStorageAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();

        Line(builder, "Storage mode", storage.Mode);
        Line(builder, "Client version", new AssemblyClientVersionProvider(typeof(DiagnosticsBundle).Assembly).Version);

        if (storage.Problem != ServerAddressProblem.None)
        {
            Line(builder, "Server address problem", storage.Problem);
        }

        if (!storage.IsServerMode)
        {
            return builder.ToString();
        }

        Line(builder, "Server host", HostOf(storage.ServerAddress));
        await DescribeServerAsync(builder, cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        SyncState? state = await context.SyncStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        Line(builder, "Last pull", state?.LastSuccessfulPullAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
        Line(builder, "Last push", state?.LastSuccessfulPushAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
        Line(builder, "Cursor", state?.Cursor?.ToString(CultureInfo.InvariantCulture) ?? "none");
        Line(builder, "Last error code", state?.LastErrorCode ?? "none");
        Line(builder, "Last request id", state?.LastRequestId ?? "none");
        Line(builder, "Signed in role", state?.UserRole ?? "unknown");
        Line(builder, "Signed in provider", state?.UserProvider ?? "unknown");

        List<PendingChange> waiting = await context.PendingChanges.AsNoTracking().ToListAsync(cancellationToken);
        Line(builder, "Changes waiting", waiting.Count);

        foreach (IGrouping<PendingChangeKind, PendingChange> kind in waiting.GroupBy(change => change.Kind).OrderBy(group => group.Key))
        {
            Line(builder, "  " + kind.Key, kind.Count());
        }

        foreach (IGrouping<string, PendingChange> code in waiting
            .Where(change => change.LastErrorCode is not null)
            .GroupBy(change => change.LastErrorCode!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Line(builder, "  Last met " + code.Key, code.Count());
        }

        return builder.ToString();
    }

    private async Task DescribeServerAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        if (serverApi is null)
        {
            Line(builder, "Server", "no connection composed");
            return;
        }

        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ServerQuestionTimeout);

        ServerResult<ServerInfoResponse> answer;

        try
        {
            answer = await serverApi.GetServerInfoAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Line(builder, "Server", "did not answer within " + ServerQuestionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
            return;
        }

        if (answer.IsSuccess)
        {
            ServerInfoResponse info = answer.Value;
            Line(builder, "Server", $"{info.Name} {info.Version}");
            Line(builder, "API version", info.ApiVersion);
            Line(builder, "Minimum client version", info.MinimumClientVersion);
            Line(builder, "Sign in mode", info.AuthMode);
        }
        else
        {
            Line(builder, "Server", $"not answered: {answer.Outcome} {answer.Code ?? string.Empty} {answer.RequestId ?? string.Empty}".TrimEnd());
        }
    }

    /// <summary>
    /// The host and, when it is not the default, the port of an address. Nothing else of it.
    /// </summary>
    private static string HostOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            ? (uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture))
            : "unknown";

    private static void Line(StringBuilder builder, string name, object? value)
    {
        builder.Append(CultureInfo.InvariantCulture, $"{name}: {value}");
        builder.AppendLine();
    }

    private async Task<int> CopyLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.LogDirectory))
        {
            return 0;
        }

        int copied = 0;

        foreach (string path in Directory.EnumerateFiles(paths.LogDirectory, "*.log"))
        {
            try
            {
                // The log is open for writing, so it is read with sharing rather than copied.
                await using FileStream source = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                ZipArchiveEntry entry = archive.CreateEntry(
                    "logs/" + Path.GetFileName(path),
                    CompressionLevel.Optimal);

                await using Stream target = entry.Open();
                await source.CopyToAsync(target, cancellationToken);

                copied++;
            }
            catch (IOException)
            {
                // One unreadable log must not cost the user the rest of the bundle.
            }
        }

        return copied;
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        await using Stream target = entry.Open();
        await target.WriteAsync(new UTF8Encoding(false).GetBytes(content), cancellationToken);
    }

    /// <summary>
    /// What a profile looks like in the bundle. The configuration is deliberately absent.
    /// </summary>
    private sealed record ProfileSummary(
        string Name,
        string? RemoteHost,
        int? RemotePort,
        string? Protocol,
        bool RequiresCredentials,
        bool HasUnsupportedOptions,
        bool IsSelfContained,
        bool? ProtectRoutes,
        int ConnectCount,
        DateTimeOffset? LastConnectedAt);
}
