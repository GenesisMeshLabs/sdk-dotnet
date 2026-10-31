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
    /// <see cref="AttestationList.Attestations"/>; without one it gets <see cref="AttestationList.Count"/>. Both leave
    /// out copies another one re-issued, which <see cref="Get"/> and <see cref="RevocationFeed"/> still name.
    /// GET /attestations
    /// </summary>
    /// <exception cref="UnauthorizedException">
    /// <c>admin_auth_failed</c>: without a signing key, a filter by issuer or subject, which would tell who is a member;
    /// or the NA does not accept the key (unknown or revoked), even without a filter.
    /// </exception>
    /// <exception cref="ForbiddenException">The key has no tier that may read (<c>insufficient_operator_tier</c>).</exception>
    /// <exception cref="InvalidOperationException">
    /// The client has a signing key but no key ID (<see cref="ClientOptions.KeyId"/>): the NA would take the request as
    /// unsigned and answer with the count. Nothing is sent.
    /// </exception>
    public Task<AttestationList> List(ListAttestationsParams? filter = null, CancellationToken ct = default)
    {
        if (_t.CanSign && string.IsNullOrEmpty(_t.SignerKeyId))
            throw new InvalidOperationException(
                "genesismesh: a signing key needs a key ID (ClientOptions.KeyId) to list attestations");
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
    /// From NA 1.8.0, for its own attestations, it also checks that it holds the copy (<c>not_held</c>) and that the key
    /// that signed it still vouches for it (<c>outlives_key</c>). Sent again on a retry, as it changes nothing.
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
    /// <param name="issuerSovereignId">
    /// This NA's own sovereign, as a check, or null. Any other value, an empty one included, is refused.
    /// </param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="BadRequestException">
    /// <paramref name="issuerSovereignId"/> is another sovereign than this NA's (<c>feed_issuer_mismatch</c>).
    /// </exception>
    public Task<SovereignRevocationFeed> RevocationFeed(string? issuerSovereignId = null, CancellationToken ct = default) =>
        _t.PublicGetAsync<SovereignRevocationFeed>("/sovereign-revocation-feed",
            issuerSovereignId is null ? null : new Dictionary<string, string> { ["issuer_sovereign_id"] = issuerSovereignId }, ct);
}
