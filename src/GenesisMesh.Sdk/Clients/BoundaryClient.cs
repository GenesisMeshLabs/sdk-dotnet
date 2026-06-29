namespace GenesisMesh;

/// <summary>Capability boundary decisions — decide and verify.</summary>
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
    /// Verifies a boundary decision signature (public route).
    /// POST /boundary/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/boundary/verify", body, ct);
}
