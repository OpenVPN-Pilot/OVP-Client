using System.Text.Json;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.Core.Tests.Settings;

/// <summary>
/// The colours of the notification area icon as the settings file keeps them.
/// </summary>
public sealed class TrayIconSettingsTests
{
    public static TheoryData<TrayStatus> Statuses()
    {
        TheoryData<TrayStatus> data = [];

        foreach (TrayStatus status in Enum.GetValues<TrayStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    [Fact]
    public void NewSettings_ShowTheStatus_AndLeaveTheIdleIconAsThePlatformDrawsIt()
    {
        TrayIconSettings settings = new();

        Assert.True(settings.ShowStatus);
        Assert.Null(settings.IdleLocal);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void EveryState_StartsWithItsDefault_AndThatDefaultIsReadable(TrayStatus status)
    {
        TrayIconSettings settings = new();
        string? colour = settings.Get(status);

        Assert.Equal(TrayIconSettings.Default(status), colour);
        Assert.True(colour is null || TrayIconColour.TryParse(colour, out _));
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void EveryState_KeepsWhatIsSetForIt_AndOnlyThat(TrayStatus status)
    {
        TrayIconSettings settings = new();

        settings.Set(status, "#010203");

        foreach (TrayStatus other in Enum.GetValues<TrayStatus>())
        {
            Assert.Equal(other == status ? "#010203" : TrayIconSettings.Default(other), settings.Get(other));
        }
    }

    [Fact]
    public void Off_IsReadWhateverItsCase_AndOnlyForThatState()
    {
        TrayIconSettings settings = new() { Failed = " Off " };

        Assert.True(settings.IsOff(TrayStatus.Failed));
        Assert.False(settings.IsOff(TrayStatus.Connected));
        Assert.False(settings.IsOff(TrayStatus.IdleLocal));
    }

    [Fact]
    public void Clone_DoesNotShareTheColours()
    {
        PilotSettings settings = new();
        PilotSettings copy = settings.Clone();

        copy.Appearance.TrayIcon.Connected = "#000000";
        copy.Appearance.TrayIcon.ShowStatus = false;

        Assert.Equal(TrayIconSettings.Default(TrayStatus.Connected), settings.Appearance.TrayIcon.Connected);
        Assert.True(settings.Appearance.TrayIcon.ShowStatus);
    }

    [Fact]
    public void TheFile_KeepsAColourThatWasTurnedBackToTheOriginal()
    {
        // Null is a choice, the platform's own drawing, and must not come back as the default.
        PilotSettings settings = new();
        settings.Appearance.TrayIcon.Connected = null;

        string json = JsonSerializer.Serialize(settings, PilotSettingsTransfer.SerializerOptions);
        PilotSettings? read = JsonSerializer.Deserialize<PilotSettings>(json, PilotSettingsTransfer.SerializerOptions);

        Assert.NotNull(read);
        Assert.Null(read.Appearance.TrayIcon.Connected);
        Assert.Equal(TrayIconSettings.Default(TrayStatus.Failed), read.Appearance.TrayIcon.Failed);
    }

    [Fact]
    public void AFileWrittenBeforeTheColours_ReadsWithTheDefaults()
    {
        const string json = """{ "appearance": { "theme": "Dark" } }""";

        PilotSettings? read = JsonSerializer.Deserialize<PilotSettings>(json, PilotSettingsTransfer.SerializerOptions);

        Assert.NotNull(read);
        Assert.True(read.Appearance.TrayIcon.ShowStatus);
        Assert.Equal(TrayIconSettings.Default(TrayStatus.Reconnecting), read.Appearance.TrayIcon.Reconnecting);
    }
}
