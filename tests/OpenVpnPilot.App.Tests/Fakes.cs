using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Settings;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Management;
using OpenVpnPilot.OpenVpn.Runtime;

namespace OpenVpnPilot.App.Tests;

/// <summary>
/// Returns every key as its own text, so a test asserts on what was asked for rather than on a
/// translation somebody may reword.
/// </summary>
internal sealed class StubLocalizer : ILocalizer
{
    public string CurrentLanguage => "en";

    public IReadOnlyList<LanguageDescriptor> AvailableLanguages { get; } =
        [new LanguageDescriptor("en", "English", "English")];

    public IReadOnlyCollection<string> Keys { get; } = [];

    public event EventHandler? LanguageChanged
    {
        add { }
        remove { }
    }

    public string this[string key] => key;

    public string Translate(string key, params object?[] arguments) => key;

    public bool TrySetLanguage(string languageCode) => languageCode == "en";

    public void Reload()
    {
    }
}

/// <summary>
/// A profile store held in memory, with only the parts a view model actually reaches for.
/// </summary>
/// <remarks>
/// The real store runs against SQLite and is covered where that matters. What the main view model
/// needs from it is a list, the tags on each entry and somewhere for a change to land, and a fake
/// makes the state a test sets up visible in one place instead of three tables.
/// </remarks>
internal sealed class FakeProfileStore : IProfileStore
{
    private readonly Dictionary<Guid, List<string>> tags = [];

    public List<Profile> Profiles { get; } = [];

    public List<Guid> Deleted { get; } = [];

    /// <summary>
    /// Every configuration written, in order, so a test can tell an edit from a save that changed nothing.
    /// </summary>
    public List<string> ConfigurationUpdates { get; } = [];

    public Profile Add(string name, params string[] tagNames)
    {
        Profile profile = new()
        {
            Name = name,
            Configuration = "client",
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        };

        Profiles.Add(profile);
        tags[profile.Id] = [.. tagNames];
        return profile;
    }

    public IReadOnlyList<string> TagsOf(Guid profileId) =>
        tags.TryGetValue(profileId, out List<string>? names) ? names : [];

    public Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Profile>>([.. Profiles]);

