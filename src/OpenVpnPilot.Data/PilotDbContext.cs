using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.Data;

/// <summary>
/// The local profile store.
/// </summary>
/// <remarks>
/// Everything lives in one SQLite file. Credentials are never stored here, only a reference to the
/// entry in the operating system keystore, so the file can be copied or exported safely.
/// </remarks>
public sealed class PilotDbContext : DbContext
{
    public PilotDbContext(DbContextOptions<PilotDbContext> options)
        : base(options)
    {
    }

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ProfileTag> ProfileTags => Set<ProfileTag>();

    public DbSet<CredentialSet> CredentialSets => Set<CredentialSet>();

    public DbSet<WatchedFolder> WatchedFolders => Set<WatchedFolder>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    public DbSet<HotkeyBinding> HotkeyBindings => Set<HotkeyBinding>();

    /// <summary>
    /// Stores every instant as ticks since the epoch of <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <remarks>
    /// SQLite refuses to order or compare a value whose type is <see cref="DateTimeOffset"/>, so a
    /// history filtered by period or sorted by time cannot be expressed at all while the default
    /// text storage is used. Ticks sort as integers, index well, and are exact.
    ///
    /// Every timestamp this application writes is an instant in universal time, so the offset
    /// carries no information and is not worth preserving.
    /// </remarks>
    private static readonly ValueConverter<DateTimeOffset, long> InstantConverter = new(
        value => value.UtcTicks,
        value => new DateTimeOffset(value, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableInstantConverter = new(
        value => value == null ? null : value.Value.UtcTicks,
        value => value == null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ApplyInstantStorage(modelBuilder);

        modelBuilder.Entity<Profile>(entity =>
        {
            entity.HasIndex(profile => profile.Name);

            // Duplicate detection on import relies on this being fast.
            entity.HasIndex(profile => profile.ContentHash);

            // A favourite slot is bound to a hotkey, so at most one profile may hold each slot.
            entity.HasIndex(profile => profile.FavouriteSlot)
                .IsUnique()
                .HasFilter("[FavouriteSlot] IS NOT NULL");

            entity.Property(profile => profile.Name).HasMaxLength(200);
            entity.Property(profile => profile.Protocol).HasMaxLength(10);
            entity.Property(profile => profile.ContentHash).HasMaxLength(64);
            entity.Property(profile => profile.Colour).HasMaxLength(9);

            entity.HasOne(profile => profile.CredentialSet)
                .WithMany(set => set.Profiles)
                .HasForeignKey(profile => profile.CredentialSetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.Property(tag => tag.Name).HasMaxLength(100);
            entity.HasIndex(tag => tag.Name).IsUnique();
        });

        modelBuilder.Entity<ProfileTag>(entity =>
        {
            entity.HasKey(link => new { link.ProfileId, link.TagId });

            entity.HasOne(link => link.Profile)
                .WithMany(profile => profile.Tags)
                .HasForeignKey(link => link.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(link => link.Tag)
                .WithMany(tag => tag.Profiles)
                .HasForeignKey(link => link.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CredentialSet>(entity =>
        {
            entity.Property(set => set.Name).HasMaxLength(200);
            entity.Property(set => set.SecretReference).HasMaxLength(200);
            entity.HasIndex(set => set.Name).IsUnique();
        });

        modelBuilder.Entity<WatchedFolder>(entity =>
        {
            entity.Property(folder => folder.Path).HasMaxLength(1000);
            entity.HasIndex(folder => folder.Path).IsUnique();
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasIndex(session => session.StartedAt);

            entity.HasOne(session => session.Profile)
                .WithMany(profile => profile.Sessions)
                .HasForeignKey(session => session.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionEvent>(entity =>
        {
            entity.Property(item => item.Level).HasMaxLength(20);

            entity.HasOne(item => item.Session)
                .WithMany(session => session.Events)
                .HasForeignKey(item => item.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HotkeyBinding>(entity =>
        {
            entity.Property(binding => binding.ActionId).HasMaxLength(100);
            entity.Property(binding => binding.Gesture).HasMaxLength(100);
            entity.HasIndex(binding => binding.ActionId).IsUnique();
        });
    }
    /// <summary>
    /// Applies the instant conversion to every timestamp in the model.
    /// </summary>
    /// <remarks>
    /// Done by walking the model rather than property by property, so a timestamp added later is
    /// stored the same way without anyone having to remember this rule.
    /// </remarks>
    private static void ApplyInstantStorage(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(InstantConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableInstantConverter);
                }
            }
        }
    }

}
