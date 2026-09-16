using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Reports what the running application currently has connected.
/// </summary>
/// <remarks>
/// A tunnel belongs to the process that created it, so there is nothing to report when the
/// application is not running. Saying so is more useful than printing an empty list, which would
/// read as "nothing is connected".
/// </remarks>
internal static class StatusCommand
{
    public static async Task<int> RunAsync()
    {
        string? reply = await PilotCommandClient.SendAsync(PilotCommands.Status);

        if (reply is null)
        {
            Console.Error.WriteLine("The application is not running, so it holds no tunnels.");
            return 4;
        }

        Console.WriteLine(reply);
        return reply.StartsWith(PilotCommands.NothingConnected, StringComparison.Ordinal) ? 1 : 0;
    }
}

/// <summary>
/// Stops a tunnel the running application owns.
/// </summary>
internal static class DisconnectCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("A profile name or --all is required.");
            return 1;
        }

        string target = args.Contains(PilotCommands.AllMarker, StringComparer.Ordinal)
            ? PilotCommands.AllMarker
            : string.Join(' ', args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)));

        if (target.Length == 0)
        {
            Console.Error.WriteLine("A profile name or --all is required.");
            return 1;
        }

        string? reply = await PilotCommandClient.SendAsync(PilotCommands.Disconnect + target);

        if (reply is null)
        {
            Console.Error.WriteLine("The application is not running, so it holds no tunnels.");
            return 4;
        }

        Console.WriteLine(reply);
        return reply.StartsWith(PilotCommands.NoSuchProfile, StringComparison.Ordinal) ? 1 : 0;
    }
}
