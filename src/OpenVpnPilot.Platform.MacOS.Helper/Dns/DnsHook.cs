using System.Collections;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenVpnPilot.Platform.MacOS.Helper.Native;
using OpenVpnPilot.Platform.MacOS.Helper.Tunnels;

namespace OpenVpnPilot.Platform.MacOS.Helper.Dns;

/// <summary>
/// Applies and removes the name servers a tunnel brings, and remembers enough to remove them again
/// when the tunnel could not.
/// </summary>
/// <remarks>
/// OpenVPN 2.7 applies name servers on macOS with its own dns-updown script, and the OpenVPN the
/// helper package builds runs this helper in its place, as the default command, without any script
/// security beyond the built in level. The helper then runs OpenVPN's script itself, with three
/// differences that are the reason it stands in between.
///
/// The environment is rebuilt from nothing. OpenVPN hands its script the environment a configuration
/// can add to, and the script is run by bash as root and calls several tools without a path, so a
/// variable such as PATH or BASH_ENV would decide what runs as root. The script gets a fixed PATH and
/// the DNS variables, and nothing else.
///
/// The values are checked. An address has to be an address and a domain a domain, before either
/// reaches a script that pastes them into commands for the system configuration daemon.
///
/// What was applied is written down before it is applied. The script keeps the previous settings in
/// the system configuration store and restores them when OpenVPN tells it the tunnel is going down,
/// which OpenVPN cannot do when it is killed. With the record, the helper runs the same removal
/// itself once the process is gone, so a tunnel that ends by force does not take the machine's name
/// servers with it. Measured on macOS: the script replaces the primary service's DNS for a tunnel
/// that redirects all traffic and keeps the original under the tunnel device's own key.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class DnsHook
{
    private const string UpScript = DnsVariables.UpScript;
    private const string DownScript = DnsVariables.DownScript;

    /// <summary>
    /// True when OpenVPN started this process as its DNS command rather than launchd as the helper.
    /// </summary>
    public static bool IsInvokedAsHook(IReadOnlyList<string> arguments) =>
        arguments.Count == 0
        && Environment.GetEnvironmentVariable("script_type") is UpScript or DownScript;

    /// <summary>
    /// The hook itself: record, then run OpenVPN's script with a clean environment.
    /// </summary>
    /// <returns>The script's exit code, which OpenVPN reports.</returns>
    public static int Run(HelperLog log, string script, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(log);

        Dictionary<string, string> environment = [];

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        if (!DnsVariables.TryCollect(environment, out Dictionary<string, string> variables, out string? problem))
        {
            log.Write($"dns hook refused its input: {problem}");
            return 1;
        }

        string device = variables["dev"];
        bool up = variables["script_type"] == UpScript;

        if (up)
        {
            Save(stateDirectory, new DnsState(device, Libc.getppid(), DateTimeOffset.UtcNow, variables));
        }

        int exitCode = RunScript(log, script, variables);

        if (!up)
        {
            Forget(stateDirectory, device);
        }

        log.Write($"dns {(up ? "up" : "down")} for {device} finished with {exitCode}");
        return exitCode;
    }

    /// <summary>
    /// Removes what a tunnel applied, when the tunnel's own process could not.
    /// </summary>
    /// <param name="processId">The OpenVPN process that ended.</param>
    public static void RestoreAfter(HelperLog log, string script, string stateDirectory, int processId)
    {
        foreach ((string path, DnsState state) in States(stateDirectory))
        {
            if (state.ProcessId == processId)
            {
                Restore(log, script, path, state);
            }
        }
    }

    /// <summary>
    /// Removes what tunnels that are no longer running left behind, for example across a crash of
    /// the helper itself.
    /// </summary>
    /// <remarks>
    /// A record older than the last boot describes settings the restart already discarded: the store
    /// the script writes to lives in memory. Replaying it would only rewrite settings that are now
    /// correct, so such a record is dropped instead.
    /// </remarks>
    public static void RestoreLeftovers(HelperLog log, string script, string stateDirectory, IReadOnlySet<int> running)
    {
        DateTimeOffset? booted = BootTime();

        foreach ((string path, DnsState state) in States(stateDirectory))
        {
            if (running.Contains(state.ProcessId))
            {
                continue;
            }

            if (booted is { } boot && state.AppliedAt < boot)
            {
                log.Write($"dns record for {state.Device} predates the last boot and is dropped");
                TryDelete(path);
                continue;
            }

            Restore(log, script, path, state);
        }
    }

    private static int RunScript(HelperLog log, string script, Dictionary<string, string> variables)
    {
        List<string> environment = ["PATH=/usr/bin:/bin:/usr/sbin:/sbin"];
        environment.AddRange(variables.Select(pair => $"{pair.Key}={pair.Value}"));

        SpawnedProcess process = SpawnedProcess.Start(script, [], environment, secret: string.Empty);
        int status = process.Exited.GetAwaiter().GetResult();

        foreach (string line in process.RecentOutput)
        {
            log.Write($"dns script: {line}");
        }

        return Libc.Exited(status) ? Libc.ExitCode(status) : 128 + Libc.TerminatingSignal(status);
    }

    private static void Restore(HelperLog log, string script, string path, DnsState state)
    {
        Dictionary<string, string> variables = new(state.Variables, StringComparer.Ordinal)
        {
            ["script_type"] = DownScript,
            ["dev"] = state.Device,
        };

        log.Write($"dns for {state.Device} is being restored after OpenVPN process {state.ProcessId} ended without doing it");

        int exitCode = RunScript(log, script, variables);
        TryDelete(path);

        log.Write($"dns restore for {state.Device} finished with {exitCode}");
    }

    private static void Save(string directory, DnsState state)
    {
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string path = Path.Combine(directory, state.Device + ".json");
        string temporary = path + ".tmp";

        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        using (FileStream stream = new(temporary, options))
        {
            JsonSerializer.Serialize(stream, state, DnsJsonContext.Default.DnsState);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static void Forget(string directory, string device) =>
        TryDelete(Path.Combine(directory, device + ".json"));

    private static IEnumerable<(string Path, DnsState State)> States(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            DnsState? state = null;

            try
            {
                state = JsonSerializer.Deserialize(File.ReadAllBytes(path), DnsJsonContext.Default.DnsState);
            }
            catch (JsonException)
            {
                // A record that cannot be read cannot be replayed either; it is removed below.
            }
            catch (IOException)
            {
                continue;
            }

            if (state is null || !DnsVariables.IsDeviceName(state.Device))
            {
                TryDelete(path);
                continue;
            }

            yield return (path, state);
        }
    }

    private static unsafe DateTimeOffset? BootTime()
    {
        // struct timeval { time_t tv_sec; suseconds_t tv_usec; } padded to sixteen bytes.
        long* value = stackalloc long[2];
        nuint length = 16;

        return SystemControl.sysctlbyname("kern.boottime", value, &length, null, 0) == 0
            ? DateTimeOffset.FromUnixTimeSeconds(value[0])
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Tried again on the next restore.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as above.
        }
    }

}

