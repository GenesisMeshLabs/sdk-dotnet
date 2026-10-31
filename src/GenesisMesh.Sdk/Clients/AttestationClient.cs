namespace GenesisMesh;

/// <summary>
/// Membership attestations: issue, revoke, recognition policy, and (1.9.0) reading and verifying them and the NA's
/// revocation feed.
/// </summary>
public sealed class AttestationClient
{
    private readonly Transport _t;
    internal AttestationClient(Transport t) => _t = t;

    /// <summary>
    /// Issues a signed membership attestation.
    /// POST /admin/attestations
    /// <para>
    /// Roles must use recognised prefixes: role:anchor, role:bridge, role:client,
    /// role:operator, role:service:&lt;name&gt;. The NA refuses a bare name with 400 invalid_role
    /// (<see cref="BadRequestException"/>).
    /// </para>
    /// </summary>
    public Task<MembershipAttestation> Issue(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<MembershipAttestation>("/admin/attestations", body, ct);

    /// <summary>
    /// Revokes an attestation by ID.
    /// POST /admin/attestations/{id}/revoke
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="attestationId"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="attestationId"/> is not one path segment (1.8.0): empty, "." or "..", or holding '/', '\',
    /// '?', '#', '%' or a control character. Nothing is signed or sent.
    /// </exception>
    public Task Revoke(string attestationId, IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync($"/admin/attestations/{Transport.PathSegment(attestationId, nameof(attestationId))}/revoke", body, ct);

    /// <summary>
    /// Sets the active recognition policy.
    /// POST /admin/recognition-policy
    /// </summary>
    public Task SavePolicy(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync("/admin/recognition-policy", body, ct);

    /// <summary>
    /// A stored attestation with its current status (1.9.0). The signed body keeps the status it was issued with: read
    /// <see cref="AttestationRecord.Status"/> for what holds now.
    /// GET /attestations/{id}
    /// </summary>
    /// <exception cref="NotFoundException">No such attestation is stored (<c>attestation_not_found</c>).</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="attestationId"/> is not one path segment: empty, "." or "..", holding '/', '\', '?', '#', '%' or
    /// a control character, or starting or ending with white space. Nothing is sent.
    /// </exception>
    public async Task<AttestationRecord> Get(string attestationId, CancellationToken ct = default) =>
        await _t.PublicGetAsync<AttestationRecord>(
            "/attestations/" + Transport.LastPathSegment(attestationId, nameof(attestationId)), null, ct).ConfigureAwait(false);

    /// <summary>
    /// Stored attestations, by issuer, subject or status (1.9.0). The NA lists them to operators and gives anyone else
    /// the count: a client with a signing key signs the request (a read-tier key or above) and gets
    /// <see cref="AttestationList.Attestations"/>; without one it gets <see cref="AttestationList.Count"/>.
    /// GET /attestations
    /// </summary>
    /// <exception cref="UnauthorizedException">
    /// Without a signing key, a filter by issuer or subject: it would tell who is a member (<c>admin_auth_failed</c>).
    /// </exception>
    public Task<AttestationList> List(ListAttestationsParams? filter = null, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (filter?.IssuerSovereignId is { } issuer) query["issuer_sovereign_id"] = issuer;
        if (filter?.SubjectId is { } subject) query["subject_id"] = subject;
        if (filter?.Status is { } status) query["status"] = status;
        return _t.CanSign
            ? _t.AdminGetAsync<AttestationList>("/attestations", query, ct)
            : _t.PublicGetAsync<AttestationList>("/attestations", query, ct);
    }

    /// <summary>
    /// The NA's verification of an attestation against a recognition policy, by default its own active one (1.9.0).
    /// For the NA's own attestations it also checks that it holds the copy (<c>not_held</c>) and (v1.8.0) that the key
    /// that signed it still vouches for it (<c>outlives_key</c>).
    /// POST /attestations/verify
    /// </summary>
    /// <exception cref="BadRequestException">No policy is given and none is active (<c>missing_recognition_policy</c>).</exception>
    /// <exception cref="ValidationException">The attestation or the policy cannot be read (<c>invalid_attestation_or_policy</c>).</exception>
    public async Task<AttestationVerification> Verify(VerifyAttestationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _t.PublicPostAsync<AttestationVerification>("/attestations/verify", request, idempotent: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The NA's active recognition policy (1.9.0).
    /// GET /recognition-policy
    /// </summary>
    /// <exception cref="NotFoundException">None is set (<c>recognition_policy_not_configured</c>).</exception>
    public Task<RecognitionPolicy> GetPolicy(CancellationToken ct = default) =>
        _t.PublicGetAsync<RecognitionPolicy>("/recognition-policy", ct);

    /// <summary>
    /// The NA's signed feed of the attestations it revoked (1.9.0): every revoked copy, superseded ones included, and
    /// (v1.6.0, with grants on) the grants it revoked that have not ended. Another sovereign's feed comes from that
    /// sovereign's NA.
    /// GET /sovereign-revocation-feed
    /// </summary>
    /// <param name="issuerSovereignId">The sovereign whose feed to read; null for this NA's.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="BadRequestException">
    /// <paramref name="issuerSovereignId"/> is another sovereign than this NA's (<c>feed_issuer_mismatch</c>).
    /// </exception>
    public Task<SovereignRevocationFeed> RevocationFeed(string? issuerSovereignId = null, CancellationToken ct = default) =>
        _t.PublicGetAsync<SovereignRevocationFeed>("/sovereign-revocation-feed",
            issuerSovereignId is null ? null : new Dictionary<string, string> { ["issuer_sovereign_id"] = issuerSovereignId }, ct);
}
