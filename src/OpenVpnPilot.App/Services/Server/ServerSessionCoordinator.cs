using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Ties the session with the server to the synchronisation and to the copy's memory of who signed in.
/// </summary>
/// <remarks>
/// <para>
/// At start the session a previous run left in the keystore is picked up without contacting the
/// server, with the person as they were last seen, so the role is known while offline and the window
/// never waits for the network. The synchronisation runs while somebody is signed in and stops when
/// they sign out.
/// </para>
/// <para>
/// In Server mode this is the <see cref="IServerSignIn"/> everything uses, so that every sign in
/// passes the one rule that needs a moment between "the server accepted" and "the tokens are used":
/// a different person than the one this copy last knew first loses the previous person's waiting
/// changes and personal data, and only then is anything sent. The same person simply continues.
/// </para>
/// </remarks>
public interface IServerSessionCoordinator
{
    /// <summary>
    /// The person signed in, or the one last seen when the session was picked up without the server.
    /// </summary>
    public CurrentUserResponse? User { get; }

    public bool IsSignedIn { get; }

    /// <summary>
    /// Picks up the stored session and starts the synchronisation when there is one.
    /// </summary>
    /// <remarks>
    /// Never contacts the server itself, so it returns quickly whatever the network does. Does
    /// nothing more when the session is already running.
    /// </remarks>
    /// <returns>True when somebody is signed in and the synchronisation was started.</returns>
    public Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Picks up the session a sign in before a restart stored, and asks the server whose it is.
    /// </summary>
    /// <remarks>
    /// The sign in that led here happened in the previous copy, which could not tell whether it was
    /// the person this copy last knew. The answer decides, as for any sign in, whether the previous
    /// person's data goes. The synchronisation is not started: whoever continues the setup runs the
    /// first one and then calls <see cref="StartAsync"/>.
    /// </remarks>
    /// <returns>The person, or why the server could not be asked; <see cref="ServerOutcome.NotSignedIn"/>
    /// when no session was stored.</returns>
    public Task<ServerResult<CurrentUserResponse>> ConfirmSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the synchronisation, for the application ending. The session stays stored.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServerSessionCoordinator"/>
public sealed class ServerSessionCoordinator : IServerSessionCoordinator, IServerSignIn, IDisposable
{
    private readonly IServerConnection connection;
    private readonly ISyncEngine engine;
    private readonly IServerAccountState account;
    private readonly IServerWipe wipe;
    private readonly ILogger<ServerSessionCoordinator> logger;

    // Start, sign in, sign out and the confirmation after a restart one at a time, so a sign in never
    // finds the synchronisation half started by somebody else.
    private readonly SemaphoreSlim gate = new(1, 1);

    // Cancelled on disposal, for the work this starts on its own rather than for a caller.
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stopping;

    private bool listening;
    private bool disposed;
    private bool running;

    public ServerSessionCoordinator(
        IServerConnection connection,
        ISyncEngine engine,
        IServerAccountState account,
        IServerWipe wipe,
        ILogger<ServerSessionCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(wipe);
        ArgumentNullException.ThrowIfNull(logger);

        this.connection = connection;
        this.engine = engine;
        this.account = account;
        this.wipe = wipe;
        this.logger = logger;
        stopping = lifetime.Token;
    }

    public CurrentUserResponse? User => connection.Session.User;

    public bool IsSignedIn => connection.Session.IsSignedIn;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            Listen();

            if (connection.Wipe.IsRequested)
            {
                return false;
            }

            if (!connection.Session.IsSignedIn)
            {
                CurrentUserResponse? lastKnown = await account.ReadLastKnownUserAsync(cancellationToken);

                if (!await connection.Session.RestoreAsync(lastKnown, cancellationToken))
                {
                    ServerAccountLog.NoStoredSession(logger, connection.ServerKey);
                    return false;
                }
            }

