using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>
/// A grant to issue (<see cref="GrantsClient.IssueGrant"/>): one bearer key, the capabilities, exactly one basis (an
/// attestation this NA issued, or an agreement it trusts), a validity and a budget, and the closed envelope of the
/// request parameters its uses may carry.
/// </summary>
public sealed record IssueGrantRequest
{
    /// <summary>A registered, active executor key: the only key that can use the grant.</summary>
    public required string BearerKeyId { get; init; }

    /// <summary>Exact capability IDs, 1 to 16.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>An attestation this NA issued; or give <see cref="Agreement"/> instead.</summary>
    public string? AttestationId { get; init; }

    /// <summary>An agreement this NA trusts; or give <see cref="AttestationId"/> instead.</summary>
    public AgreementRecord? Agreement { get; init; }

    /// <summary>With an agreement basis only: the context freshness sequence; null for 0.</summary>
    public long? ContextFreshnessSeq { get; init; }

    /// <summary>When it starts: within the NA's clock skew of now and 24 hours from now; null for now.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>When it ends: at most 24 hours (and the NA's limit) after it starts.</summary>
    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>The budget: at most the NA's limit (<c>NA_GRANT_MAX_USES</c>, never over 10,000).</summary>
    public required int MaxUses { get; init; }

    /// <summary>How many uses may follow the use a time token names; null for the NA's limit, or <see cref="MaxUses"/> when lower.</summary>
    public int? MaxUsesPerTimeToken { get; init; }

    /// <summary>
    /// The closed envelope: every request parameter a use may carry, by name (<c>[A-Za-z0-9_-]{1,64}</c>, at most 64).
    /// Null for none: a use then carries no request parameters.
    /// </summary>
    public IReadOnlyDictionary<string, EnvelopeParameter>? ParameterEnvelope { get; init; }
}

/// <summary>
/// Pre-issued grants (Genesis Mesh 1.6.0, beta routes; 1.9.0 in this SDK), as the TypeScript SDK's
/// <c>GrantsClient</c>: issuing, reading and revoking grants (admin), and the holder's calls, authenticated by the
/// grant's bearer key: time tokens and uses. With the NA's grants off (<c>EVIDENCE_GRANTS=off</c>, its default) every
/// route answers <c>404 grants_disabled</c> (<see cref="NotFoundException"/>).
/// </summary>
public sealed class GrantsClient
{
    /// <summary>The NA's largest page of grants or uses.</summary>
    private const int MaxPage = 500;

    private readonly Transport _t;

    internal GrantsClient(Transport transport) => _t = transport;

