using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;
using OpenVpnPilot.Data;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Collects what someone would need to diagnose a problem, and nothing more.
/// </summary>
/// <remarks>
/// The bundle is meant to be attached to a support request, which means it will leave the machine.
/// It therefore carries the environment report, the logs and a profile listing, and never the
/// configurations, the credentials or the database itself: a stored profile contains a private key,
/// and a bundle that quietly included one would be a leak the user did not know they were making.
/// </remarks>
public sealed class DiagnosticsBundle
{
    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IApplicationPaths paths;
    private readonly IOpenVpnEnvironmentProbe probe;
    private readonly ISettingsService settings;
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public DiagnosticsBundle(
        IApplicationPaths paths,
        IOpenVpnEnvironmentProbe probe,
        ISettingsService settings,
        IDbContextFactory<PilotDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.paths = paths;
        this.probe = probe;
        this.settings = settings;
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
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
