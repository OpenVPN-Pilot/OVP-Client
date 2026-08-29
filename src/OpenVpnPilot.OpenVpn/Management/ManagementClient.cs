using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenVpnPilot.OpenVpn.Management;

/// <summary>
/// A client for the OpenVPN management interface.
/// </summary>
/// <remarks>
/// Two behaviours of the interface shape this implementation, both established by measurement:
/// commands must be issued strictly one at a time because OpenVPN silently discards commands that
/// arrive while it is still answering a previous one, and the initial password prompt arrives
/// without a trailing newline so it cannot be read with line based reading.
///
/// A command is answered by the read loop, so a command registered after that loop has ended would
/// wait for a reply nobody is left to send. The end of the loop is therefore recorded and a command
/// issued afterwards fails immediately.
/// </remarks>
public sealed class ManagementClient : IAsyncDisposable
{
    private const string PasswordPrompt = "ENTER PASSWORD:";
    private const string PasswordAccepted = "SUCCESS: password is correct";

    /// <summary>
    /// Reported whenever the channel is gone, so a caller has one condition to handle rather than
    /// one per way of finding out.
    /// </summary>
    internal const string ClosedMessage = "The management connection closed.";

    /// <summary>
    /// OpenVPN reads a management password of at most this length from a single line.
    /// </summary>
    private const int MaximumSingleLinePasswordLength = 256;

    private readonly Stream stream;
    private readonly ILogger<ManagementClient> logger;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly Channel<ManagementMessage> notifications =
        Channel.CreateUnbounded<ManagementMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource lifetime = new();
    private readonly StringBuilder pending = new();

    /// <summary>
    /// Guards the handover between registering a command and the read loop ending, so a command can
    /// never be registered into a client that has nothing left to answer it.
    /// </summary>
    private readonly Lock closureGate = new();

    /// <summary>
    /// Set once the read loop has ended, which is the only sign that the far end has gone.
    /// </summary>
    private Exception? closure;

    private TaskCompletionSource<CommandResult>? inFlight;
    private List<string>? inFlightLines;
    private CommandCompletion inFlightCompletion;
    private TaskCompletionSource<bool>? authentication;
    private Task? readLoop;
    private bool disposed;

    public ManagementClient(Stream stream, ILogger<ManagementClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        this.stream = stream;
        this.logger = logger ?? NullLogger<ManagementClient>.Instance;
    }

    /// <summary>
    /// Asynchronous notifications from OpenVPN, in arrival order.
    /// </summary>
    public ChannelReader<ManagementMessage> Notifications => notifications.Reader;

    /// <summary>
    /// Starts the read loop and completes the password handshake.
    /// </summary>
    /// <param name="password">
    /// The password passed to OpenVPN on startup. Pass null when the interface has none.
    /// </param>
    public async Task StartAsync(string? password, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (password is { Length: > MaximumSingleLinePasswordLength })
        {
            throw new ArgumentException(
                $"The management password must not exceed {MaximumSingleLinePasswordLength} characters.",
                nameof(password));
        }

        authentication = password is null
            ? null
            : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        readLoop = Task.Run(() => ReadLoopAsync(password, lifetime.Token), CancellationToken.None);

        if (authentication is null)
        {
            return;
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            authentication);

        await authentication.Task;
    }

