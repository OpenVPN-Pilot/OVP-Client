using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using OpenVpnPilot.Platform.MacOS.Helper.Dns;
using OpenVpnPilot.Platform.MacOS.Helper.Native;
using OpenVpnPilot.Platform.MacOS.Helper.Policy;
using OpenVpnPilot.Platform.MacOS.Helper.Security;
using OpenVpnPilot.Platform.MacOS.Helper.Tunnels;
using OpenVpnPilot.Platform.MacOS.Protocol;

namespace OpenVpnPilot.Platform.MacOS.Helper;

/// <summary>
/// Answers sessions on the helper socket and runs the tunnels they ask for.
/// </summary>
/// <remarks>
/// Every connection is a session, and every tunnel belongs to the session that started it. When the
/// session ends, for whatever reason, its tunnels are ended with it: an application that crashed can
/// no longer answer the credential prompts or the stop signal of its tunnels, and nobody else knows
/// their management passwords, so leaving them running would leave tunnels nobody can control.
///
/// Nothing a caller sends is trusted. The identity comes from the socket, every value of a request is
/// checked before it is used, the configuration is read and rewritten by the policy, and the command
/// line is built here. What the caller hears back is the decision and its reason; the details go to
/// the helper's own log.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class HelperServer
{
    public const int MaximumSessions = 64;

    public const int MaximumSessionsPerAccount = 16;

    /// <summary>
    /// The largest configuration accepted, far above any real one.
    /// </summary>
    public const int MaximumConfigurationLength = 512 * 1024;

    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ListeningTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the last words of a process that ended are waited for before it is explained without them.
    /// </summary>
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionEndGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumTerminateGrace = TimeSpan.FromSeconds(30);

    private readonly HelperOptions options;
    private readonly HelperLog log;
    private readonly IGroupMembership membership;
    private readonly TunnelRegistry tunnels = new();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, uint> sessions = [];
    private DateTimeOffset idleSince = DateTimeOffset.UtcNow;
    private string? openVpnVersion;

    public HelperServer(HelperOptions options, HelperLog log, IGroupMembership membership)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(membership);

        this.options = options;
        this.log = log;
        this.membership = membership;
    }

    private static string Version =>
        typeof(HelperServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(HelperServer).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    /// <summary>
    /// Accepts sessions until the helper has been idle for the given time, or forever when null.
    /// </summary>
    public async Task RunAsync(Socket listener, TimeSpan? idleExit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);

        PrepareRuntimeDirectory();
        DnsHook.RestoreLeftovers(log, options.DnsScriptPath, options.DnsStateDirectory, tunnels.ProcessIds());

        // Read once: the build only changes with a new package, and installing one restarts the helper.
        openVpnVersion = ProbeOpenVpnVersion();

        log.Write($"helper {Version} listening, OpenVPN {openVpnVersion ?? "missing"}");

        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (idleExit is { } idle)
        {
            _ = Task.Run(() => ExitWhenIdleAsync(idle, stopping), CancellationToken.None);
        }

        while (!stopping.IsCancellationRequested)
        {
            Socket client;

            try
            {
                client = await listener.AcceptAsync(stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(client, stopping.Token), CancellationToken.None);
        }

        log.Write("helper stopping");
    }

    private async Task ExitWhenIdleAsync(TimeSpan idle, CancellationTokenSource stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);

            lock (gate)
            {
                if (sessions.Count > 0 || tunnels.Count > 0)
                {
                    idleSince = DateTimeOffset.UtcNow;
                    continue;
                }

                if (DateTimeOffset.UtcNow - idleSince < idle)
                {
                    continue;
                }
            }

            // launchd keeps the socket and starts the helper again on the next connection.
            await stopping.CancelAsync();
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        Guid sessionId = Guid.NewGuid();
        Caller? caller = null;

        try
        {
            caller = Caller.Identify(client, membership);

            if (!TryOpenSession(sessionId, caller.UserId))
            {
                log.Write($"session refused for uid {caller.UserId}: too many sessions");
                return;
            }

            await using NetworkStream stream = new(client, ownsSocket: false);

            if (!await GreetAsync(stream, caller, cancellationToken))
            {
                return;
            }

            log.Write($"session {sessionId:N} opened by uid {caller.UserId} pid {caller.ProcessId}, {caller.Describe()}");

            while (!cancellationToken.IsCancellationRequested)
            {
                HelperRequest? request = await HelperFraming.ReadRequestAsync(stream, cancellationToken);

                if (request is null)
                {
                    break;
                }

                HelperResponse response;

                try
                {
                    response = await AnswerAsync(request, caller, sessionId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Deliberately everything below cancellation. A request that goes unanswered
                    // leaves the caller waiting for a tunnel that was never started, and a service
                    // that stops talking without saying why cannot be diagnosed from the outside.
                    log.Write($"session {sessionId:N} could not answer {request.Type}: {exception}");

                    response = Refuse(
                        HelperRefusal.Failed,
                        $"The helper could not carry out the request: {exception.Message}");
                }

                await HelperFraming.WriteResponseAsync(stream, response, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or HelperProtocolException
            or OperationCanceledException or InvalidOperationException)
        {
            log.Write($"session {sessionId:N} ended: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            client.Dispose();
            CloseSession(sessionId, caller);
        }
    }

    private bool TryOpenSession(Guid sessionId, uint userId)
    {
        lock (gate)
        {
            if (sessions.Count >= MaximumSessions
                || sessions.Values.Count(owner => owner == userId) >= MaximumSessionsPerAccount)
            {
                return false;
            }

            sessions[sessionId] = userId;
            return true;
        }
    }

    /// <summary>
    /// Ends the session's tunnels. The application that owned them can no longer control them.
    /// </summary>
    private void CloseSession(Guid sessionId, Caller? caller)
    {
        lock (gate)
        {
            if (!sessions.Remove(sessionId))
            {
                return;
            }

            idleSince = DateTimeOffset.UtcNow;
        }

        IReadOnlyList<Tunnel> orphaned = tunnels.InSession(sessionId);

        if (orphaned.Count > 0)
        {
            log.Write($"session {sessionId:N} of uid {caller?.UserId} closed with {orphaned.Count} tunnel(s), which are ended");
        }

        foreach (Tunnel tunnel in orphaned)
        {
            _ = tunnel.Process.TerminateAsync(SessionEndGrace);
        }
    }

    private async Task<bool> GreetAsync(NetworkStream stream, Caller caller, CancellationToken cancellationToken)
    {
        using CancellationTokenSource hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hello.CancelAfter(HelloTimeout);

        HelperRequest? request = await HelperFraming.ReadRequestAsync(stream, hello.Token);

        if (request?.Type != HelperMessageType.Hello)
        {
            await HelperFraming.WriteResponseAsync(stream, Refuse(HelperRefusal.Protocol, "A session begins with hello."), cancellationToken);
            return false;
        }

        if (request.ProtocolVersion != HelperProtocol.Version)
        {
            await HelperFraming.WriteResponseAsync(
                stream,
                new HelperResponse
                {
                    Type = HelperMessageType.Refused,
                    ProtocolVersion = HelperProtocol.Version,
                    HelperVersion = Version,
                    RefusalCode = HelperRefusal.Version,
                    Message = $"The helper speaks protocol {HelperProtocol.Version} and the caller {request.ProtocolVersion}. "
                        + "Install the helper package that belongs to this version of the application.",
                },
                cancellationToken);

            return false;
        }

        await HelperFraming.WriteResponseAsync(
            stream,
            new HelperResponse
            {
                Type = HelperMessageType.Hello,
                ProtocolVersion = HelperProtocol.Version,
                HelperVersion = Version,
                OpenVpnPath = options.OpenVpnPath,
                OpenVpnVersion = openVpnVersion,
                Authorised = caller.IsAuthorised,
                Authorisation = caller.Describe(),
            },
            cancellationToken);

        return true;
    }

    private async Task<HelperResponse> AnswerAsync(
        HelperRequest request,
        Caller caller,
        Guid sessionId,
        CancellationToken cancellationToken) => request.Type switch
        {
            HelperMessageType.Launch when request.Launch is { } launch => await LaunchAsync(launch, caller, sessionId, cancellationToken),
            HelperMessageType.Launch => Refuse(HelperRefusal.Protocol, "A launch request carries what to launch in its 'launch' member."),
            HelperMessageType.Terminate => await TerminateAsync(request, caller, cancellationToken),
            HelperMessageType.List => List(caller, sessionId),
            _ => Refuse(HelperRefusal.Protocol, $"'{request.Type}' is not a request the helper answers."),
        };

    private async Task<HelperResponse> LaunchAsync(
        LaunchSpecification specification,
        Caller caller,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (CommandLine.Validate(specification) is { } invalid)
        {
            return Refuse(HelperRefusal.Protocol, invalid);
        }

        // A password is there because the validation above refuses a launch without one; a member
        // of a message can always be absent, so the one value that must be present is taken here.
        string managementPassword = specification.ManagementPassword!;

        if (specification.Configuration is not null && !caller.IsAuthorised)
        {
            log.Write($"launch refused for uid {caller.UserId}: not authorised for its own configuration");

            return Refuse(
                HelperRefusal.NotAuthorised,
                $"This account may start only configurations an administrator installed under {HelperInstallation.ConfigurationsDirectory}. "
                + $"Administrators and members of the group '{HelperInstallation.AuthorisedGroup}' may start their own.");
        }

        string configuration;

        try
        {
            configuration = specification.Configuration ?? ReadInstalledConfiguration(specification.InstalledConfiguration!);
        }
        catch (ConfigurationRefusedException exception)
        {
            return Refuse(HelperRefusal.Configuration, exception.Message);
        }

        if (configuration.Length > MaximumConfigurationLength)
        {
            return Refuse(HelperRefusal.Configuration, "The configuration is larger than the helper accepts.");
        }

        string copy;

        try
        {
            // Root may already run anything it likes, so only everyone else is held to the policy.
            copy = caller.UserId == 0 ? configuration : ConfigurationPolicy.Check(configuration);
        }
        catch (ConfigurationRefusedException exception)
        {
            log.Write($"launch refused for uid {caller.UserId}: {exception.Message}");
            return Refuse(HelperRefusal.Configuration, exception.Message);
        }

        if (tunnels.CheckRoom(caller.UserId) is { } full)
        {
            return Refuse(HelperRefusal.Limit, full);
        }

        if (!File.Exists(options.OpenVpnPath))
        {
            return Refuse(HelperRefusal.LaunchFailed, $"The OpenVPN the helper runs is missing at {options.OpenVpnPath}.");
        }

        return await StartAsync(specification, copy, managementPassword, caller, sessionId, cancellationToken);
    }

    private async Task<HelperResponse> StartAsync(
        LaunchSpecification specification,
        string configuration,
        string managementPassword,
        Caller caller,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(options.RuntimeDirectory, caller.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"));
        string temporary = Path.Combine(directory, "tmp");
        string configurationPath = Path.Combine(directory, "config.ovpn");

        CreatePrivateDirectory(Path.GetDirectoryName(directory)!);
        CreatePrivateDirectory(directory);
        CreatePrivateDirectory(temporary);

        FileStreamOptions privateFile = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        await using (FileStream stream = new(configurationPath, privateFile))
        {
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(configuration), cancellationToken);
        }

        TaskCompletionSource<bool> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SpawnedProcess process;

        try
        {
            process = SpawnedProcess.Start(
                options.OpenVpnPath,
                CommandLine.Build(configurationPath, directory, temporary, specification),
                ["PATH=/usr/bin:/bin:/usr/sbin:/sbin"],
                managementPassword);
        }
        catch (IOException exception)
        {
            DeleteDirectory(directory);
            log.Write($"launch failed for uid {caller.UserId}: {exception.Message}");
            return Refuse(HelperRefusal.LaunchFailed, exception.Message);
        }

        process.OutputReceived += (_, line) =>
        {
            if (line.Contains("MANAGEMENT: TCP Socket listening", StringComparison.Ordinal))
            {
                listening.TrySetResult(true);
            }
        };

        Tunnel tunnel = new(process, caller.UserId, sessionId, directory, DateTimeOffset.UtcNow);
        tunnels.Add(tunnel);

        _ = Task.Run(() => RetireWhenEndedAsync(tunnel), CancellationToken.None);

        log.Write($"openvpn {process.ProcessId} started for uid {caller.UserId} in session {sessionId:N}");

        // Answered once OpenVPN is listening for the client, so a configuration it refuses is
        // reported with OpenVPN's own words rather than as a management port that never answers.
        Task finished = await Task.WhenAny(listening.Task, process.Exited, Task.Delay(ListeningTimeout, cancellationToken));

        if (finished == process.Exited)
        {
            // What it said last explains why it went, and can still be in the pipe when it is gone.
            _ = await Task.WhenAny(process.OutputDrained, Task.Delay(OutputDrainGrace, CancellationToken.None));

            string reason = string.Join(" ", process.RecentOutput.TakeLast(3));

            return Refuse(
                HelperRefusal.LaunchFailed,
                reason.Length > 0 ? $"OpenVPN ended at once: {reason}" : "OpenVPN ended at once.");
        }

        return new HelperResponse { Type = HelperMessageType.Launched, ProcessId = process.ProcessId };
    }

    /// <summary>
    /// Clears up after a tunnel, however it ended.
    /// </summary>
    private async Task RetireWhenEndedAsync(Tunnel tunnel)
    {
        int status = await tunnel.Process.Exited;

        tunnels.Remove(tunnel);
        DeleteDirectory(tunnel.Directory);

        string how = Libc.Exited(status)
            ? $"exited with {Libc.ExitCode(status)}"
            : $"ended by signal {Libc.TerminatingSignal(status)}";

        log.Write($"openvpn {tunnel.Process.ProcessId} {how}");

        // A process that ended by itself has already removed its name servers. One that was killed
        // could not, and the record its hook left is what puts them back.
        DnsHook.RestoreAfter(log, options.DnsScriptPath, options.DnsStateDirectory, tunnel.Process.ProcessId);
    }

    private async Task<HelperResponse> TerminateAsync(HelperRequest request, Caller caller, CancellationToken cancellationToken)
    {
        Tunnel? tunnel = tunnels.Find(request.ProcessId, caller.UserId);

        if (tunnel is null)
        {
            // Already ended, or never this caller's: in both cases there is nothing it may end.
            return new HelperResponse { Type = HelperMessageType.Terminated, ProcessId = request.ProcessId };
        }

        TimeSpan grace = TimeSpan.FromMilliseconds(Math.Clamp(request.GraceMilliseconds, 0, (int)MaximumTerminateGrace.TotalMilliseconds));

        try
        {
            await tunnel.Process.Exited.WaitAsync(grace, cancellationToken);
        }
        catch (TimeoutException)
        {
            log.Write($"openvpn {tunnel.Process.ProcessId} ignored the stop signal and is ended for uid {caller.UserId}");
            await tunnel.Process.TerminateAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return new HelperResponse { Type = HelperMessageType.Terminated, ProcessId = request.ProcessId };
    }

    private HelperResponse List(Caller caller, Guid sessionId) => new()
    {
        Type = HelperMessageType.Tunnels,
        Tunnels = [.. tunnels.OwnedBy(caller.UserId)
            .Select(tunnel => new TunnelDescription(tunnel.Process.ProcessId, tunnel.StartedAt, tunnel.SessionId == sessionId))],
    };

    /// <summary>
    /// Reads a configuration an administrator installed, after checking that nobody else could have
    /// put it there.
    /// </summary>
    private string ReadInstalledConfiguration(string name)
    {
        if (name.Length == 0 || name.StartsWith('.') || name.Contains('/', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ConfigurationRefusedException("An installed configuration is named by its file name alone.");
        }

        string path = Path.Combine(options.ConfigurationsDirectory, name);
        FileInfo file = new(path);

        if (!file.Exists || file.LinkTarget is not null)
        {
            throw new ConfigurationRefusedException($"There is no installed configuration called {name}.");
        }

        // Owned by root and not writable by anyone else, so an administrator is the only one who
        // could have written it; the directory is checked when the package installs it.
        if (options.RequireRootOwnership
            && ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
                || !OwnedByRoot(path)))
        {
            throw new ConfigurationRefusedException($"{name} is writable by an account other than root, so it is not started.");
        }

        return File.ReadAllText(path);
    }

    private static unsafe bool OwnedByRoot(string path)
    {
        // The 64 bit inode struct stat: st_dev (4), st_mode (2), st_nlink (2), st_ino (8), then st_uid
        // at offset 16. On Intel the plain lstat still fills the old layout, so the INODE64 variant is
        // called there; on Apple silicon the plain name is the only one and already uses this layout.
        byte* buffer = stackalloc byte[256];

        int result = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X64
                ? FileStatus.lstat64(path, buffer)
                : FileStatus.lstat(path, buffer);

        return result == 0 && *(uint*)(buffer + 16) == 0;
    }

    private void PrepareRuntimeDirectory()
    {
        CreatePrivateDirectory(options.RuntimeDirectory);

        // A configuration left behind by a helper that ended abruptly still holds a private key. No
        // tunnel is running when the helper starts, so none of them is in use.
        foreach (string account in Directory.EnumerateDirectories(options.RuntimeDirectory))
        {
            if (Path.GetFileName(account) == Path.GetFileName(options.DnsStateDirectory))
            {
                continue;
            }

            DeleteDirectory(account);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException exception)
        {
            log.Write($"{path} could not be removed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            log.Write($"{path} could not be removed: {exception.Message}");
        }
    }

    /// <summary>
    /// The version the OpenVPN build reports, read from its first line of --version output.
    /// </summary>
    private string? ProbeOpenVpnVersion()
    {
        if (!File.Exists(options.OpenVpnPath))
        {
            return null;
        }

        try
        {
            SpawnedProcess probe = SpawnedProcess.Start(options.OpenVpnPath, ["--version"], ["PATH=/usr/bin:/bin:/usr/sbin:/sbin"], string.Empty);

            if (!probe.Exited.Wait(TimeSpan.FromSeconds(5)))
            {
                probe.Signal(Libc.SigKill);
                return null;
            }

            // The version is printed and the process ends; the line can still be in the pipe.
            _ = probe.OutputDrained.Wait(TimeSpan.FromSeconds(2));

            // "OpenVPN 2.7.7 aarch64-apple-darwin25.6.0 [SSL (OpenSSL)] ..."
            string[] words = probe.RecentOutput.FirstOrDefault(line => line.StartsWith("OpenVPN ", StringComparison.Ordinal))
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

            return words.Length > 1 ? words[1] : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static HelperResponse Refuse(string code, string message) => new()
    {
        Type = HelperMessageType.Refused,
        RefusalCode = code,
        Message = message,
    };
}

/// <summary>
/// Where the helper finds what it runs. Fixed in an installation; set on the command line only for
/// tests, and the command line of the installed helper is written by the root owned job definition.
/// </summary>
internal sealed record HelperOptions(
    string OpenVpnPath,
    string DnsScriptPath,
    string RuntimeDirectory,
    string ConfigurationsDirectory,
    bool RequireRootOwnership)
{
    public string DnsStateDirectory => Path.Combine(RuntimeDirectory, "dns");

    public static HelperOptions Installed { get; } = new(
        HelperInstallation.OpenVpnPath,
        HelperInstallation.DnsScriptPath,
        HelperInstallation.RuntimeDirectory,
        HelperInstallation.ConfigurationsDirectory,
        RequireRootOwnership: true);
}

internal static unsafe partial class FileStatus
{
    [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lstat(string path, byte* buffer);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lstat64(string path, byte* buffer);
}