    public Task<IReadOnlyList<TagSummary>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TagSummary>>(
            tags.Values
                .SelectMany(names => names)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new TagSummary(
                    Guid.NewGuid(),
                    name,
                    null,
                    tags.Count(entry => entry.Value.Contains(name, StringComparer.OrdinalIgnoreCase))))
                .ToList());

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetProfileTagsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>(
            tags.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)[.. entry.Value]));

    public Task SetProfileTagsAsync(
        Guid profileId,
        IReadOnlyList<string> tagNames,
        CancellationToken cancellationToken = default)
    {
        tags[profileId] = [.. tagNames];
        return Task.CompletedTask;
    }

    public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        Deleted.Add(profileId);
        Profiles.RemoveAll(profile => profile.Id == profileId);
        tags.Remove(profileId);
        return Task.CompletedTask;
    }

    public Task<string?> GetConfigurationAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Configuration);

    public Task RecordConnectionAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetFavouriteAsync(Guid profileId, bool isFavourite, CancellationToken cancellationToken = default)
    {
        Profile? profile = Profiles.FirstOrDefault(item => item.Id == profileId);

        if (profile is not null)
        {
            profile.IsFavourite = isFavourite;
        }

        return Task.CompletedTask;
    }

    public Task SetFavouriteSlotAsync(Guid profileId, int? slot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Guid?> GetProfileInSlotAsync(int slot, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    public Task<Guid?> GetLastConnectedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    public Task RenameProfileAsync(Guid profileId, string name, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetProfileNotesAsync(Guid profileId, string? notes, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ConfigurationUpdate> UpdateConfigurationAsync(
        Guid profileId,
        string configuration,
        CancellationToken cancellationToken = default)
    {
        Profile? profile = Profiles.FirstOrDefault(candidate => candidate.Id == profileId);

        if (profile is null)
        {
            return Task.FromResult(new ConfigurationUpdate(false, null));
        }

        profile.Configuration = configuration;
        ConfigurationUpdates.Add(configuration);
        return Task.FromResult(new ConfigurationUpdate(true, null));
    }

    public Task SetRouteProtectionAsync(
        Guid profileId,
        bool? protectRoutes,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class FakeSettingsService : ISettingsService
{
    public PilotSettings Current { get; private set; } = new();

    public event EventHandler<PilotSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<PilotSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        change(Current);
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(PilotSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A keystore that protects nothing, which is exactly why it is only a test.
/// </summary>
internal sealed class FakeSecrets : ISecretStore
{
    private readonly Dictionary<string, StoredSecret> entries = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.GetValueOrDefault(reference));

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        entries[reference] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        entries.Remove(reference);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. entries.Keys]);

    public Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        int count = entries.Count;
        entries.Clear();
        return Task.FromResult(count);
    }
}

/// <summary>
/// Builds a connection manager that can be constructed but never starts anything.
/// </summary>
/// <remarks>
/// The main view model needs one to exist. What a manager does with a real tunnel is covered where
/// the manager lives; here it only has to answer that nothing is connected.
/// </remarks>
internal static class IdleConnections
{
    public static ConnectionManager Create() => new(
        new RefusingLauncher(),
        new UnusableChannelFactory(),
        new SilentCredentialProvider(),
        new NowhereMaterializer());

    private sealed class RefusingLauncher : IOpenVpnLauncher
    {
        public Task<OpenVpnLaunchResult> LaunchAsync(
            OpenVpnLaunchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OpenVpnLaunchResult.Refused(0x80070005, "Nothing is launched in a test."));
    }

    private sealed class UnusableChannelFactory : IManagementChannelFactory
    {
        public Task<Stream> ConnectAsync(int port, CancellationToken cancellationToken) =>
            throw new IOException("Nothing is connected in a test.");
    }

    private sealed class SilentCredentialProvider : ICredentialProvider
    {
        public Task<VpnCredentials?> RequestAsync(
            CredentialRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<VpnCredentials?>(null);
    }

    private sealed class NowhereMaterializer : IProfileMaterializer
    {
        public Task<MaterialisedProfile> MaterialiseAsync(
            Guid profileId,
            string configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MaterialisedProfile(
                Path.Combine(Path.GetTempPath(), $"openvpnpilot-test-{profileId:N}.ovpn")));

        public int RemoveStaleFiles() => 0;
    }
}

/// <summary>
/// Reports an environment that can connect, so a test is about the view model rather than about
/// whether the machine running it has OpenVPN installed.
/// </summary>
internal sealed class ReadyEnvironmentProbe : IOpenVpnEnvironmentProbe
{
    public Task<OpenVpnEnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new OpenVpnEnvironmentReport(
        [
            new EnvironmentCheck(EnvironmentCheckId.Installation, EnvironmentCheckStatus.Passed, "Test"),
            new EnvironmentCheck(EnvironmentCheckId.InteractiveService, EnvironmentCheckStatus.Passed, "Test"),
        ]));

    public static EnvironmentGate Gate() => new(
        new ReadyEnvironmentProbe(),
        TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentGate>.Instance);
}

/// <summary>
/// An update coordinator that contacts nothing, because the settings name no repository.
/// </summary>
/// <remarks>
/// A test must never reach the network. The check is skipped when no repository is configured, so
/// leaving that empty is enough and no stand in for the release list is needed.
/// </remarks>
internal static class SilentUpdates
{
    public static UpdateCoordinator Coordinator()
    {
        FakeSettingsService settings = new();
        settings.Current.Advanced.CheckForUpdates = false;
        settings.Current.Advanced.UpdateRepository = null;

        return new UpdateCoordinator(
            settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCoordinator>.Instance);
    }
}
