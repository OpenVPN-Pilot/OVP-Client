using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace OpenVpnPilot.Platform.Windows.InteractiveService;

/// <summary>
/// Speaks the OpenVPN interactive service startup protocol.
/// </summary>
/// <remarks>
/// The service starts openvpn under the system account, which is what lets a tunnel come up without
/// an elevation prompt. The startup message is three UTF-16 strings, each terminated by a NUL, and
/// it must arrive as a single write because the pipe runs in message mode.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InteractiveServicePipeClient
{
    /// <summary>
    /// The default service instance. A named instance uses the form openvpn$name\service.
    /// </summary>
    public const string DefaultPipeName = @"openvpn\service";

    private readonly string pipeName;

    public InteractiveServicePipeClient(string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
    }

    /// <summary>
    /// True when the service pipe can be opened, which means the interactive service is running.
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut);
            pipe.Connect(timeout: 250);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the service to start an OpenVPN process.
    /// </summary>
    /// <param name="workingDirectory">The directory the process starts in.</param>
    /// <param name="options">The command line, without the executable name.</param>
    /// <param name="standardInput">
    /// Text written to the process standard input. Carries the management password, so it must never
    /// be logged.
    /// </param>
    public async Task<InteractiveServiceReply> StartAsync(
        string workingDirectory,
        string options,
        string standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(standardInput);

        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout: 5000, cancellationToken);
        pipe.ReadMode = PipeTransmissionMode.Message;

        string message = workingDirectory + '\0' + options + '\0' + standardInput + '\0';
        await pipe.WriteAsync(Encoding.Unicode.GetBytes(message), cancellationToken);
        await pipe.FlushAsync(cancellationToken);

        byte[] buffer = new byte[4096];
        int read = await pipe.ReadAsync(buffer, cancellationToken);

        return ParseReply(Encoding.Unicode.GetString(buffer, 0, read));
    }

    /// <summary>
    /// The reply is UTF-16 and line feed separated: status, process id, then a literal description.
    /// </summary>
    internal static InteractiveServiceReply ParseReply(string raw)
    {
        string[] parts = raw.Split('\n');
        uint status = ParseHex(parts.ElementAtOrDefault(0));

        if (status != 0)
        {
            string detail = string.Join(
                " ",
                parts.Skip(1).Select(part => part.Trim()).Where(part => part.Length > 0));

            return InteractiveServiceReply.Refused(status, detail, raw);
        }

        return InteractiveServiceReply.Started((int)ParseHex(parts.ElementAtOrDefault(1)), raw);
    }

    private static uint ParseHex(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : 0;
    }
}

/// <summary>
/// What the interactive service answered.
/// </summary>
public sealed record InteractiveServiceReply
{
    private InteractiveServiceReply(bool started, int processId, uint status, string detail, string raw)
    {
        IsStarted = started;
        ProcessId = processId;
        Status = status;
        Detail = detail;
        Raw = raw;
    }

    public bool IsStarted { get; }

    public int ProcessId { get; }

    /// <summary>
    /// A Windows error code. Zero when the process started.
    /// </summary>
    public uint Status { get; }

    public string Detail { get; }

    /// <summary>
    /// The undecoded reply, kept for diagnostics.
    /// </summary>
    public string Raw { get; }

    public static InteractiveServiceReply Started(int processId, string raw) =>
        new(true, processId, 0, string.Empty, raw);

    public static InteractiveServiceReply Refused(uint status, string detail, string raw) =>
        new(false, 0, status, detail, raw);
}
