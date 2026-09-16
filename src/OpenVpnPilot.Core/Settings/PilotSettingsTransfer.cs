using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenVpnPilot.Core.Settings;

/// <summary>
/// Carries settings from one machine to another, leaving behind what only makes sense where it was.
/// </summary>
/// <remarks>
/// A settings file mixes what a person prefers with what describes the machine: where the window was
/// on which display, where OpenVPN is installed, whether the system starts the application, and which
/// shared file this machine keeps in step with. The first kind is worth handing on and the second
/// kind is wrong anywhere else, so it is cleared on the way out and kept from the receiving machine
/// on the way in.
///
/// The form is the settings file's own, so a package carries exactly what the file would say.
/// </remarks>
public static class PilotSettingsTransfer
{
    /// <summary>
    /// The options the settings file is read and written with.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The settings as they travel, without anything that describes this machine.
    /// </summary>
    public static JsonElement Export(PilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        PilotSettings copy = settings.Clone();

        copy.General.MainWindow = new WindowPlacementSettings();
        copy.General.QuickMenuScreen = null;
        copy.General.StartWithSystem = false;
        copy.Advanced.OpenVpnPath = null;
        copy.Advanced.PortableMode = false;

        return JsonSerializer.SerializeToElement(copy, SerializerOptions);
    }

    /// <summary>
    /// The settings a package carried, with this machine's own values put back where they belong.
    /// </summary>
    /// <returns>The settings to apply, or null when what arrived cannot be read as settings.</returns>
    public static PilotSettings? Import(JsonElement carried, PilotSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        PilotSettings? incoming;

        try
        {
            incoming = carried.ValueKind == JsonValueKind.Object
                ? carried.Deserialize<PilotSettings>(SerializerOptions)
                : null;
        }
        catch (JsonException)
        {
            // A package from somewhere this cannot read. Nothing is applied rather than half of it.
            return null;
        }

        if (incoming is null)
        {
            return null;
        }

        incoming.Migrate();

        incoming.General.MainWindow = current.General.MainWindow.Clone();
        incoming.General.QuickMenuScreen = current.General.QuickMenuScreen;
        incoming.General.StartWithSystem = current.General.StartWithSystem;
        incoming.Advanced.OpenVpnPath = current.Advanced.OpenVpnPath;
        incoming.Advanced.PortableMode = current.Advanced.PortableMode;

        return incoming;
    }
}
