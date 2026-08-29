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

        await ProfilePackageFile.WriteAsync(PackagePath, content);

        ProfilePackageContent read = await ProfilePackageFile.ReadAsync(PackagePath);

        await using PilotDbContext target = CreateContext("target.db");
        PackageApplyResult result = await new ProfilePackageService(target).ApplyAsync(read);

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Skipped);

        Profile restored = await target.Profiles.SingleAsync(profile => profile.Name == "site-alpha");

        Assert.Equal("client\nremote vpn.example.com 1194", restored.Configuration);
        Assert.Equal("vpn.example.com", restored.RemoteHost);
        Assert.NotNull(restored.FolderId);

        Folder folder = await target.Folders.SingleAsync(item => item.Id == restored.FolderId);
        Assert.Equal("Customers", folder.Name);
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
    public async Task CredentialsAreRefused_WhenThePackageWouldNotBeProtected()
    {
        ProfilePackageContent content = new()
        {
            Credentials = [new PackagedCredential(Guid.NewGuid(), "Auth", "operator", "secret")],
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProfilePackageFile.WriteAsync(PackagePath, content));

        Assert.Contains("passphrase", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(PackagePath));
    }

    [Fact]
    public async Task AFileThatIsNotAPackage_IsRefusedRatherThanGuessedAt()
    {
        await File.WriteAllTextAsync(PackagePath, "just some text");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProfilePackageFile.ReadAsync(PackagePath));

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

        // The folder the profile is filed under has to travel with it or the filing is lost.
        Assert.Empty(content.Folders);
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
        Folder folder = new() { Name = "Customers" };
        context.Folders.Add(folder);

        Tag tag = new() { Name = "production" };
        context.Tags.Add(tag);

        Profile alpha = new()
        {
            Name = "site-alpha",
            Configuration = "client\nremote vpn.example.com 1194",
            ContentHash = new string('a', 64),
            FolderId = folder.Id,
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
