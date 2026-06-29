namespace GenesisMesh;

/// <summary>Membership attestations — issue, revoke, recognition policy.</summary>
public sealed class AttestationClient
{
    private readonly Transport _t;
    internal AttestationClient(Transport t) => _t = t;

    /// <summary>
    /// Issues a signed membership attestation.
    /// POST /admin/attestations
    /// <para>
    /// Roles must use recognised prefixes: role:anchor, role:bridge, role:client,
    /// role:operator, role:service:&lt;name&gt;. Bare names return HTTP 422.
    /// </para>
    /// </summary>
    public Task<MembershipAttestation> Issue(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<MembershipAttestation>("/admin/attestations", body, ct);

    /// <summary>
    /// Revokes an attestation by ID.
    /// POST /admin/attestations/{id}/revoke
    /// </summary>
    public Task Revoke(string attestationId, IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync($"/admin/attestations/{attestationId}/revoke", body, ct);

    /// <summary>
    /// Sets the active recognition policy.
    /// POST /admin/recognition-policy
    /// </summary>
    public Task SavePolicy(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync("/admin/recognition-policy", body, ct);
}
