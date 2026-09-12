using System.Runtime.Versioning;
using System.Text.Json;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;
using OpenVpnPilot.Platform.MacOS.Security;

namespace OpenVpnPilot.Platform.MacOS.Tests.Security;

/// <summary>
/// Exercises the store against the real keychain, under a service of its own.
/// </summary>
/// <remarks>
/// Never under the service the application uses: these tests write, delete and clear, and running
/// them must not cost anybody the sign ins they have stored. Each run gets a fresh service and
/// removes what it wrote, so nothing accumulates in the login keychain either.
///
/// <para>
/// Everything here goes through the same interop the store uses, from this same process, and
/// nothing shells out to the security command. That is not a matter of taste: the keychain asks
/// about a program it does not recognise, the security command is one, and a test suite that puts a
/// system dialog on somebody's screen and waits for it is worse than no test suite. Items this
/// process created, it may read without anybody being asked.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStoreTests : IDisposable
{
    private readonly string service = "OpenVpnPilotTests-" + Guid.NewGuid().ToString("N");

    private KeychainSecretStore Store() => new(null, service);

    [MacOSFact]
    public async Task Write_ThenRead_ReturnsWhatWasStored()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("someone", "a-password"));

        StoredSecret? read = await store.TryReadAsync("profile/one/Auth");

        Assert.NotNull(read);
        Assert.Equal("someone", read.Username);
        Assert.Equal("a-password", read.Password);
    }

    /// <summary>
    /// The whole point of one item: a second profile is available without anything being asked again.
    /// </summary>
    [MacOSFact]
    public async Task Write_SeveralReferences_AllLandInOneItem()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("first", "one"));
        await store.WriteAsync("profile/two/Auth", new StoredSecret("second", "two"));
        await store.WriteAsync("profile/three/Auth", new StoredSecret(null, "three"));

        Assert.Equal("one", (await store.TryReadAsync("profile/one/Auth"))?.Password);
        Assert.Equal("two", (await store.TryReadAsync("profile/two/Auth"))?.Password);
        Assert.Equal("three", (await store.TryReadAsync("profile/three/Auth"))?.Password);
        Assert.Null((await store.TryReadAsync("profile/three/Auth"))?.Username);

        Assert.Equal(["credentials"], Accounts());
    }

    [MacOSFact]
    public async Task Write_SameReferenceTwice_KeepsTheLatest()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("someone", "before"));
        await store.WriteAsync("profile/one/Auth", new StoredSecret("someone", "after"));

        Assert.Equal("after", (await store.TryReadAsync("profile/one/Auth"))?.Password);
    }

    [MacOSFact]
    public async Task Delete_RemovesOneAndLeavesTheRest()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("first", "one"));
        await store.WriteAsync("profile/two/Auth", new StoredSecret("second", "two"));

        await store.DeleteAsync("profile/one/Auth");

        Assert.Null(await store.TryReadAsync("profile/one/Auth"));
        Assert.Equal("two", (await store.TryReadAsync("profile/two/Auth"))?.Password);
    }

    [MacOSFact]
    public async Task Delete_TheLastOne_TakesTheItemWithIt()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("first", "one"));

        await store.DeleteAsync("profile/one/Auth");

        Assert.Empty(Accounts());
    }

    [MacOSFact]
    public async Task List_ReportsEveryReference()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/two/Auth", new StoredSecret("second", "two"));
        await store.WriteAsync("profile/one/Auth", new StoredSecret("first", "one"));

        Assert.Equal(["profile/one/Auth", "profile/two/Auth"], await store.ListAsync());
    }

    [MacOSFact]
    public async Task List_WhenNothingWasStored_IsEmpty()
    {
        Assert.Empty(await Store().ListAsync());
    }

    [MacOSFact]
    public async Task Clear_RemovesEverythingAndCountsIt()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/one/Auth", new StoredSecret("first", "one"));
        await store.WriteAsync("profile/two/Auth", new StoredSecret("second", "two"));

        Assert.Equal(2, await store.ClearAsync());
        Assert.Empty(await store.ListAsync());
        Assert.Empty(Accounts());
    }

    /// <summary>
    /// An item written by the version that kept one per profile is still readable, moves into the
    /// one item, and the old one is gone afterwards.
    /// </summary>
    [MacOSFact]
    public async Task Read_AnItemFromAnEarlierVersion_IsFoldedIn()
    {
        WriteItemTheOldWay("profile/old/Auth", "someone", "from-before");

        KeychainSecretStore store = Store();
        StoredSecret? read = await store.TryReadAsync("profile/old/Auth");

        Assert.NotNull(read);
        Assert.Equal("someone", read.Username);
        Assert.Equal("from-before", read.Password);

        // What is left is the one item, and the one it came from is gone.
        Assert.Equal(["credentials"], Accounts());
        Assert.Equal("from-before", (await store.TryReadAsync("profile/old/Auth"))?.Password);
    }

    [MacOSFact]
    public async Task Read_AnItemFromAnEarlierVersion_LeavesTheOthersAlone()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/new/Auth", new StoredSecret("other", "current"));
        WriteItemTheOldWay("profile/old/Auth", "someone", "from-before");

        Assert.Equal("from-before", (await store.TryReadAsync("profile/old/Auth"))?.Password);
        Assert.Equal("current", (await store.TryReadAsync("profile/new/Auth"))?.Password);
    }

    [MacOSFact]
    public async Task List_WithAnItemFromAnEarlierVersion_ReportsItToo()
    {
        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/new/Auth", new StoredSecret("other", "current"));
        WriteItemTheOldWay("profile/old/Auth", "someone", "from-before");

        Assert.Equal(["profile/new/Auth", "profile/old/Auth"], await store.ListAsync());
    }

    [MacOSFact]
    public async Task Write_ReplacesAnItemFromAnEarlierVersion()
    {
        WriteItemTheOldWay("profile/old/Auth", "someone", "from-before");

        KeychainSecretStore store = Store();
        await store.WriteAsync("profile/old/Auth", new StoredSecret("someone", "now"));

        Assert.Equal(["credentials"], Accounts());
        Assert.Equal("now", (await store.TryReadAsync("profile/old/Auth"))?.Password);
    }

    [MacOSFact]
    public async Task Read_SomethingNeverStored_IsNull()
    {
        Assert.Null(await Store().TryReadAsync("profile/missing/Auth"));
    }

    /// <summary>
    /// An item in the shape the previous version wrote: its own item, the reference as the account.
    /// </summary>
    private void WriteItemTheOldWay(string reference, string username, string password)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new KeychainSecretStore.Payload(username, password), KeychainJsonContext.Default.Payload);

        using CoreFoundationHandle item = CoreFoundation.CreateMutableDictionary();
        using CoreFoundationHandle name = CoreFoundation.CreateString(service);
        using CoreFoundationHandle account = CoreFoundation.CreateString(reference);
        using CoreFoundationHandle data = CoreFoundation.CreateData(payload);

        CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.AttributeService, name.Value);
        CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.AttributeAccount, account.Value);
        CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.ValueData, data.Value);

        int status = SecurityFramework.SecItemAdd(item.Value, out nint added);

        if (added != 0)
        {
            CoreFoundation.CFRelease(added);
        }

        Assert.Equal(SecurityFramework.Success, status);
    }

    /// <summary>
    /// The accounts the keychain holds under this test's service, read from attributes alone.
    /// </summary>
    private List<string> Accounts()
    {
        using CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();
        using CoreFoundationHandle name = CoreFoundation.CreateString(service);

        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, name.Value);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.ReturnAttributes, CoreFoundation.BooleanTrue);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.MatchLimit, SecurityFramework.MatchLimitAll);

        int status = SecurityFramework.SecItemCopyMatching(query.Value, out nint result);

        if (status != SecurityFramework.Success)
        {
            return [];
        }

        using CoreFoundationHandle items = new(result);
        List<string> accounts = [];

        if (CoreFoundation.CFGetTypeID(items.Value) == CoreFoundation.CFArrayGetTypeID())
        {
            nint count = CoreFoundation.CFArrayGetCount(items.Value);

            for (nint index = 0; index < count; index++)
            {
                Add(CoreFoundation.CFArrayGetValueAtIndex(items.Value, index), accounts);
            }
        }
        else
        {
            Add(items.Value, accounts);
        }

        accounts.Sort(StringComparer.Ordinal);
        return accounts;
    }

    private static void Add(nint attributes, List<string> accounts)
    {
        if (attributes == 0 || CoreFoundation.CFGetTypeID(attributes) != CoreFoundation.CFDictionaryGetTypeID())
        {
            return;
        }

        string? account = CoreFoundation.ReadString(
            CoreFoundation.CFDictionaryGetValue(attributes, SecurityFramework.AttributeAccount));

        if (!string.IsNullOrEmpty(account))
        {
            accounts.Add(account);
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (string account in Accounts())
        {
            using CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();
            using CoreFoundationHandle name = CoreFoundation.CreateString(service);
            using CoreFoundationHandle which = CoreFoundation.CreateString(account);

            CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
            CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, name.Value);
            CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeAccount, which.Value);

            int status = SecurityFramework.SecItemDelete(query.Value);

            if (status is not (SecurityFramework.Success or SecurityFramework.ItemNotFound))
            {
                // Nothing can be done about it from here, and what is left behind names a test
                // service rather than anybody's profile.
                continue;
            }
        }
    }
}