/// <summary>
/// The variables OpenVPN's DNS script reads, and what counts as a valid value for each.
/// </summary>
/// <remarks>
/// Separate from the hook that passes them on, because this is where a pushed value from a server
/// is judged and that judgement is worth testing on its own. An address has to be an address and a
/// domain a domain: the script pastes them into commands for the system configuration daemon, and
/// it runs as root.
/// </remarks>
internal static partial class DnsVariables
{
    public const string UpScript = "dns-up";

    public const string DownScript = "dns-down";

    /// <summary>
    /// Keeps the variables OpenVPN's script reads, after checking every value.
    /// </summary>
    internal static bool TryCollect(
        IReadOnlyDictionary<string, string> environment,
        out Dictionary<string, string> variables,
        out string? problem)
    {
        variables = new Dictionary<string, string>(StringComparer.Ordinal);
        problem = null;

        if (environment.ContainsKey("dns_vars_file"))
        {
            // Only used when OpenVPN has dropped its privileges, which the helper never lets it do.
            problem = "dns_vars_file is set";
            return false;
        }

        if (!environment.TryGetValue("script_type", out string? scriptType) || scriptType is not (UpScript or DownScript))
        {
            problem = "script_type is not dns-up or dns-down";
            return false;
        }

        if (!environment.TryGetValue("dev", out string? device) || !DeviceName().IsMatch(device))
        {
            problem = "dev does not name a tunnel device";
            return false;
        }

        variables["script_type"] = scriptType;
        variables["dev"] = device;

        foreach ((string name, string value) in environment)
        {
            if (!name.StartsWith("dns_", StringComparison.Ordinal))
            {
                continue;
            }

            string? accepted = Check(name, value);

            if (accepted is null)
            {
                problem = $"{name} does not hold a valid value";
                return false;
            }

            variables[name] = accepted;
        }

        return true;
    }

