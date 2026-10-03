using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Tells the person in a window of its own that the account no longer has access.
/// </summary>
/// <remarks>
/// A window rather than a notification, because what follows is a restart into another set of
/// profiles, and a person who missed a passing message would find their list changed without a word.
/// A copy started without any window has nobody to tell; the log carries it there.
/// </remarks>
internal sealed class WindowAccountRevokedNotice : IAccountRevokedNotice
{
    private readonly ILocalizer localizer;
    private readonly IApplicationActivation? activation;
    private readonly bool showsWindows;

    public WindowAccountRevokedNotice(ILocalizer localizer, bool showsWindows, IApplicationActivation? activation)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        this.localizer = localizer;
        this.showsWindows = showsWindows;
        this.activation = activation;
    }

    public Task ShowAsync(ServerWipeDirective directive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);

        if (!showsWindows)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string host = directive.BaseAddress.IsDefaultPort ? directive.BaseAddress.Host : directive.BaseAddress.Authority;

        Dispatcher.UIThread.Post(() =>
        {
            NoticeViewModel notice = new(
                localizer["revoked.title"],
                localizer.Translate("revoked.message", host),
                string.IsNullOrEmpty(directive.RequestId) ? null : localizer.Translate("signIn.reference", directive.RequestId));

            NoticeWindow window = new() { DataContext = notice };
            window.Closed += (_, _) => seen.TrySetResult();
            window.Show();

            activation?.BringToFront();
            window.Activate();
        });

        return seen.Task.WaitAsync(cancellationToken);
    }
}
