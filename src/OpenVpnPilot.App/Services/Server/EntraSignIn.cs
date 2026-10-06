using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Signs a person in with Microsoft and hands back the access token the server exchanges.
/// </summary>
/// <remarks>
/// Always interactive. The server's own refresh token is what keeps a session. What Microsoft hands
/// over besides the token is returned as <see cref="EntraSignInResult.State"/>, so that the session
/// can ask Microsoft again without the person when the server wants proof; see <see cref="IEntraRenewal"/>.
/// </remarks>
public interface IEntraSignIn
{
    /// <param name="entra">What the server published about its Microsoft sign in, used exactly as given.</param>
    /// <param name="cancellationToken">Ends the wait for the browser.</param>
    public Task<EntraSignInResult> AcquireAccessTokenAsync(EntraInfoResponse entra, CancellationToken cancellationToken = default);
}

public enum EntraSignInOutcome
{
    Success,

    /// <summary>
    /// The person closed the browser or cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Microsoft or the browser refused; <see cref="EntraSignInResult.ErrorCode"/> says how.
    /// </summary>
    Failed,
}

/// <param name="Outcome">How it went.</param>
/// <param name="AccessToken">The token, for a success only. Never written anywhere.</param>
/// <param name="ErrorCode">The library's error code, for a failure.</param>
/// <param name="State">What to renew the session with later, for a success only. A credential, kept
/// in the keystore and nowhere else.</param>
public sealed record EntraSignInResult(
    EntraSignInOutcome Outcome,
    string? AccessToken = null,
    string? ErrorCode = null,
    string? State = null)
{
    // Keeps the token and the state out of anything that prints the record, a log line included.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Outcome = ").Append(Outcome).Append(", ErrorCode = ").Append(ErrorCode);
        return true;
    }
}

/// <summary>
/// The Microsoft sign in through the Microsoft Authentication Library, in the system browser.
/// </summary>
/// <remarks>
/// <para>
/// A public client using the authorisation code flow with PKCE, which the library does for an
/// interactive request. The browser is the system's, never an embedded one: the person sees the real
/// Microsoft page in the browser they trust, with whatever single sign on and second factor it
/// already holds. The redirect goes to <c>http://localhost</c> on a port the library picks, which is
/// what the server's documentation registers.
/// </para>
/// <para>
/// A new client is built for every sign in and every renewal, with a token cache that starts empty or
/// from the state handed in, and whose content is handed back as the new state. The library's own
/// persistence is not used: the state belongs to one session with one server, lives in the keystore
/// beside its refresh token, on the Mac in the same keychain item, and goes when the session goes.
/// </para>
/// </remarks>
public sealed class MsalEntraSignIn : IEntraSignIn, IEntraRenewal
{
    // Long enough for Microsoft to answer a refresh, short enough not to hold the session for long.
    private static readonly TimeSpan RenewalTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The redirect the server's documentation tells the operator to register.
    /// </summary>
    public const string RedirectUri = "http://localhost";

    private readonly ILocalizer localizer;
    private readonly ILogger<MsalEntraSignIn> logger;

    public MsalEntraSignIn(ILocalizer localizer, ILogger<MsalEntraSignIn> logger)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(logger);

