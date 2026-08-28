using System.Globalization;
using System.Text;
using Avalonia;

namespace OpenVpnPilot.App;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            // A failure during startup would otherwise leave a running process with no window and
            // nothing in the log, which is impossible to diagnose. The report is written before the
            // logger exists, so it goes to a file next to the other application data.
            WriteStartupFailure(exception);
            return 1;
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

    private static void WriteStartupFailure(Exception exception)
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
                $"{DateTimeOffset.Now:O} startup failed{Environment.NewLine}{exception}{Environment.NewLine}");

            File.AppendAllText(Path.Combine(directory, "startup-failure.log"), report, Encoding.UTF8);
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
