using System.Globalization;
using System.Text.RegularExpressions;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper.Tunnels;

/// <summary>
/// Builds the command line OpenVPN is started with, from values the helper checked itself.
/// </summary>
/// <remarks>
/// The options are the ones the Windows client passes, so both platforms drive OpenVPN the same
/// way, with three differences. The management password arrives on an inherited descriptor, because
/// on Unix OpenVPN only reads a password from standard input when standard input is a terminal.
/// Script security is set to its built in level after the configuration, so that a later option
/// wins over anything the configuration says. The working and temporary directories are the
/// tunnel's own, which only root can open.
///
/// Every element is its own argument. Nothing here is ever joined into a line a shell would split.
/// </remarks>
internal static partial class CommandLine
{
    public const int MaximumPullFilters = 32;

    /// <summary>
    /// The descriptor the management password arrives on, which the command line names as a file.
    /// </summary>
    /// <remarks>
    /// On Unix OpenVPN only reads a password from standard input when standard input is a terminal,
    /// so it is handed an inherited pipe under a name instead. It never touches a disk.
    /// </remarks>
    public const int SecretDescriptor = 3;

    public static IReadOnlyList<string> Build(
        string configurationPath,
        string tunnelDirectory,
        string temporaryDirectory,
        LaunchSpecification specification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        ArgumentNullException.ThrowIfNull(specification);

        List<string> arguments =
        [
            "--config", configurationPath,
            "--management", "127.0.0.1", specification.ManagementPort.ToString(CultureInfo.InvariantCulture),
            $"/dev/fd/{SecretDescriptor}",
            "--management-query-passwords",
            "--management-hold",
            "--management-forget-disconnect",
            "--auth-retry", "interact",
            "--verb", specification.Verbosity.ToString(CultureInfo.InvariantCulture),
            "--script-security", "1",
            "--cd", tunnelDirectory,
            "--tmp-dir", temporaryDirectory,
        ];

        foreach (PullFilterSpecification filter in specification.PullFilters ?? [])
        {
            arguments.Add("--pull-filter");
            arguments.Add(filter.Action);
            arguments.Add(filter.Text);
        }

        return arguments;
    }

    /// <summary>
    /// Checks the values a caller sent before any of them is used.
    /// </summary>
    /// <returns>Null when the specification is acceptable, otherwise the reason it is not.</returns>
    public static string? Validate(LaunchSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        if ((specification.Configuration is null) == (specification.InstalledConfiguration is null))
        {
            return "A launch names either a configuration or an installed configuration, and exactly one of them.";
        }

        if (specification.ManagementPort is < 1024 or > 65535)
        {
            return "The management port has to be between 1024 and 65535.";
        }

        if (specification.ManagementPassword is null || !ManagementPassword().IsMatch(specification.ManagementPassword))
        {
            return "The management password has to be 16 to 128 letters and digits.";
        }

        if (specification.Verbosity is < 0 or > 11)
        {
            return "The verbosity has to be between 0 and 11.";
        }

        // Absent is none, and every filter in a list that is there has to be one.
        IReadOnlyList<PullFilterSpecification> filters = specification.PullFilters ?? [];

        if (filters.Count > MaximumPullFilters)
        {
            return $"No more than {MaximumPullFilters} pull filters are accepted.";
        }

        foreach (PullFilterSpecification? filter in filters)
        {
            if (filter?.Action is not ("accept" or "ignore" or "reject")
                || filter.Text is null
                || filter.Text.Length is 0 or > 200
                || filter.Text.Any(char.IsControl))
            {
                return "A pull filter is an action of accept, ignore or reject and a short text.";
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9]{16,128}$")]
    private static partial Regex ManagementPassword();
}
