using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Security;

/// <summary>
/// Keeps credentials in the user's login keychain, all of them in one generic password item.
/// </summary>
/// <remarks>
/// The user name travels inside the protected data together with the password rather than in an
/// attribute, because attributes can be read without the keychain asking.
///
/// <para>
/// One item rather than one per profile, and the reason is measured. The keychain decides who may
/// read an item by the code signature of the asking program, and it decides it per item: a build it
/// has not seen raises the system's own prompt, and allowing it covers that one item. Nothing this
/// application does can avoid the prompt, because the signature here is ad-hoc and its hash changes
/// with every build; what it can decide is how often the prompt appears. With an item per profile
/// that was once per profile per build. With one item it is once per build, and the sign in for
/// every profile is available afterwards.
/// </para>
/// <para>
/// The cost of that is stated plainly: one approval hands over every stored sign in rather than one.
/// They were all readable by the same application either way, and the alternative was a person
/// answering the same dialog thirteen times to connect to the second profile of the day.
/// </para>
/// <para>
/// Which profiles have a sign in stored is kept in an attribute rather than in the protected data,
/// so counting them and listing them never raises a prompt. That list is not secret, and it was
/// readable without one before this, when the reference was the account name of its own item.
/// </para>
/// <para>
/// An item written by an earlier version is folded into this one the first time that profile is
/// asked for, and removed once it is. Nothing is migrated in bulk: that would ask about every
/// profile at once, which is the very thing this is here to stop.
/// </para>
/// <para>
/// Every call may block on the prompt, so each one runs on the thread pool rather than on whatever
/// thread asked, which is usually the one drawing the window. They take a lock between them,
/// because reading, changing and writing the item back is not atomic.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStore : ISecretStore
{
    /// <summary>
    /// The service every item carries. Fixed for good, because it is how existing items are found.
    /// </summary>
    public const string Service = "OpenVpnPilot";

    /// <summary>
    /// The account of the item that holds every sign in. A reference never looks like this, so it
    /// cannot collide with an item written by an earlier version.
    /// </summary>
    private const string Vault = "credentials";

    private const string Label = "OpenVPN Pilot";

    private const string Description = "OpenVPN Pilot sign ins";

    private readonly ILogger<KeychainSecretStore> logger;
    private readonly Lock gate = new();
    private readonly string service;

    /// <param name="service">
    /// Which service the items carry. <see cref="Service"/> everywhere the application runs, and
    /// overridden only so the store can be exercised without touching what the application stores.
    /// </param>
    public KeychainSecretStore(ILogger<KeychainSecretStore>? logger = null, string? service = null)
    {
        this.logger = logger ?? NullLogger<KeychainSecretStore>.Instance;
        this.service = string.IsNullOrWhiteSpace(service) ? Service : service;
    }

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public Task<StoredSecret?> TryReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Task.Run(() => Read(reference), cancellationToken);
    }

    public Task WriteAsync(string reference, StoredSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);
        return Task.Run(() => Write(reference, secret), cancellationToken);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Task.Run(() => Delete(reference), cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(List, cancellationToken);

    public Task<int> ClearAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Clear, cancellationToken);

    private StoredSecret? Read(string reference)
    {
        lock (gate)
        {
            Vaulted vault = ReadVault();

            if (vault.Entries.TryGetValue(reference, out Payload? stored))
            {
                return new StoredSecret(stored.Username, stored.Password);
            }

            // Refused means the person said no or the keychain would not answer. Falling through to
            // the older item would be harmless, but writing the vault afterwards would replace
            // every sign in in it with this one, so nothing is written on that path.
            if (vault.Outcome == VaultOutcome.Refused)
            {
                return null;
            }

            StoredSecret? legacy = ReadItem(reference);

            if (legacy is null)
            {
                return null;
            }

            vault.Entries[reference] = new Payload(legacy.Username, legacy.Password);
            WriteVault(vault.Entries);
            DeleteItem(reference);

            return legacy;
        }
    }

    private void Write(string reference, StoredSecret secret)
    {
        lock (gate)
        {
            Vaulted vault = ReadVault();

            if (vault.Outcome == VaultOutcome.Refused)
            {
                throw new InvalidOperationException(
                    "The keychain did not hand over the stored sign ins, so a new one cannot be added "
                    + "without losing them.");
            }

            vault.Entries[reference] = new Payload(secret.Username, secret.Password);
            WriteVault(vault.Entries);

            // Whatever an earlier version wrote for this profile is now a stale duplicate.
            DeleteItem(reference);
        }
    }

    private void Delete(string reference)
    {
        lock (gate)
        {
            Vaulted vault = ReadVault();

            if (vault.Outcome == VaultOutcome.Found && vault.Entries.Remove(reference))
            {
                if (vault.Entries.Count == 0)
                {
                    DeleteItem(Vault);
                }
                else
                {
                    WriteVault(vault.Entries);
                }
            }

            DeleteItem(reference);
        }
    }

    /// <summary>
    /// Every reference that has a sign in stored, read from attributes so nothing is asked.
    /// </summary>
    private IReadOnlyList<string> List()
    {
        lock (gate)
        {
            Stored stored = ReadAccounts();
            List<string> references = [.. stored.Vaulted, .. stored.LegacyAccounts];

            references.Sort(StringComparer.Ordinal);
            return references;
        }
    }

    private int Clear()
    {
        lock (gate)
        {
            Stored stored = ReadAccounts();

            DeleteItem(Vault);

            foreach (string account in stored.LegacyAccounts)
            {
                DeleteItem(account);
            }

            return stored.Vaulted.Count + stored.LegacyAccounts.Count;
        }
    }

    /// <summary>
    /// The item holding every sign in, and whether it was there at all.
    /// </summary>
    private Vaulted ReadVault()
    {
        using CoreFoundationHandle query = Query(Vault);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.ReturnData, CoreFoundation.BooleanTrue);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.MatchLimit, SecurityFramework.MatchLimitOne);

        int status = SecurityFramework.SecItemCopyMatching(query.Value, out nint result);

        if (status == SecurityFramework.ItemNotFound)
        {
            return new Vaulted(VaultOutcome.Absent, []);
        }

        if (status != SecurityFramework.Success)
        {
            // Denied, dismissed or unavailable. The caller asks the person instead, which is the
            // only way forward that does not end the connection over a keychain decision.
            KeychainLog.ReadRefused(logger, SecurityFramework.DescribeStatus(status));
            return new Vaulted(VaultOutcome.Refused, []);
        }

        using CoreFoundationHandle data = new(result);
        byte[]? plain = CoreFoundation.ReadData(data.Value);

        if (plain is null)
        {
            return new Vaulted(VaultOutcome.Absent, []);
        }

        try
        {
            Dictionary<string, Payload>? entries =
                JsonSerializer.Deserialize(plain, KeychainJsonContext.Default.DictionaryStringPayload);

            return new Vaulted(VaultOutcome.Found, entries ?? []);
        }
        catch (JsonException exception)
        {
            // Written by something else under the same service, or damaged. Treated as absent, and
            // reported as refused so that nothing overwrites what could not be understood.
            KeychainLog.PayloadUnreadable(logger, exception);
            return new Vaulted(VaultOutcome.Refused, []);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void WriteVault(Dictionary<string, Payload> entries)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entries, KeychainJsonContext.Default.DictionaryStringPayload);

        string[] names = [.. entries.Keys];
        Array.Sort(names, StringComparer.Ordinal);
        byte[] index = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(names, KeychainJsonContext.Default.StringArray));

        try
        {
            using CoreFoundationHandle data = CoreFoundation.CreateData(plain);
            using CoreFoundationHandle listed = CoreFoundation.CreateData(index);

            using CoreFoundationHandle update = CoreFoundation.CreateMutableDictionary();
            CoreFoundation.CFDictionarySetValue(update.Value, SecurityFramework.ValueData, data.Value);
            CoreFoundation.CFDictionarySetValue(update.Value, SecurityFramework.AttributeGeneric, listed.Value);

            using CoreFoundationHandle existing = Query(Vault);
            int status = SecurityFramework.SecItemUpdate(existing.Value, update.Value);

            if (status == SecurityFramework.ItemNotFound)
            {
                using CoreFoundationHandle item = Query(Vault);
                using CoreFoundationHandle label = CoreFoundation.CreateString(Label);
                using CoreFoundationHandle description = CoreFoundation.CreateString(Description);

                CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.ValueData, data.Value);
                CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.AttributeGeneric, listed.Value);
                CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.AttributeLabel, label.Value);
                CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.AttributeDescription, description.Value);

                status = SecurityFramework.SecItemAdd(item.Value, out nint added);

                if (added != 0)
                {
                    CoreFoundation.CFRelease(added);
                }
            }

            if (status != SecurityFramework.Success)
            {
                throw new InvalidOperationException(
                    $"The keychain did not store the sign in: {SecurityFramework.DescribeStatus(status)}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// One item written by a version that kept a separate item per profile.
    /// </summary>
    private StoredSecret? ReadItem(string reference)
    {
        using CoreFoundationHandle query = Query(reference);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.ReturnData, CoreFoundation.BooleanTrue);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.MatchLimit, SecurityFramework.MatchLimitOne);

        int status = SecurityFramework.SecItemCopyMatching(query.Value, out nint result);

        if (status == SecurityFramework.ItemNotFound)
        {
            return null;
        }

        if (status != SecurityFramework.Success)
        {
            KeychainLog.ReadRefused(logger, SecurityFramework.DescribeStatus(status));
            return null;
        }

        using CoreFoundationHandle data = new(result);
        byte[]? plain = CoreFoundation.ReadData(data.Value);

        if (plain is null)
        {
            return null;
        }

        try
        {
            Payload? payload = JsonSerializer.Deserialize(plain, KeychainJsonContext.Default.Payload);
            return payload is null ? null : new StoredSecret(payload.Username, payload.Password);
        }
        catch (JsonException exception)
        {
            KeychainLog.PayloadUnreadable(logger, exception);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void DeleteItem(string account)
    {
        using CoreFoundationHandle query = Query(account);
        int status = SecurityFramework.SecItemDelete(query.Value);

        if (status is not (SecurityFramework.Success or SecurityFramework.ItemNotFound))
        {
            // The item stays until the next attempt. Failing a settings save over it would be worse.
            KeychainLog.DeleteRefused(logger, SecurityFramework.DescribeStatus(status));
        }
    }

    /// <summary>
    /// What the keychain holds under this service, read from attributes alone.
    /// </summary>
    private Stored ReadAccounts()
    {
        using CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();
        using CoreFoundationHandle name = CoreFoundation.CreateString(service);

        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, name.Value);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.ReturnAttributes, CoreFoundation.BooleanTrue);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.MatchLimit, SecurityFramework.MatchLimitAll);

        int status = SecurityFramework.SecItemCopyMatching(query.Value, out nint result);

        if (status == SecurityFramework.ItemNotFound)
        {
            return new Stored([], []);
        }

        if (status != SecurityFramework.Success)
        {
            KeychainLog.ListRefused(logger, SecurityFramework.DescribeStatus(status));
            return new Stored([], []);
        }

        using CoreFoundationHandle items = new(result);
        List<string> vaulted = [];
        List<string> legacy = [];

        if (CoreFoundation.CFGetTypeID(items.Value) == CoreFoundation.CFArrayGetTypeID())
        {
            nint count = CoreFoundation.CFArrayGetCount(items.Value);

            for (nint index = 0; index < count; index++)
            {
                Sort(CoreFoundation.CFArrayGetValueAtIndex(items.Value, index), vaulted, legacy);
            }
        }
        else
        {
            Sort(items.Value, vaulted, legacy);
        }

        return new Stored(vaulted, legacy);
    }

    /// <summary>
    /// Files one item's attributes under what it is: the one holding every sign in, or one written
    /// by an earlier version whose account is the reference itself.
    /// </summary>
    private void Sort(nint attributes, List<string> vaulted, List<string> legacy)
    {
        if (attributes == 0 || CoreFoundation.CFGetTypeID(attributes) != CoreFoundation.CFDictionaryGetTypeID())
        {
            return;
        }

        string? account = CoreFoundation.ReadString(
            CoreFoundation.CFDictionaryGetValue(attributes, SecurityFramework.AttributeAccount));

        if (string.IsNullOrEmpty(account))
        {
            return;
        }

        if (account != Vault)
        {
            legacy.Add(account);
            return;
        }

        byte[]? index = CoreFoundation.ReadData(
            CoreFoundation.CFDictionaryGetValue(attributes, SecurityFramework.AttributeGeneric));

        if (index is null || index.Length == 0)
        {
            return;
        }

        try
        {
            string[]? names = JsonSerializer.Deserialize(index, KeychainJsonContext.Default.StringArray);

            if (names is not null)
            {
                vaulted.AddRange(names);
            }
        }
        catch (JsonException exception)
        {
            // The list of names is a convenience, not the truth. A damaged one hides profiles from
            // the settings screen until the next write, and must not take the screen down.
            KeychainLog.PayloadUnreadable(logger, exception);
        }
    }

    /// <summary>
    /// The identity of one item: this application's service and the account.
    /// </summary>
    private CoreFoundationHandle Query(string account)
    {
        CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();

        using CoreFoundationHandle service = CoreFoundation.CreateString(this.service);
        using CoreFoundationHandle name = CoreFoundation.CreateString(account);

        // The dictionary retains what is put into it, so the strings can be released here.
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, service.Value);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeAccount, name.Value);

        return query;
    }

    /// <summary>
    /// What is stored for one profile inside the protected data.
    /// </summary>
    internal sealed record Payload(string? Username, string Password);

    /// <summary>
    /// Why reading the item ended the way it did. Absent and refused are not the same thing: one
    /// may be written over and the other may not.
    /// </summary>
    private enum VaultOutcome
    {
        Absent,
        Found,
        Refused,
    }

    private sealed record Vaulted(VaultOutcome Outcome, Dictionary<string, Payload> Entries);

    private sealed record Stored(List<string> Vaulted, List<string> LegacyAccounts);
}

/// <summary>
/// Serialisation of the protected payload without reflection.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(KeychainSecretStore.Payload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, KeychainSecretStore.Payload>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class KeychainJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>
/// Source generated log messages for <see cref="KeychainSecretStore"/>.
/// </summary>
/// <remarks>
/// Only statuses are logged. A reference names a profile, never a secret, and is left out anyway.
/// </remarks>
internal static partial class KeychainLog
{
    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Warning,
        Message = "The keychain did not hand over a stored sign in: {Status}. The person is asked instead.")]
    public static partial void ReadRefused(ILogger logger, string status);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Warning,
        Message = "A keychain item under this application's service could not be read.")]
    public static partial void PayloadUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Warning,
        Message = "The keychain did not remove a stored sign in: {Status}.")]
    public static partial void DeleteRefused(ILogger logger, string status);

    [LoggerMessage(
        EventId = 5103,
        Level = LogLevel.Warning,
        Message = "The keychain did not list the stored sign ins: {Status}.")]
    public static partial void ListRefused(ILogger logger, string status);
}
