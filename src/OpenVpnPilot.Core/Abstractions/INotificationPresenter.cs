namespace OpenVpnPilot.Core.Abstractions;

/// <summary>
/// Shows a short notification outside the application window.
/// </summary>
/// <remarks>
/// The text is already localized when it arrives here, because only the presentation layer knows
/// which language is active. The implementation decides what a notification looks like on the
/// platform, and reports honestly when it cannot show one at all.
/// </remarks>
public interface INotificationPresenter
{
    /// <summary>
    /// False when this machine offers no notification surface, so callers can stay quiet rather
    /// than pretending a message was delivered.
    /// </summary>
    public bool IsAvailable { get; }

    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the user activates a notification, carrying the tag it was shown with.
    /// </summary>
    public event EventHandler<string>? Activated;
}

/// <summary>
/// One notification, already in the user's language.
/// </summary>
/// <param name="Title">Short heading.</param>
/// <param name="Message">One or two lines of detail.</param>
/// <param name="Severity">Decides the icon and, on some platforms, the sound.</param>
/// <param name="Tag">
/// Opaque identifier reported back when the notification is activated, so the application can bring
/// up whatever it was about.
/// </param>
public sealed record NotificationRequest(
    string Title,
    string Message,
    NotificationSeverity Severity = NotificationSeverity.Information,
    string? Tag = null);

public enum NotificationSeverity
{
    Information,
    Warning,
    Error,
}
