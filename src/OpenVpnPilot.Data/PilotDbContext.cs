using Microsoft.EntityFrameworkCore;
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

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<ProfileTag> ProfileTags => Set<ProfileTag>();

    public DbSet<CredentialSet> CredentialSets => Set<CredentialSet>();

    public DbSet<WatchedFolder> WatchedFolders => Set<WatchedFolder>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    public DbSet<HotkeyBinding> HotkeyBindings => Set<HotkeyBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

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

            entity.HasOne(profile => profile.Folder)
                .WithMany(folder => folder.Profiles)
                .HasForeignKey(profile => profile.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(profile => profile.CredentialSet)
                .WithMany(set => set.Profiles)
                .HasForeignKey(profile => profile.CredentialSetId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.Property(folder => folder.Name).HasMaxLength(200);

            entity.HasOne(folder => folder.Parent)
                .WithMany(folder => folder.Children)
                .HasForeignKey(folder => folder.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
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
}
