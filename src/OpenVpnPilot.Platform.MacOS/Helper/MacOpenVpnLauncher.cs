using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// Starts OpenVPN through the privileged helper, which is what lets a tunnel come up without a
/// password prompt per connection.
/// </summary>
/// <remarks>
/// The request is translated into the helper's own terms rather than into a command line. The only
/// extra options the application ever adds are the route protection pull filters, and those become
/// structured filters; anything else is refused here with a sentence, because the helper would
/// refuse it anyway and a refusal with no explanation is worse.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOpenVpnLauncher : IOpenVpnLauncher
{
    /// <summary>
    /// How long a launch may take: writing the configuration, starting OpenVPN and answering.
    /// </summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(20);

    private readonly HelperSession session;
    private readonly ILogger<MacOpenVpnLauncher> logger;

    public MacOpenVpnLauncher(HelperSession session, ILogger<MacOpenVpnLauncher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        this.session = session;
        this.logger = logger ?? NullLogger<MacOpenVpnLauncher>.Instance;
    }

    public async Task<OpenVpnLaunchResult> LaunchAsync(
        OpenVpnLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.LogPath))
        {
            // The helper writes nothing as root at a path a caller chose. The management interface
            // carries the log in any case, and that is where this application reads it.
            return Refuse(LaunchRefusal.Unsupported, "A log file for OpenVPN cannot be requested on macOS.");
        }

        if (!LaunchRequest.TryReadPullFilters(request.AdditionalOptions, out List<PullFilterSpecification> filters, out string? problem))
        {
            return Refuse(LaunchRefusal.Unsupported, problem!);
        }

        LaunchSpecification specification;

        try
        {
            specification = await DescribeAsync(request, filters, cancellationToken);
        }
        catch (IOException exception)
        {
            return Refuse(LaunchRefusal.Unreadable, $"The configuration could not be read: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refuse(LaunchRefusal.Unreadable, $"The configuration could not be read: {exception.Message}");
        }

        HelperResponse response;

        try
        {
            response = await session.SendAsync(
                new HelperRequest { Type = HelperMessageType.Launch, Launch = specification },
                cancellationToken,
                LaunchTimeout);
        }
        catch (HelperUnavailableException exception)
        {
            MacLauncherLog.HelperUnavailable(logger, exception);
            return Refuse(
                LaunchRefusal.Unavailable,
                "The OpenVPN Pilot helper is not installed or not running, so no tunnel can be started.");
        }
        catch (HelperProtocolException exception)
        {
            MacLauncherLog.HelperUnavailable(logger, exception);
            return Refuse(LaunchRefusal.Protocol, exception.Message);
        }

        if (response.Type == HelperMessageType.Launched && response.ProcessId > 0)
        {
            return OpenVpnLaunchResult.Started(response.ProcessId);
        }

        MacLauncherLog.LaunchRefused(logger, response.RefusalCode ?? "unknown", response.Message ?? string.Empty);

        return Refuse(
            LaunchRefusal.FromHelper(response.RefusalCode),
            response.Message ?? "The helper refused to start OpenVPN.");
    }

    /// <summary>
    /// Turns the request into what the helper is told.
    /// </summary>
    /// <remarks>
    /// A configuration an administrator installed is named rather than sent, because that is what
    /// lets an account that is not authorised use it at all. Anything else is sent as text.
    /// </remarks>
    private static async Task<LaunchSpecification> DescribeAsync(
        OpenVpnLaunchRequest request,
        IReadOnlyList<PullFilterSpecification> filters,
        CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(request.ConfigurationPath);
        string? installed = LaunchRequest.InstalledName(path);

        return new LaunchSpecification
        {
            Configuration = installed is null
                ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                : null,
            InstalledConfiguration = installed,
            ManagementPort = request.ManagementEndpoint,
            ManagementPassword = request.ManagementPassword,
            Verbosity = Math.Clamp(request.Verbosity, 0, 11),
            PullFilters = filters,
        };
    }

    private static OpenVpnLaunchResult Refuse(uint code, string message) =>
        OpenVpnLaunchResult.Refused(code, message);
}

/// <summary>
/// The numbers a refused launch carries on macOS, where there is no system error code to report.
/// </summary>
/// <remarks>
/// They exist for the log and for anything that has to tell refusals apart. The sentence that goes
/// with each is what a person reads.
/// </remarks>
public static class LaunchRefusal
{
    public const uint Protocol = 1;
    public const uint Version = 2;
    public const uint NotAuthorised = 3;
    public const uint Configuration = 4;
    public const uint Limit = 5;
    public const uint NotFound = 6;
    public const uint LaunchFailed = 7;
    public const uint Unavailable = 8;
    public const uint Unsupported = 9;
    public const uint Unreadable = 10;

    public static uint FromHelper(string? code) => code switch
    {
        HelperRefusal.Version => Version,
        HelperRefusal.NotAuthorised => NotAuthorised,
        HelperRefusal.Configuration => Configuration,
        HelperRefusal.Limit => Limit,
        HelperRefusal.NotFound => NotFound,
        HelperRefusal.LaunchFailed => LaunchFailed,
        _ => Protocol,
    };
}

/// <summary>
/// Source generated log messages for <see cref="MacOpenVpnLauncher"/>.
/// </summary>
internal static partial class MacLauncherLog
{
    [LoggerMessage(
        EventId = 5210,
        Level = LogLevel.Warning,
        Message = "The helper could not be reached to start a tunnel.")]
    public static partial void HelperUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5211,
        Level = LogLevel.Warning,
        Message = "The helper refused to start a tunnel ({Code}): {Reason}")]
    public static partial void LaunchRefused(ILogger logger, string code, string reason);
}
