using System.Text.RegularExpressions;

namespace OpenVpnPilot.Platform.MacOS.Tests.Client;

/// <summary>
/// Holds the two places that keep this application out of the Dock to the same answer.
/// </summary>
/// <remarks>
/// This application lives in the menu bar and never joins the Dock, because macOS enters an
/// application that is in the Dock in its list of recent applications and that entry outlives the
/// window, the quitting and the process. It takes both the bundle declaring an accessory application
/// and the application builder leaving Avalonia's own Dock presence off: the bundle decides what the
/// process starts as, and Avalonia sets the policy again while it starts. Either one alone leaves the
/// application regular for a moment, and a moment is enough to be entered.
///
/// Both are read as text, because neither the shell script nor the application's entry point is
/// something these tests compile against. Nothing else notices when one of them goes: the
/// application still runs, and the Dock keeps an entry nobody asked for.
/// </remarks>
public sealed class DockPresenceTests
{
    [Fact]
    public void TheBundleIsBuiltAsAnAccessoryApplication()
    {
        Assert.Matches(
            new Regex(@"<key>LSUIElement</key>\s*<true/>", RegexOptions.None, TimeSpan.FromSeconds(5)),
            ReadRepositoryFile(Path.Combine("installer", "build-macos.sh")));
    }

    [Fact]
    public void TheApplicationBuilderLeavesTheDockToTheWindow()
    {
        Assert.Contains(
            "ShowInDock = false",
            ReadRepositoryFile(Path.Combine("src", "OpenVpnPilot.App", "Program.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Found by walking up from the test assembly, because a test runs from its output directory.
    /// </summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{relativePath} was not found above the test assembly.");
    }
}