            await StartEngineAsync(cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ServerResult<CurrentUserResponse>> ConfirmSessionAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            Listen();

            CurrentUserResponse? lastKnown = await account.ReadLastKnownUserAsync(cancellationToken);

            if (!connection.Session.IsSignedIn && !await connection.Session.RestoreAsync(lastKnown, cancellationToken))
            {
                return ServerResult.Failed<CurrentUserResponse>(ServerOutcome.NotSignedIn, detail: "No session was stored.");
            }

            ServerResult<CurrentUserResponse> answer = await connection.Api.GetCurrentUserAsync(cancellationToken);

            if (answer.IsSuccess)
            {
                await AdoptAsync(lastKnown, answer.Value, cancellationToken);
            }

            return answer;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            await StopEngineAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ServerCheckResult> CheckServerAsync(CancellationToken cancellationToken = default) =>
        connection.SignIn.CheckServerAsync(cancellationToken);

    public Task<ServerResult<CurrentUserResponse>> SignInAsync(
        string username,
        string? password,
        CancellationToken cancellationToken = default) =>
        SignInCoreAsync(token => connection.SignIn.SignInAsync(username, password, token), cancellationToken);

    public Task<ServerResult<CurrentUserResponse>> SignInWithEntraAsync(
        string entraAccessToken,
        CancellationToken cancellationToken = default) =>
        SignInCoreAsync(token => connection.SignIn.SignInWithEntraAsync(entraAccessToken, token), cancellationToken);

    /// <summary>
    /// Signs out and stops the synchronisation. The copy and its waiting changes stay, so signing in
    /// again as the same person continues where this left off.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            await StopEngineAsync(cancellationToken);
            await connection.SignIn.SignOutAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        // Registered under more than one service type, so the container disposes it more than once.
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (listening)
        {
            connection.Session.Changed -= OnSessionChanged;
            connection.Wipe.WipeRequested -= OnWipeRequested;
        }

        lifetime.Cancel();
        lifetime.Dispose();
        gate.Dispose();
    }

    private async Task<ServerResult<CurrentUserResponse>> SignInCoreAsync(
        Func<CancellationToken, Task<ServerResult<CurrentUserResponse>>> signIn,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            Listen();

            CurrentUserResponse? lastKnown = await account.ReadLastKnownUserAsync(cancellationToken);

            // Stopped before the new tokens exist, so no push can carry the previous person's
            // changes in the new person's name while the rule below is still deciding.
            bool wasRunning = running;
            await StopEngineAsync(cancellationToken);

            ServerResult<CurrentUserResponse> answer = await signIn(cancellationToken);

            if (answer.IsSuccess)
            {
                await AdoptAsync(lastKnown, answer.Value, cancellationToken);
                await StartEngineAsync(cancellationToken);
            }
            else if (wasRunning && connection.Session.IsSignedIn && !connection.Wipe.IsRequested)
            {
                // A failed attempt leaves the session that was there untouched.
                await StartEngineAsync(cancellationToken);
            }

            return answer;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies the rule for who signed in, then remembers them.
    /// </summary>
    private async Task AdoptAsync(CurrentUserResponse? lastKnown, CurrentUserResponse user, CancellationToken cancellationToken)
    {
        if (lastKnown is not null && lastKnown.Id != user.Id)
        {
            ServerAccountLog.DifferentUserSignedIn(logger, connection.ServerKey, lastKnown.Id, user.Id);
            await account.DiscardPersonalDataAsync(cancellationToken);
        }

        await account.RememberUserAsync(user, cancellationToken);
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        if (running)
        {
            return;
        }

        await engine.StartAsync(cancellationToken);
        running = true;
    }

    private async Task StopEngineAsync(CancellationToken cancellationToken)
    {
        if (!running)
        {
            return;
        }

        await engine.StopAsync(cancellationToken);
        running = false;
    }

    /// <summary>
    /// Starts listening to the session and the wipe signal, once. Called under the gate.
    /// </summary>
    private void Listen()
    {
        if (listening)
        {
            return;
        }

        listening = true;
        connection.Session.Changed += OnSessionChanged;
        connection.Wipe.WipeRequested += OnWipeRequested;

        // A directive that arrived before anybody listened, which only a call made before the start
        // could have received, is carried out all the same.
        if (connection.Wipe.Directive is { } early)
        {
            BeginWipe(early);
        }
    }

    private void OnSessionChanged(object? sender, ServerSessionChangedEventArgs change)
    {
        // A refresh can bring a changed record, the role above all, which the copy remembers so the
        // role is right while offline too. Sign in and sign out are handled where they are made.
        if (change is { Change: ServerSessionChange.UserChanged, User: { } user })
        {
            _ = RememberObservedAsync(user, stopping);
        }
    }

    private async Task RememberObservedAsync(CurrentUserResponse user, CancellationToken cancellationToken)
    {
        try
        {
            await account.RememberUserAsync(user, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The application is closing. The record is remembered again at the next refresh.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Raised from a refresh that belongs to someone else's call, so nobody waits for this.
            // The role shown offline stays the previous one until the next change is remembered.
            ServerAccountLog.UserNotRemembered(logger, connection.ServerKey, exception);
        }
    }

    private void OnWipeRequested(object? sender, ServerWipeDirectiveEventArgs directive) => BeginWipe(directive.Directive);

    private void BeginWipe(ServerWipeDirective directive)
    {
        // The signal is raised on the thread that received the answer, which must not wait for a
        // wipe that disconnects tunnels and deletes folders.
        _ = Task.Run(async () =>
        {
            try
            {
                await wipe.WipeAsync(directive, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Every step of the wipe reports its own failures; this is what none of them caught.
                ServerAccountLog.WipeFailed(logger, connection.ServerKey, directive.RequestId, exception);
            }
        });
    }
}