        this.localizer = localizer;
        this.logger = logger;
    }

    public async Task<EntraSignInResult> AcquireAccessTokenAsync(
        EntraInfoResponse entra,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entra);

        string[] scopes = Scopes(entra);
        IPublicClientApplication client = Build(entra);
        CacheCapture cache = new(client, null);

        try
        {
            AuthenticationResult result = await client
                .AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = Page(localizer["signIn.entraBrowserDone"]),
                    HtmlMessageError = Page(localizer["signIn.entraBrowserFailed"]),
                })
                .ExecuteAsync(cancellationToken);

            return new EntraSignInResult(EntraSignInOutcome.Success, result.AccessToken, State: cache.State);
        }
        catch (MsalClientException exception) when (exception.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            ServerAccountLog.EntraCancelled(logger);
            return new EntraSignInResult(EntraSignInOutcome.Cancelled);
        }
        catch (MsalException exception)
        {
            ServerAccountLog.EntraFailed(logger, exception.ErrorCode, exception);
            return new EntraSignInResult(EntraSignInOutcome.Failed, ErrorCode: exception.ErrorCode);
        }
    }

    public async Task<EntraRenewalResult> RenewAsync(
        EntraInfoResponse entra,
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entra);
        ArgumentException.ThrowIfNullOrEmpty(state);

        IPublicClientApplication client = Build(entra);
        CacheCapture cache = new(client, state);

        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(RenewalTimeout);

        try
        {
            IEnumerable<IAccount> accounts = await client.GetAccountsAsync();

            // The cache was started empty for the sign in it came from, so it holds that one person.
            if (accounts.FirstOrDefault() is not { } account)
            {
                return new EntraRenewalResult(EntraRenewalOutcome.InteractionRequired, ErrorCode: "no_account");
            }

            AuthenticationResult result = await client
                .AcquireTokenSilent(Scopes(entra), account)
                .ExecuteAsync(bounded.Token);

            return new EntraRenewalResult(EntraRenewalOutcome.Renewed, result.AccessToken, cache.State ?? state);
        }
        catch (MsalUiRequiredException exception)
        {
            return new EntraRenewalResult(EntraRenewalOutcome.InteractionRequired, ErrorCode: exception.ErrorCode);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new EntraRenewalResult(EntraRenewalOutcome.Unavailable, ErrorCode: MsalError.RequestTimeout);
        }
        catch (HttpRequestException exception)
        {
            // The library lets a network failure through as it is.
            ServerAccountLog.EntraFailed(logger, "network", exception);
            return new EntraRenewalResult(EntraRenewalOutcome.Unavailable, ErrorCode: "network");
        }
        catch (MsalException exception) when (IsTransient(exception))
        {
            ServerAccountLog.EntraFailed(logger, exception.ErrorCode, exception);
            return new EntraRenewalResult(EntraRenewalOutcome.Unavailable, ErrorCode: exception.ErrorCode);
        }
        catch (MsalException exception)
        {
            // Anything else Microsoft or the library refuses is settled the way it was settled at
            // first: by the person signing in.
            ServerAccountLog.EntraFailed(logger, exception.ErrorCode, exception);
            return new EntraRenewalResult(EntraRenewalOutcome.InteractionRequired, ErrorCode: exception.ErrorCode);
        }
    }

    /// <summary>
    /// True when the failure says Microsoft could not be asked, rather than what it answered.
    /// </summary>
    private static bool IsTransient(MsalException exception) => exception switch
    {
        MsalServiceException service when service.StatusCode is 429 or >= 500 => true,
        _ => exception.ErrorCode is MsalError.RequestTimeout or MsalError.ServiceNotAvailable
            || exception.InnerException is HttpRequestException,
    };

    private static string[] Scopes(EntraInfoResponse entra) =>
        entra.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IPublicClientApplication Build(EntraInfoResponse entra) =>
        PublicClientApplicationBuilder
            .Create(entra.ClientId)
            .WithAuthority(entra.Authority)
            .WithRedirectUri(RedirectUri)
            .Build();

    /// <summary>
    /// Fills a client's token cache from a state and keeps what the cache holds afterwards.
    /// </summary>
    /// <remarks>
    /// The library asks before every access, a write in the middle of a renewal included, so it is
    /// always handed the latest content rather than the state it started from.
    /// </remarks>
    private sealed class CacheCapture
    {
        private byte[]? current;

        public CacheCapture(IPublicClientApplication client, string? state)
        {
            current = state is null ? null : Convert.FromBase64String(state);

            client.UserTokenCache.SetBeforeAccess(arguments =>
            {
                if (current is not null)
                {
                    arguments.TokenCache.DeserializeMsalV3(current, shouldClearExistingCache: true);
                }
            });

            client.UserTokenCache.SetAfterAccess(arguments =>
            {
                if (arguments.HasStateChanged)
                {
                    current = arguments.TokenCache.SerializeMsalV3();
                    State = Convert.ToBase64String(current);
                }
            });
        }

        /// <summary>
        /// The cache as the library last changed it, or null when it did not change.
        /// </summary>
        public string? State { get; private set; }
    }

    /// <summary>
    /// The page the browser shows when it hands the person back.
    /// </summary>
    /// <remarks>
    /// The library fills two placeholders in the failure page with braces, so a brace in a
    /// translation would be taken for one; the text is encoded and the braces are left out.
    /// </remarks>
    private string Page(string text)
    {
        string encoded = System.Net.WebUtility.HtmlEncode(text.Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal));

        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(localizer["app.name"])}</title></head>"
            + $"<body style=\"font-family:sans-serif;margin:3em\"><p>{encoded}</p></body></html>";
    }
}
