using System.Runtime.Versioning;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Helper;
using OpenVpnPilot.Platform.MacOS.Protocol;
using OpenVpnPilot.Platform.MacOS.Runtime;
using OpenVpnPilot.Platform.MacOS.Security;
using OpenVpnPilot.Platform.MacOS.Shell;

namespace OpenVpnPilot.Platform.MacOS.Tests.Client;

/// <summary>
/// The application's side: what it sends to the helper, and what it writes on this machine.
/// </summary>
public sealed class ClientSideTests
{
    [Fact]
    public void PullFilters_TheRouteProtectionTheApplicationAdds_IsUnderstood()
    {
        string[] options =
        [
            "--pull-filter ignore \"redirect-gateway\"",
            "--pull-filter ignore \"dhcp-option\"",
            "--pull-filter ignore \"block-outside-dns\"",
        ];

        Assert.True(LaunchRequest.TryReadPullFilters(options, out List<PullFilterSpecification> filters, out string? problem));
        Assert.Null(problem);

        Assert.Equal(
            [
                new PullFilterSpecification("ignore", "redirect-gateway"),
                new PullFilterSpecification("ignore", "dhcp-option"),
                new PullFilterSpecification("ignore", "block-outside-dns"),
            ],
            filters);
    }

    /// <summary>
    /// The helper takes no options, so anything else has to be reported here rather than sent and
    /// refused with no explanation.
    /// </summary>
    [Theory]
    [InlineData("--up /tmp/x")]
    [InlineData("--log /tmp/x")]
    [InlineData("--pull-filter")]
    [InlineData("--pull-filter ignore")]
    [InlineData("--pull-filter delete \"route\"")]
    [InlineData("--pull-filter ignore \"unterminated")]
    [InlineData("--pull-filter ignore \"route\" extra")]
    public void PullFilters_AnythingButAPullFilter_IsReported(string option)
    {
        Assert.False(LaunchRequest.TryReadPullFilters([option], out _, out string? problem));
        Assert.Contains(option, problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void PullFilters_NoOptions_IsAccepted()
    {
        Assert.True(LaunchRequest.TryReadPullFilters([], out List<PullFilterSpecification> filters, out _));
        Assert.Empty(filters);
    }

    [Theory]
    [InlineData("/Library/Application Support/OpenVpnPilot/Configurations/site.ovpn", "site.ovpn")]
    [InlineData("/Library/Application Support/OpenVpnPilot/Configurations/a b.ovpn", "a b.ovpn")]
    public void InstalledName_AConfigurationAnAdministratorInstalled_IsNamedByItsFile(string path, string expected)
    {
        Assert.Equal(expected, LaunchRequest.InstalledName(path));
    }

    [Theory]
    [InlineData("/Users/someone/Library/Application Support/OpenVpnPilot/runtime/site.ovpn")]
    [InlineData("/Library/Application Support/OpenVpnPilot/Configurations/nested/site.ovpn")]
    [InlineData("/Library/Application Support/OpenVpnPilot/ConfigurationsElsewhere/site.ovpn")]
    [InlineData("/tmp/site.ovpn")]
    public void InstalledName_AnythingElse_IsSentAsText(string path)
    {
        Assert.Null(LaunchRequest.InstalledName(path));
    }

    [Fact]
    public async Task Framing_ARequestAndItsAnswer_SurviveTheWire()
    {
        using MemoryStream wire = new();

        HelperRequest request = new()
        {
            Type = HelperMessageType.Launch,
            ProtocolVersion = HelperProtocol.Version,
            Launch = new LaunchSpecification
            {
                Configuration = "client\nremote vpn.example.com 1194\n",
                ManagementPort = 25340,
                ManagementPassword = "0123456789ABCDEF",
                PullFilters = [new PullFilterSpecification("ignore", "redirect-gateway")],
            },
        };

        await HelperFraming.WriteRequestAsync(wire, request, CancellationToken.None);
        wire.Position = 0;

        HelperRequest? read = await HelperFraming.ReadRequestAsync(wire, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(request.Type, read.Type);
        Assert.Equal(request.ProtocolVersion, read.ProtocolVersion);
        Assert.NotNull(read.Launch);
        Assert.Equal(request.Launch!.Configuration, read.Launch.Configuration);
        Assert.Equal(request.Launch.ManagementPort, read.Launch.ManagementPort);
        Assert.Equal(request.Launch.ManagementPassword, read.Launch.ManagementPassword);
        Assert.Equal(request.Launch.Verbosity, read.Launch.Verbosity);
        Assert.Equal(request.Launch.PullFilters, read.Launch.PullFilters);
    }

    [Fact]
    public async Task Framing_AClosedConnectionBetweenMessages_ReadsAsNothing()
    {
        using MemoryStream wire = new();

        Assert.Null(await HelperFraming.ReadRequestAsync(wire, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 255, 255, 255, 255 })]
    [InlineData(new byte[] { 0, 0, 0, 8, 1, 2, 3 })]
    public async Task Framing_AMessageThatIsNotOne_IsRefused(byte[] bytes)
    {
        using MemoryStream wire = new(bytes);

        await Assert.ThrowsAsync<HelperProtocolException>(
            async () => await HelperFraming.ReadRequestAsync(wire, CancellationToken.None));
    }

    [Fact]
    public async Task Framing_AMessageBeyondTheLimit_IsRefusedBeforeItIsWritten()
    {
        using MemoryStream wire = new();

        HelperRequest tooLarge = new()
        {
            Type = HelperMessageType.Launch,
            Launch = new LaunchSpecification { Configuration = new string('x', HelperProtocol.MaximumMessageBytes) },
        };

        await Assert.ThrowsAsync<HelperProtocolException>(
            async () => await HelperFraming.WriteRequestAsync(wire, tooLarge, CancellationToken.None));
    }

    /// <summary>
    /// A materialised configuration carries the private key, so it is written where only this
    /// account can read it, from the moment it exists.
    /// </summary>
    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public async Task Materialise_WritesTheConfigurationForTheOwnerAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "ovp-materialise-" + Guid.NewGuid().ToString("N"));

        try
        {
            MacProfileMaterializer materializer = new(root);

            await using MaterialisedProfile profile = await materializer.MaterialiseAsync(
                Guid.NewGuid(),
                "client\n",
                CancellationToken.None);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(profile.Path));

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(root));

            Assert.Equal("client\n", await File.ReadAllTextAsync(profile.Path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A file left behind by a previous run keeps the mode it was created with, so it is replaced
    /// rather than truncated.
    /// </summary>
    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public async Task Materialise_OverALeftoverFileWithWiderAccess_NarrowsItAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "ovp-materialise-" + Guid.NewGuid().ToString("N"));
        Guid profileId = Guid.NewGuid();

        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, $"{profileId:N}.ovpn");
            await File.WriteAllTextAsync(path, "stale", CancellationToken.None);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            MacProfileMaterializer materializer = new(root);
            await using MaterialisedProfile profile = await materializer.MaterialiseAsync(profileId, "client\n", CancellationToken.None);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(profile.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void RemoveStaleFiles_ClearsWhatAPreviousRunLeft()
    {
        string root = Path.Combine(Path.GetTempPath(), "ovp-materialise-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "one.ovpn"), "client");
            File.WriteAllText(Path.Combine(root, "two.ovpn"), "client");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "kept");

            Assert.Equal(2, new MacProfileMaterializer(root).RemoveStaleFiles());
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The login agent asks Launch Services to open the application, so it is opened the way a
    /// double click opens it rather than as a job launchd supervises.
    /// </summary>
    [Fact]
    public void LoginAgent_IsAPropertyListThatOpensTheApplicationHidden()
    {
        string definition = LoginAgent.BuildDefinition(
            "org.openvpnpilot.app.login",
            "org.openvpnpilot.app",
            ["/usr/bin/open", "-g", "-a", "/Applications/OpenVpnPilot.app", "--args", "--background"]);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", definition, StringComparison.Ordinal);
        Assert.Contains("<string>org.openvpnpilot.app.login</string>", definition, StringComparison.Ordinal);
        Assert.Contains("<string>--background</string>", definition, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>", definition, StringComparison.Ordinal);
        Assert.Contains("<key>AssociatedBundleIdentifiers</key>", definition, StringComparison.Ordinal);

        // It has to be a property list macOS can read, not only text that looks like one.
        System.Xml.Linq.XDocument.Parse(definition);
    }

    [Theory]
    [InlineData("/Applications/OpenVpnPilot.app/Contents/MacOS/OpenVpnPilot", "/Applications/OpenVpnPilot.app")]
    [InlineData("/Users/someone/Developer/OpenVpnPilot/bin/Debug/net10.0/OpenVpnPilot", null)]
    [InlineData("/Applications/OpenVpnPilot.app/Contents/Resources/OpenVpnPilot", null)]
    public void BundleOf_FindsTheApplicationBundleAnExecutableSitsIn(string executable, string? expected)
    {
        Assert.Equal(expected, LoginAgent.BundleOf(executable));
    }

    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void Installation_TheKeychainService_StaysWhatItWas()
    {
        // Changing it would orphan every sign in already stored under the old name.
        Assert.Equal("OpenVpnPilot", KeychainSecretStore.Service);
        Assert.Equal("org.openvpnpilot.helper", HelperInstallation.Label);
        Assert.Equal("/var/run/org.openvpnpilot.helper.sock", HelperInstallation.SocketPath);
        Assert.Equal("org.openvpnpilot.app.login", LaunchAgentAutoStartManager.Label);
    }

    [Fact]
    public void Installation_EveryPathTheHelperUsesIsAbsolute()
    {
        foreach (string path in new[]
        {
            HelperInstallation.SocketPath,
            HelperInstallation.ExecutablePath,
            HelperInstallation.JobDefinitionPath,
            HelperInstallation.SupportDirectory,
            HelperInstallation.OpenVpnPath,
            HelperInstallation.DnsScriptPath,
            HelperInstallation.ConfigurationsDirectory,
            HelperInstallation.RuntimeDirectory,
            HelperInstallation.UninstallScriptPath,
        })
        {
            Assert.StartsWith("/", path, StringComparison.Ordinal);
        }
    }
}
