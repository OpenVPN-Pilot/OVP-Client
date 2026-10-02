using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services.Server;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Signs in again to the server the application works with, after the session ended.
/// </summary>
/// <remarks>
/// The server is asked about itself first, as at every sign in, because the way it signs people in
/// may have changed. The form is the same as on the first start; the sign in goes through the
/// session coordinator, so a different person than the last one first loses the previous person's
/// waiting changes, and the synchronisation starts again on success.
/// </remarks>
public sealed partial class ServerSignInViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizer localizer;
    private readonly IServerSignIn signIn;
    private readonly IEntraSignIn entra;
    private readonly Uri address;

    private Func<string>? message;

    public ServerSignInViewModel(ILocalizer localizer, IServerSignIn signIn, IEntraSignIn entra, IServerApi api)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(entra);
        ArgumentNullException.ThrowIfNull(api);

        this.localizer = localizer;
        this.signIn = signIn;
        this.entra = entra;
        address = api.BaseAddress;
    }

    /// <summary>
    /// Raised when the window has done its job or was given up on.
    /// </summary>
    public event EventHandler? Closed;

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial SignInViewModel? Form { get; set; }

    public string? Message => message?.Invoke();

    public bool HasMessage => message is not null;

    /// <summary>
    /// Asks the server about itself and shows the form it asks for.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsChecking = true;

        try
        {
            ServerCheckResult check = await signIn.CheckServerAsync(cancellationToken);

            if (!check.IsCompatible || check.Info is null)
            {
                Show(() => ServerMessages.CheckFailure(localizer, check) ?? string.Empty);
                return;
            }

            SignInViewModel form = new(localizer, signIn, entra, check.Info, address);
            form.SignedIn += OnSignedIn;
            Form = form;
        }
        finally
        {
            IsChecking = false;
        }
    }

    public void Dispose()
    {
        if (Form is { } form)
        {
            form.SignedIn -= OnSignedIn;
            form.Dispose();
            Form = null;
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    private void OnSignedIn(object? sender, CurrentUserResponse user) => Closed?.Invoke(this, EventArgs.Empty);

    private void Show(Func<string>? text)
    {
        message = text;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
    }
}
