using NSec.Cryptography;

namespace GenesisMesh;

/// <summary>
/// Signs admin requests for an operator key (<see cref="ClientOptions.Signer"/>) or, from 1.9.0, execution evidence for
/// an executor key (<see cref="ExecutionRecorder"/>). Implement it to keep the key out of this SDK, for example in an
/// HSM. A key read from Key Vault at start, which has no Ed25519 keys, is a
/// <see cref="SeedSigner"/>.
/// </summary>
public interface ISigner
{
    /// <summary>The operator key ID the NA knows the key by, sent in <c>X-Admin-Key-Id</c>.</summary>
    string KeyId { get; }

    /// <summary>The 64-byte Ed25519 signature of <paramref name="message"/>.</summary>
    /// <param name="message">The canonical bytes to sign.</param>
    /// <param name="cancellationToken">Cancels a signer that waits, such as a remote one.</param>
    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}

/// <summary>A signer for an Ed25519 seed held in memory, as <see cref="ClientOptions.SigningKey"/> is.</summary>
public sealed class SeedSigner : ISigner
{
    private readonly byte[] _seed;

    /// <summary>A signer for <paramref name="seedBase64"/>, known to the NA as <paramref name="keyId"/>.</summary>
    /// <param name="seedBase64">The base64 32-byte Ed25519 seed, as <c>genesis-mesh keygen operator</c> writes it.</param>
    /// <param name="keyId">The operator key ID registered with the NA.</param>
    /// <exception cref="ArgumentException">The seed is not base64 of 32 bytes, or the key ID is empty.</exception>
    public SeedSigner(string seedBase64, string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        _seed = Auth.LoadSeed(seedBase64);
        KeyId = keyId;
    }

    /// <summary>The signer of <see cref="ClientOptions.SigningKey"/>, under <see cref="ClientOptions.KeyId"/> as given (even empty).</summary>
    internal SeedSigner(byte[] seed, string keyId)
    {
        _seed = seed;
        KeyId = keyId;
    }

    /// <inheritdoc/>
    public string KeyId { get; }

    /// <inheritdoc/>
    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, _seed, KeyBlobFormat.RawPrivateKey);
        return ValueTask.FromResult(SignatureAlgorithm.Ed25519.Sign(key, message.Span));
    }
}
