using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.App.Services.Server;
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
/// <remarks>
/// A binding the person changes is reported to the change recorder inside the same save. Writing the
/// defaults is not: on a machine that joins a server they would otherwise be pushed over the
/// shortcuts the person already keeps there, before those had even been read.
/// </remarks>
public sealed class HotkeyStore : IHotkeyStore
{
    private readonly IDbContextFactory<PilotDbContext> contextFactory;
    private readonly IChangeRecorder changeRecorder;

    public HotkeyStore(IDbContextFactory<PilotDbContext> contextFactory, IChangeRecorder changeRecorder)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(changeRecorder);

        this.contextFactory = contextFactory;
        this.changeRecorder = changeRecorder;
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
                await StageAsync(context, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (existing is null)
        {
            context.HotkeyBindings.Add(new HotkeyBinding { ActionId = actionId, Gesture = gesture });
        }
        else if (string.Equals(existing.Gesture, gesture, StringComparison.Ordinal) && existing.IsEnabled)
        {
            return;
        }
        else
        {
            existing.Gesture = gesture;
            existing.IsEnabled = true;
        }

        await StageAsync(context, cancellationToken);
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

    private Task StageAsync(PilotDbContext context, CancellationToken cancellationToken) =>
        changeRecorder.StageAsync(context, PendingChangeKind.Hotkeys, cancellationToken: cancellationToken);
}
