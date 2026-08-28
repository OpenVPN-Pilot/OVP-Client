using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Asks the user for credentials in a dialog.
/// </summary>
/// <remarks>
/// Credentials are held only for as long as it takes to pass them to OpenVPN. Once a keystore backed
/// credential manager exists, this provider will consult it first and prompt only when nothing is
/// stored or a stored value was refused.
/// </remarks>
public sealed class InteractiveCredentialProvider : ICredentialProvider
{
    private readonly IProfileNameLookup profileNames;

    public InteractiveCredentialProvider(IProfileNameLookup profileNames)
    {
        ArgumentNullException.ThrowIfNull(profileNames);
        this.profileNames = profileNames;
    }

    public async Task<VpnCredentials?> RequestAsync(
        CredentialRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string profileName = profileNames.GetDisplayName(request.ProfileId);

        // The supervisor raises this from a background pump, so the dialog must be marshalled.
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            CredentialPromptViewModel viewModel = new(
                profileName,
                request.Realm,
                request.NeedsUsername,
                request.IsRetry);

            CredentialPromptWindow window = new() { DataContext = viewModel };

            Window? owner = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            bool confirmed = await viewModel.Result;
            window.Close();

            if (!confirmed || viewModel.Password.Length == 0)
            {
                return null;
            }

            return new VpnCredentials(
                request.NeedsUsername ? viewModel.Username : null,
                viewModel.Password);
        });
    }
}