    /// <summary>
    /// The value to pass on, or null when the variable or its value is not acceptable.
    /// </summary>
    private static string? Check(string name, string value)
    {
        if (SearchDomain().IsMatch(name) || DomainList().IsMatch(name) || ServerName().IsMatch(name))
        {
            return Domain().IsMatch(value) && value.Length <= 253 ? value : null;
        }

        if (ServerAddress().IsMatch(name))
        {
            return IPAddress.TryParse(value, out IPAddress? address) ? address.ToString() : null;
        }

        if (ServerPort().IsMatch(name))
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
                ? port.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        if (ServerSetting().Match(name) is { Success: true } setting)
        {
            return (setting.Groups[1].Value, value) switch
            {
                ("dnssec", "yes" or "no" or "optional") => value,
                ("transport", "plain" or "DoH" or "DoT") => value,
                _ => null,
            };
        }

        return null;
    }

    [GeneratedRegex("^(utun|tun|tap)[0-9]{1,4}$")]
    private static partial Regex DeviceName();

    [GeneratedRegex("^dns_search_domain_[0-9]{1,3}$")]
    private static partial Regex SearchDomain();

    [GeneratedRegex("^dns_server_[0-9]{1,3}_(resolve|exclude)_domain_[0-9]{1,3}$")]
    private static partial Regex DomainList();

    [GeneratedRegex("^dns_server_[0-9]{1,3}_sni$")]
    private static partial Regex ServerName();

    [GeneratedRegex("^dns_server_[0-9]{1,3}_address_[0-9]{1,3}$")]
    private static partial Regex ServerAddress();

    [GeneratedRegex("^dns_server_[0-9]{1,3}_port_[0-9]{1,3}$")]
    private static partial Regex ServerPort();

    [GeneratedRegex("^dns_server_[0-9]{1,3}_(dnssec|transport)$")]
    private static partial Regex ServerSetting();

    /// <summary>
    /// A host or domain name: letters, digits and inner dashes per label, an optional final dot.
    /// </summary>
    [GeneratedRegex("^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)*[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.?$")]
    private static partial Regex Domain();

    public static bool IsDeviceName(string value) => DeviceName().IsMatch(value);
}

/// <summary>
/// What a tunnel applied, as the helper recorded it.
/// </summary>
internal sealed record DnsState(
    string Device,
    int ProcessId,
    DateTimeOffset AppliedAt,
    IReadOnlyDictionary<string, string> Variables);

[JsonSerializable(typeof(DnsState))]
internal sealed partial class DnsJsonContext : JsonSerializerContext
{
}

internal static unsafe partial class SystemControl
{
    [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    public static partial int sysctlbyname(string name, void* value, nuint* length, void* newValue, nuint newLength);
}
