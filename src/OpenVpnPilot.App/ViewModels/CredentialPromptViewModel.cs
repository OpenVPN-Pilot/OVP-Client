using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Asks the user for the credentials a server requested.
/// </summary>
public sealed partial class CredentialPromptViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CredentialPromptViewModel(string profileName, string realm, bool needsUsername, bool isRetry)
    {
        ProfileName = profileName;
        Realm = realm;
        NeedsUsername = needsUsername;
        IsRetry = isRetry;
    }

    public string ProfileName { get; }

    public string Realm { get; }

    public bool NeedsUsername { get; }

    /// <summary>
    /// True when a previous attempt was refused, so the prompt can say so.
    /// </summary>
    public bool IsRetry { get; }

    public string Title => NeedsUsername
        ? $"Sign in to {ProfileName}"
        : $"Unlock {Realm} for {ProfileName}";

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>
    /// Completes with true when the user confirmed and false when they cancelled.
    /// </summary>
    public Task<bool> Result => completion.Task;

    [RelayCommand]
    private void Confirm() => completion.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => completion.TrySetResult(false);

    /// <summary>
    /// Called when the window closes without either button being used.
    /// </summary>
    public void Abandon() => completion.TrySetResult(false);
}
