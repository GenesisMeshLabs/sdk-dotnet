namespace GenesisMesh;

/// <summary>The Network Authority's own keys (v1.5.0).</summary>
public sealed class NetworkAuthorityClient
{
    private readonly Transport _t;
    internal NetworkAuthorityClient(Transport t) => _t = t;

    /// <summary>
    /// The NA's key history: the signed genesis block, every succession and each key's validity. It proves nothing
    /// by itself; <see cref="VerifiedKeyHistory"/> verifies it.
    /// GET /na/key-history
    /// </summary>
    public Task<KeyHistoryView> KeyHistory(CancellationToken ct = default) =>
        _t.PublicGetAsync<KeyHistoryView>("/na/key-history", ct);

    /// <summary>
    /// The NA's key history, verified from <paramref name="rootPublicKey"/>, the root key the caller holds out of
    /// band (<see cref="OfflineVerifier.VerifyKeyHistoryResponse"/>): the served keys are ignored and the history is
    /// rebuilt from the genesis block down. Pass it to the offline verifiers to check records signed before and
    /// after a succession, each with the key valid at its time.
    /// GET /na/key-history
    /// </summary>
    /// <exception cref="KeyHistoryException">The history does not verify; its first failure is the code.</exception>
    /// <exception cref="System.Text.Json.JsonException">The response is not strict JSON.</exception>
    public async Task<NAKeyHistory> VerifiedKeyHistory(string rootPublicKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPublicKey);
        var body = await _t.PublicGetTextAsync("/na/key-history", ct).ConfigureAwait(false);
        var result = OfflineVerifier.VerifyKeyHistoryResponse(body, rootPublicKey);
        if (!result.Verified || result.History is null) throw new KeyHistoryException(result);
        return result.History;
    }
}

/// <summary>
/// The NA's key history was refused (<see cref="NetworkAuthorityClient.VerifiedKeyHistory"/>, v1.5.0):
/// <see cref="GenesisMeshException.Code"/> is the first failure's reason, such as <c>genesis_root_mismatch</c>.
/// </summary>
public sealed class KeyHistoryException : GenesisMeshException
{
    /// <summary>A refused key history.</summary>
    /// <param name="verification">The failed verification.</param>
    public KeyHistoryException(KeyHistoryVerificationResult verification)
        : base(0, Reason(verification), Describe(verification), "genesismesh: " + Describe(verification))
    {
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
    }

    /// <summary>The failed verification: the reason and the key sequence it failed at.</summary>
    public KeyHistoryVerificationResult Verification { get; }

    private static string Reason(KeyHistoryVerificationResult? verification) => verification?.Reason ?? "key_history_invalid";

    private static string Describe(KeyHistoryVerificationResult? verification) =>
        $"the NA key history is refused: {Reason(verification)} (key {verification?.KeySequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})";
}
