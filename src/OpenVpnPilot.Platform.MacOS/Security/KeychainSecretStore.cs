using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Platform.MacOS.Interop;

namespace OpenVpnPilot.Platform.MacOS.Security;

/// <summary>
/// Keeps credentials in the user's login keychain, one generic password item per reference.
/// </summary>
/// <remarks>
/// The reference is the item's account and the service is fixed, so every item this application
/// wrote can be found, listed and removed without touching anybody else's. The user name travels
/// inside the protected data together with the password rather than in an attribute, because
/// attributes can be read without the keychain asking.
///
/// The keychain decides who may read an item, and it may ask the person first: the first read by a
/// build it has not seen, the companion command included, raises the system's own prompt. A prompt
/// that is dismissed or denied is answered as if nothing were stored, which sends the caller to its
/// own credential prompt rather than failing the connection.
///
/// Every call may block on that prompt, so each one runs on the thread pool rather than on whatever
/// thread asked, which is usually the one drawing the window.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStore : ISecretStore
{
    /// <summary>
    /// The service every item carries. Fixed for good, because it is how existing items are found.
    /// </summary>
    public const string Service = "OpenVpnPilot";

    private const string Label = "OpenVPN Pilot";

    private const string Description = "OpenVPN Pilot sign in";

    private readonly ILogger<KeychainSecretStore> logger;

    public KeychainSecretStore(ILogger<KeychainSecretStore>? logger = null)
    {
        this.logger = logger ?? NullLogger<KeychainSecretStore>.Instance;
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

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> references = await ListAsync(cancellationToken);

        foreach (string reference in references)
        {
            await DeleteAsync(reference, cancellationToken);
        }

        return references.Count;
    }

    private StoredSecret? Read(string reference)
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
            // Denied, dismissed or unavailable. The caller asks the person instead, which is the
            // only way forward that does not end the connection over a keychain decision.
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
            // Written by something else under the same service, or damaged. Treated as absent.
            KeychainLog.PayloadUnreadable(logger, exception);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void Write(string reference, StoredSecret secret)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(secret.Username, secret.Password),
            KeychainJsonContext.Default.Payload);

        try
        {
            using CoreFoundationHandle data = CoreFoundation.CreateData(plain);

            using CoreFoundationHandle update = CoreFoundation.CreateMutableDictionary();
            CoreFoundation.CFDictionarySetValue(update.Value, SecurityFramework.ValueData, data.Value);

            using CoreFoundationHandle existing = Query(reference);
            int status = SecurityFramework.SecItemUpdate(existing.Value, update.Value);

            if (status == SecurityFramework.ItemNotFound)
            {
                using CoreFoundationHandle item = Query(reference);
                using CoreFoundationHandle label = CoreFoundation.CreateString(Label);
                using CoreFoundationHandle description = CoreFoundation.CreateString(Description);

                CoreFoundation.CFDictionarySetValue(item.Value, SecurityFramework.ValueData, data.Value);
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

    private void Delete(string reference)
    {
        using CoreFoundationHandle query = Query(reference);
        int status = SecurityFramework.SecItemDelete(query.Value);

        if (status is not (SecurityFramework.Success or SecurityFramework.ItemNotFound))
        {
            // The item stays until the next attempt. Failing a settings save over it would be worse.
            KeychainLog.DeleteRefused(logger, SecurityFramework.DescribeStatus(status));
        }
    }

    private IReadOnlyList<string> List()
    {
        using CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();
        using CoreFoundationHandle service = CoreFoundation.CreateString(Service);

        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, service.Value);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.ReturnAttributes, CoreFoundation.BooleanTrue);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.MatchLimit, SecurityFramework.MatchLimitAll);

        int status = SecurityFramework.SecItemCopyMatching(query.Value, out nint result);

        if (status == SecurityFramework.ItemNotFound)
        {
            return [];
        }

        if (status != SecurityFramework.Success)
        {
            KeychainLog.ListRefused(logger, SecurityFramework.DescribeStatus(status));
            return [];
        }

        using CoreFoundationHandle items = new(result);
        List<string> references = [];

        if (CoreFoundation.CFGetTypeID(items.Value) == CoreFoundation.CFArrayGetTypeID())
        {
            nint count = CoreFoundation.CFArrayGetCount(items.Value);

            for (nint index = 0; index < count; index++)
            {
                AddAccount(CoreFoundation.CFArrayGetValueAtIndex(items.Value, index), references);
            }
        }
        else
        {
            AddAccount(items.Value, references);
        }

        references.Sort(StringComparer.Ordinal);
        return references;
    }

    private static void AddAccount(nint attributes, List<string> references)
    {
        if (attributes == 0 || CoreFoundation.CFGetTypeID(attributes) != CoreFoundation.CFDictionaryGetTypeID())
        {
            return;
        }

        string? account = CoreFoundation.ReadString(
            CoreFoundation.CFDictionaryGetValue(attributes, SecurityFramework.AttributeAccount));

        if (!string.IsNullOrEmpty(account))
        {
            references.Add(account);
        }
    }

    /// <summary>
    /// The identity of one item: this application's service and the reference as the account.
    /// </summary>
    private static CoreFoundationHandle Query(string reference)
    {
        CoreFoundationHandle query = CoreFoundation.CreateMutableDictionary();

        using CoreFoundationHandle service = CoreFoundation.CreateString(Service);
        using CoreFoundationHandle account = CoreFoundation.CreateString(reference);

        // The dictionary retains what is put into it, so the strings can be released here.
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.Class, SecurityFramework.ClassGenericPassword);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeService, service.Value);
        CoreFoundation.CFDictionarySetValue(query.Value, SecurityFramework.AttributeAccount, account.Value);

        return query;
    }

    /// <summary>
    /// What is stored as the item's protected data.
    /// </summary>
    internal sealed record Payload(string? Username, string Password);
}

/// <summary>
/// Serialisation of the protected payload without reflection.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(KeychainSecretStore.Payload))]
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
