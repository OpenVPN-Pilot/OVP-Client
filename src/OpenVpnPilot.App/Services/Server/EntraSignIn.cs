using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Signs a person in with Microsoft and hands back the access token the server exchanges.
/// </summary>
/// <remarks>
/// Always interactive. The server's own refresh token is what keeps a session; Microsoft is asked
/// again only when the server says the person has to prove who they are again, and then the person
/// is meant to see it happen.
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
public sealed record EntraSignInResult(EntraSignInOutcome Outcome, string? AccessToken = null, string? ErrorCode = null)
{
    // Keeps the token out of anything that prints the record, a log line included.
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
/// A new client is built for every sign in, so the library's token cache lives in memory for that
/// one sign in and nothing of Microsoft's is kept: the server's refresh token is what lasts.
/// </para>
/// </remarks>
public sealed class MsalEntraSignIn : IEntraSignIn
{
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

        string[] scopes = entra.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IPublicClientApplication client = PublicClientApplicationBuilder
            .Create(entra.ClientId)
            .WithAuthority(entra.Authority)
            .WithRedirectUri(RedirectUri)
            .Build();

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

            return new EntraSignInResult(EntraSignInOutcome.Success, result.AccessToken);
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
