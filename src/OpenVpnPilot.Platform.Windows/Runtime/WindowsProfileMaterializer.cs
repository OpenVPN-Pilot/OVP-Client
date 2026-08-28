using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.Platform.Windows.Runtime;

/// <summary>
/// Writes configurations to a private runtime directory that the interactive service can read.
/// </summary>
/// <remarks>
/// The location is deliberate. The interactive service refuses to start a process whose working
/// directory sits directly under the user's AppData, failing with CreateProcessAsUser and
/// ERROR_DIRECTORY; it does not load the user profile before creating the process. Directories under
/// ProgramData work, are not subject to temporary file cleanup, and remain reachable by the service.
///
/// Each user gets their own subdirectory, keyed by security identifier, so that on a shared machine
/// one account cannot read another's materialised configuration. Both the directory and the file
/// have inheritance disabled, because a materialised profile contains the private key inline.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsProfileMaterializer : IProfileMaterializer
{
    private readonly string rootDirectory;

    public WindowsProfileMaterializer(string? rootDirectory = null)
    {
        this.rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenVpnPilot",
            "runtime");
    }

    public async Task<MaterialisedProfile> MaterialiseAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("The current identity has no user SID.");

        string directory = Path.Combine(rootDirectory, user.Value);
        EnsurePrivateDirectory(directory, user);

        string path = Path.Combine(directory, $"{profileId:N}.ovpn");
        FileInfo file = new(path);

        // Truncate first so a leftover file cannot be read while its permissions are being replaced.
        using (file.Create())
        {
        }

        try
        {
            file.SetAccessControl(BuildFileSecurity(user));
            await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(configuration), cancellationToken);
        }
        catch
        {
            // Never leave a configuration behind that could not be locked down.
            TryDelete(path);
            throw;
        }

        return new MaterialisedProfile(path);
    }

    public int RemoveStaleFiles()
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        int removed = 0;

        foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.ovpn", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException)
            {
                // A file still held open belongs to something else; it is left alone.
            }
            catch (UnauthorizedAccessException)
            {
                // Another account's directory, which this user must not touch.
            }
        }

        return removed;
    }

    private static void EnsurePrivateDirectory(string directory, SecurityIdentifier user)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        Directory.CreateDirectory(rootOf(directory));
        new DirectoryInfo(directory).Create(security);

        static string rootOf(string directory) =>
            Path.GetDirectoryName(directory) ?? directory;
    }

    private static FileSecurity BuildFileSecurity(SecurityIdentifier user)
    {
        FileSecurity security = new();

        // Inherited permissions would typically include groups that must not read a private key.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        // The service impersonates the caller, so the process reads the file as this same user.
        // The local system entry keeps the file readable for service side diagnostics.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            FileSystemRights.Read,
            AccessControlType.Allow));

        return security;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Reported through the original exception instead.
        }
        catch (UnauthorizedAccessException)
        {
            // Reported through the original exception instead.
        }
    }
}
