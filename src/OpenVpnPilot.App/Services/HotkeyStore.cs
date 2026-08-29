using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.Services;

/// <summary>
/// Reads and writes the shortcut bindings.
/// </summary>
/// <remarks>
/// Bindings live in the database rather than in the settings file because one of them points at a
/// profile, and a shortcut that outlives the profile it connects would be a dangling reference the
/// settings file could not clean up on its own.
/// </remarks>
public interface IHotkeyStore
{
    public Task<IReadOnlyList<HotkeyBindingRecord>> GetBindingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the combination for an action, or removes the binding when the gesture is null.
    /// </summary>
    public Task SetBindingAsync(
        string actionId,
        string? gesture,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the shipped defaults for actions that are not bound yet.
    /// </summary>
    /// <remarks>
    /// Intended to run once, on the first start. The caller is responsible for not calling it again
    /// afterwards, because a shortcut the user cleared on purpose must stay cleared.
    /// </remarks>
    public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One binding as the interface needs it.
/// </summary>
public sealed record HotkeyBindingRecord(string ActionId, string Gesture, bool IsEnabled);

/// <summary>
/// Entity Framework backed implementation.
/// </summary>
public sealed class HotkeyStore : IHotkeyStore
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;

    public HotkeyStore(IDbContextFactory<PilotDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        this.contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<HotkeyBindingRecord>> GetBindingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.HotkeyBindings
            .AsNoTracking()
            .OrderBy(binding => binding.ActionId)
            .Select(binding => new HotkeyBindingRecord(binding.ActionId, binding.Gesture, binding.IsEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task SetBindingAsync(
        string actionId,
        string? gesture,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        HotkeyBinding? existing = await context.HotkeyBindings
            .FirstOrDefaultAsync(binding => binding.ActionId == actionId, cancellationToken);

        if (gesture is null or { Length: 0 })
        {
            if (existing is not null)
            {
                context.HotkeyBindings.Remove(existing);
                await context.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (existing is null)
        {
            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = actionId, Gesture = gesture });
        }
        else
        {
            existing.Gesture = gesture;
            existing.IsEnabled = true;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using PilotDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

        HashSet<string> known = await context.HotkeyBindings
            .Select(binding => binding.ActionId)
            .ToHashSetAsync(cancellationToken);

        bool added = false;

        foreach ((string actionId, string gesture) in HotkeyActions.Defaults)
        {
            if (known.Contains(actionId))
            {
                continue;
            }

            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = actionId, Gesture = gesture });
            added = true;
        }

        if (added)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
