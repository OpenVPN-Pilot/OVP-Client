using CommunityToolkit.Mvvm.Input;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// A message the person has to see before the application goes on, with one button.
/// </summary>
/// <param name="title">The window's title and the heading.</param>
/// <param name="message">What happened and what happens next, in plain words.</param>
/// <param name="reference">A line for whoever reads a log, such as a request id; null for none.</param>
public sealed partial class NoticeViewModel(string title, string message, string? reference) : ViewModelBase
{
    /// <summary>
    /// Raised when the person has read it.
    /// </summary>
    public event EventHandler? Acknowledged;

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string? Reference { get; } = reference;

    public bool HasReference => !string.IsNullOrEmpty(Reference);

    [RelayCommand]
    private void Acknowledge() => Acknowledged?.Invoke(this, EventArgs.Empty);
}
