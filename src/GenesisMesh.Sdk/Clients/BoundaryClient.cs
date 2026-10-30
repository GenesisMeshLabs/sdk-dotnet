namespace GenesisMesh;

/// <summary>Capability boundary decisions — evaluate under policies, decide (legacy) and verify.</summary>
public sealed class BoundaryClient
{
    private readonly Transport _t;
    internal BoundaryClient(Transport t) => _t = t;

    /// <summary>
    /// Issues a signed boundary decision for a capability request.
    /// POST /admin/boundary/decide
    /// </summary>
    public Task<BoundaryDecision> Decide(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<BoundaryDecision>("/admin/boundary/decide", body, ct);

    /// <summary>
    /// A policy-aware decision under exactly one basis, <see cref="EvaluateRequest.AttestationId"/> or
    /// <see cref="EvaluateRequest.Agreement"/>: the signed decision and its justification proof. A denial is a signed
    /// decision with <see cref="BoundaryDecision.Authorized"/> false, not an error. Verify the decision before acting
    /// on it (<see cref="OfflineVerifier.VerifyBoundaryDecision"/>).
    /// POST /admin/boundary/evaluate
    /// </summary>
    /// <exception cref="ArgumentException">Neither basis is set, or both are.</exception>
    /// <exception cref="SecretMaterialException">
    /// The context holds a field named like a secret, a value that looks like a key or token, or more than
    /// <see cref="EvidenceMetadata.MaxMetadataBytes"/>: nothing is signed or sent. This is the SDK's own guard (the NA
    /// does not check contexts); put identifiers, versions and timestamps in a context, never secret values.
    /// </exception>
    public async Task<BoundaryEvaluation> Evaluate(EvaluateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.AttestationId is null) == (request.Agreement is null))
            throw new ArgumentException("genesismesh: set exactly one of AttestationId or Agreement", nameof(request));
        if (EvidenceMetadata.CheckMetadataOnly(Transport.ToElement(request.Context ?? new EvaluationContext())) is { } reason)
            throw new SecretMaterialException(reason);
        return await _t.AdminPostAsync<BoundaryEvaluation>("/admin/boundary/evaluate", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a boundary decision signature (public route).
    /// POST /boundary/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/boundary/verify", body, idempotent: true, ct);
}
