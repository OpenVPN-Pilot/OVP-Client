using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.InteractiveService;

/// <summary>
/// Starts OpenVPN through the interactive service so that connecting needs no elevation prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenVpnLauncher : IOpenVpnLauncher
{
    private readonly InteractiveServicePipeClient pipeClient;

    public WindowsOpenVpnLauncher(InteractiveServicePipeClient pipeClient)
    {
        ArgumentNullException.ThrowIfNull(pipeClient);
        this.pipeClient = pipeClient;
    }

    public async Task<OpenVpnLaunchResult> LaunchAsync(
        OpenVpnLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string options = BuildCommandLine(request);

        // The trailing line feed stands in for the return key after the password is typed.
        string standardInput = request.ManagementPassword + "\n";

        InteractiveServiceReply reply = await pipeClient.StartAsync(
            request.WorkingDirectory,
            options,
            standardInput,
            cancellationToken);

        return reply.IsStarted
            ? OpenVpnLaunchResult.Started(reply.ProcessId)
            : OpenVpnLaunchResult.Refused(reply.Status, reply.Detail);
    }

    /// <summary>
    /// Builds the option string. Every option here is accepted by the service option whitelist, so
    /// the same command line works for callers that are not otherwise authorised.
    /// </summary>
    internal static string BuildCommandLine(OpenVpnLaunchRequest request)
    {
        StringBuilder builder = new();

        builder.Append("--config ").Append(QuotePath(request.ConfigurationPath));
        builder.Append(CultureInfo.InvariantCulture, $" --management 127.0.0.1 {request.ManagementEndpoint} stdin");
        builder.Append(" --management-query-passwords");
        builder.Append(" --management-hold");
        builder.Append(" --management-forget-disconnect");
        builder.Append(" --auth-retry interact");
        builder.Append(" --verb 3");

        if (!string.IsNullOrWhiteSpace(request.LogPath))
        {
            builder.Append(" --log ").Append(QuotePath(request.LogPath));
        }

        foreach (string option in request.AdditionalOptions)
        {
            if (!string.IsNullOrWhiteSpace(option))
            {
                builder.Append(' ').Append(option);
            }
        }

        return builder.ToString();
    }

    // The service parses the option string with the standard command line splitter, so a path is
    // quoted and any embedded quote is escaped.
    private static string QuotePath(string path) =>
        "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
