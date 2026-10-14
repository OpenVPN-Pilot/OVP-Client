using System.Xml.Linq;

namespace OpenVpnPilot.Platform.MacOS.Shell;

/// <summary>
/// The job definition that opens the application at login, and where an executable's bundle is.
/// </summary>
/// <remarks>
/// Apart from the manager that writes the file, because a property list macOS can read and a bundle
/// found correctly are both worth a test of their own.
/// </remarks>
internal static class LoginAgent
{
    /// <summary>
    /// The launchd job definition, written as a property list.
    /// </summary>
    public static string BuildDefinition(string label, string bundleIdentifier, IReadOnlyList<string> arguments)
    {
        XElement Key(string name) => new("key", name);
        XElement Text(string value) => new("string", value);

        XDocument document = new(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    Key("Label"),
                    Text(label),
                    Key("ProgramArguments"),
                    new XElement("array", arguments.Select(Text)),
                    Key("RunAtLoad"),
                    new XElement("true"),
                    Key("LimitLoadToSessionType"),
                    Text("Aqua"),
                    Key("ProcessType"),
                    Text("Interactive"),
                    Key("AssociatedBundleIdentifiers"),
                    new XElement("array", Text(bundleIdentifier)))));

        return document.Declaration + Environment.NewLine + document.ToString() + Environment.NewLine;
    }

    /// <summary>
    /// The application bundle an executable sits in, when it sits in one.
    /// </summary>
    /// <remarks>
    /// The executable is an absolute macOS path. It is split as text rather than through the file
    /// system types, which would resolve it against the drive and separator of whatever system the
    /// decision is tested on.
    /// </remarks>
    public static string? BundleOf(string executable)
    {
        string[] parts = executable.Split('/');

        return parts.Length >= 4
            && parts[^2] == "MacOS"
            && parts[^3] == "Contents"
            && parts[^4].EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? string.Join('/', parts[..^3])
                : null;
    }
}
