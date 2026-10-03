using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The signed in state with one server: who is signed in, and the tokens that prove it.
/// </summary>
/// <remarks>
/// <para>
/// The access token lives in memory only. The refresh token is a credential and lives in the
/// keystore under <see cref="SecretReference.ForServerRefreshToken"/>, so a session survives a
/// restart; a copy is held in memory while the application runs.
/// </para>
/// <para>
/// A refresh token is single use, and two refreshes with the same token end the session for good.
/// Every refresh therefore runs behind one lock per session, and a caller that finds a refresh in
/// progress waits for its result instead of starting another. The new refresh token is stored before
/// the new access token is handed to anyone.
/// </para>
/// </remarks>
public interface IServerSession
{
    /// <summary>
    /// The key of the server this session belongs to.
    /// </summary>
    public string ServerKey { get; }

    /// <summary>
    /// True while there is a refresh token to keep the session going with.
    /// </summary>
    public bool IsSignedIn { get; }

    /// <summary>
    /// The signed in user as last reported, or the last known one handed to
    /// <see cref="RestoreAsync"/>. Null when nobody has signed in.
    /// </summary>
    public CurrentUserResponse? User { get; }

    /// <summary>
    /// Raised after signing in, signing out, the server ending the session, and a change of the user
    /// record such as the role. Raised outside every lock, on the thread that made the change.
    /// </summary>
    public event EventHandler<ServerSessionChangedEventArgs>? Changed;

