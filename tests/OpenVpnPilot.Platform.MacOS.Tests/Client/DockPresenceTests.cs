using System.Text.RegularExpressions;

namespace OpenVpnPilot.Platform.MacOS.Tests.Client;

/// <summary>
/// Holds the two places that decide what the application starts as to the same answer.
/// </summary>
/// <remarks>
/// A window is what puts this application in the Dock, and launching must not. That takes both the
/// bundle declaring an accessory application and the application builder leaving Avalonia's own Dock
/// presence off, because the bundle decides what the process starts as and Avalonia sets the policy
/// again while it starts. Either one alone leaves the application regular for a moment, and a moment
/// is enough: measured on macOS 26, a copy started with its window hidden was entered in the Dock's
/// list of recent applications and left a tile there that outlived the process.
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
