using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Starts and stops the application itself.
/// </summary>
/// <remarks>
/// `ovp` is the front door. Something that brings a tunnel up before a remote session should not
/// have to know where the executable lives, nor whether a window happens to be open, so starting and
/// stopping are commands here rather than a second executable to call.
/// </remarks>
internal static class StartApplicationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (PilotCommandClient.IsApplicationRunning())
        {
            Console.WriteLine("It is already running.");
            return 0;
        }

        bool headless = args.Contains("--headless", StringComparer.Ordinal);

        return await ApplicationLauncher.StartAsync(headless) ? 0 : 4;
    }
}

/// <summary>
/// Ends the running application, stopping its tunnels on the way out.
/// </summary>
internal static class StopApplicationCommand
{
    public static async Task<int> RunAsync()
    {
        string? reply = await PilotCommandClient.SendAsync(PilotCommands.Quit);

        if (reply is null)
        {
            Console.WriteLine("Nothing is running.");
            return 0;
        }

        Console.WriteLine(reply);
        return 0;
    }
}
