using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Services.Library;

/// <summary>
/// The platform keystore, reporting when a profile's sign in is written or removed.
/// </summary>
/// <remarks>
/// A shared library carries the sign ins, so storing one after a prompt, forgetting one after the
/// server refused it, or signing in again, are all changes the library has to hear about. They are
/// made from several places, and the keystore is the one thing they all go through.
/// </remarks>
public sealed class ObservedSecretStore : ISecretStore
{
    private readonly ISecretStore inner;

    public ObservedSecretStore(ISecretStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <summary>
    /// Raised after a profile's sign in was written or removed. Not raised for anything else stored.
    /// </summary>
    public event EventHandler? ProfileSecretsChanged;

    public bool IsAvailable => inner.IsAvailable;

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default) =>
        inner.TryReadAsync(reference, cancellationToken);

    public async Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(reference, secret, cancellationToken);
        Report(reference);
    }

    public async Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        await inner.DeleteAsync(reference, cancellationToken);
        Report(reference);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        inner.ListAsync(cancellationToken);

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        int removed = await inner.ClearAsync(cancellationToken);

        if (removed > 0)
        {
            ProfileSecretsChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private void Report(string reference)
    {
        if (SecretReference.TryParse(reference, out _, out _))
        {
            ProfileSecretsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
