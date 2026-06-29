namespace GenesisMesh;

/// <summary>Selective capability disclosure — commit, nullifier, prove, verify.</summary>
public sealed class DisclosureClient
{
    private readonly Transport _t;
    internal DisclosureClient(Transport t) => _t = t;

    /// <summary>
    /// Commits to a capability set (Merkle root).
    /// POST /admin/disclosure/commit
    /// </summary>
    public Task<CapabilityCommitment> Commit(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<CapabilityCommitment>("/admin/disclosure/commit", body, ct);

    /// <summary>
    /// Issues a one-time nullifier for a proof.
    /// POST /admin/disclosure/nullifier
    /// </summary>
    public Task<Dictionary<string, object?>> Nullifier(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<Dictionary<string, object?>>("/admin/disclosure/nullifier", body, ct);

    /// <summary>
    /// Generates a Merkle membership proof (public route).
    /// POST /disclosure/prove
    /// </summary>
    public Task<CapabilityMembershipProof> Prove(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<CapabilityMembershipProof>("/disclosure/prove", body, ct);

    /// <summary>
    /// Verifies a capability membership proof (public route).
    /// POST /disclosure/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/disclosure/verify", body, ct);
}
