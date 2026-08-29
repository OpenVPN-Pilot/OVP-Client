using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Writes and reads portable packages for the user interface.
/// </summary>
/// <remarks>
/// Exists so the export and import screens never touch a database context directly, in the same way
/// the profile store keeps Entity Framework out of the view models.
/// </remarks>
public interface IProfilePackageWriter
{
    /// <summary>
    /// Writes the given profiles to a package.
    /// </summary>
    /// <param name="passphrase">
    /// Required. A package is one file carrying private keys, so it is encrypted or it is not
    /// written.
    /// </param>
    /// <returns>How many profiles were written.</returns>
    public Task<int> WriteAsync(
        string path,
        IReadOnlyCollection<Guid> profileIds,
        string passphrase,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a package and writes it into the store.
    /// </summary>
    public Task<PackageApplyResult> ApplyAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class ProfilePackageWriter : IProfilePackageWriter
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly TimeProvider timeProvider;

    public ProfilePackageWriter(
        IDbContextFactory<PilotDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
    }

    public async Task<int> WriteAsync(
        string path,
        IReadOnlyCollection<Guid> profileIds,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profileIds);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        ProfilePackageContent content = await new ProfilePackageService(context, timeProvider)
            .CreateAsync(profileIds, credentials: null, cancellationToken);

        await ProfilePackageFile.WriteAsync(path, content, passphrase, cancellationToken);

        return content.Profiles.Count;
    }

    public async Task<PackageApplyResult> ApplyAsync(
        string path,
        string? passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ProfilePackageContent content = await ProfilePackageFile.ReadAsync(path, passphrase, cancellationToken);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await new ProfilePackageService(context, timeProvider).ApplyAsync(content, cancellationToken);
    }
}
