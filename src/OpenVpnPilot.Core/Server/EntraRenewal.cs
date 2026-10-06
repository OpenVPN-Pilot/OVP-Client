using System.Text;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.Core.Server;

/// <summary>
/// Obtains a Microsoft access token for a server again without the person, from what their
/// interactive sign in left behind.
/// </summary>
/// <remarks>
/// A server in Entra mode ends every session a fixed time after the Microsoft sign in, because it
/// has no other way to notice an account that was disabled in the directory. Answering that with a
/// browser window every morning is not how an application that stays signed in behaves; Microsoft
/// still decides, because a disabled account, a changed password or a conditional access policy makes
/// the silent attempt fail and the person is asked as before.
/// </remarks>
public interface IEntraRenewal
{
    /// <param name="entra">What the server publishes about its Microsoft sign in now.</param>
    /// <param name="state">What the last sign in or renewal left behind, as <see cref="EntraRenewalResult.State"/>
    /// or the interactive sign in handed it over.</param>
    /// <param name="cancellationToken">Ends the attempt.</param>
    public Task<EntraRenewalResult> RenewAsync(EntraInfoResponse entra, string state, CancellationToken cancellationToken = default);
}

public enum EntraRenewalOutcome
{
    Renewed,

    /// <summary>
    /// Microsoft wants the person: the account, its password or a policy changed, or what was kept
    /// has expired. Only an interactive sign in helps.
    /// </summary>
    InteractionRequired,

    /// <summary>
    /// Microsoft could not be asked, for now. Nothing is decided, and the attempt is repeated later.
    /// </summary>
    Unavailable,
}

/// <param name="Outcome">How it went.</param>
/// <param name="AccessToken">The token for the server's exchange, on success only. Never written anywhere.</param>
/// <param name="State">What to keep for the next renewal, on success only.</param>
/// <param name="ErrorCode">The library's error code, when it failed.</param>
public sealed record EntraRenewalResult(
    EntraRenewalOutcome Outcome,
    string? AccessToken = null,
    string? State = null,
    string? ErrorCode = null)
{
    // Keeps the token and the state out of anything that prints the record, a log line included.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Outcome = ").Append(Outcome).Append(", ErrorCode = ").Append(ErrorCode);
        return true;
    }
}

/// <summary>
/// For a client that never renews, such as one assembled without a Microsoft sign in at all.
/// </summary>
public sealed class NoEntraRenewal : IEntraRenewal
{
    public Task<EntraRenewalResult> RenewAsync(EntraInfoResponse entra, string state, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EntraRenewalResult(EntraRenewalOutcome.InteractionRequired));
}
