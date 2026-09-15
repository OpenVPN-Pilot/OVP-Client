using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// Asks for the passphrase of the shared library, either one that exists or a new one.
/// </summary>
/// <remarks>
/// What is typed is tried before the prompt closes, so a passphrase the file does not open with is
/// said on the spot, beside the field it was typed into, and can be typed again. A prompt that closed
/// first and complained afterwards would make somebody start over for a typing mistake.
/// </remarks>
public sealed partial class PassphrasePromptViewModel : ViewModelBase
{
    /// <summary>
    /// Short enough not to be a nuisance, long enough that the work factor has something to protect.
    /// </summary>
    public const int MinimumLength = 8;

    private readonly ILocalizer localizer;
    private readonly Func<string, Task<string?>> accept;

    /// <param name="accept">
    /// Tries the passphrase, and returns why it was not accepted, or null when it was.
    /// </param>
    public PassphrasePromptViewModel(
        ILocalizer localizer,
        string title,
        string message,
        bool isNew,
        Func<string, Task<string?>> accept)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(accept);

        this.localizer = localizer;
        this.accept = accept;

        Title = title;
        Message = message;
        IsNew = isNew;
    }

    /// <summary>
    /// Raised with true when a passphrase was accepted, and with false when the prompt was cancelled.
    /// </summary>
    public event EventHandler<bool>? Closed;

    public string Title { get; }

    public string Message { get; }

    /// <summary>
    /// True for a passphrase being chosen, which is typed twice and has a minimum length.
    /// </summary>
    public bool IsNew { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Passphrase { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(Problem))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Confirmation { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// What was said when the passphrase was last tried.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Refusal { get; set; }

    public bool CanAccept => !IsBusy && Passphrase.Length > 0 && NewPassphraseProblem is null;

    public string? Problem => Refusal ?? (Passphrase.Length > 0 ? NewPassphraseProblem : null);

    public bool HasProblem => Problem is { Length: > 0 };

    private string? NewPassphraseProblem
    {
        get
        {
            if (!IsNew)
            {
                return null;
            }

            if (Passphrase.Length < MinimumLength)
            {
                return localizer.Translate("export.passphraseTooShort", MinimumLength);
            }

            return string.Equals(Passphrase, Confirmation, StringComparison.Ordinal)
                ? null
                : localizer["export.passphraseMismatch"];
        }
    }

    partial void OnPassphraseChanged(string value) => Refusal = null;

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (!CanAccept)
        {
            return;
        }

        IsBusy = true;

        try
        {
            string? refusal = await accept(Passphrase);

            if (refusal is not null)
            {
                Refusal = refusal;
                return;
            }

            // Done with, and no reason to stay in memory.
            Passphrase = string.Empty;
            Confirmation = string.Empty;
            Closed?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Passphrase = string.Empty;
        Confirmation = string.Empty;
        Closed?.Invoke(this, false);
    }
}
