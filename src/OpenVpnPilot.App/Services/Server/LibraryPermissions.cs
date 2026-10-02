using Microsoft.Extensions.Logging;
using OpenVpnPilot.Core.Server.Contracts;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// Whether the person may change what everybody shares: create, edit, import and delete profiles.
/// </summary>
/// <remarks>
/// Always on this computer's own library. With a server it is the role the server last gave, known
/// while offline as well, so a person who is not an administrator is never offered a button that
/// could only earn a refusal. Favourites, shortcut slots, settings and connecting are everybody's.
/// </remarks>
public interface ILibraryPermissions
{
    public bool CanChangeShared { get; }

    /// <summary>
    /// Raised after <see cref="CanChangeShared"/> changed, on whichever thread noticed it.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads the role again.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The local library, where the person owns everything.
/// </summary>
public sealed class LocalLibraryPermissions : ILibraryPermissions
{
    public bool CanChangeShared => true;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// A server's copy: administrators only, as the role was last known.
/// </summary>
/// <remarks>
/// Off until the role has been read, so nothing is offered that a moment later turns out to be
/// forbidden. Read again after every synchronisation, which is where a changed role arrives.
/// </remarks>
public sealed class ServerLibraryPermissions : ILibraryPermissions, IDisposable
{
    private readonly IServerAccountState account;
    private readonly ISyncEngine engine;
    private readonly ILogger<ServerLibraryPermissions> logger;
    private volatile bool canChangeShared;

    public ServerLibraryPermissions(
        IServerAccountState account,
        ISyncEngine engine,
        ILogger<ServerLibraryPermissions> logger)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        this.account = account;
        this.engine = engine;
        this.logger = logger;

        engine.StatusChanged += OnStatusChanged;
    }

    public bool CanChangeShared => canChangeShared;

    public event EventHandler? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        CurrentUserResponse? user = await account.ReadLastKnownUserAsync(cancellationToken);
        bool now = user?.IsAdministrator == true;

        if (now != canChangeShared)
        {
            canChangeShared = now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => engine.StatusChanged -= OnStatusChanged;

    private async void OnStatusChanged(object? sender, EventArgs arguments)
    {
        if (engine.Status.State == SyncState.Synchronising)
        {
            return;
        }

        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An event handler has nobody to hand a failure to. The last known answer stands.
            ServerLibraryLog.PermissionsUnread(logger, exception);
        }
    }
}
