namespace GenesisMesh;

/// <summary>Trust evidence — build and verify.</summary>
public sealed class EvidenceClient
{
    private readonly Transport _t;
    internal EvidenceClient(Transport t) => _t = t;

    /// <summary>
    /// Builds and signs trust evidence from a <see cref="TrustDecision"/>.
    /// POST /admin/trust-evidence
    /// <para>
    /// verdict must be "allow" | "block" | "escalate" | "warn" — not "trusted".
    /// </para>
    /// </summary>
    public Task<TrustEvidence> Build(TrustDecision decision, CancellationToken ct = default) =>
        _t.AdminPostAsync<TrustEvidence>("/admin/trust-evidence",
            new Dictionary<string, object?> { ["decision"] = decision }, ct);

    /// <summary>
    /// Verifies trust evidence signatures (public route).
    /// POST /trust-evidence/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/trust-evidence/verify", body, ct);
}
