using OpenVpnPilot.Core.Settings;

namespace OpenVpnPilot.Core.Tests.Settings;

/// <summary>
/// Bringing a settings file written by an older build up to the current layout.
/// </summary>
/// <remarks>
/// The distinction this exists to protect is between a value somebody chose and a value that was
/// merely written out because the object had a property. Only the second kind may be replaced, and
/// only once.
/// </remarks>
public sealed class PilotSettingsMigrationTests
{
    [Fact]
    public void AFileFromBeforeTheStampExisted_AdoptsTheUpdateDefaults()
    {
        PilotSettings stored = new()
        {
            SchemaVersion = 0,
            Advanced = { CheckForUpdates = false, UpdateRepository = null },
        };

        Assert.True(stored.Migrate());

        Assert.True(stored.Advanced.CheckForUpdates);
        Assert.False(string.IsNullOrWhiteSpace(stored.Advanced.UpdateRepository));
        Assert.Equal(PilotSettings.CurrentSchemaVersion, stored.SchemaVersion);
    }

    [Fact]
    public void AFileAlreadyAtTheCurrentLayout_IsLeftAlone()
    {
        PilotSettings stored = new()
        {
            SchemaVersion = PilotSettings.CurrentSchemaVersion,
            Advanced = { CheckForUpdates = false, UpdateRepository = null },
        };

        Assert.False(stored.Migrate());

        // Somebody switched the check off through the settings screen. That is an answer, and it
        // survives every later start.
        Assert.False(stored.Advanced.CheckForUpdates);
        Assert.Null(stored.Advanced.UpdateRepository);
    }

    [Fact]
    public void AFileNamingTheRepositoryTheProjectMovedFrom_FollowsTheMove()
    {
        PilotSettings stored = new()
        {
            SchemaVersion = 1,
            Advanced = { UpdateRepository = AdvancedSettings.FormerUpdateRepository },
        };

        Assert.True(stored.Migrate());

        Assert.Equal(new PilotSettings().Advanced.UpdateRepository, stored.Advanced.UpdateRepository);
    }

    [Fact]
    public void AFileNamingSomebodyElsesRepository_KeepsIt()
    {
        // A fork follows its own releases, and the field is the only place that can be said.
        PilotSettings stored = new()
        {
            SchemaVersion = 1,
            Advanced = { UpdateRepository = "someone/their-fork" },
        };

        Assert.True(stored.Migrate());

        Assert.Equal("someone/their-fork", stored.Advanced.UpdateRepository);
    }

    [Fact]
    public void MigratingTwice_ChangesNothingTheSecondTime()
    {
        PilotSettings stored = new() { SchemaVersion = 0 };

        Assert.True(stored.Migrate());
        Assert.False(stored.Migrate());
    }

    [Fact]
    public void Cloning_CarriesTheLayoutStamp()
    {
        PilotSettings stored = new() { SchemaVersion = PilotSettings.CurrentSchemaVersion };

        Assert.Equal(PilotSettings.CurrentSchemaVersion, stored.Clone().SchemaVersion);
    }

    [Fact]
    public void AFreshSettingsObject_IsNotYetStamped()
    {
        // Otherwise a file written before the stamp existed would look current and never migrate.
        Assert.Equal(0, new PilotSettings().SchemaVersion);
    }
}
