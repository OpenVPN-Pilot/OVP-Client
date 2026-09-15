using Microsoft.EntityFrameworkCore;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Data.Tagging;

/// <summary>
/// Finds the tag a name stands for within one unit of work, adding it when the store has none.
/// </summary>
/// <remarks>
/// Looking a tag up in the database once per profile does not see a tag that an earlier profile in
/// the same unit of work added and that is not saved yet. The same tag was then added twice, the
/// unique index on the name refused the second, and a package with two profiles sharing a new tag
/// ended the application halfway through being imported. The catalogue therefore reads the tags
/// once and remembers what it adds.
///
/// Names are matched without regard to case, which is how the sidebar counts them and how dropping
/// a profile on a tag decides that it is already there. Matching by case here alone would put
/// "Office" and "office" in the sidebar as two entries that filter the same set.
/// </remarks>
public sealed class TagCatalogue
{
    private readonly PilotDbContext context;
    private readonly Dictionary<string, Tag> byName;

    private TagCatalogue(PilotDbContext context, Dictionary<string, Tag> byName)
    {
        this.context = context;
        this.byName = byName;
    }

    /// <summary>
    /// Reads the tags the store holds. Tracked, so a tag found here can be linked directly.
    /// </summary>
    public static async Task<TagCatalogue> LoadAsync(
        PilotDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<Tag> tags = await context.Tags.ToListAsync(cancellationToken);
        Dictionary<string, Tag> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (Tag tag in tags)
        {
            // A store written before names were matched this way may hold two that differ only in
            // case. The first one wins, and the other is left as it is rather than merged silently.
            byName.TryAdd(tag.Name, tag);
        }

        return new TagCatalogue(context, byName);
    }

    /// <summary>
    /// The tag with this name, added to the context when there is none yet.
    /// </summary>
    public Tag Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        if (byName.TryGetValue(trimmed, out Tag? existing))
        {
            return existing;
        }

        Tag tag = new() { Name = trimmed };
        context.Tags.Add(tag);
        byName[trimmed] = tag;

        return tag;
    }
}
