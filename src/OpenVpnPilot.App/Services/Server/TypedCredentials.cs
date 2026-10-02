using OpenVpnPilot.Core.Abstractions;

namespace OpenVpnPilot.App.Services.Server;

/// <summary>
/// The answers a person typed into the credential prompt, kept until the connection they were
/// typed for has shown whether they work.
/// </summary>
/// <remarks>
/// <para>
/// This is the first half of sharing a sign in that worked: the credential provider notes what was
/// typed, and <see cref="VaultShareRecorder"/> decides on <c>Connected</c> that it is worth sending
/// to the shared vault. The provider cannot decide that itself, because it never learns how the
/// connection went, and it cannot listen to the connection manager, which depends on it.
/// </para>
/// <para>
/// Nothing is noted for an answer read from the keystore, for an attempt that carried a one time
/// code in either form, or while the application works with this computer's own profiles. A code
/// is valid once, so sharing the password that came with it would hand everybody half a sign in.
/// </para>
/// <para>
/// An answer the person did not ask to be remembered is held here in memory and nowhere else, and
/// moves to <see cref="IHeldVaultSecrets"/> once the connection proved it, so the push can read it.
/// </para>
/// </remarks>
public interface ITypedCredentialLedger
{
    /// <summary>
    /// The provider answered a request from the keystore, so nothing typed is pending for it.
    /// </summary>
    public void NoteStored(CredentialRequest request);

    /// <summary>
    /// The person typed an answer to a request.
    /// </summary>
    /// <param name="remembered">True when the answer was written to the keystore as well.</param>
    public void NoteTyped(CredentialRequest request, VpnCredentials answer, bool remembered);

    /// <summary>
    /// The profile connected: hands over what was typed for it and forgets it here.
    /// </summary>
    public IReadOnlyList<TypedAnswer> TakeConnected(Guid profileId);

    /// <summary>
    /// The attempt ended without connecting, so nothing typed for it is worth anything.
    /// </summary>
    public void Forget(Guid profileId);
}

/// <summary>
/// One answer that was typed and worked.
/// </summary>
/// <param name="Unremembered">
/// The answer itself when it was not written to the keystore, so it can be held until the push;
/// null when the keystore has it.
/// </param>
public sealed record TypedAnswer(Guid ProfileId, string Realm, StoredSecret? Unremembered)
{
    // The secret is left out of anything that prints the record.
    public override string ToString() =>
        $"{nameof(TypedAnswer)} {{ ProfileId = {ProfileId}, Realm = {Realm}, Remembered = {Unremembered is null} }}";
}

/// <summary>
/// Sign ins that worked and are to be shared, held in memory until the push has sent them, because
/// the person chose not to store them on this computer.
/// </summary>
/// <remarks>
/// Never written anywhere: not to the keystore, which the person declined, and not to the database,
/// which never holds a secret. A restart before the push therefore loses them, and the marker that
/// named them is dropped as having nothing to send.
/// </remarks>
public interface IHeldVaultSecrets
{
    public void Hold(Guid profileId, string realm, StoredSecret secret);

    public StoredSecret? Peek(Guid profileId, string realm);

    /// <summary>
    /// Forgets one, once the push is done with it, whatever the server answered.
    /// </summary>
    public void Release(Guid profileId, string realm);

    /// <summary>
    /// Forgets every one, which is part of forgetting every stored credential.
    /// </summary>
    public void Clear();
}

/// <summary>
/// The in-memory implementation of both: one object, because what the ledger hands over is what
/// the holder keeps.
/// </summary>
public sealed class TypedCredentials : ITypedCredentialLedger, IHeldVaultSecrets
{
    private readonly IStorageModeContext mode;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Attempt> attempts = [];
    private readonly Dictionary<(Guid ProfileId, string Realm), StoredSecret> held = [];

    public TypedCredentials(IStorageModeContext mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        this.mode = mode;
    }

    public void NoteStored(CredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (gate)
        {
            if (attempts.TryGetValue(request.ProfileId, out Attempt? attempt))
            {
                attempt.Answers.Remove(request.Realm);
            }
        }
    }

    public void NoteTyped(CredentialRequest request, VpnCredentials answer, bool remembered)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(answer);

        // On the local library there is nobody to share with, so a password is not kept a moment longer.
        if (!mode.IsServerMode)
        {
            return;
        }

        lock (gate)
        {
            if (!attempts.TryGetValue(request.ProfileId, out Attempt? attempt))
            {
                attempt = new Attempt();
                attempts[request.ProfileId] = attempt;
            }

            if (request.Challenge is not null || answer.ChallengeResponse is not null)
            {
                // Nothing of an attempt that needed a code is shared, not even the password typed
                // before the code was asked for.
                attempt.NeedsCode = true;
                attempt.Answers.Clear();
                return;
            }

            if (!attempt.NeedsCode)
            {
                // A retry replaces what was typed before it, which the server has just refused.
                attempt.Answers[request.Realm] = remembered ? null : new StoredSecret(answer.Username, answer.Password);
            }
        }
    }

    public IReadOnlyList<TypedAnswer> TakeConnected(Guid profileId)
    {
        lock (gate)
        {
            if (!attempts.Remove(profileId, out Attempt? attempt) || attempt.NeedsCode)
            {
                return [];
            }

            return [.. attempt.Answers.Select(answer => new TypedAnswer(profileId, answer.Key, answer.Value))];
        }
    }

    public void Forget(Guid profileId)
    {
        lock (gate)
        {
            attempts.Remove(profileId);
        }
    }

    public void Hold(Guid profileId, string realm, StoredSecret secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentNullException.ThrowIfNull(secret);

        lock (gate)
        {
            held[(profileId, realm)] = secret;
        }
    }

    public StoredSecret? Peek(Guid profileId, string realm)
    {
        lock (gate)
        {
            return held.GetValueOrDefault((profileId, realm));
        }
    }

    public void Release(Guid profileId, string realm)
    {
        lock (gate)
        {
            held.Remove((profileId, realm));
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            held.Clear();
            attempts.Clear();
        }
    }

    /// <summary>
    /// What was typed for one profile since its connection attempt began.
    /// </summary>
    private sealed class Attempt
    {
        /// <summary>
        /// By realm; the value is null when the keystore has the answer.
        /// </summary>
        public Dictionary<string, StoredSecret?> Answers { get; } = new(StringComparer.Ordinal);

        public bool NeedsCode { get; set; }
    }
}
