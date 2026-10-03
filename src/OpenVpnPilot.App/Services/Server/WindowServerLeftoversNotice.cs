using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.App.Views;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Server;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Tells the person in a window of its own that a server withdrew the account and that what this
/// computer kept of it has been removed.
/// </summary>
/// <remarks>
/// A window rather than the line under the sign in form, because it reports something the form does
/// not show: a copy of that server's profiles and its stored sign ins were removed from this
/// computer. The application stays where it is, so the notice says that too.
/// </remarks>
internal sealed class WindowServerLeftoversNotice : IServerLeftoversNotice
{
    private readonly ILocalizer localizer;
    private readonly IApplicationActivation? activation;
    private readonly bool showsWindows;

    public WindowServerLeftoversNotice(ILocalizer localizer, bool showsWindows, IApplicationActivation? activation)
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
                localizer.Translate("revoked.leftoversMessage", host),
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
