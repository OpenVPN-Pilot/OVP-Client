namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Inspects whether the machine can run tunnels, and explains precisely what is missing when it cannot.
/// </summary>
/// <remarks>
/// Each aspect is reported separately so the user is told what to do rather than being shown a
/// generic failure. This runs before the main window and backs the doctor command.
/// </remarks>
public interface IOpenVpnEnvironmentProbe
{
    public Task<OpenVpnEnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of every environment check, in the order they were evaluated.
/// </summary>
public sealed record OpenVpnEnvironmentReport(IReadOnlyList<EnvironmentCheck> Checks)
{
    /// <summary>
    /// True when nothing blocks a connection attempt.
    /// </summary>
    public bool CanConnect => Checks.All(check => check.Status != EnvironmentCheckStatus.Failed);

    /// <summary>
    /// True when connecting will work but something still needs attention, such as the caller not
    /// being authorised to use configurations outside the OpenVPN configuration directory.
    /// </summary>
    public bool HasWarnings => Checks.Any(check => check.Status == EnvironmentCheckStatus.Warning);

    public EnvironmentCheck? this[EnvironmentCheckId id] =>
        Checks.FirstOrDefault(check => check.Id == id);
}

/// <summary>
/// One aspect of the environment, with the detail needed to act on it.
/// </summary>
/// <param name="Id">Which aspect was checked.</param>
/// <param name="Status">Whether it blocks, warns or passes.</param>
/// <param name="Detail">
/// A factual description of what was found, such as a resolved path or version. Free of guidance,
/// because the wording shown to the user is localized in the presentation layer.
/// </param>
public sealed record EnvironmentCheck(EnvironmentCheckId Id, EnvironmentCheckStatus Status, string Detail);

public enum EnvironmentCheckStatus
{
    Passed,

    /// <summary>
    /// Connections work, but under a restriction the user should know about.
    /// </summary>
    Warning,

    /// <summary>
    /// Connections cannot work until this is resolved.
    /// </summary>
    Failed,
}

public enum EnvironmentCheckId
{
    /// <summary>
    /// The OpenVPN Community registry key and its paths.
    /// </summary>
    Installation,

    /// <summary>
    /// The openvpn executable named by the registry.
    /// </summary>
    Executable,

    /// <summary>
    /// The executable reports a supported version.
    /// </summary>
    Version,

    /// <summary>
    /// The interactive service is installed and running.
    /// </summary>
    InteractiveService,

    /// <summary>
    /// The service control pipe can be opened.
    /// </summary>
    ServicePipe,

    /// <summary>
    /// The caller may launch configurations from outside the OpenVPN configuration directory.
    /// </summary>
    Authorisation,

    /// <summary>
    /// A different OpenVPN product was found in place of the Community edition.
    /// </summary>
    ConflictingProduct,
}