    /// <summary>
    /// Sends a command and waits for its complete response.
    /// </summary>
    public async Task<CommandResult> SendAsync(
        string command,
        CommandCompletion completion = CommandCompletion.FirstTerminalLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(disposed, this);

        await commandGate.WaitAsync(cancellationToken);
        try
        {
            TaskCompletionSource<CommandResult> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (closureGate)
            {
                // Registering and closing are ordered against each other here. Either the command is
                // registered first and the closing read loop fails it, or the closure is recorded
                // first and the command never waits at all.
                if (closure is not null)
                {
                    throw new InvalidOperationException(ClosedMessage, closure);
                }

                inFlight = completionSource;
                inFlightLines = [];
                inFlightCompletion = completion;
            }

            ManagementClientLog.CommandSent(logger, command);

            try
            {
                await WriteLineAsync(command, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A socket that refuses the write is the same condition as one that stopped reading.
                throw new InvalidOperationException(ClosedMessage, exception);
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<CommandResult>)state!).TrySetCanceled(),
                completionSource);

            return await completionSource.Task;
        }
        finally
        {
            inFlight = null;
            inFlightLines = null;
            commandGate.Release();
        }
    }

    /// <summary>
    /// Enables the notification stream this client relies on. Hold is not released here, because
    /// OpenVPN ignores a release that arrives before it reports being held.
    /// </summary>
    public async Task OpenSessionAsync(int byteCountIntervalSeconds = 1, CancellationToken cancellationToken = default)
    {
        await SendAsync("version 6", cancellationToken: cancellationToken);
        await SendAsync("state on", cancellationToken: cancellationToken);
        await SendAsync($"bytecount {byteCountIntervalSeconds}", cancellationToken: cancellationToken);

        // "log on all" replays the history and therefore terminates with END rather than SUCCESS.
        await SendAsync("log on", cancellationToken: cancellationToken);
    }

    public Task<CommandResult> ReleaseHoldAsync(CancellationToken cancellationToken = default) =>
        SendAsync("hold release", cancellationToken: cancellationToken);

    public Task<CommandResult> SignalAsync(string signal, CancellationToken cancellationToken = default) =>
        SendAsync($"signal {signal}", cancellationToken: cancellationToken);

    /// <summary>
    /// Answers a credential request. Values are never logged.
    /// </summary>
    public async Task SendCredentialsAsync(
        string realm,
        string? username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(realm);
        ArgumentNullException.ThrowIfNull(password);

        string quotedRealm = Quote(realm);

        if (username is not null)
        {
            await SendAsync($"username {quotedRealm} {Quote(username)}", cancellationToken: cancellationToken);
        }

        await SendAsync($"password {quotedRealm} {Quote(password)}", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Escapes a value for the management interface, which delimits arguments with double quotes.
    /// </summary>
    internal static string Quote(string value)
    {
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');

        foreach (char character in value)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.Append('"').ToString();
    }

    private async Task ReadLoopAsync(string? password, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                await DrainAsync(password, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown is expected and needs no special handling.
        }
        catch (IOException exception)
        {
            ManagementClientLog.ConnectionClosed(logger, exception);
        }
        finally
        {
            InvalidOperationException ended = new(ClosedMessage);

            lock (closureGate)
            {
                closure = ended;
                FailPendingWork(ended);
            }

            notifications.Writer.TryComplete();
        }
    }

    private async Task DrainAsync(string? password, CancellationToken cancellationToken)
    {
        while (true)
        {
            string text = pending.ToString();

            // The prompt has no trailing newline, so it must be matched on the raw buffer.
            if (password is not null
                && authentication is { Task.IsCompleted: false }
                && text.EndsWith(PasswordPrompt, StringComparison.Ordinal))
            {
                pending.Clear();
                await WriteLineAsync(password, cancellationToken);
                continue;
            }

            int index = text.IndexOf('\n', StringComparison.Ordinal);
            if (index < 0)
            {
                return;
            }

            string line = text[..index].TrimEnd('\r');
            pending.Remove(0, index + 1);

            if (line.Length > 0)
            {
                await HandleLineAsync(line);
            }
        }
    }

    private async Task HandleLineAsync(string line)
    {
        ManagementMessage message = ManagementMessageParser.Parse(line);

        if (authentication is { Task.IsCompleted: false })
        {
            if (line.StartsWith(PasswordAccepted, StringComparison.Ordinal))
            {
                authentication.TrySetResult(true);
                return;
            }

            if (message is CommandResponseMessage { Kind: CommandResponseKind.Error } failure)
            {
                authentication.TrySetException(
                    new InvalidOperationException($"Management authentication failed: {failure.Text}"));
                return;
            }
        }

        if (message is CommandResponseMessage response)
        {
            CompleteCommand(response);
            return;
        }

        await notifications.Writer.WriteAsync(message);
    }

    private void CompleteCommand(CommandResponseMessage response)
    {
        if (inFlight is null || inFlightLines is null)
        {
            ManagementClientLog.UnsolicitedResponse(logger, response.RawLine);
            return;
        }

        inFlightLines.Add(response.Text);

        bool finished = inFlightCompletion == CommandCompletion.EndMarker
            ? response.Kind is CommandResponseKind.End or CommandResponseKind.Error
            : response.IsTerminal;

        if (!finished)
        {
            return;
        }

        CommandResult result = new(
            Succeeded: response.Kind != CommandResponseKind.Error,
            Text: response.Text,
            Lines: inFlightLines);

        inFlight.TrySetResult(result);
    }

    private void FailPendingWork(Exception exception)
    {
        authentication?.TrySetException(exception);
        inFlight?.TrySetException(exception);
    }

    private async Task WriteLineAsync(string text, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync();

        if (readLoop is not null)
        {
            try
            {
                await readLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        lifetime.Dispose();
        commandGate.Dispose();
        await stream.DisposeAsync();
    }
}

/// <summary>
/// The complete response to one command.
/// </summary>
public sealed record CommandResult(bool Succeeded, string Text, IReadOnlyList<string> Lines);

public enum CommandCompletion
{
    /// <summary>
    /// The response is a single SUCCESS or ERROR line.
    /// </summary>
    FirstTerminalLine,

    /// <summary>
    /// The response is a multi line dump terminated by END, such as a log or status replay.
    /// </summary>
    EndMarker,
}