    /// <summary>
    /// Issues a grant (privileged tier). The NA checks the basis and every active policy over the grant's whole envelope
    /// and validity, then signs the grant and its first time token and stores the grant. Not retried: a second call
    /// would issue a second grant.
    /// POST /admin/grants
    /// </summary>
    /// <param name="request">The grant to issue.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ValidationException">
    /// The NA will not issue it, with the first problem's code (every problem in its details): from
    /// <c>grant_basis_invalid</c> to <c>grant_gate_may_deny</c> (an applied policy could deny a request the envelope
    /// allows); or the bearer key is unknown, not an executor key or retired (<c>grant_bearer_key_unknown</c>,
    /// <c>grant_bearer_key_not_executor</c>, <c>grant_bearer_key_retired</c>).
    /// </exception>
    /// <exception cref="BadRequestException">
    /// The request is not one the NA takes: <c>invalid_grant_request</c>, <c>ambiguous_basis</c> (not exactly one
    /// basis), <c>invalid_grant_validity</c>, <c>invalid_grant_envelope</c> (a parameter it cannot read, or names and
    /// values that may carry secret material), <c>grant_limit_exceeded</c> (over this NA's limits).
    /// </exception>
    /// <exception cref="ConflictException">
    /// <c>grant_issuance_conflict</c>: what the grant rests on changed meanwhile; issue it again.
    /// </exception>
    public async Task<IssuedGrant> IssueGrant(IssueGrantRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>
        {
            ["bearer_key_id"] = request.BearerKeyId,
            ["capabilities"] = request.Capabilities,
            ["valid_until"] = EvidenceStoreClient.Rfc3339(request.ValidUntil),
            ["max_uses"] = request.MaxUses,
        };
        if (request.AttestationId is not null) body["attestation_id"] = request.AttestationId;
        if (request.Agreement is not null) body["agreement"] = request.Agreement;
        if (request.ContextFreshnessSeq is { } freshness) body["context_freshness_seq"] = freshness;
        if (request.ValidFrom is { } from) body["valid_from"] = EvidenceStoreClient.Rfc3339(from);
        if (request.MaxUsesPerTimeToken is { } perToken) body["max_uses_per_time_token"] = perToken;
        if (request.ParameterEnvelope is { } envelope)
        {
            var parameters = new JsonObject();
            foreach (var (name, parameter) in envelope) parameters[name] = JsonNode.Parse(parameter.ToJson());
            body["parameter_envelope"] = parameters;
        }
        return await _t.AdminPostAsync<IssuedGrant>("/admin/grants", body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of grants, newest first (read tier). Pass <see cref="GrantList.NextBefore"/> as the next
    /// <see cref="GrantListParams.Before"/>, or use <see cref="AllGrants"/>.
    /// GET /admin/grants
    /// </summary>
    /// <param name="page">The filter and the page; null for the first page of every grant.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GrantListParams.Limit"/> is not 1 to 500: nothing is sent.</exception>
    /// <exception cref="BadRequestException">
    /// <c>invalid_grant_filter</c> (a status other than <c>active</c>, <c>expired</c> or <c>revoked</c>) or
    /// <c>invalid_page</c> (a cursor that is not a grant's ID).
    /// </exception>
    public async Task<GrantList> Grants(GrantListParams? page = null, CancellationToken ct = default) =>
        await _t.AdminGetAsync<GrantList>("/admin/grants", ListQuery(page?.Status, page?.BearerKeyId, page?.Limit, page?.Before), ct)
            .ConfigureAwait(false);

    /// <summary>Every grant matching the filter, newest first, following the pages (read tier).</summary>
    /// <param name="status">As <see cref="GrantListParams.Status"/>.</param>
    /// <param name="bearerKeyId">As <see cref="GrantListParams.BearerKeyId"/>.</param>
    /// <param name="limit">Grants a page, 1 to 500; null for the NA's default (100).</param>
    /// <param name="ct">Cancels the reading.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not 1 to 500: nothing is sent.</exception>
    /// <exception cref="InvalidOperationException">The NA's cursor did not advance.</exception>
    public IAsyncEnumerable<GrantSummary> AllGrants(string? status = null, string? bearerKeyId = null, int? limit = null,
        CancellationToken ct = default)
    {
        ListQuery(status, bearerKeyId, limit, null);
        return GrantsFrom(status, bearerKeyId, limit, ct);
    }

    private async IAsyncEnumerable<GrantSummary> GrantsFrom(string? status, string? bearerKeyId, int? limit,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? before = null;
        while (true)
        {
            var page = await _t.AdminGetAsync<GrantList>("/admin/grants", ListQuery(status, bearerKeyId, limit, before), ct)
                .ConfigureAwait(false);
            foreach (var grant in page.Grants) yield return grant;
            if (page.NextBefore is not { } next) yield break;
            if (next == before) throw new InvalidOperationException("genesismesh: the grant list's cursor did not advance");
            before = next;
        }
    }

    private static Dictionary<string, string>? ListQuery(string? status, string? bearerKeyId, int? limit, string? before)
    {
        CheckLimit(limit);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (status is not null) query["status"] = status;
        if (bearerKeyId is not null) query["bearer_key_id"] = bearerKeyId;
        if (limit is { } n) query["limit"] = n.ToString(CultureInfo.InvariantCulture);
        if (before is not null) query["before"] = before;
        return query.Count == 0 ? null : query;
    }

    private static void CheckLimit(int? limit)
    {
        if (limit is not { } l) return;
        ArgumentOutOfRangeException.ThrowIfLessThan(l, 1, nameof(limit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(l, MaxPage, nameof(limit));
    }

    /// <summary>
    /// One grant: its state, the uses left, the signed grant, its policy versions and its revocation (read tier).
    /// GET /admin/grants/&lt;grant_id&gt;
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="NotFoundException">No such grant (<c>grant_not_found</c>).</exception>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is not one path segment: nothing is sent.</exception>
    public async Task<GrantDetail> Grant(string grantId, CancellationToken ct = default) =>
        await _t.AdminGetAsync<GrantDetail>($"/admin/grants/{Transport.LastPathSegment(grantId, nameof(grantId))}", null, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// One page of a grant's use chain, newest first (read tier): its admitted uses, and the quarantined uses that
    /// continued it. A use refused as a conflict or a chain mismatch is not in the chain. Pass
    /// <see cref="GrantUses.NextBefore"/> as the next <see cref="GrantUsesParams.Before"/>, or use <see cref="AllGrantUses"/>.
    /// GET /admin/grants/&lt;grant_id&gt;/uses
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="page">The page; null for the first.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GrantUsesParams.Limit"/> is not 1 to 500: nothing is sent.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is not one path segment: nothing is sent.</exception>
    /// <exception cref="NotFoundException">No such grant (<c>grant_not_found</c>).</exception>
    public async Task<GrantUses> GrantUses(string grantId, GrantUsesParams? page = null, CancellationToken ct = default) =>
        await _t.AdminGetAsync<GrantUses>(UsesPath(grantId), UsesQuery(page?.Limit, page?.Before), ct).ConfigureAwait(false);

    /// <summary>Every position of a grant's use chain, newest first, following the pages (read tier).</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="limit">Uses a page, 1 to 500; null for the NA's default (100).</param>
    /// <param name="ct">Cancels the reading.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not 1 to 500: nothing is sent.</exception>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is not one path segment: nothing is sent.</exception>
    /// <exception cref="InvalidOperationException">The NA's cursor did not advance.</exception>
    public IAsyncEnumerable<GrantUseSummary> AllGrantUses(string grantId, int? limit = null, CancellationToken ct = default)
    {
        var path = UsesPath(grantId);
        UsesQuery(limit, null);
        return UsesFrom(path, limit, ct);
    }

    private async IAsyncEnumerable<GrantUseSummary> UsesFrom(string path, int? limit, [EnumeratorCancellation] CancellationToken ct)
    {
        int? before = null;
        while (true)
        {
            var page = await _t.AdminGetAsync<GrantUses>(path, UsesQuery(limit, before), ct).ConfigureAwait(false);
            foreach (var use in page.Uses) yield return use;
            if (page.NextBefore is not { } next) yield break;
            if (next == before) throw new InvalidOperationException("genesismesh: the use chain's cursor did not advance");
            before = next;
        }
    }

    private static string UsesPath(string grantId) => $"/admin/grants/{Transport.LastPathSegment(grantId, nameof(grantId))}/uses";

    private static Dictionary<string, string>? UsesQuery(int? limit, int? before)
    {
        CheckLimit(limit);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (limit is { } n) query["limit"] = n.ToString(CultureInfo.InvariantCulture);
        if (before is { } b) query["before"] = b.ToString(CultureInfo.InvariantCulture);
        return query.Count == 0 ? null : query;
    }

    /// <summary>
    /// Revokes a grant now (privileged tier): uses executed from now on are refused (<c>grant_revoked</c>), and the NA's
    /// revocation feed lists it. The revocation's reason is <c>operator</c>; <paramref name="reason"/> is its detail. Not
    /// retried: sent again, it would find the grant revoked.
    /// POST /admin/grants/&lt;grant_id&gt;/revoke
    /// </summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="reason">Why, 1 to 1024 characters; null for none.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ConflictException"><c>grant_already_revoked</c>.</exception>
    /// <exception cref="BadRequestException"><c>invalid_grant_revocation</c>: the reason is not 1 to 1024 characters.</exception>
    /// <exception cref="NotFoundException">No such grant (<c>grant_not_found</c>).</exception>
    /// <exception cref="ArgumentException"><paramref name="grantId"/> is not one path segment: nothing is sent.</exception>
    public async Task<GrantRevoked> RevokeGrant(string grantId, string? reason = null, CancellationToken ct = default) =>
        await _t.AdminPostAsync<GrantRevoked>($"/admin/grants/{Transport.LastPathSegment(grantId, nameof(grantId))}/revoke",
            reason is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["reason"] = reason }, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// A time token for the grant's holder, bound to the head of its use chain: the request names the uses made
    /// (<c>use_sequence</c>) and the last one's digest (<c>use_head_digest</c>), and is signed by the grant's bearer key
    /// (<see cref="GrantRecords.SignTimeTokenRequest"/>). Returns the token exactly as the NA signed it, to give to the
    /// next use (<see cref="GrantUseInput.TimeToken"/>); <see cref="OfflineVerifier.VerifyTimeToken(string, IReadOnlyList{string})"/>
    /// checks it. Retried on a transient failure: each answer is a token for the same head.
    /// POST /grants/time-tokens
    /// </summary>
    /// <param name="request">The signed request.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ConflictException">
    /// The grant can no longer be used: <c>grant_expired</c>, <c>grant_revoked</c>, <c>grant_budget_exhausted</c> or
    /// <c>grant_bearer_key_retired</c> (a holder falls back to break-glass when the NA cannot be reached later); or the
    /// request names another head than the NA holds (<c>time_token_request_behind</c>,
    /// <c>time_token_request_mismatch</c>).
    /// </exception>
    /// <exception cref="ValidationException">
    /// <c>time_token_request_malformed</c>, <c>time_token_request_invalid_signature</c>, or
    /// <c>time_token_request_stale</c> (<c>requested_at</c> more than 300 seconds from the NA's clock).
    /// </exception>
    /// <exception cref="NotFoundException">No such grant (<c>grant_not_found</c>).</exception>
    /// <exception cref="NetworkException">
    /// No answer, or one without a time token (<see cref="NetworkException.ResponseBodyUnreadable"/>): ask again.
    /// </exception>
    public async Task<JsonElement> RequestTimeToken(TimeTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var answer = await _t.PublicPostAsync<TimeTokenAnswer>("/grants/time-tokens",
            new Dictionary<string, object?> { ["request"] = request }, idempotent: true, ct).ConfigureAwait(false);
        // The NA answered but its answer holds no token: it may have issued one, as for a body that could not be read.
        return answer.TimeToken.ValueKind == JsonValueKind.Object
            ? answer.TimeToken
            : throw new NetworkException("the NA's answer holds no time token", null, connectFailed: false,
                NetworkException.ResponseBodyUnreadable, 201);
    }

    /// <summary>
    /// Submits one use signed with the grant's bearer key (<see cref="GrantRecords.SignUse"/>), authenticated by that
    /// signature. The NA admits uses in their chain's order and gives each its place in its resource's chain
    /// (<see cref="RecordSubmission.ResourceSequence"/>): <c>recorded</c>, or <c>duplicate</c> for the same use again, so
    /// it is safe to retry. Keep uses in the record outbox instead (<see cref="EvidenceStoreClient.EnqueueRecord(GrantExecutionEvidence, CancellationToken)"/>)
    /// when the NA may be out of reach.
    /// POST /evidence/grant-executions
    /// </summary>
    /// <param name="use">The signed use.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ValidationException">
    /// The NA refused the use, with a code of <see cref="GrantUseRejectionCodes.All"/>. One it keeps
    /// (<see cref="GrantUseRejectionCodes.Quarantined"/>) names its quarantine entry in the details
    /// (<c>quarantine_id</c>), the same one when the use is sent again; <c>grant_use_chain_gap</c> asks for the earlier
    /// uses first, and <c>grant_use_in_future</c> to send it again later.
    /// </exception>
    /// <exception cref="ConflictException">Another use took its place in the chain (<c>grant_use_conflict</c>).</exception>
    /// <exception cref="NotFoundException">No such grant (<c>grant_not_found</c>).</exception>
    public async Task<RecordSubmission> SubmitGrantUse(GrantExecutionEvidence use, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(use);
        return await _t.PublicPostAsync<RecordSubmission>("/evidence/grant-executions",
            new Dictionary<string, object?> { ["evidence"] = use }, idempotent: true, ct).ConfigureAwait(false);
    }

    /// <summary>The NA's answer to a time token request.</summary>
    private sealed class TimeTokenAnswer
    {
        [JsonPropertyName("time_token")]
        public JsonElement TimeToken { get; set; }
    }
}
