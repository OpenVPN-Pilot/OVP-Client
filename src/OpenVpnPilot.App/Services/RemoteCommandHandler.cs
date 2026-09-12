using System.Globalization;
using System.Text;
using Avalonia.Threading;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Ipc;
using OpenVpnPilot.Core.Vpn;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Answers the commands the companion command sends to a running application.
/// </summary>
/// <remarks>
/// The replies are plain text meant to be printed straight to a terminal, so they are deliberately
/// not localized: a command line tool is read by whoever typed the command, and its output is often
/// piped into something else.
/// </remarks>
public sealed class RemoteCommandHandler
{
    private readonly MainWindowViewModel viewModel;
    private readonly ConnectionManager connections;

    public RemoteCommandHandler(MainWindowViewModel viewModel, ConnectionManager connections)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(connections);

        this.viewModel = viewModel;
        this.connections = connections;
    }

    /// <summary>
    /// Raised when another process asked this copy to end.
    /// </summary>
    /// <remarks>
    /// A copy started with no window has no menu to quit from, so ending it has to be something a
    /// launcher can ask for. Shutting down is the lifetime's business, not this handler's.
    /// </remarks>
    public event EventHandler? ShutdownRequested;

    /// <summary>
    /// Raised with the path of a file another process asked this copy to import.
    /// </summary>
    /// <remarks>
    /// The handler does not own a window and must not learn to. It reports what was asked for and
    /// whoever coordinates the windows decides what importing looks like.
    /// </remarks>
    public event EventHandler<string>? ImportRequested;

    /// <summary>
    /// Set once the profile list has been read, which is when a command naming a profile can be
    /// answered truthfully.
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// False in a copy started without a window, where there is nothing to show a wizard in.
    /// </summary>
    /// <remarks>
    /// A headless copy exists to be driven by a launcher. Importing there is what the companion
    /// command is for, and saying so is better than reporting an import that nothing carried out.
    /// </remarks>
    public bool HasWindows { get; set; } = true;

    public async Task<string> HandleAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Everything below touches the view model, which belongs to the user interface thread.
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (command == PilotCommands.Ping)
            {
                return IsReady ? PilotCommands.Ready : "starting";
            }

            if (command == PilotCommands.Status)
            {
                return DescribeStatus();
            }

            if (command == PilotCommands.Quit)
            {
                // Answered before shutting down, so the caller reads the reply rather than a
                // closed pipe. Stopping the tunnels happens on the way out.
                Dispatcher.UIThread.Post(() => ShutdownRequested?.Invoke(this, EventArgs.Empty));
                return "Stopping.";
            }

            if (command.StartsWith(PilotCommands.Connect, StringComparison.Ordinal))
            {
                return await ConnectAsync(command[PilotCommands.Connect.Length..].Trim());
            }

            if (command.StartsWith(PilotCommands.Disconnect, StringComparison.Ordinal))
            {
                return await DisconnectAsync(command[PilotCommands.Disconnect.Length..].Trim());
            }

            if (command.StartsWith(PilotCommands.Import, StringComparison.Ordinal))
            {
                return Import(command[PilotCommands.Import.Length..].Trim());
            }

            return $"Unknown command '{command}'.";
        });
    }

    /// <summary>
    /// Hands a file to whoever imports, having first established that it is there.
    /// </summary>
    /// <remarks>
    /// Checked here rather than in the wizard because the answer goes back to whoever asked. A shell
    /// that passed a path this process cannot see, which is what a mapped drive in another session
    /// looks like, should be told that rather than shown an empty wizard.
    /// </remarks>
    private string Import(string path)
    {
        if (path.Length == 0)
        {
            return "A path is required.";
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return $"'{path}' does not exist.";
        }

        if (!HasWindows)
        {
            return "This copy runs without a window. Import from a terminal with: ovp import <path>";
        }

        ImportRequested?.Invoke(this, path);
        return $"Importing {path}.";
    }

    private string DescribeStatus()
    {
        List<ProfileItemViewModel> active = viewModel.AllProfiles
            .Where(profile => !profile.IsIdle)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (active.Count == 0)
        {
            return PilotCommands.NothingConnected;
        }

        StringBuilder builder = new();

        foreach (ProfileItemViewModel profile in active)
        {
            // The state by its own name and not the label the interface shows. What goes back over
            // this channel is read by ovp, which decides an exit code from it and prints it to a
            // terminal, and both of those have to mean the same thing on a machine set to any
            // language. The window is where a translated word belongs.
            builder.Append(CultureInfo.InvariantCulture, $"{profile.Name,-44} {profile.Status.State,-14}");

            if (profile.Status.State == VpnConnectionState.Connected)
            {
                builder.Append(CultureInfo.InvariantCulture,
                    $" {profile.LocalAddressDisplay,-16} {profile.UptimeDisplay}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> ConnectAsync(string name)
    {
        ProfileItemViewModel? profile = Match(name);

        if (profile is null)
        {
            return $"{PilotCommands.NoSuchProfile} '{name}'.";
        }

        if (!profile.IsIdle)
        {
            return $"{profile.Name} is already {profile.Status.State.ToString().ToLowerInvariant()}.";
        }

        await viewModel.ConnectByIdAsync(profile.Id);
        return $"Connecting {profile.Name}.";
    }

    private async Task<string> DisconnectAsync(string name)
    {
        if (name == PilotCommands.AllMarker)
        {
            int count = connections.ActiveCount;
            await connections.DisconnectAllAsync();
            return $"Stopped {count} connection(s).";
        }

        ProfileItemViewModel? profile = Match(name);

        if (profile is null)
        {
            return $"{PilotCommands.NoSuchProfile} '{name}'.";
        }

        if (profile.IsIdle)
        {
            return $"{profile.Name} is not connected.";
        }

        await connections.DisconnectAsync(profile.Id);
        return $"Disconnected {profile.Name}.";
    }

    /// <summary>
    /// Finds a profile by name, preferring an exact match over a partial one.
    /// </summary>
    /// <remarks>
    /// Partial matching is what makes the command usable, but a name that is also the prefix of a
    /// longer one must still reach the profile the user actually named.
    /// </remarks>
    private ProfileItemViewModel? Match(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        return viewModel.AllProfiles
            .FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? viewModel.AllProfiles
                .Where(profile => profile.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }
}
