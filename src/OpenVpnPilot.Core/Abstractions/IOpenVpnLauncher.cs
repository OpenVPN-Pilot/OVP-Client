namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Starts an OpenVPN process with a management interface the caller can attach to.
/// </summary>
/// <remarks>
/// The implementation decides how privileges are obtained. On Windows that is the interactive
/// service, which avoids an elevation prompt per connection.
/// </remarks>
public interface IOpenVpnLauncher
{
    public Task<OpenVpnLaunchResult> LaunchAsync(OpenVpnLaunchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything needed to start one tunnel.
/// </summary>
/// <param name="ConfigurationPath">The materialised configuration file.</param>
/// <param name="WorkingDirectory">The directory the process starts in. Relative paths resolve against it.</param>
/// <param name="ManagementEndpoint">The loopback port the management interface will listen on.</param>
/// <param name="ManagementPassword">
/// Handed to the process over its standard input so it never reaches disk.
/// </param>
/// <param name="AdditionalOptions">
/// Extra command line options, for example pull filters that protect the host routing table.
/// </param>
/// <param name="LogPath">Optional path for the OpenVPN log file.</param>
public sealed record OpenVpnLaunchRequest(
    string ConfigurationPath,
    string WorkingDirectory,
    int ManagementEndpoint,
    string ManagementPassword,
    IReadOnlyList<string> AdditionalOptions,
    string? LogPath = null);

/// <summary>
/// The result of a launch attempt.
/// </summary>
public sealed record OpenVpnLaunchResult
{
    private OpenVpnLaunchResult(bool succeeded, int processId, uint errorCode, string message)
    {
        Succeeded = succeeded;
        ProcessId = processId;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Succeeded { get; }

    public int ProcessId { get; }

    /// <summary>
    /// The platform error code when the launch was refused. Zero on success.
    /// </summary>
    public uint ErrorCode { get; }

    public string Message { get; }

    public static OpenVpnLaunchResult Started(int processId) =>
        new(true, processId, 0, string.Empty);

    public static OpenVpnLaunchResult Refused(uint errorCode, string message) =>
        new(false, 0, errorCode, message);
}
