using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Answers credential requests from protected storage, and asks the user only when it has to.
/// </summary>
/// <remarks>
/// The order matters. A stored secret is tried first, so a profile connected daily never prompts.
/// A refusal deletes the stored secret before prompting again: keeping a value the server has just
/// rejected would make every later connection fail silently in the same way, and the user would have
/// no way to correct it from the dialog.
///
/// A one time code is never stored. It is valid once by definition, so remembering it would only
/// guarantee a failed attempt the next time.
/// </remarks>
public sealed class StoredCredentialProvider : ICredentialProvider
{
    private readonly IProfileNameLookup profileNames;
    private readonly ISecretStore secrets;
    private readonly ISettingsService settings;
    private readonly ILocalizer localizer;

    public StoredCredentialProvider(
        IProfileNameLookup profileNames,
        ISecretStore secrets,
        ISettingsService settings,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(profileNames);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);

        this.profileNames = profileNames;
        this.secrets = secrets;
        this.settings = settings;
        this.localizer = localizer;
    }

    public async Task<VpnCredentials?> RequestAsync(
        CredentialRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string reference = SecretReference.ForProfile(request.ProfileId, request.Realm);
        CredentialSettings preferences = settings.Current.Credentials;

        if (request.IsRetry)
        {
            await secrets.DeleteAsync(reference, cancellationToken);
        }

        StoredSecret? stored = preferences.UseStoredSecrets && !request.IsRetry
            ? await secrets.TryReadAsync(reference, cancellationToken)
            : null;

        // A challenge always needs a person, because the code exists only in the moment.
        if (stored is not null && request.Challenge is null)
        {
            return new VpnCredentials(stored.Username, stored.Password);
        }

        PromptResult? answer = await PromptAsync(request, stored, cancellationToken);

        if (answer is null)
        {
            return null;
        }

        if (answer.Remember && secrets.IsAvailable)
        {
            await secrets.WriteAsync(
                reference,
                new StoredSecret(answer.Credentials.Username, answer.Credentials.Password),
                cancellationToken);
        }

        return answer.Credentials;
    }

    private async Task<PromptResult?> PromptAsync(
        CredentialRequest request,
        StoredSecret? stored,
        CancellationToken cancellationToken)
    {
        string profileName = profileNames.GetDisplayName(request.ProfileId);
        bool canRemember = secrets.IsAvailable && settings.Current.Credentials.UseStoredSecrets;
        bool rememberByDefault = settings.Current.Credentials.RememberByDefault;

        // The supervisor raises this from a background pump, so the dialog must be marshalled.
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            CredentialPromptViewModel viewModel = new(
                localizer,
                profileName,
                request,
                stored,
                canRemember,
                rememberByDefault);

            CredentialPromptWindow window = new() { DataContext = viewModel };

            Window? owner = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (owner is not null && owner.IsVisible)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            await using CancellationTokenRegistration registration = cancellationToken.Register(
                viewModel.Abandon);

            bool confirmed = await viewModel.Result;
            window.Close();

            return confirmed && viewModel.HasUsableAnswer
                ? new PromptResult(viewModel.ToCredentials(), viewModel.Remember)
                : null;
        });
    }

    private sealed record PromptResult(VpnCredentials Credentials, bool Remember);
}
