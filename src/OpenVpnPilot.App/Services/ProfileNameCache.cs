using System.Collections.Concurrent;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Resolves a profile identifier to the name shown to the user.
/// </summary>
public interface IProfileNameLookup
{
    public string GetDisplayName(Guid profileId);
}

/// <summary>
/// Holds the profile names currently loaded, so components that only need a label do not have to
/// query the database or depend on the view model.
/// </summary>
/// <remarks>
/// This exists to keep the credential prompt independent of the main view model. Wiring the prompt
/// straight to the view model creates a dependency cycle, because the view model needs the
/// connection manager, which needs the credential provider.
/// </remarks>
public sealed class ProfileNameCache : IProfileNameLookup
{
    private readonly ConcurrentDictionary<Guid, string> names = new();

    public void Replace(IEnumerable<KeyValuePair<Guid, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        names.Clear();
        foreach ((Guid id, string name) in entries)
        {
            names[id] = name;
        }
    }

    /// <summary>
    /// The profile's name, or a neutral placeholder when it is not loaded.
    /// </summary>
    public string GetDisplayName(Guid profileId) =>
        names.TryGetValue(profileId, out string? name) ? name : "this profile";
}
