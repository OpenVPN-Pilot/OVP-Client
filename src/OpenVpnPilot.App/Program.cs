using System.Globalization;
using System.Text;
using Avalonia;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Ipc;

namespace OpenVpnPilot.App;

internal sealed class Program
{
    /// <summary>
    /// Reported when the options could not be understood.
    /// </summary>
    private const int InvalidArguments = 1;

    /// <summary>
    /// Reported when actions were asked for and no copy was listening to carry them out.
    /// </summary>
    private const int NothingListening = 4;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        ReportFailures();

        StartupOptions options = StartupOptions.Parse(args);

        if (options.Error is { } problem)
        {
            WriteConsole($"{problem}{Environment.NewLine}{Environment.NewLine}{StartupOptions.Usage}");
            return InvalidArguments;
        }

        if (options.ShowHelp)
        {
            WriteConsole(StartupOptions.Usage);
            return 0;
        }

        SingleInstanceGuard guard = new();

        if (!guard.TryClaim())
        {
            // Another copy owns the profile store and the running tunnels, so this one hands over.
            int result = HandOver(options);
            guard.Dispose();
            return result;
        }

        if (options.Quit)
        {
            // Nothing was running, so there is nothing to end and no reason to start one.
            guard.Dispose();
            return 0;
        }

        try
        {
            App.InstanceGuard = guard;
            App.Startup = options;

            DeclareApplicationIdentity();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            // A failure during startup would otherwise leave a running process with no window and
            // nothing in the log, which is impossible to diagnose. The report is written before the
            // logger exists, so it goes to a file next to the other application data.
            WriteFailure("startup", exception);
            return 1;
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>
    /// Passes the requested actions to the copy that already runs.
    /// </summary>
    /// <remarks>
    /// A launcher does not know or care whether the application was already open, so the same
    /// command line has to mean the same thing either way. Without actions this is the ordinary
    /// case of someone starting it twice, which brings the window forward.
    /// </remarks>
    private static int HandOver(StartupOptions options)
    {
        if (!options.HasActions)
        {
            SingleInstanceGuard.RequestActivationAsync().GetAwaiter().GetResult();
            return 0;
        }

        bool answered = false;

        foreach (string command in options.ToCommands())
        {
            string? reply = PilotCommandClient.SendAsync(command).GetAwaiter().GetResult();

            if (reply is null)
            {
                continue;
            }

            answered = true;

            if (reply.Length > 0)
            {
                WriteConsole(reply);
            }
        }

        return answered ? 0 : NothingListening;
    }

    /// <summary>
    /// Tells Windows who this process is, before anything it shows can be attributed to nobody.
    /// </summary>
    /// <remarks>
    /// This has to happen before the first notification and therefore before the framework starts,
    /// because the shell reads the identity from the process when the notification is raised and a
    /// process that has none is given a generated one. It is a platform call, so the platform
    /// neutral part of the application never sees it: on a system that is not Windows there is
    /// simply nothing to declare.
    /// </remarks>
    private static void DeclareApplicationIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            Platform.Windows.Shell.WindowsAppIdentity.Apply("OpenVPN Pilot");
        }
    }

    /// <summary>
    /// Writes to the terminal that started the process, when there is one.
    /// </summary>
    /// <remarks>
    /// The application is a windowed executable, so it has no console of its own. Started from a
    /// terminal it inherits that one and this is read; started from a shortcut it goes nowhere,
    /// which is the correct outcome for a message nobody asked to see.
    /// </remarks>
    private static void WriteConsole(string text)
    {
        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // There is no console attached. The exit code still carries the outcome.
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Records the failures that end the process without passing through anything that logs.
    /// </summary>
    /// <remarks>
    /// An exception nobody catches leaves the runtime raising 0xE0434352, which Windows shows as an
    /// unknown software error naming an address in a system library and nothing else. During a
    /// shutdown that dialog is all there is, because the process is gone before it could write
    /// anything. Recording it here does not swallow it: the process still ends, and the report says
    /// what ended it.
    /// </remarks>
    private static void ReportFailures()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteFailure("unhandled exception", exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) => WriteFailure("unobserved task", args.Exception);
    }

    private static void WriteFailure(string stage, Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenVpnPilot",
                "logs");

            Directory.CreateDirectory(directory);

            string report = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}{exception}{Environment.NewLine}");

            File.AppendAllText(Path.Combine(directory, "failure.log"), report, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Nothing further can be done at this point.
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing further can be done at this point.
        }
    }
}
