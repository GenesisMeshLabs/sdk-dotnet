using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Tests;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Pre-issued grants, the NA's calls (1.9.0): issuing, listing, reading and revoking grants, a grant's use chain, time
/// tokens and uses, and uses in the record outbox.
/// </summary>
public class GrantsClientTests
{
    private sealed record Sent(string Method, string Path, string? Body, bool Admin);

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refused(int status, string code, string details = "null") =>
        Json(status, """{"error":{"code":""" + JsonSerializer.Serialize(code) + ""","message":"refused","details":""" + details + "}}");

    private static GenesisMeshClient Client(List<Sent> sent, Func<HttpRequestMessage, HttpResponseMessage> answer, int retries = 0,
        IOutbox<RecordOutboxEntry>? outbox = null) => new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = "TEST",
            Retry = new RetrySettings { Attempts = retries, BaseDelay = TimeSpan.Zero },
            RecordOutbox = outbox,
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, Uri.UnescapeDataString(req.RequestUri!.PathAndQuery),
                    req.Content?.ReadAsStringAsync().Result, req.Headers.Contains("X-Admin-Key-Id")));
                return answer(req);
            }),
        });

    private static readonly JsonElement Suite = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "grants.json"))).RootElement;

    private static string Record(string id) =>
        Suite.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id).GetProperty("record").GetRawText();

    private static string Entry(string kind, string recordId) =>
        $$"""{"store_sequence":7,"entry_kind":"{{kind}}","recorded_at":"2026-01-01T00:00:01Z","payload_digest":"00","prev_entry_digest":"11","decision_id":null,"context_id":null,"vendor_id":null,"attestation_id":null,"capability":null,"outcome":null,"evidence_id":null,"executor_sovereign_id":null,"exec_sequence_no":null,"resource_id":null,"resource_action":null,"resource_sequence":null,"record_id":"{{recordId}}"}""";

    private static string Summary(string id = "g-1", string status = "active") =>
        $$"""
        {"grant_id":"{{id}}","status":"{{status}}","bearer_key_id":"executor-b","bearer_sovereign_id":"device-b","capabilities":["secret.rotate"],
         "basis_kind":"attestation","basis_id":"a-1","valid_from":"2026-01-01T00:00:00Z","valid_until":"2026-01-01T04:00:00Z","max_uses":3,
         "uses":1,"issued_at":"2025-12-31T23:59:00Z","issued_by_operator":"ops","store_sequence":7,"revoked_at":null,"revocation_reason":null}
        """;

    // ── Issuing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueGrant_SendsTheRequestAndKeepsTheTimeTokenAsSigned()
    {
        var sent = new List<Sent>();
        var token = Record("canonical-TimeToken-first");
        using var gm = Client(sent, _ => Json(201,
            $$"""{"grant":{{Record("signature-CapabilityGrant-signer")}},"time_token":{{token}},"entry":{{Entry("grant", "g-1")}},"entry_digest":"22"}"""));
        var issued = await gm.Grants.IssueGrant(new IssueGrantRequest
        {
            BearerKeyId = "executor-b",
            Capabilities = ["secret.rotate"],
            AttestationId = "a-1",
            ValidFrom = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)),
            ValidUntil = new DateTimeOffset(2026, 1, 1, 4, 0, 0, 250, TimeSpan.Zero),
            MaxUses = 3,
            MaxUsesPerTimeToken = 2,
            ParameterEnvelope = new Dictionary<string, EnvelopeParameter>
            {
                ["lifetime_days"] = new(true, null, null, new NumericRange("integer", GrantNumber.Of(1), GrantNumber.Of(90), false, false)),
                ["region"] = new(false, [JsonSerializer.SerializeToElement("eu-west")], null, null),
            },
        });
        Assert.Equal(("POST", "/admin/grants", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(
            """{"bearer_key_id":"executor-b","capabilities":["secret.rotate"],"valid_until":"2026-01-01T04:00:00.250000Z","max_uses":3,"attestation_id":"a-1","valid_from":"2026-01-01T00:00:00Z","max_uses_per_time_token":2,"parameter_envelope":{"lifetime_days":{"required":true,"one_of":null,"subset_of":null,"number_range":{"number_type":"integer","minimum":1,"maximum":90,"exclusive_minimum":false,"exclusive_maximum":false}},"region":{"required":false,"one_of":["eu-west"],"subset_of":null,"number_range":null}}}""",
            sent[0].Body);
        Assert.Equal((7L, "22"), (issued.Entry.StoreSequence, issued.EntryDigest));
        Assert.Equal(token, issued.TimeToken.GetRawText());
        Assert.True(OfflineVerifier.VerifyTimeToken(issued.TimeToken.GetRawText(), [Key("signature-TimeToken-signer")]));
        Assert.Equal(GrantRules.RecordDigest("CapabilityGrant", Record("signature-CapabilityGrant-signer")), GrantRules.GrantDigest(issued.Grant));
    }

    private static string Key(string id) =>
        Suite.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id).GetProperty("public_key").GetString()!;

    [Fact]
    public async Task IssueGrant_LeavesOutWhatIsNotGiven_AndIsNotRetried()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Refused(503, "unavailable"), retries: 2);
        var agreement = new AgreementRecord { AgreementId = "agr-1" };
        await Assert.ThrowsAsync<ServerException>(() => gm.Grants.IssueGrant(new IssueGrantRequest
        {
            BearerKeyId = "executor-b",
            Capabilities = ["secret.rotate"],
            Agreement = agreement,
            ContextFreshnessSeq = 4,
            ValidUntil = new DateTimeOffset(2026, 1, 1, 4, 0, 0, TimeSpan.Zero),
            MaxUses = 1,
        }));
        Assert.Single(sent);
        var body = JsonNode.Parse(sent[0].Body!)!.AsObject();
        Assert.Equal(["bearer_key_id", "capabilities", "valid_until", "max_uses", "agreement", "context_freshness_seq"], body.Select(p => p.Key));
        Assert.Equal("agr-1", body["agreement"]!["agreement_id"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.Grants.IssueGrant(null!));
    }

    [Fact]
    public async Task ARefusalKeepsTheNAsCode()
    {
        using var gm = Client([], _ => Refused(422, "grant_gate_may_deny", """{"problems":[{"reason":"grant_gate_may_deny"}]}"""));
        var request = new IssueGrantRequest { BearerKeyId = "k", Capabilities = ["c"], AttestationId = "a", ValidUntil = DateTimeOffset.UtcNow, MaxUses = 1 };
        var e = await Assert.ThrowsAsync<ValidationException>(() => gm.Grants.IssueGrant(request));
        Assert.Equal("grant_gate_may_deny", e.Code);
        Assert.Equal("grant_gate_may_deny", e.Details.GetProperty("problems")[0].GetProperty("reason").GetString());
        using var off = Client([], _ => Refused(404, "grants_disabled"));
        Assert.Equal("grants_disabled", (await Assert.ThrowsAsync<NotFoundException>(() => off.Grants.Grants())).Code);
    }

    // ── Reading ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Grants_SendsTheFilterAndReadsAPage()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, $$"""{"grants":[{{Summary()}}],"next_before":"g-1"}"""));
        var page = await gm.Grants.Grants(new GrantListParams { Status = GrantStatuses.Active, BearerKeyId = "executor-b", Limit = 1, Before = "g-0" });
        Assert.Equal(("GET", "/admin/grants?status=active&bearer_key_id=executor-b&limit=1&before=g-0", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        var grant = Assert.Single(page.Grants);
        Assert.Equal(("g-1", GrantStatuses.Active, 3, 1, 7L, "g-1"), (grant.GrantId, grant.Status, grant.MaxUses, grant.Uses, grant.StoreSequence, page.NextBefore));
        Assert.Null(grant.RevokedAt);
        await gm.Grants.Grants();
        Assert.Equal("/admin/grants", sent[1].Path);
    }

    [Fact]
    public async Task AllGrants_FollowsThePages()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => req.RequestUri!.Query.Contains("before=g-2", StringComparison.Ordinal)
            ? Json(200, $$"""{"grants":[{{Summary("g-1")}}],"next_before":null}""")
            : Json(200, $$"""{"grants":[{{Summary("g-3")}},{{Summary("g-2")}}],"next_before":"g-2"}"""));
        var ids = new List<string>();
        await foreach (var g in gm.Grants.AllGrants(GrantStatuses.Revoked, limit: 2)) ids.Add(g.GrantId);
        Assert.Equal(["g-3", "g-2", "g-1"], ids);
        Assert.Equal(["/admin/grants?status=revoked&limit=2", "/admin/grants?status=revoked&limit=2&before=g-2"], sent.Select(s => s.Path));
        using var stuck = Client([], _ => Json(200, $$"""{"grants":[{{Summary()}}],"next_before":"g-1"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in stuck.Grants.AllGrants()) { } });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ALimitOutOfRangeSendsNothing(int limit)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.Grants.Grants(new GrantListParams { Limit = limit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.Grants.AllGrants(limit: limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.Grants.GrantUses("g-1", new GrantUsesParams { Limit = limit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.Grants.AllGrantUses("g-1", limit));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Grant_ReadsTheGrantItsPoliciesAndItsRevocation()
    {
        var sent = new List<Sent>();
        var detail = Summary(status: "revoked").TrimEnd().TrimEnd('}')
            + $$""","remaining_uses":2,"chain_length":1,"grant":{{Record("signature-CapabilityGrant-signer")}},"policy_versions":[{"policy_id":"p-1","version":2}],"revocation":{{Record("signature-GrantRevocationRecord-signer")}}}""";
        using var gm = Client(sent, _ => Json(200, detail));
        var grant = await gm.Grants.Grant("g-1");
        Assert.Equal(("GET", "/admin/grants/g-1", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal((GrantStatuses.Revoked, 2, 1), (grant.Status, grant.RemainingUses, grant.ChainLength));
        Assert.Equal(("p-1", 2), (grant.PolicyVersions[0].PolicyId, grant.PolicyVersions[0].Version));
        Assert.Equal(GrantRevocationReasons.Operator, grant.Revocation!.Reason);
        Assert.True(OfflineVerifier.VerifyGrantRevocation(JsonSerializer.Serialize(grant.Revocation), [Key("signature-GrantRevocationRecord-signer")]));
        // A grant ID is one path segment: nothing is sent otherwise.
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Grants.Grant("g/1"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Grants.RevokeGrant(".."));
        Assert.Throws<ArgumentException>(() => gm.Grants.AllGrantUses("a?b"));
        Assert.Single(sent);
    }

    private static string Use(int sequence, string state = "admitted") =>
        $$"""{"use_id":"u-{{sequence}}","use_sequence":{{sequence}},"state":"{{state}}","rejection_code":{{(state == "admitted" ? "null" : "\"grant_envelope_mismatch\"")}},"executed_at":"2026-01-01T00:10:00Z","recorded_at":"2026-01-01T00:20:00Z","store_sequence":{{sequence + 7}},"resource_id":"kv:vault/api-key","resource_sequence":{{(state == "admitted" ? sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},"outcome":"success"}""";

    [Fact]
    public async Task GrantUses_ReadsTheChainNewestFirst()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => req.RequestUri!.Query.Contains("before=2", StringComparison.Ordinal)
            ? Json(200, $$"""{"grant_id":"g-1","uses":[{{Use(1)}}],"next_before":null}""")
            : Json(200, $$"""{"grant_id":"g-1","uses":[{{Use(3)}},{{Use(2, "quarantined")}}],"next_before":2}"""));
        var page = await gm.Grants.GrantUses("g-1", new GrantUsesParams { Limit = 2 });
        Assert.Equal(("GET", "/admin/grants/g-1/uses?limit=2", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(2, page.NextBefore);
        Assert.Equal(("quarantined", "grant_envelope_mismatch", (long?)null), (page.Uses[1].State, page.Uses[1].RejectionCode, page.Uses[1].ResourceSequence));
        Assert.Equal(3L, page.Uses[0].ResourceSequence);
        var all = new List<int>();
        await foreach (var use in gm.Grants.AllGrantUses("g-1", 2)) all.Add(use.UseSequence);
        Assert.Equal([3, 2, 1], all);
        Assert.Equal("/admin/grants/g-1/uses?limit=2&before=2", sent[^1].Path);
        using var stuck = Client([], _ => Json(200, $$"""{"grant_id":"g-1","uses":[{{Use(1)}}],"next_before":1}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in stuck.Grants.AllGrantUses("g-1")) { } });
    }

    [Fact]
    public async Task APositionWhoseEntryRetentionRemovedHasNoEntryFields()
    {
        using var gm = Client([], _ => Json(200,
            """{"grant_id":"g-1","uses":[{"use_id":"u-1","use_sequence":1,"state":"admitted","rejection_code":null,"executed_at":"2026-01-01T00:10:00Z","recorded_at":null,"store_sequence":8,"resource_id":null,"resource_sequence":null,"outcome":null}],"next_before":null}"""));
        var use = Assert.Single((await gm.Grants.GrantUses("g-1")).Uses);
        Assert.Equal(("u-1", 8L, "2026-01-01T00:10:00Z"), (use.UseId, use.StoreSequence, use.ExecutedAt));
        Assert.Null(use.RecordedAt);
        Assert.Null(use.ResourceId);
        Assert.Null(use.ResourceSequence);
        Assert.Null(use.Outcome);
    }

    [Fact]
    public async Task RevokeGrant_SendsTheReasonOnlyWhenGiven()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200,
            $$"""{"grant_id":"g-1","status":"revoked","revocation":{{Record("signature-GrantRevocationRecord-signer")}},"entry":{{Entry("grant_revocation", "r-1")}},"entry_digest":"44"}"""));
        var revoked = await gm.Grants.RevokeGrant("g-1", "rotated out");
        Assert.Equal(("POST", "/admin/grants/g-1/revoke", true, """{"reason":"rotated out"}"""), (sent[0].Method, sent[0].Path, sent[0].Admin, sent[0].Body));
        Assert.Equal((GrantStatuses.Revoked, "44"), (revoked.Status, revoked.EntryDigest));
        await gm.Grants.RevokeGrant("g-1");
        Assert.Equal("{}", sent[1].Body);
        using var again = Client([], _ => Refused(409, "grant_already_revoked"));
        Assert.Equal("grant_already_revoked", (await Assert.ThrowsAsync<ConflictException>(() => again.Grants.RevokeGrant("g-1"))).Code);
    }

    // ── The holder's calls ─────────────────────────────────────────────────

    // The reference's test key "b" (seed bytes 32 to 63): the grant's bearer key.
    private static readonly SeedSigner Bearer = new(Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()), "executor-b");

    [Fact]
    public async Task RequestTimeToken_SendsTheSignedRequestAndReturnsTheTokenAsSigned()
    {
        var sent = new List<Sent>();
        var token = Record("signature-TimeToken-signer");
        var calls = 0;
        using var gm = Client(sent, _ => ++calls == 1 ? Refused(503, "unavailable") : Json(201, $$"""{"time_token":{{token}}}"""), retries: 1);
        var request = await GrantRecords.SignTimeTokenRequest(new TimeTokenRequestInput { GrantId = "g-1", UseSequence = 0 }, Bearer);
        var issued = await gm.Grants.RequestTimeToken(request);
        Assert.Equal(token, issued.GetRawText());
        Assert.Equal(2, sent.Count);
        Assert.Equal(("POST", "/grants/time-tokens", false), (sent[1].Method, sent[1].Path, sent[1].Admin));
        var body = JsonNode.Parse(sent[1].Body!)!["request"]!.ToJsonString();
        Assert.True(OfflineVerifier.VerifyTimeTokenRequest(body, Key("signature-TimeTokenRequest-signer")));
        // An answer without a token: the NA may have issued one, so it is read as an answer that could not be read.
        using var empty = Client([], _ => Json(201, "{}"));
        Assert.Equal(NetworkException.ResponseBodyUnreadable,
            (await Assert.ThrowsAsync<NetworkException>(() => empty.Grants.RequestTimeToken(request))).Code);
        using var ended = Client([], _ => Refused(409, "grant_expired"));
        Assert.Equal("grant_expired", (await Assert.ThrowsAsync<ConflictException>(() => ended.Grants.RequestTimeToken(request))).Code);
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.Grants.RequestTimeToken(null!));
    }

    private static GrantExecutionEvidence SampleUse() =>
        JsonSerializer.Deserialize<GrantExecutionEvidence>(Record("signature-GrantExecutionEvidence-signer"))!;

    [Fact]
    public async Task SubmitGrantUse_IsPublicAndRetried_AndReadsItsResourcePosition()
    {
        var sent = new List<Sent>();
        var use = SampleUse();
        var calls = 0;
        using var gm = Client(sent, _ => ++calls == 1
            ? Refused(503, "unavailable")
            : Json(201, $$"""{"status":"recorded","entry":{{Entry("grant_execution", use.UseId)}},"entry_digest":"55","payload":{{Record("signature-GrantExecutionEvidence-signer")}},"resource_sequence":4}"""),
            retries: 1);
        var result = await gm.Grants.SubmitGrantUse(use);
        Assert.Equal(("recorded", 4L), (result.Status, result.ResourceSequence));
        Assert.Equal(("POST", "/evidence/grant-executions", false), (sent[1].Method, sent[1].Path, sent[1].Admin));
        Assert.Equal(Canonical.FromJson(Record("signature-GrantExecutionEvidence-signer")),
            Canonical.FromJson(JsonNode.Parse(sent[1].Body!)!["evidence"]!.ToJsonString()));
        // A use the NA refuses for good names its quarantine entry.
        using var refused = Client([], _ => Refused(422, "grant_envelope_mismatch", """{"quarantine_id":"q-1"}"""));
        var e = await Assert.ThrowsAsync<ValidationException>(() => refused.Grants.SubmitGrantUse(use));
        Assert.Equal(("grant_envelope_mismatch", "q-1"), (e.Code, e.Details.GetProperty("quarantine_id").GetString()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.Grants.SubmitGrantUse(null!));
    }

    [Fact]
    public async Task AUseIsKeptInTheRecordOutboxAndSubmitted()
    {
        var sent = new List<Sent>();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        var use = SampleUse();
        var entry = EvidenceOutbox.RecordEntry(use);
        Assert.Equal((RecordOutboxKinds.GrantExecution, use.UseId, OutboxStates.Pending), (entry.Kind, entry.Id, entry.State));
        Assert.Equal(Canonical.FromJson(Record("signature-GrantExecutionEvidence-signer")), Canonical.FromJson(entry.Record.GetRawText()));
        using var gm = Client(sent, _ => Json(201,
            $$"""{"status":"recorded","entry":{{Entry("grant_execution", use.UseId)}},"entry_digest":"55","payload":{{Record("signature-GrantExecutionEvidence-signer")}},"resource_sequence":4}"""),
            outbox: outbox);
        var delivered = await gm.EvidenceStore.EnqueueRecord(use);
        Assert.Equal(("recorded", 4L), (delivered.Submission!.Status, delivered.Submission.ResourceSequence));
        Assert.Equal(("POST", "/evidence/grant-executions"), (sent[0].Method, sent[0].Path));
        Assert.Empty(await outbox.List());
        // Refused for good: a dead letter naming its quarantine entry.
        using var refusing = Client([], _ => Refused(422, "grant_revoked", """{"quarantine_id":"q-2"}"""), outbox: outbox);
        var kept = (await refusing.EvidenceStore.EnqueueRecord(use)).Queued!;
        Assert.Equal((OutboxStates.DeadLetter, "grant_revoked", "q-2"), (kept.State, kept.LastError!.Code, kept.LastError.QuarantineId));
        Assert.Throws<ArgumentNullException>(() => EvidenceOutbox.RecordEntry((GrantExecutionEvidence)null!));
    }

    [Theory]
    [InlineData(201, "")]
    [InlineData(204, "")]
    [InlineData(201, """{"time_token":null}""")]
    public async Task AnAnswerWithoutATimeTokenIsOneThatCouldNotBeRead(int status, string body)
    {
        using var gm = Client([], _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var request = await GrantRecords.SignTimeTokenRequest(new TimeTokenRequestInput { GrantId = "g-1", UseSequence = 0 }, Bearer);
        Assert.Equal(NetworkException.ResponseBodyUnreadable, (await Assert.ThrowsAsync<NetworkException>(() => gm.Grants.RequestTimeToken(request))).Code);
    }

    [Fact]
    public async Task AnEmptyPageEndsTheReading()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => req.RequestUri!.AbsolutePath.EndsWith("/uses", StringComparison.Ordinal)
            ? Json(200, """{"grant_id":"g-1","uses":[],"next_before":7}""")
            : Json(200, """{"grants":[],"next_before":"g-9"}"""));
        await foreach (var _ in gm.Grants.AllGrants()) Assert.Fail("no grant");
        await foreach (var _ in gm.Grants.AllGrantUses("g-1")) Assert.Fail("no use");
        Assert.Equal(2, sent.Count);
    }

    // ── A grant's uses in the record outbox ────────────────────────────────

    /// <summary>A use of <paramref name="grantId"/> at <paramref name="sequence"/> (its signature is not checked here).</summary>
    private static GrantExecutionEvidence UseAt(int sequence, string grantId = "00000000-0000-4000-8000-000000000010")
    {
        var use = SampleUse();
        use.UseSequence = sequence;
        use.GrantId = grantId;
        use.UseId = $"00000000-0000-4000-8000-{sequence:D6}{grantId[^6..]}";
        return use;
    }

    private static RecordOutboxEntry DeadLetter(GrantExecutionEvidence use, string code, string? quarantineId) =>
        EvidenceOutbox.RecordEntry(use) with
        {
            State = OutboxStates.DeadLetter,
            Attempts = 1,
            LastError = new SubmissionFailure { Status = 422, Code = code, Message = "refused", QuarantineId = quarantineId },
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> NA(Func<JsonNode, HttpResponseMessage> answer) =>
        req => answer(JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!["evidence"]!);

    private static HttpResponseMessage Recorded(JsonNode use) =>
        Json(201, $$"""{"status":"recorded","entry":{{Entry("grant_execution", use["use_id"]!.GetValue<string>())}},"entry_digest":"55","payload":{{use.ToJsonString()}},"resource_sequence":1}""");

    [Theory]
    [InlineData("grant_use_invalid_signature", null)]
    [InlineData("grant_use_malformed", null)]
    [InlineData("grant_use_conflict", "q-1")]
    [InlineData("grant_use_chain_mismatch", "q-1")]
    public async Task AUseBehindOneThatNeverTookItsPlaceIsADeadLetter(string refusal, string? quarantineId)
    {
        // Use 1 was refused before it took its place in the chain: the gap it left never closes, and the flush goes on.
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(DeadLetter(UseAt(1), refusal, quarantineId));
        var second = UseAt(2);
        var other = UseAt(1, "00000000-0000-4000-8000-000000000099");
        await outbox.Add(EvidenceOutbox.RecordEntry(second));
        await outbox.Add(EvidenceOutbox.RecordEntry(other));
        var sent = new List<Sent>();
        using var gm = Client(sent, NA(use => use["grant_id"]!.GetValue<string>() == other.GrantId ? Recorded(use) : Refused(422, "grant_use_chain_gap")),
            outbox: outbox);
        var flushed = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        var dead = Assert.Single(flushed.DeadLettered);
        Assert.Equal((second.UseId, EvidenceOutbox.PredecessorDeadLettered), (dead.Id, dead.LastError!.Code));
        Assert.Contains("use 1 of this grant was refused before it took its place in the chain", dead.LastError.Message, StringComparison.Ordinal);
        Assert.Equal([other.UseId], flushed.Admitted.Select(e => e.Id));
        Assert.Empty(flushed.Pending);
    }

    [Fact]
    public async Task AGapBehindAUseTheNAKeptInTheChainIsRetried()
    {
        // Quarantined for a reason that keeps its place: the NA places the next use once it holds this one.
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(DeadLetter(UseAt(1), "grant_use_outside_validity", "q-1"));
        await outbox.Add(EvidenceOutbox.RecordEntry(UseAt(2)));
        using var gm = Client([], NA(_ => Refused(422, "grant_use_chain_gap")), outbox: outbox);
        var flushed = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(["grant_use_chain_gap"], flushed.Pending.Select(e => e.LastError!.Code));
        Assert.Empty(flushed.DeadLettered);
    }

    [Fact]
    public async Task AGapIsRetriedWhenNoEarlierUseOfThisGrantWasRefused()
    {
        // A dead letter of another grant, or one later in this grant's chain, leaves this use's gap to close.
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(DeadLetter(UseAt(1, "00000000-0000-4000-8000-000000000099"), "grant_use_invalid_signature", null));
        await outbox.Add(DeadLetter(UseAt(3), "grant_use_invalid_signature", null));
        await outbox.Add(EvidenceOutbox.RecordEntry(UseAt(2)));
        using var gm = Client([], NA(_ => Refused(422, "grant_use_chain_gap")), outbox: outbox);
        var flushed = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(["grant_use_chain_gap"], flushed.Pending.Select(e => e.LastError!.Code));
        Assert.Empty(flushed.DeadLettered);
    }

    /// <summary>A record outbox whose listing fails after the first.</summary>
    private sealed class ListsOnce(MemoryOutbox<RecordOutboxEntry> inner) : IOutbox<RecordOutboxEntry>
    {
        private int _lists;

        public Task Add(RecordOutboxEntry entry, CancellationToken ct = default) => inner.Add(entry, ct);
        public Task Update(RecordOutboxEntry entry, CancellationToken ct = default) => inner.Update(entry, ct);
        public Task Remove(string id, CancellationToken ct = default) => inner.Remove(id, ct);
        public Task<IReadOnlyList<RecordOutboxEntry>> List(CancellationToken ct = default) =>
            Interlocked.Increment(ref _lists) == 1 ? inner.List(ct) : throw new IOException("the disk went away");
    }

    [Fact]
    public async Task AGapIsRetriedWhenTheOutboxCannotBeReadToLookBehindIt()
    {
        var inner = new MemoryOutbox<RecordOutboxEntry>();
        await inner.Add(DeadLetter(UseAt(1), "grant_use_invalid_signature", null));
        await inner.Add(EvidenceOutbox.RecordEntry(UseAt(2)));
        using var gm = Client([], NA(_ => Refused(422, "grant_use_chain_gap")), outbox: new ListsOnce(inner));
        var flushed = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(["grant_use_chain_gap"], flushed.Pending.Select(e => e.LastError!.Code));
    }

    [Fact]
    public async Task AGrantsUsesAreSentInTheirChainsOrder()
    {
        // Kept in the other order (two acts at once, offline): the NA still gets use 1 first.
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(EvidenceOutbox.RecordEntry(UseAt(2)));
        await outbox.Add(EvidenceOutbox.RecordEntry(UseAt(1)));
        var order = new List<int>();
        using var gm = Client([], NA(use =>
        {
            order.Add(use["use_sequence"]!.GetValue<int>());
            return Recorded(use);
        }), outbox: outbox);
        var flushed = await gm.EvidenceStore.FlushRecords();
        Assert.Equal([1, 2], order);
        Assert.Equal(2, flushed.Admitted.Count);
    }

    // ── Numbers for an envelope's bounds ───────────────────────────────────

    [Fact]
    public void ANumberIsMadeAsTheReferenceHoldsIt()
    {
        Assert.Equal("5", GrantNumber.Of(5).ToString());
        Assert.Equal("9007199254740993", GrantNumber.Of(9007199254740993L).ToString());
        Assert.Equal("5", GrantNumber.Of(5.0).ToString());
        Assert.Equal("2.5", GrantNumber.Of(2.5).ToString());
        Assert.True(GrantNumber.Of(5.0).IsIntegral);
        Assert.Equal("18446744073709551615", GrantNumber.Of(BigInteger.Pow(2, 64) - 1).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => GrantNumber.Of(BigInteger.Pow(2, 64)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrantNumber.Of(-BigInteger.Pow(2, 63) - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrantNumber.Of(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrantNumber.Of(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrantNumber.Of(1e20));
    }
}