    /// <summary>
    /// Picks up a session a previous run left in the keystore, without contacting the server.
    /// </summary>
    /// <param name="lastKnownUser">The user as the application last saw it, so the role can be shown offline.</param>
    /// <returns>True when a refresh token was found.</returns>
    public Task<bool> RestoreAsync(CurrentUserResponse? lastKnownUser, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over the tokens of a sign in: the refresh token is stored first, then the access token
    /// becomes usable.
    /// </summary>
    public Task EstablishAsync(TokenResponse tokens, CancellationToken cancellationToken = default);

    /// <summary>
    /// An access token to call with, refreshed first when it expires within a minute.
    /// </summary>
    /// <returns>
    /// The token on success; otherwise why there is none, such as <see cref="ServerOutcome.NotSignedIn"/>
    /// or the refresh's own failure.
    /// </returns>
    public Task<ServerResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes because the server refused <paramref name="rejectedAccessToken"/>, unless another
    /// caller has refreshed meanwhile, in which case that result is shared.
    /// </summary>
    public Task<ServerResult<string>> RefreshAsync(string? rejectedAccessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session on the server, then discards both tokens whatever the server answered.
    /// </summary>
    public Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards both tokens without contacting the server, for when talking to it is pointless or
    /// forbidden, such as after the wipe directive.
    /// </summary>
    public Task ForgetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What changed about a session.
/// </summary>
public enum ServerSessionChange
{
    SignedIn,

    /// <summary>
    /// The user record changed on a refresh, the role for example.
    /// </summary>
    UserChanged,

    /// <summary>
    /// The person signed out, or the tokens were discarded.
    /// </summary>
    SignedOut,

    /// <summary>
    /// The server refused the refresh token; signing in again is needed. Nothing was erased.
    /// </summary>
    SessionEnded,
}

/// <param name="Change">What happened.</param>
/// <param name="User">The user afterwards; null once signed out.</param>
/// <param name="PreviousUser">The user before, so a sign in as somebody else can be told apart.</param>
/// <param name="Code">The server's code when it ended the session.</param>
/// <param name="RequestId">The request that ended it.</param>
public sealed class ServerSessionChangedEventArgs(
    ServerSessionChange change,
    CurrentUserResponse? user,
    CurrentUserResponse? previousUser,
    string? code = null,
    string? requestId = null) : EventArgs
{
    public ServerSessionChange Change { get; } = change;

    public CurrentUserResponse? User { get; } = user;

    public CurrentUserResponse? PreviousUser { get; } = previousUser;

    public string? Code { get; } = code;

    public string? RequestId { get; } = requestId;
}

/// <summary>
/// The session with one server.
/// </summary>
public sealed class ServerSession : IServerSession, IDisposable
{
    /// <summary>
    /// How long before the access token's expiry a refresh is made instead of a call that would be
    /// refused.
    /// </summary>
    public static readonly TimeSpan ProactiveRefreshMargin = TimeSpan.FromMinutes(1);

    private readonly ServerTransport transport;
    private readonly ISecretStore secrets;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly string refreshReference;

    // Every refresh, sign in and sign out takes this, so no two of them ever use a token at once.
    private readonly SemaphoreSlim gate = new(1, 1);

    // Guards the fields below for readers that do not take the gate.
    private readonly Lock state = new();

    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt;
    private string? refreshToken;
    private CurrentUserResponse? user;

    // Counts completed refreshes, so a caller that waited for one knows it may use its result.
    private long generation;
    private ServerResult<string>? lastRefresh;

    internal ServerSession(
        string serverKey,
        ServerTransport transport,
        ISecretStore secrets,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        refreshReference = SecretReference.ForServerRefreshToken(serverKey);
        ServerKey = serverKey;
        this.transport = transport;
        this.secrets = secrets;
        this.time = time;
        this.logger = logger;
    }

    public string ServerKey { get; }

    public bool IsSignedIn
    {
        get
        {
            lock (state)
            {
                return refreshToken is not null;
            }
        }
    }

    public CurrentUserResponse? User
    {
        get
        {
            lock (state)
            {
                return user;
            }
        }
    }

    public event EventHandler<ServerSessionChangedEventArgs>? Changed;

    public async Task<bool> RestoreAsync(CurrentUserResponse? lastKnownUser, CancellationToken cancellationToken = default)
    {
        if (!secrets.IsAvailable)
        {
            return false;
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            StoredSecret? stored = await secrets.TryReadAsync(refreshReference, cancellationToken);

            if (stored is null || string.IsNullOrEmpty(stored.Password))
            {
                return false;
            }

            lock (state)
            {
                refreshToken = stored.Password;
                accessToken = null;
                user = lastKnownUser;
                lastRefresh = null;
                generation++;
            }

            ServerSessionLog.Restored(logger, ServerKey);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EstablishAsync(TokenResponse tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        CurrentUserResponse? previous;

        await gate.WaitAsync(cancellationToken);

        try
        {
            previous = User;
            await KeepAsync(tokens, cancellationToken);

            lock (state)
            {
                lastRefresh = null;
                generation++;
            }
        }
        finally
        {
            gate.Release();
        }

        ServerSessionLog.SignedIn(logger, ServerKey, tokens.User.Id, tokens.User.Role, tokens.User.Provider);
        Changed?.Invoke(this, new ServerSessionChangedEventArgs(ServerSessionChange.SignedIn, tokens.User, previous));
    }

    public async Task<ServerResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        string? current;

        lock (state)
        {
            if (refreshToken is null)
            {
                return NotSignedIn();
            }

            current = accessToken;

            if (current is not null && IsFresh())
            {
                return ServerResult.Succeeded<string>(current, 200, null);
            }
        }

        return await RefreshAsync(current, cancellationToken);
    }

    public async Task<ServerResult<string>> RefreshAsync(
        string? rejectedAccessToken,
        CancellationToken cancellationToken = default)
    {
        long observed = Interlocked.Read(ref generation);
        ServerSessionChangedEventArgs? change = null;
        ServerResult<string> outcome;

        await gate.WaitAsync(cancellationToken);

        try
        {
            string? token;

            lock (state)
            {
                // A refresh finished while this caller waited: its result is this caller's too, a
                // failure included, so ten callers never become ten attempts.
                if (generation != observed && lastRefresh is not null)
                {
                    return lastRefresh;
                }

                // Somebody already holds a newer token than the one the server refused.
                if (accessToken is not null
                    && !string.Equals(accessToken, rejectedAccessToken, StringComparison.Ordinal)
                    && IsFresh())
                {
                    return ServerResult.Succeeded<string>(accessToken, 200, null);
                }

                token = refreshToken;
            }

            if (token is null)
            {
                return NotSignedIn();
            }

            ServerResult<TokenResponse> answer = await transport.SendAsync<TokenResponse>(
                new ServerRequest(HttpMethod.Post, ServerPaths.Refresh, new RefreshRequest(token)),
                cancellationToken);

            (outcome, change) = await ApplyRefreshAsync(answer, cancellationToken);

            lock (state)
            {
                lastRefresh = outcome;
                generation++;
            }
        }
        finally
        {
            gate.Release();
        }

        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }

        return outcome;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        CurrentUserResponse? previous;

        await gate.WaitAsync(cancellationToken);

        try
        {
            string? token;

            lock (state)
            {
                token = refreshToken;
                previous = user;
            }

            if (token is not null)
            {
                ServerResult answer = await transport.SendAsync(
                    new ServerRequest(HttpMethod.Post, ServerPaths.Logout, new LogoutRequest(token)),
                    cancellationToken);

                if (!answer.IsSuccess)
                {
                    ServerSessionLog.SignOutNotConfirmed(logger, ServerKey, answer.Outcome, answer.Code, answer.RequestId);
                }
            }

            await DiscardAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        ServerSessionLog.SignedOut(logger, ServerKey);
        Changed?.Invoke(this, new ServerSessionChangedEventArgs(ServerSessionChange.SignedOut, null, previous));
    }

    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        CurrentUserResponse? previous;

        await gate.WaitAsync(cancellationToken);

        try
        {
            previous = User;
            await DiscardAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        ServerSessionLog.SignedOut(logger, ServerKey);
        Changed?.Invoke(this, new ServerSessionChangedEventArgs(ServerSessionChange.SignedOut, null, previous));
    }

    public void Dispose() => gate.Dispose();

    /// <summary>
    /// Takes a refresh's answer over, under the gate.
    /// </summary>
    private async Task<(ServerResult<string> Outcome, ServerSessionChangedEventArgs? Change)> ApplyRefreshAsync(
        ServerResult<TokenResponse> answer,
        CancellationToken cancellationToken)
    {
        if (answer.IsSuccess)
        {
            TokenResponse tokens = answer.Value;
            CurrentUserResponse? previous = User;

            await KeepAsync(tokens, cancellationToken);
            ServerSessionLog.Refreshed(logger, ServerKey, tokens.AccessTokenExpiresAt);

            ServerSessionChangedEventArgs? change = null;

            if (previous is not null && previous != tokens.User)
            {
                if (!string.Equals(previous.Role, tokens.User.Role, StringComparison.Ordinal))
                {
                    ServerSessionLog.RoleChanged(logger, ServerKey, tokens.User.Id, previous.Role, tokens.User.Role);
                }

                change = new ServerSessionChangedEventArgs(ServerSessionChange.UserChanged, tokens.User, previous);
            }

            return (ServerResult.Succeeded<string>(tokens.AccessToken, answer.Status ?? 200, answer.RequestId), change);
        }

        ServerSessionLog.RefreshFailed(logger, ServerKey, answer.Outcome, answer.Code, answer.RequestId);

        if (answer.Outcome == ServerOutcome.Problem
            && answer.Code is { } code
            && ServerErrorCodes.SignInRequired.Contains(code))
        {
            // The server will not take this refresh token again. Only signing in helps, and nothing
            // that was synchronised is touched: the copy stays usable offline until then.
            CurrentUserResponse? previous = User;
            await DiscardAsync(cancellationToken);
            ServerSessionLog.SessionEnded(logger, ServerKey, code, answer.RequestId);

            return (answer.AsFailure<string>(), new ServerSessionChangedEventArgs(
                ServerSessionChange.SessionEnded,
                null,
                previous,
                code,
                answer.RequestId));
        }

        // Offline, throttled, the directory unreachable, or the wipe directive: the tokens stay as
        // they are. A wipe is carried out by whoever listens for it, not here.
        return (answer.AsFailure<string>(), null);
    }

    /// <summary>
    /// Stores the refresh token, and only then makes the access token usable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous refresh token is already used up on the server, so when the keystore refuses
    /// the new one the session carries on from memory for this run rather than ending now: ending
    /// it would not bring the old token back.
    /// </para>
    /// <para>
    /// What the keystore still holds is then removed. Presented at the next start it would be a
    /// token used twice, which the server answers by ending the session as reused; without it the
    /// next start simply asks for a sign in.
    /// </para>
    /// </remarks>
    private async Task KeepAsync(TokenResponse tokens, CancellationToken cancellationToken)
    {
        if (secrets.IsAvailable)
        {
            try
            {
                await secrets.WriteAsync(refreshReference, new StoredSecret(null, tokens.RefreshToken), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or CryptographicException
                or InvalidOperationException)
            {
                ServerSessionLog.RefreshTokenNotStored(logger, ServerKey, exception);
                await RemoveStoredTokenAsync(cancellationToken);
            }
        }

        lock (state)
        {
            refreshToken = tokens.RefreshToken;
            user = tokens.User;
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt;
            accessToken = tokens.AccessToken;
        }
    }

    /// <summary>
    /// Drops both tokens, under the gate.
    /// </summary>
    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        lock (state)
        {
            accessToken = null;
            refreshToken = null;
            user = null;
            lastRefresh = null;
            generation++;
        }

        if (secrets.IsAvailable)
        {
            await RemoveStoredTokenAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Removes the refresh token from the keystore. A failure is written down and not passed on,
    /// because nothing the caller could do differently follows from it.
    /// </summary>
    private async Task RemoveStoredTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await secrets.DeleteAsync(refreshReference, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or InvalidOperationException)
        {
            ServerSessionLog.RefreshTokenNotRemoved(logger, ServerKey, exception);
        }
    }

    /// <summary>
    /// True while the access token has more than the margin left. Called under the state lock.
    /// </summary>
    private bool IsFresh() => time.GetUtcNow() < accessTokenExpiresAt - ProactiveRefreshMargin;

    private static ServerResult<string> NotSignedIn() =>
        ServerResult.Failed<string>(ServerOutcome.NotSignedIn, detail: "Not signed in to this server.");
}
