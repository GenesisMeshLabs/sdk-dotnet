namespace GenesisMesh;

/// <summary>Consensus voting and proof assembly — vote, proof, verify.</summary>
public sealed class ConsensusClient
{
    private readonly Transport _t;
    internal ConsensusClient(Transport t) => _t = t;

    /// <summary>
    /// Casts a validator vote signed by the NA.
    /// POST /admin/consensus/vote
    /// </summary>
    public Task<ConsensusVote> Vote(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<ConsensusVote>("/admin/consensus/vote", body, ct);

    /// <summary>
    /// Assembles a consensus proof from validator votes.
    /// POST /admin/consensus/proof
    /// </summary>
    public Task<ConsensusProof> Proof(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<ConsensusProof>("/admin/consensus/proof", body, ct);

    /// <summary>
    /// Verifies a consensus proof and threshold (public route).
    /// POST /consensus/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/consensus/verify", body, ct);
}
