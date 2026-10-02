using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// The sign in protocol with one server: is it one this client can use, then who signs in.
/// </summary>
public interface IServerSignIn
{
    /// <summary>
    /// Asks <c>GET /api/v1/server/info</c> and judges the answer. Always the first step, when an
    /// address is entered and again at every start.
    /// </summary>
    public Task<ServerCheckResult> CheckServerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs in with a user name, and a password for modes <c>file</c> and <c>ldap</c>.
    /// </summary>
    /// <param name="password">Null in mode <c>none</c>, where the server ignores it.</param>
    /// <returns>The signed in user, or the refusal: show its code's meaning, and its request id.</returns>
    public Task<ServerResult<CurrentUserResponse>> SignInAsync(
        string username,
        string? password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Trades an access token from the Microsoft sign in for this server's own tokens, in mode
    /// <c>entra</c>. Obtaining that token is the caller's part.
    /// </summary>
    public Task<ServerResult<CurrentUserResponse>> SignInWithEntraAsync(
        string entraAccessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session on the server and discards both tokens whatever it answers.
    /// </summary>
    public Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether a server is one this client can work with.
/// </summary>
public enum ServerCompatibility
{
    Compatible,

    /// <summary>
    /// The address answers, but not as an OpenVPN Pilot Server does.
    /// </summary>
    NotAPilotServer,

    /// <summary>
    /// The server speaks another API version. Client and server do not match.
    /// </summary>
    ApiVersionUnsupported,

    /// <summary>
    /// The server requires a newer client. Update before signing in.
    /// </summary>
    ClientOutdated,

    /// <summary>
    /// The question could not be asked or answered; <see cref="ServerCheckResult.Transport"/> says why,
    /// such as offline or a certificate that is not trusted.
    /// </summary>
    Unknown,
}

/// <param name="Compatibility">The verdict.</param>
/// <param name="Info">What the server said about itself, when it said anything readable.</param>
/// <param name="Transport">The call itself, for its outcome and request id.</param>
/// <param name="ClientVersion">This client's version, as it was compared.</param>
public sealed record ServerCheckResult(
    ServerCompatibility Compatibility,
    ServerInfoResponse? Info,
    ServerResult Transport,
    Version ClientVersion)
{
    public bool IsCompatible => Compatibility == ServerCompatibility.Compatible;
}

/// <summary>
/// Signs in to one server through its API and keeps the result in its session.
/// </summary>
public sealed class ServerSignIn : IServerSignIn
{
    private readonly IServerApi api;
    private readonly ServerTransport transport;
    private readonly IServerSession session;
    private readonly IClientVersionProvider version;
    private readonly ILogger logger;

    internal ServerSignIn(
        IServerApi api,
        ServerTransport transport,
        IServerSession session,
        IClientVersionProvider version,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(logger);

        this.api = api;
        this.transport = transport;
        this.session = session;
        this.version = version;
        this.logger = logger;
    }

    public async Task<ServerCheckResult> CheckServerAsync(CancellationToken cancellationToken = default)
    {
        Version own = version.Version;
        ServerResult<ServerInfoResponse> answer = await api.GetServerInfoAsync(cancellationToken);

        ServerCheckResult result = answer.Outcome switch
        {
            ServerOutcome.Success => new ServerCheckResult(Judge(answer.Value, own), answer.Value, answer, own),

            // Something answered over https, but not with the server's shape: a web site, a proxy's
            // page, a different service on that address.
            ServerOutcome.InvalidResponse => new ServerCheckResult(ServerCompatibility.NotAPilotServer, null, answer, own),
            ServerOutcome.Problem when answer.Code is null =>
                new ServerCheckResult(ServerCompatibility.NotAPilotServer, null, answer, own),

            _ => new ServerCheckResult(ServerCompatibility.Unknown, null, answer, own),
        };

        if (result.Compatibility is not (ServerCompatibility.Compatible or ServerCompatibility.Unknown))
        {
            ServerSessionLog.ServerNotUsable(
                logger,
                api.BaseAddress,
                result.Compatibility,
                result.Info?.Name,
                result.Info?.Version,
                result.Info?.ApiVersion,
                result.Info?.MinimumClientVersion,
                own);
        }

        return result;
    }

    public async Task<ServerResult<CurrentUserResponse>> SignInAsync(
        string username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        ServerResult<TokenResponse> answer = await transport.SendAsync<TokenResponse>(
            new ServerRequest(HttpMethod.Post, ServerPaths.Login, new LoginRequest(username, password)),
            cancellationToken);

        return await TakeOverAsync(answer, cancellationToken);
    }

    public async Task<ServerResult<CurrentUserResponse>> SignInWithEntraAsync(
        string entraAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entraAccessToken);

        ServerResult<TokenResponse> answer = await transport.SendAsync<TokenResponse>(
            new ServerRequest(HttpMethod.Post, ServerPaths.EntraExchange, new EntraExchangeRequest(entraAccessToken)),
            cancellationToken);

        return await TakeOverAsync(answer, cancellationToken);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => session.SignOutAsync(cancellationToken);

    /// <summary>
    /// Compares the server's answer with what this client speaks.
    /// </summary>
    internal static ServerCompatibility Judge(ServerInfoResponse info, Version own)
    {
        if (!string.Equals(info.Name, ServerInfoResponse.ExpectedName, StringComparison.Ordinal))
        {
            return ServerCompatibility.NotAPilotServer;
        }

        if (!string.Equals(info.ApiVersion, PilotHeaders.CurrentApiVersion, StringComparison.Ordinal))
        {
            return ServerCompatibility.ApiVersionUnsupported;
        }

        if (!TryParseVersion(info.MinimumClientVersion, out Version? minimum))
        {
            // The contract promises a version here; anything else is not the server it describes.
            return ServerCompatibility.NotAPilotServer;
        }

        return Complete(own) < Complete(minimum) ? ServerCompatibility.ClientOutdated : ServerCompatibility.Compatible;
    }

    /// <summary>
    /// Reads a version the way the server does: a suffix such as <c>-beta.1</c> is ignored.
    /// </summary>
    internal static bool TryParseVersion(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Version.TryParse(text.Trim().Split('-', '+')[0], out parsed);
    }

    // A missing part ranks below zero in System.Version, which would make 1.9 older than 1.9.0.
    private static Version Complete(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private async Task<ServerResult<CurrentUserResponse>> TakeOverAsync(
        ServerResult<TokenResponse> answer,
        CancellationToken cancellationToken)
    {
        if (!answer.IsSuccess)
        {
            ServerSessionLog.SignInFailed(logger, session.ServerKey, answer.Outcome, answer.Code, answer.RequestId);
            return answer.AsFailure<CurrentUserResponse>();
        }

        await session.EstablishAsync(answer.Value, cancellationToken);

        return ServerResult.Succeeded<CurrentUserResponse>(answer.Value.User, answer.Status ?? 200, answer.RequestId);
    }
}
