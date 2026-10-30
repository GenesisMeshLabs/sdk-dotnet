namespace GenesisMesh;

/// <summary>
/// The declarative boundary policy lifecycle (v0.58): validate, publish, activate (and roll back), deactivate, list
/// and verify. Publishing, activating and deactivating need a privileged key.
/// </summary>
public sealed class PolicyClient
{
    private readonly Transport _t;
    internal PolicyClient(Transport t) => _t = t;

    /// <summary>
    /// A dry run of <paramref name="intent"/> against the NA's installed gate types: the issues publishing would
    /// refuse it for, and the version it would create.
    /// POST /admin/boundary-policies/validate
    /// </summary>
    public Task<PolicyValidation> Validate(BoundaryPolicyIntent intent, CancellationToken ct = default) =>
        _t.AdminPostAsync<PolicyValidation>("/admin/boundary-policies/validate", intent, ct);

    /// <summary>
    /// Validates, signs and stores <paramref name="intent"/> as a new inactive version (privileged). An invalid intent
    /// is refused with 400 <c>boundary_policy_invalid</c>, its issues in <see cref="GenesisMeshException.Details"/>.
    /// POST /admin/boundary-policies
    /// </summary>
    public Task<BoundaryPolicy> Publish(BoundaryPolicyIntent intent, CancellationToken ct = default) =>
        _t.AdminPostAsync<BoundaryPolicy>("/admin/boundary-policies", intent, ct);

    /// <summary>
    /// Every stored version of every policy.
    /// GET /admin/boundary-policies
    /// </summary>
    public async Task<IReadOnlyList<PolicyVersionSummary>> List(CancellationToken ct = default) =>
        (await _t.AdminGetAsync<PolicyList>("/admin/boundary-policies", null, ct).ConfigureAwait(false)).Policies;

    /// <summary>
    /// The active set, its health and the enforcement mode.
    /// GET /admin/boundary-policies/active
    /// </summary>
    public Task<ActivePolicyStatus> Active(CancellationToken ct = default) =>
        _t.AdminGetAsync<ActivePolicyStatus>("/admin/boundary-policies/active", null, ct);

    /// <summary>
    /// Every version of one policy, newest first, with the signed bodies.
    /// GET /admin/boundary-policies/{policy_id}/history
    /// </summary>
    public Task<PolicyHistory> History(string policyId, CancellationToken ct = default) =>
        _t.AdminGetAsync<PolicyHistory>(
            $"/admin/boundary-policies/{Transport.PathSegment(policyId, nameof(policyId))}/history", null, ct);

    /// <summary>
    /// Activates <paramref name="version"/> of a policy (privileged); activating an older version is the rollback.
    /// POST /admin/boundary-policies/{policy_id}/activate
    /// </summary>
    public Task<PolicyActivation> Activate(string policyId, int version, CancellationToken ct = default) =>
        _t.AdminPostAsync<PolicyActivation>(
            $"/admin/boundary-policies/{Transport.PathSegment(policyId, nameof(policyId))}/activate",
            new Dictionary<string, object?> { ["version"] = version }, ct);

    /// <summary>
    /// Deactivates an active version of a policy (privileged).
    /// POST /admin/boundary-policies/{policy_id}/deactivate
    /// </summary>
    public Task<PolicyActivation> Deactivate(string policyId, int version, CancellationToken ct = default) =>
        _t.AdminPostAsync<PolicyActivation>(
            $"/admin/boundary-policies/{Transport.PathSegment(policyId, nameof(policyId))}/deactivate",
            new Dictionary<string, object?> { ["version"] = version }, ct);

    /// <summary>
    /// Verifies a policy's signature (public route): <paramref name="policy"/> as the NA returned it (from JSON,
    /// deserialize it with <see cref="Auth.SerializerOptions"/>). <paramref name="issuerPublicKeys"/> defaults to the
    /// NA's own key.
    /// POST /boundary-policies/verify
    /// </summary>
    public Task<PolicyVerification> Verify(BoundaryPolicy policy, IReadOnlyList<string>? issuerPublicKeys = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var body = new Dictionary<string, object?> { ["policy"] = policy };
        if (issuerPublicKeys is not null) body["issuer_public_keys"] = issuerPublicKeys;
        return _t.PublicPostAsync<PolicyVerification>("/boundary-policies/verify", body, idempotent: true, ct);
    }
}
