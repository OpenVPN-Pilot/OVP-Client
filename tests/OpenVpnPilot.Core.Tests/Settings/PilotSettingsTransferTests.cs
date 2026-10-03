using System.Text.Json;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Core.Storage;

namespace OpenVpnPilot.Core.Tests.Settings;

/// <summary>
/// What travels between machines, and what stays where it belongs.
/// </summary>
/// <remarks>
/// The storage mode and the installation identity are the values whose escape would do real damage:
/// a machine switched to a store nobody chose there, or two machines a server cannot tell apart.
/// </remarks>
public sealed class PilotSettingsTransferTests
{
    private static readonly Guid ExportingInstallation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ReceivingInstallation = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void Export_LeavesTheModeTheServerAndTheIdentityBehind()
    {
        PilotSettings settings = ServerMachine(ExportingInstallation);

        JsonElement exported = PilotSettingsTransfer.Export(settings);
        string text = exported.GetRawText();

        Assert.DoesNotContain("pilot.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ExportingInstallation.ToString(), text, StringComparison.OrdinalIgnoreCase);

        PilotSettings? read = exported.Deserialize<PilotSettings>(PilotSettingsTransfer.SerializerOptions);
        Assert.NotNull(read);
        Assert.Equal(StorageMode.Local, read.Storage.Mode);
        Assert.Null(read.Storage.ServerUrl);
        Assert.Null(read.Installation.Id);
    }

    [Fact]
    public void Export_DoesNotChangeTheSettingsItWasGiven()
    {
        PilotSettings settings = ServerMachine(ExportingInstallation);

        PilotSettingsTransfer.Export(settings);

        Assert.Equal(StorageMode.Server, settings.Storage.Mode);
        Assert.Equal(ExportingInstallation, settings.Installation.Id);
    }

    [Fact]
    public void Import_KeepsTheReceivingMachinesModeServerAndIdentity()
    {
        PilotSettings receiving = new()
        {
            Storage = new StorageSettings { Mode = StorageMode.Local, ServerUrl = "https://other.example.com" },
            Installation = new InstallationSettings { Id = ReceivingInstallation },
        };

        // Written by hand rather than through Export, so the carried values are really there.
        JsonElement carried = JsonSerializer.SerializeToElement(
            ServerMachine(ExportingInstallation),
            PilotSettingsTransfer.SerializerOptions);

        PilotSettings? imported = PilotSettingsTransfer.Import(carried, receiving);

        Assert.NotNull(imported);
        Assert.Equal(StorageMode.Local, imported.Storage.Mode);
        Assert.Equal("https://other.example.com", imported.Storage.ServerUrl);
        Assert.Equal(ReceivingInstallation, imported.Installation.Id);

        // The portable part still arrives.
        Assert.Equal("de", imported.General.Language);
    }

    [Fact]
    public void Import_ModeOfTheReceivingMachine_IsACopyNotTheSameObject()
    {
        PilotSettings receiving = ServerMachine(ReceivingInstallation);
        JsonElement carried = PilotSettingsTransfer.Export(new PilotSettings());

        PilotSettings? imported = PilotSettingsTransfer.Import(carried, receiving);

        Assert.NotNull(imported);
        Assert.NotSame(receiving.Storage, imported.Storage);
        Assert.NotSame(receiving.Installation, imported.Installation);
    }

    private static PilotSettings ServerMachine(Guid installation) => new()
    {
        General = new GeneralSettings { Language = "de" },
        Storage = new StorageSettings { Mode = StorageMode.Server, ServerUrl = "https://pilot.example.com" },
        Installation = new InstallationSettings { Id = installation },
    };
}
