using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Creates the HTTP client for one configured server.
/// </summary>
public interface IServerHttpClientFactory
{
    /// <summary>
    /// A client whose every answer passes the wipe check of <paramref name="wipe"/>.
    /// </summary>
    /// <param name="baseAddress">The server's address, <c>https</c> only.</param>
    /// <exception cref="ArgumentException">The address is not an absolute <c>https</c> address.</exception>
    public HttpClient Create(Uri baseAddress, ServerWipeSignal wipe);
}

/// <summary>
/// Builds the pipeline every call to a server goes through.
/// </summary>
/// <remarks>
/// <para>
/// One client per server, owned here rather than by a host wide factory, because the project does
/// not reference one and a server connection lives exactly as long as the mode does. The order of
/// the handlers is the point: the mandatory headers are added on the way out, and on the way back
/// the wipe check sees the answer first, then the traffic log, then the caller.
/// </para>
/// <para>
/// The client's own timeout is switched off. Each call brings its own, 15 seconds for an ordinary
/// call and longer for a batch upload, which one shared client could not express. No
/// <c>Expect: 100-continue</c>, which only adds a round trip to every body sent. Redirects are not
/// followed: the API never sends one, and following one could carry a token somewhere else.
/// </para>
/// <para>
/// Certificate validation is the operating system's, unchanged. There is no setting that weakens
/// it, and there must never be one: an operator with a private authority installs it in the trust
/// store. Only a test passes its own innermost handler.
/// </para>
/// </remarks>
public sealed class ServerHttpClientFactory : IServerHttpClientFactory
{
    private readonly IClientVersionProvider version;
    private readonly IInstallationIdProvider installation;
    private readonly TimeProvider time;
    private readonly ILoggerFactory loggers;
    private readonly Func<HttpMessageHandler> primaryHandler;
    private readonly string platform;

    /// <param name="primaryHandler">
    /// The handler that talks to the network. Null for the platform's own, which is what the
    /// application uses; tests pass a scripted one.
    /// </param>
    /// <param name="platform">The platform name sent; null for <see cref="ClientPlatform.Current"/>.</param>
    public ServerHttpClientFactory(
        IClientVersionProvider version,
        IInstallationIdProvider installation,
        TimeProvider time,
        ILoggerFactory loggers,
        Func<HttpMessageHandler>? primaryHandler = null,
        string? platform = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggers);

        this.version = version;
        this.installation = installation;
        this.time = time;
        this.loggers = loggers;
        this.primaryHandler = primaryHandler ?? CreatePlatformHandler;
        this.platform = platform ?? ClientPlatform.Current;
    }

    public HttpClient Create(Uri baseAddress, ServerWipeSignal wipe)
    {
        ArgumentNullException.ThrowIfNull(wipe);

        Uri address = NormaliseBaseAddress(baseAddress);
        ILogger logger = loggers.CreateLogger("OpenVpnPilot.Core.Server.ServerHttp");

        HttpMessageHandler pipeline = new PilotHeadersHandler(address, version, installation, time, platform)
        {
            InnerHandler = new ServerTrafficLogHandler(logger)
            {
                InnerHandler = new WipeDirectiveHandler(address, wipe, time, logger)
                {
                    InnerHandler = primaryHandler(),
                },
            },
        };

        HttpClient client = new(pipeline, disposeHandler: true)
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.ExpectContinue = false;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(ServerProblem.MediaType));

        return client;
    }

    /// <summary>
    /// Checks the address and makes it end with a slash, so relative paths resolve below it.
    /// </summary>
    /// <exception cref="ArgumentException">Not an absolute <c>https</c> address.</exception>
    public static Uri NormaliseBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (!baseAddress.IsAbsoluteUri || !string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException("A server is reached over https only.", nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("A server address carries no query and no fragment.", nameof(baseAddress));
        }

        string text = baseAddress.AbsoluteUri;
        return text.EndsWith('/') ? baseAddress : new Uri(text + "/");
    }

    private static SocketsHttpHandler CreatePlatformHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),

        // A server that moves to another address is found again without restarting the application.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
