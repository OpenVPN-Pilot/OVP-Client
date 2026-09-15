using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenVpnPilot.Data;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.Data.Packaging;

namespace OpenVpnPilot.Data.Tests.Packaging;

/// <summary>
/// A package moves private keys between machines, so both what it carries and what it refuses to
/// carry are worth pinning down.
/// </summary>
public sealed class ProfilePackageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ovp-package-").FullName;

    private string PackagePath => Path.Combine(root, "set.ovppkg");

    [Fact]
    public async Task APackageRoundTripsThroughAnEmptyStore()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await ProfilePackageFile.WriteAsync(PackagePath, content, "round trip");

        ProfilePackageContent read = await ProfilePackageFile.ReadAsync(PackagePath, "round trip");

        await using PilotDbContext target = CreateContext("target.db");
        PackageApplyResult result = await new ProfilePackageService(target).ApplyAsync(read);

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Skipped);

        Profile restored = await target.Profiles.SingleAsync(profile => profile.Name == "site-alpha");

        Assert.Equal("client\nremote vpn.example.com 1194", restored.Configuration);
        Assert.Equal("vpn.example.com", restored.RemoteHost);

        // Tags travel with the profile, because they are how the set is organised.
        Assert.Contains(
            await target.ProfileTags.Include(link => link.Tag).ToListAsync(),
            link => link.ProfileId == restored.Id && link.Tag!.Name == "production");
    }

    /// <summary>
    /// A package that carries sign ins has to say which profile each one now belongs to.
    /// </summary>
    /// <remarks>
    /// Applying a package creates new profiles with new identifiers, so a credential addressed to the
    /// identifier it had on the machine that wrote it would belong to nothing. Handing fifty profiles
    /// to someone who then has to type fifty passwords is most of the work left undone.
    /// </remarks>
    [Fact]
    public async Task Credentials_AreReaddressedToTheProfilesTheyWereImportedAs()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        Guid alpha = await source.Profiles
            .Where(profile => profile.Name == "site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync(
            profileIds: null,
            credentials: [new PackagedCredential(alpha, "Auth", "operator", "secret")]);

        await using PilotDbContext target = CreateContext("target.db");
        PackageApplyResult result = await new ProfilePackageService(target).ApplyAsync(content);

        Guid imported = await target.Profiles
            .Where(profile => profile.Name == "site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        PackagedCredential carried = Assert.Single(result.Credentials);

        Assert.NotEqual(alpha, imported);
        Assert.Equal(imported, carried.ProfileId);
        Assert.Equal("Auth", carried.Realm);
        Assert.Equal("operator", carried.Username);
        Assert.Equal("secret", carried.Password);
    }

    /// <summary>
    /// A package sent only to fill in the sign ins for a set that is already imported has to work.
    /// </summary>
    [Fact]
    public async Task Credentials_ForAProfileTheStoreAlreadyHas_AreAddressedToTheStoredOne()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        Guid alpha = await source.Profiles
            .Where(profile => profile.Name == "site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync(
            profileIds: null,
            credentials: [new PackagedCredential(alpha, "Auth", "operator", "secret")]);

        await using PilotDbContext target = CreateContext("target.db");
        ProfilePackageService service = new(target);

        await service.ApplyAsync(content);

        Guid imported = await target.Profiles
            .Where(profile => profile.Name == "site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        PackageApplyResult second = await service.ApplyAsync(content);

        Assert.Equal(0, second.Added);
        Assert.Equal(imported, Assert.Single(second.Credentials).ProfileId);
    }

    /// <summary>
    /// Several profiles sharing a tag the store does not have yet is the ordinary shape of a package.
    /// </summary>
    /// <remarks>
    /// Each profile looked its tags up in the database, where a tag added for the previous profile
    /// was not yet saved, so the same tag was added once per profile. Saving then broke the unique
    /// index on the name, and the exception ended the application in the middle of an import.
    /// </remarks>
    [Fact]
    public async Task ProfilesSharingATagTheStoreLacks_ShareOneTag()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        content = content with
        {
            Profiles = content.Profiles
                .Select(profile => profile with { Tags = ["office", "Shared"] })
                .ToList(),
        };

        await using PilotDbContext target = CreateContext("target.db");
        target.Tags.Add(new Tag { Name = "shared" });
        await target.SaveChangesAsync();

        PackageApplyResult result = await new ProfilePackageService(target).ApplyAsync(content);

        Assert.Equal(2, result.Added);

        List<Tag> tags = await target.Tags.Include(tag => tag.Profiles).OrderBy(tag => tag.Name).ToListAsync();

        // A tag differing only in case is the same tag, as it is everywhere else in the interface.
        Assert.Equal(["office", "shared"], tags.Select(tag => tag.Name));
        Assert.All(tags, tag => Assert.Equal(2, tag.Profiles.Count));
    }

    [Fact]
    public async Task APackageWrittenWithoutCredentials_CarriesNone()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await ProfilePackageFile.WriteAsync(PackagePath, content, "round trip");
        ProfilePackageContent read = await ProfilePackageFile.ReadAsync(PackagePath, "round trip");

        Assert.Empty(read.Credentials);
    }

    /// <summary>
    /// A package is the only place a stored password is ever written outside the keystore, so the
    /// encryption has to actually cover it.
    /// </summary>
    [Fact]
    public async Task ACredentialIsNotReadableInTheWrittenFile()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        Guid alpha = await source.Profiles
            .Where(profile => profile.Name == "site-alpha")
            .Select(profile => profile.Id)
            .SingleAsync();

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync(
            profileIds: null,
            credentials: [new PackagedCredential(alpha, "Auth", "operator", "correct-horse")]);

        await ProfilePackageFile.WriteAsync(PackagePath, content, "round trip");

        byte[] raw = await File.ReadAllBytesAsync(PackagePath);

        Assert.DoesNotContain("correct-horse", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyingTheSamePackageTwice_ChangesNothingTheSecondTime()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await using PilotDbContext target = CreateContext("target.db");
        ProfilePackageService service = new(target);

        await service.ApplyAsync(content);
        PackageApplyResult second = await service.ApplyAsync(content);

        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, await target.Profiles.CountAsync());
    }

    [Fact]
    public async Task APackageDoesNotReplaceAProfileThatAlreadyHasTheSameName()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await using PilotDbContext target = CreateContext("target.db");

        target.Profiles.Add(new Profile
        {
            Name = "site-alpha",
            Configuration = "something else entirely",
            ContentHash = new string('f', 64),
        });

        await target.SaveChangesAsync();

        await new ProfilePackageService(target).ApplyAsync(content);

        // The existing profile keeps its name and its configuration; the incoming one is renamed.
        Profile existing = await target.Profiles.SingleAsync(profile => profile.Name == "site-alpha");
        Assert.Equal("something else entirely", existing.Configuration);

        Assert.True(await target.Profiles.AnyAsync(profile => profile.Name == "site-alpha (2)"));
    }

    [Fact]
    public async Task APackageWithAPassphrase_CannotBeOpenedWithoutIt()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await ProfilePackageFile.WriteAsync(PackagePath, content, "correct horse");

        Assert.True(await ProfilePackageFile.IsEncryptedAsync(PackagePath));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProfilePackageFile.ReadAsync(PackagePath));

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => ProfilePackageFile.ReadAsync(PackagePath, "wrong passphrase"));

        ProfilePackageContent read = await ProfilePackageFile.ReadAsync(PackagePath, "correct horse");
        Assert.Equal(2, read.Profiles.Count);
    }

    [Fact]
    public async Task APackageThatWasAltered_FailsToOpenRatherThanOpeningChanged()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();
        await ProfilePackageFile.WriteAsync(PackagePath, content, "passphrase");

        byte[] raw = await File.ReadAllBytesAsync(PackagePath);
        raw[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(PackagePath, raw);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => ProfilePackageFile.ReadAsync(PackagePath, "passphrase"));
    }

    [Fact]
    public async Task APackageCannotBeWrittenWithoutAPassphrase()
    {
        ProfilePackageContent content = new();

        // The file carries private keys and is made to be moved, so there is no unprotected form.
        await Assert.ThrowsAsync<ArgumentException>(
            () => ProfilePackageFile.WriteAsync(PackagePath, content, string.Empty));

        Assert.False(File.Exists(PackagePath));
    }

    /// <summary>
    /// A layout this build does not know may mean something it would misread, and a shared library
    /// written back from a misreading would lose what the newer version put there.
    /// </summary>
    [Fact]
    public void APackageFromANewerFormat_IsRefused()
    {
        ProfilePackageContent content = new() { FormatVersion = ProfilePackageContent.CurrentFormatVersion + 1, WrittenBy = "9.0.0" };

        byte[] encoded = ProfilePackageFile.Encode(content, "passphrase");

        PackageTooNewException refused = Assert.Throws<PackageTooNewException>(
            () => ProfilePackageFile.Decode(encoded, "passphrase"));

        Assert.Equal("9.0.0", refused.WrittenBy);
    }

    /// <summary>
    /// A package from before the layout carried settings and deletions still opens.
    /// </summary>
    [Fact]
    public void APackageOfFormatOne_ReadsWithNothingOfTheLaterParts()
    {
        ProfilePackageContent content = new()
        {
            FormatVersion = 1,
            Profiles = [new PackagedProfile { Id = Guid.NewGuid(), Name = "site-alpha", Configuration = "client" }],
        };

        ProfilePackageContent read = ProfilePackageFile.Decode(ProfilePackageFile.Encode(content, "passphrase"), "passphrase");

        Assert.Equal(1, read.FormatVersion);
        Assert.Null(read.Settings);
        Assert.Empty(read.DeletedProfiles);
        Assert.Null(Assert.Single(read.Profiles).UpdatedAt);
    }

    [Fact]
    public async Task AFileThatIsNotAPackage_IsRefusedRatherThanGuessedAt()
    {
        await File.WriteAllTextAsync(PackagePath, "just some text");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProfilePackageFile.ReadAsync(PackagePath, "anything"));

        Assert.False(await ProfilePackageFile.IsEncryptedAsync(PackagePath));
    }

    [Fact]
    public async Task ShortcutsTravel_ButDoNotTakeOverOneTheStoreAlreadyHas()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        source.HotkeyBindings.Add(new HotkeyBinding
        {
            ActionId = "ToggleQuickSwitcher",
            Gesture = "Control+Alt+V",
        });

        source.HotkeyBindings.Add(new HotkeyBinding
        {
            ActionId = "DisconnectAll",
            Gesture = "Control+Alt+D",
        });

        await source.SaveChangesAsync();

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync();

        await using PilotDbContext target = CreateContext("target.db");

        target.HotkeyBindings.Add(new HotkeyBinding
        {
            ActionId = "ToggleQuickSwitcher",
            Gesture = "Control+Shift+P",
        });

        await target.SaveChangesAsync();

        PackageApplyResult result = await new ProfilePackageService(target).ApplyAsync(content);

        Assert.Equal(1, result.Hotkeys);

        HotkeyBinding kept = await target.HotkeyBindings
            .SingleAsync(binding => binding.ActionId == "ToggleQuickSwitcher");

        // A shortcut belongs to the machine, so the package must not take one away.
        Assert.Equal("Control+Shift+P", kept.Gesture);
    }

    [Fact]
    public async Task ASelectionPacksOnlyWhatWasAskedFor()
    {
        await using PilotDbContext source = CreateContext("source.db");
        await Seed(source);

        Profile only = await source.Profiles.SingleAsync(profile => profile.Name == "site-beta");

        ProfilePackageContent content = await new ProfilePackageService(source).CreateAsync([only.Id]);

        PackagedProfile packaged = Assert.Single(content.Profiles);
        Assert.Equal("site-beta", packaged.Name);
    }

    private PilotDbContext CreateContext(string name)
    {
        DbContextOptions<PilotDbContext> options = new DbContextOptionsBuilder<PilotDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, name)}")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        PilotDbContext context = new(options);
        context.Database.Migrate();
        return context;
    }

    private static async Task Seed(PilotDbContext context)
    {
        Tag tag = new() { Name = "production" };
        context.Tags.Add(tag);

        Profile alpha = new()
        {
            Name = "site-alpha",
            Configuration = "client\nremote vpn.example.com 1194",
            ContentHash = new string('a', 64),
            RemoteHost = "vpn.example.com",
            RemotePort = 1194,
            Protocol = "udp",
        };

        Profile beta = new()
        {
            Name = "site-beta",
            Configuration = "client\nremote vpn2.example.com 1195",
            ContentHash = new string('b', 64),
            RemoteHost = "vpn2.example.com",
            RemotePort = 1195,
            Protocol = "tcp",
        };

        context.Profiles.AddRange(alpha, beta);
        await context.SaveChangesAsync();

        context.ProfileTags.Add(new ProfileTag { ProfileId = alpha.Id, TagId = tag.Id });
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }
}
