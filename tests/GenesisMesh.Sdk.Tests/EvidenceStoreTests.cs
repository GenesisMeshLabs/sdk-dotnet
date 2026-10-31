using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// The evidence store client (1.9.0): submission, search, resource heads, resource states, executor keys, and the store's
/// status, verification, histories, export and retention.
/// </summary>
public class EvidenceStoreTests
{
    // Records the Python reference signed (see ExecutionTests): two on one resource, one without a resource.
    private const string First = """{"evidence_id": "00000000-0000-4000-8000-000000000001", "sequence_no": 1, "decision_id": "dec-1", "context_id": "ctx-1", "agreement_id": "att-1", "executor_sovereign_id": "executor-a", "executed_capability": "partner-secret.rotate", "execution_parameters": {"app_id": "app-1", "lifetime_days": 90, "ratio": 0.5, "note": "café"}, "executed_at": "2026-10-30T12:00:00.123456Z", "outcome": "success", "outcome_detail": null, "prev_evidence_digest": null, "resource_id": "partner:app-1/secret", "resource_action": "rotate", "resource_sequence": 1, "prev_resource_digest": null, "signature": {"key_id": "executor-key-1", "sig": "L8Pzt1q8XHvsyAxu0C33cfShyP0CEcIqJXmjO31IEId2Z6HgJZS1ULXD6gxv0v1fIga/PYZaDVaW9gW8hKn3Cg=="}}""";
    private const string Second = """{"evidence_id": "00000000-0000-4000-8000-000000000002", "sequence_no": 2, "decision_id": "dec-1", "context_id": "ctx-1", "agreement_id": "att-1", "executor_sovereign_id": "executor-a", "executed_capability": "partner-secret.rotate", "execution_parameters": {}, "executed_at": "2026-10-30T12:00:01Z", "outcome": "partial", "outcome_detail": "old credential kept for overlap", "prev_evidence_digest": "f17308c1723d35d0c7abfdfa49779146b02c50a3c3411eaf5aad6f9f872e6995", "resource_id": "partner:app-1/secret", "resource_action": "rotate", "resource_sequence": 2, "prev_resource_digest": "f17308c1723d35d0c7abfdfa49779146b02c50a3c3411eaf5aad6f9f872e6995", "signature": {"key_id": "executor-key-1", "sig": "xK4H22uXMTJxTx8Mz+mLXr8YJT37vQYjar+7nyFaR984/Zx7sXL1ZvZGSPl2ewnY9D2XPVqL5Hzq1jrkALyRCQ=="}}""";
    private const string Plain = """{"evidence_id": "00000000-0000-4000-8000-000000000003", "sequence_no": 1, "decision_id": "dec-2", "context_id": "ctx-2", "agreement_id": "att-1", "executor_sovereign_id": "executor-a", "executed_capability": "partner-secret.create", "execution_parameters": {"app_id": "app-2"}, "executed_at": "2026-10-30T12:00:02.500000Z", "outcome": "failure", "outcome_detail": "graph_unavailable", "prev_evidence_digest": null, "signature": {"key_id": "executor-key-1", "sig": "lFHbhO4iTqDDVZMyJ5x62fsONBj1LsHF1/kSVxwK0FvfPKEi/yJrBFWtpTlWvwk4DXyedEKt0MMqc1UVuhNbAg=="}}""";
    private const string SecondDigest = "e82e42ba7147d2e8946792b51eee91d6ff4b69b50e48a840bed2e31d22f668fe";

    private sealed record Sent(string Method, string Path, string? Body, bool Admin);

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A client whose NA answers with <paramref name="answer"/>, recording each request.</summary>
    private static GenesisMeshClient Client(List<Sent> sent, Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = "TEST",
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, Uri.UnescapeDataString(req.RequestUri!.PathAndQuery),
                    req.Content?.ReadAsStringAsync().Result, req.Headers.Contains("X-Admin-Key-Id")));
                return answer(req);
            }),
        });

    private static string Entry(long sequence, string kind, string? resourceId = null) =>
        new JsonObject
        {
            ["store_sequence"] = sequence,
            ["entry_kind"] = kind,
            ["recorded_at"] = "2026-10-30T12:00:03Z",
            ["payload_digest"] = "00",
            ["prev_entry_digest"] = null,
            ["decision_id"] = null,
            ["context_id"] = null,
            ["vendor_id"] = null,
            ["attestation_id"] = null,
            ["capability"] = null,
            ["outcome"] = null,
            ["evidence_id"] = null,
            ["executor_sovereign_id"] = null,
            ["exec_sequence_no"] = null,
            ["resource_id"] = resourceId,
            ["resource_action"] = null,
            ["resource_sequence"] = null,
        }.ToJsonString();

    private static string Page(long? next, params (long Sequence, string Kind, string Payload)[] entries) =>
        $$"""{"count":{{entries.Length}},"next_after_sequence":{{(next is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},"entries":[{{string.Join(",",
            entries.Select(e => $$"""{"schema":"gm.evidence.event","schema_version":1,"entry":{{Entry(e.Sequence, e.Kind)}},"entry_digest":"aa","payload":{{e.Payload}}}"""))}}]}""";

    private static ExecutionEvidence Record(string json) => JsonSerializer.Deserialize<ExecutionEvidence>(json, Auth.SerializerOptions)!;

    // ── Submission ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_PostsTheRecordAsSigned_WithoutOperatorHeaders()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(201, $$"""{"status":"recorded","entry":{{Entry(7, "execution", "partner:app-1/secret")}},"entry_digest":"bb"}"""));
        var plain = Record(Plain);
        var submission = await gm.EvidenceStore.Submit(plain);
        Assert.Equal(("recorded", 7L, "bb"), (submission.Status, submission.Entry.StoreSequence, submission.EntryDigest));
        var request = Assert.Single(sent);
        Assert.Equal(("POST", "/evidence/execution", false), (request.Method, request.Path, request.Admin));
        var evidence = JsonNode.Parse(request.Body!)!["evidence"]!.AsObject();
        Assert.DoesNotContain(evidence, p => p.Key.StartsWith("resource_", StringComparison.Ordinal) || p.Key == "prev_resource_digest");
        // The typed record, sent as it is, still verifies under the executor's key.
        Assert.True(OfflineVerifier.VerifyExecutionSignature(evidence.ToJsonString(), "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik="));
    }

    [Fact]
    public async Task Submit_RefusesSecretMaterial_BeforeSending()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(201, "{}"));
        var record = Record(Plain);
        record.ExecutionParameters = JsonDocument.Parse("""{"password":"hunter2"}""").RootElement.Clone();
        // Thrown from the task, as Boundary.Evaluate throws, not from the call.
        var refused = gm.EvidenceStore.Submit(record);
        await Assert.ThrowsAsync<SecretMaterialException>(() => refused);
        var missing = gm.EvidenceStore.Submit(null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => missing);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Submit_ThrowsTheNAsRefusal()
    {
        using var gm = Client([], _ => Json(422, """{"error":{"code":"evidence_out_of_scope","message":"signing key covers only resources starting with 'partner:'"}}"""));
        var refusal = await Assert.ThrowsAsync<ValidationException>(() => gm.EvidenceStore.Submit(Record(First)));
        Assert.Equal("evidence_out_of_scope", refusal.Code);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_SendsEveryFilter_Signed()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, Page(null, (3, "execution", First))));
        var page = await gm.EvidenceStore.Search(new EvidenceSearchParams
        {
            VendorId = "v",
            AttestationId = "a",
            Capability = "c",
            ResourceId = "kv:vault/s",
            Outcome = "success",
            EntryKind = "execution",
            DecisionId = "d",
            GrantId = "g",
            Since = "2026-10-01T00:00:00Z",
            Until = "2026-11-01T00:00:00Z",
            AfterSequence = 5,
            Limit = 10,
        });
        Assert.Equal((1, (long?)null), (page.Count, page.NextAfterSequence));
        var e = Assert.Single(page.Entries);
        Assert.Equal(("gm.evidence.event", 1, 3L, "execution"), (e.Schema, e.SchemaVersion, e.Entry.StoreSequence, e.Entry.EntryKind));
        Assert.Equal("dec-1", Record(e.Payload.GetRawText()).DecisionId);
        var request = Assert.Single(sent);
        Assert.True(request.Admin);
        Assert.Equal("GET", request.Method);
        foreach (var part in new[] { "vendor_id=v", "attestation_id=a", "capability=c", "resource_id=kv:vault/s", "outcome=success",
                     "entry_kind=execution", "decision_id=d", "grant_id=g", "since=2026-10-01T00:00:00Z",
                     "until=2026-11-01T00:00:00Z", "after_sequence=5", "limit=10" })
            Assert.Contains(part, request.Path);
    }

    [Fact]
    public async Task Search_WithoutFilters_SendsNone()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, Page(null)));
        Assert.Empty((await gm.EvidenceStore.Search()).Entries);
        Assert.Equal("/admin/evidence", Assert.Single(sent).Path);
    }

    [Fact]
    public async Task Iterate_FollowsThePages()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Json(200, req.RequestUri!.Query.Contains("after_sequence=0")
            ? Page(2, (1, "execution", First), (2, "execution", Second))
            : Page(null, (3, "execution", Plain))));
        var ids = new List<long>();
        await foreach (var e in gm.EvidenceStore.Iterate(new EvidenceSearchParams { EntryKind = "execution" }))
            ids.Add(e.Entry.StoreSequence);
        Assert.Equal(new[] { 1L, 2L, 3L }, ids);
        Assert.Equal(2, sent.Count);
        Assert.Contains("after_sequence=2", sent[1].Path);
        Assert.Contains("entry_kind=execution", sent[1].Path);
    }

    [Fact]
    public async Task Iterate_RefusesACursorThatDoesNotAdvance()
    {
        using var gm = Client([], _ => Json(200, Page(4, (4, "execution", First))));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in gm.EvidenceStore.Iterate(new EvidenceSearchParams { AfterSequence = 4 })) { }
        });
    }

    // ── Resource heads ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResourceHead_ReadsTheNAsHead_WithTheResourcePathKept()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200,
            $$"""{"resource_id":"kv:vault/secret","resource_sequence":2,"record_digest":"{{SecondDigest}}","entry_kind":"execution","record_id":"00000000-0000-4000-8000-000000000002"}"""));
        var stored = await gm.EvidenceStore.StoredResourceHead("kv:vault/secret");
        Assert.Equal(("kv:vault/secret", 2L, "execution", "00000000-0000-4000-8000-000000000002"),
            (stored.ResourceId, stored.ResourceSequence, stored.EntryKind, stored.RecordId));
        var head = await gm.EvidenceStore.ResourceHead("kv:vault/secret");
        Assert.Equal((2L, SecondDigest), (head!.ResourceSequence, head.RecordDigest));
        Assert.All(sent, s => Assert.Equal(("GET", "/admin/evidence/resource-heads/kv:vault/secret", true), (s.Method, s.Path, s.Admin)));
    }

    [Fact]
    public async Task ResourceHead_IsNullForAResourceWithoutHistory_AndThrowsForAMissingRoute()
    {
        using var none = Client([], _ => Json(404, """{"error":{"code":"resource_not_found","message":"no evidence for this resource"}}"""));
        Assert.Null(await none.EvidenceStore.ResourceHead("kv:vault/new"));
        using var old = Client([], _ => Json(404, """{"error":{"code":"not_found","message":"no such route"}}"""));
        await Assert.ThrowsAsync<NotFoundException>(() => old.EvidenceStore.ResourceHead("kv:vault/new"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kv:vault/../admin")]
    [InlineData("kv:vault//secret")]
    [InlineData("/kv:vault")]
    [InlineData("kv:vault/a?b")]
    [InlineData("kv:vault/50%")]
    [InlineData("kv:vault/secret ")]
    [InlineData(" kv:vault/secret")]
    [InlineData("kv:vault/secret\t")]
    public async Task ResourceHead_RefusesAnIdThatIsNotPathSegments(string resourceId)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.ResourceHead(resourceId));
        var stored = gm.EvidenceStore.StoredResourceHead(resourceId);
        await Assert.ThrowsAsync<ArgumentException>(() => stored);
        Assert.Empty(sent);
    }

    // ── Resource states ──────────────────────────────────────────────────────

    private const string BreakGlass = """{"break_glass_id":"bg-1","resource_id":"partner:app-1/secret","resource_action":"revoke","outcome":"success","execution_parameters":{"key_id":"k-9"},"executed_at":"2026-10-30T12:00:05Z"}""";
    private const string BreakGlassOnly = """{"break_glass_id":"bg-2","resource_id":"partner:app-9/secret","resource_action":"rotate","outcome":"failure","execution_parameters":{},"executed_at":"2026-10-30T12:00:06Z"}""";

    [Fact]
    public async Task ResourceStates_KeepsEachResourcesLatestChange()
    {
        // The second record is stored before the first: states follow the resource chain, not the store order.
        using var gm = Client([], req => Json(200, req.RequestUri!.Query.Contains("entry_kind=break_glass")
            ? Page(null, (6, "break_glass", BreakGlassOnly))
            : Page(null, (4, "execution", Second), (5, "execution", First), (6, "execution", Plain))));
        var states = await gm.EvidenceStore.ResourceStates();
        Assert.Equal(new[] { "partner:app-1/secret", "partner:app-9/secret" }, states.Keys.Order(StringComparer.Ordinal));

        var app1 = states["partner:app-1/secret"];
        Assert.Equal((2L, SecondDigest, "rotate", "partial", "rotate", "dec-1", "2026-10-30T12:00:01Z"),
            (app1.ResourceSequence, app1.RecordDigest, app1.LastAction, app1.LastOutcome, app1.LastSuccessAction,
                app1.LastDecisionId, app1.LastExecutedAt));
        Assert.Equal("app-1", app1.LastSuccessParameters.GetProperty("app_id").GetString());
        Assert.Null(app1.LastBreakGlassId);

        var app9 = states["partner:app-9/secret"];
        Assert.Equal((0L, "", "", "failure", null, "bg-2"),
            (app9.ResourceSequence, app9.RecordDigest, app9.LastDecisionId, app9.LastOutcome, app9.LastSuccessAction, app9.LastBreakGlassId));
        Assert.Equal(JsonValueKind.Undefined, app9.LastSuccessParameters.ValueKind);
    }

    [Fact]
    public async Task ResourceStates_FollowTheResourceChain_WhenTheClockDisagrees()
    {
        // The second record on the resource carries an earlier executed_at than the first (a host whose clock is
        // behind): ordered by time, the first comes last, and must not move the head back.
        var early = JsonNode.Parse(Second)!.AsObject();
        early["executed_at"] = "2026-10-30T11:59:00Z";
        var earlyJson = early.ToJsonString();
        using var gm = Client([], req => Json(200, req.RequestUri!.Query.Contains("entry_kind=break_glass")
            ? Page(null)
            : Page(null, (4, "execution", First), (5, "execution", earlyJson))));
        var state = (await gm.EvidenceStore.ResourceStates())["partner:app-1/secret"];
        Assert.Equal((2L, OfflineVerifier.ExecutionDigest(earlyJson), "partial", "2026-10-30T11:59:00Z"),
            (state.ResourceSequence, state.RecordDigest, state.LastOutcome, state.LastExecutedAt));
    }

    [Fact]
    public async Task ResourceStates_OrdersChangesMadeAtOnceByStoreSequence()
    {
        // Two break-glass changes stamped with the same time: the one stored later is the latest.
        var later = JsonNode.Parse(BreakGlassOnly)!.AsObject();
        later["break_glass_id"] = "bg-3";
        later["outcome"] = "success";
        using var gm = Client([], req => Json(200, req.RequestUri!.Query.Contains("entry_kind=break_glass")
            ? Page(null, (8, "break_glass", later.ToJsonString()), (7, "break_glass", BreakGlassOnly))
            : Page(null)));
        var state = (await gm.EvidenceStore.ResourceStates())["partner:app-9/secret"];
        Assert.Equal(("bg-3", "success", "rotate"), (state.LastBreakGlassId, state.LastOutcome, state.LastSuccessAction));
    }

    [Fact]
    public async Task ResourceStates_CountsABreakGlassChangeAsTheResourcesChange()
    {
        using var gm = Client([], req => Json(200, req.RequestUri!.Query.Contains("entry_kind=break_glass")
            ? Page(null, (9, "break_glass", BreakGlass))
            : Page(null, (4, "execution", First), (5, "execution", Second))));
        var state = (await gm.EvidenceStore.ResourceStates())["partner:app-1/secret"];
        // The chain head and decision stay the newest execution record's.
        Assert.Equal((2L, SecondDigest, "dec-1"), (state.ResourceSequence, state.RecordDigest, state.LastDecisionId));
        Assert.Equal(("revoke", "success", "revoke", "bg-1", "2026-10-30T12:00:05Z"),
            (state.LastAction, state.LastOutcome, state.LastSuccessAction, state.LastBreakGlassId, state.LastExecutedAt));
        Assert.Equal("k-9", state.LastSuccessParameters.GetProperty("key_id").GetString());
    }

    // ── Executor keys ────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterExecutorKey_SendsTheKey_LimitedToItsPrefix()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(201,
            """{"key_id":"portal-executor","executor_sovereign_id":"partner-portal","active":true,"role":"executor","resource_prefix":"partner:"}"""));
        var state = await gm.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
        {
            KeyId = "portal-executor",
            PublicKey = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=",
            ExecutorSovereignId = "partner-portal",
            ResourcePrefix = "partner:",
        });
        Assert.Equal(("portal-executor", true, "executor", "partner:"), (state.KeyId, state.Active, state.Role, state.ResourcePrefix));
        var request = Assert.Single(sent);
        Assert.Equal(("POST", "/admin/evidence/executor-keys", true), (request.Method, request.Path, request.Admin));
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("partner:", body["resource_prefix"]!.GetValue<string>());
        Assert.False(body.ContainsKey("role"));
        var missing = gm.EvidenceStore.RegisterExecutorKey(null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => missing);
    }

    // ── Status, verification and histories (1.9.0) ───────────────────────────

    private static readonly Lazy<JsonObject> Reference = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    /// <summary>The retention checkpoint the Python reference signed.</summary>
    private static string Checkpoint() => Reference.Value["checkpoint"]!.ToJsonString();

    private static HttpResponseMessage Error(int status, string code) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "refused" } }.ToJsonString());

    private static string Event(long sequence, string kind, string payload) =>
        $$"""{"schema":"gm.evidence.event","schema_version":1,"entry":{{Entry(sequence, kind)}},"entry_digest":"aa","payload":{{payload}}}""";

    private const string Verified = """{"verified":true,"checked_entries":3,"decisions":1,"executions":2,"failures":[]}""";

    [Fact]
    public async Task Status_ReadsTheStoresPositionAndCounts()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """
            {"evidence_store":"on","entries":5,"last_store_sequence":5,"rejections":1,"active_executor_keys":2,
            "retention_checkpoint":null,"latest_anchor":{"anchor_sequence":1,"store_sequence":4,"anchored_at":"2026-10-31T08:00:00+00:00"},
            "unanchored_entries":1,"unjudged_records":0,"policy_history_started":null,"registry_healthy":false,
            "registry_problems":["the policy history has not started"],"evidence_remediation":"on",
            "remediation":{"due":1,"review_pending":0},"evidence_grants":"on","grants_issued":2,"grants_active":1,
            "grants_revoked":1,"grant_uses":3}
            """));
        var status = await gm.EvidenceStore.Status();
        Assert.Equal(("on", 5L, 5L, 1L, 2L, (long?)null), (status.EvidenceStore, status.Entries, status.LastStoreSequence,
            status.Rejections, status.ActiveExecutorKeys, status.RetentionCheckpoint));
        Assert.Equal((1L, 4L, 1L), (status.LatestAnchor!.AnchorSequence, status.LatestAnchor.StoreSequence, status.UnanchoredEntries));
        Assert.Equal((false, "the policy history has not started"), (status.RegistryHealthy, Assert.Single(status.RegistryProblems!)));
        Assert.Equal(("on", 1L), (status.EvidenceRemediation, status.Remediation!["due"]));
        Assert.Equal(("on", 2L, 1L, 1L, 3L), (status.EvidenceGrants, status.GrantsIssued, status.GrantsActive, status.GrantsRevoked, status.GrantUses));
        Assert.Equal(("GET", "/admin/evidence/status", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
    }

    [Fact]
    public async Task Status_WithTheStoreOff_LeavesTheRestNull()
    {
        using var gm = Client([], _ => Json(200, """{"evidence_store":"off","retention_checkpoint":null}"""));
        var status = await gm.EvidenceStore.Status();
        Assert.Equal("off", status.EvidenceStore);
        Assert.Null(status.Entries);
        Assert.Null(status.LatestAnchor);
        Assert.Null(status.EvidenceGrants);
    }

    [Fact]
    public async Task Verify_ReadsTheNAsVerification_WithItsAnchors()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """
            {"verified":false,"checked_entries":4,"decisions":1,"executions":1,"observations":2,
            "failures":[{"store_sequence":null,"reason":"invalid_signature","detail":"retention_checkpoint"},
                        {"store_sequence":3,"reason":"store_chain_break","detail":""}],
            "anchors":{"anchors_checked":1,"anchors_matched":1,"anchors_before_export":0,"anchors_after_export":0,
                       "linked_start":true,"anchored_through_sequence":2,"unanchored_entries":2,
                       "chain":{"verified":true,"checked_anchors":1,"first_anchor_sequence":1,"last_anchor_sequence":1,
                                "anchored_through_sequence":2,"failures":[]}},
            "warnings":[{"store_sequence":4,"reason":"unsigned_field","detail":"x"}]}
            """));
        var v = await gm.EvidenceStore.Verify();
        Assert.Equal((false, 4L, 1L, 1L, 2L, 0L), (v.Verified, v.CheckedEntries, v.Decisions, v.Executions, v.Observations, v.BreakGlass));
        Assert.Equal(new (long?, string)[] { (null, "invalid_signature"), (3, "store_chain_break") },
            v.Failures.Select(f => (f.StoreSequence, f.Reason)));
        Assert.Equal((true, 2L, 2L), (v.Anchors!.LinkedStart, v.Anchors.AnchoredThroughSequence, v.Anchors.UnanchoredEntries));
        Assert.Equal((true, 1L, 2L), (v.Anchors.Chain!.Verified, v.Anchors.Chain.CheckedAnchors, v.Anchors.Chain.AnchoredThroughSequence));
        Assert.Equal("unsigned_field", Assert.Single(v.Warnings!).Reason);
        Assert.Equal(("GET", "/admin/evidence/verify", true), (sent[0].Method, sent[0].Path, sent[0].Admin));

        // Written back as the NA sends it: counts of 0 left out, a null position kept.
        var again = JsonNode.Parse(JsonSerializer.Serialize(v, Auth.SerializerOptions))!.AsObject();
        Assert.False(again.ContainsKey("break_glass"));
        Assert.Equal(2, (long)again["observations"]!);
        Assert.True(again["failures"]![0]!.AsObject().ContainsKey("store_sequence"));
    }

    [Fact]
    public async Task ResourceHistory_KeepsTheResourcePath_AndReadsTheVerification()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200,
            $$"""{"resource_id":"kv:vault/secret","truncated":false,"entries":[{{Event(2, "execution", First)}}],"verification":{{Verified}}}"""));
        var history = await gm.EvidenceStore.ResourceHistory("kv:vault/secret");
        Assert.Equal(("kv:vault/secret", false, 2L, true), (history.ResourceId, history.Truncated,
            Assert.Single(history.Entries).Entry.StoreSequence, history.Verification.Verified));
        Assert.Equal(("GET", "/admin/evidence/resources/kv:vault/secret", true), (sent[0].Method, sent[0].Path, sent[0].Admin));

        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.ResourceHistory("kv:vault//secret"));
        Assert.Single(sent);
        using var none = Client([], _ => Error(404, "resource_not_found"));
        Assert.Equal("resource_not_found", (await Assert.ThrowsAsync<NotFoundException>(() => none.EvidenceStore.ResourceHistory("kv:x"))).Code);
    }

    [Fact]
    public async Task VendorHistory_SendsTheVendorAsOneSegment()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, $$"""{"vendor_id":"vendor-1","truncated":true,"entries":[],"verification":{{Verified}}}"""));
        var history = await gm.EvidenceStore.VendorHistory("vendor-1");
        Assert.Equal(("vendor-1", true), (history.VendorId, history.Truncated));
        Assert.Equal(("GET", "/admin/evidence/vendors/vendor-1"), (sent[0].Method, sent[0].Path));

        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.VendorHistory("vendor/1"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.VendorHistory("vendor%2F1"));
        // The URI drops white space at the end of the path: the request would sign one path and send another.
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.VendorHistory("vendor-1 "));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.VendorHistory(" vendor-1"));
        Assert.Single(sent);
        await gm.EvidenceStore.VendorHistory("vendor.1.");
        Assert.Equal("/admin/evidence/vendors/vendor.1.", sent[1].Path);
        using var none = Client([], _ => Error(404, "vendor_not_found"));
        Assert.Equal("vendor_not_found", (await Assert.ThrowsAsync<NotFoundException>(() => none.EvidenceStore.VendorHistory("v"))).Code);
    }

    // ── Export (1.9.0) ───────────────────────────────────────────────────────

    private static string Line(long sequence) => Event(sequence, "execution", First);

    private static HttpResponseMessage Lines(params long[] sequences) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(sequences.Select(s => Line(s) + "\n")), Encoding.UTF8, "application/x-ndjson"),
        };

    [Fact]
    public async Task ExportText_SendsThePage_AndReturnsTheLinesAsSent()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Lines(5, 6));
        var text = await gm.EvidenceStore.ExportText(new EvidenceExportParams { SinceSequence = 4, Limit = 2 });
        Assert.Equal(Line(5) + "\n" + Line(6) + "\n", text);
        Assert.Equal(("GET", "/admin/evidence/export?since_sequence=4&limit=2", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        await gm.EvidenceStore.ExportText();
        Assert.Equal("/admin/evidence/export", sent[1].Path);
    }

    [Fact]
    public async Task Export_ParsesTheLines()
    {
        using var gm = Client([], _ => Lines(5, 6));
        var events = await gm.EvidenceStore.Export();
        Assert.Equal([5L, 6L], events.Select(e => e.Entry.StoreSequence));
        Assert.Equal("dec-1", Record(events[0].Payload.GetRawText()).DecisionId);
    }

    private static long Since(HttpRequestMessage req) =>
        long.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["since_sequence"]!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ExportAll_FollowsThePages_UntilAShortOne()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Since(req) switch
        {
            0 => Lines(1, 2),
            2 => Lines(3, 4),
            _ => Lines(5),
        });
        var sequences = new List<long>();
        await foreach (var e in gm.EvidenceStore.ExportAll(pageSize: 2)) sequences.Add(e.Entry.StoreSequence);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], sequences);
        Assert.Equal(["since_sequence=0&limit=2", "since_sequence=2&limit=2", "since_sequence=4&limit=2"],
            sent.Select(s => s.Path.Split('?')[1]));
    }

    [Fact]
    public async Task ExportAll_StopsAtAnEmptyPage_AfterAFullOne()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Since(req) == 7 ? Lines(8, 9) : Lines());
        var sequences = new List<long>();
        await foreach (var e in gm.EvidenceStore.ExportAll(7, 2)) sequences.Add(e.Entry.StoreSequence);
        Assert.Equal([8L, 9L], sequences);
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData(new long[] { 4 })]
    [InlineData(new long[] { 5, 5 })]
    [InlineData(new long[] { 6, 5 })]
    public async Task ExportAll_RefusesAnExportThatDoesNotAdvance(long[] sequences)
    {
        using var gm = Client([], _ => Lines(sequences));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in gm.EvidenceStore.ExportAll(4)) { }
        });
    }

    [Fact]
    public void ExportAll_RefusesACursorOrPageSizeOutOfRange_BeforeSending()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Lines());
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.EvidenceStore.ExportAll(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.EvidenceStore.ExportAll(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.EvidenceStore.ExportAll(pageSize: 1001));
        Assert.Empty(sent);
    }

    // ── Executor keys and retention (1.9.0) ──────────────────────────────────

    [Fact]
    public async Task ListExecutorKeys_ReadsRetiredKeysToo()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """
            {"executor_keys":[
              {"key_id":"k1","public_key":"O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=","executor_sovereign_id":"ex",
               "registered_at":"2026-10-31T08:00:00+00:00","retired_at":"2026-10-31T09:00:00+00:00","role":"executor","resource_prefix":null},
              {"key_id":"k2","public_key":"O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=","executor_sovereign_id":"ex",
               "registered_at":"2026-10-31T09:00:00+00:00","retired_at":null,"role":"observer","resource_prefix":"partner:"}]}
            """));
        var keys = await gm.EvidenceStore.ListExecutorKeys();
        Assert.Equal(new (string, string?, string?, string?)[]
            {
                ("k1", "2026-10-31T09:00:00+00:00", "executor", null),
                ("k2", null, "observer", "partner:"),
            }, keys.Select(k => (k.KeyId, k.RetiredAt, k.Role, k.ResourcePrefix)));
        Assert.Equal(("GET", "/admin/evidence/executor-keys", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
    }

    [Fact]
    public async Task RetireExecutorKey_PostsToTheKeysRoute()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """{"key_id":"portal-executor","active":false}"""));
        var state = await gm.EvidenceStore.RetireExecutorKey("portal-executor");
        Assert.Equal(("portal-executor", false, (string?)null), (state.KeyId, state.Active, state.ExecutorSovereignId));
        Assert.Equal(("POST", "/admin/evidence/executor-keys/portal-executor/retire", "{}", true),
            (sent[0].Method, sent[0].Path, sent[0].Body, sent[0].Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RetireExecutorKey("../ops"));
        Assert.Single(sent);

        using var retired = Client([], _ => Error(409, "executor_key_retired"));
        Assert.Equal("executor_key_retired", (await Assert.ThrowsAsync<ConflictException>(() => retired.EvidenceStore.RetireExecutorKey("k"))).Code);
        using var unknown = Client([], _ => Error(404, "executor_key_not_found"));
        Assert.Equal("executor_key_not_found", (await Assert.ThrowsAsync<NotFoundException>(() => unknown.EvidenceStore.RetireExecutorKey("k"))).Code);
    }

    [Theory]
    [InlineData(30, null, """{"older_than_days":30}""")]
    [InlineData(null, 12L, """{"through_sequence":12}""")]
    [InlineData(30, 12L, """{"older_than_days":30,"through_sequence":12}""")]
    public async Task ApplyRetention_SendsWhatIsGiven(int? days, long? through, string body)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, $$"""{"removed_count":3,"checkpoint":{{Checkpoint()}}}"""));
        var result = await gm.EvidenceStore.ApplyRetention(days, through);
        Assert.Equal((3L, 3L, (RetentionHeldBack?)null), (result.RemovedCount, result.Checkpoint!.RemovedThroughSequence, result.HeldBack));
        Assert.Equal(("POST", "/admin/evidence/retention/apply", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(sent[0].Body!)));
    }

    [Fact]
    public async Task ApplyRetention_RefusesNothingOrLessThanOne_BeforeSending()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """{"removed_count":0,"checkpoint":null}"""));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.ApplyRetention(null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.EvidenceStore.ApplyRetention(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.EvidenceStore.ApplyRetention(null, 0));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task ApplyRetention_ReadsWhatHeldItBack()
    {
        using var gm = Client([], _ => Json(200, """
            {"removed_count":0,"checkpoint":null,"held_back":{"reason":"not_archived","archived_through_sequence":0,
             "message":"retention stops after entry 0, the last one an archive covers"}}
            """));
        var result = await gm.EvidenceStore.ApplyRetention(30);
        Assert.Equal((0L, (RetentionCheckpoint?)null), (result.RemovedCount, result.Checkpoint));
        Assert.Equal(("not_archived", (long?)0, (long?)null), (result.HeldBack!.Reason, result.HeldBack.ArchivedThroughSequence, result.HeldBack.StoreSequence));

        using var unjudged = Client([], _ => Json(200, """
            {"removed_count":0,"checkpoint":null,"held_back":{"reason":"unjudged_records","store_sequence":4,
             "entry_kind":"observation","record_id":"obs-1","unjudged_records":2,"message":"judge them"}}
            """));
        var held = (await unjudged.EvidenceStore.ApplyRetention(30)).HeldBack!;
        Assert.Equal((4L, "observation", "obs-1", 2L), (held.StoreSequence, held.EntryKind, held.RecordId, held.UnjudgedRecords));
    }

    [Fact]
    public void ARetentionCheckpointKeepsItsSignedForm()
    {
        // The checkpoint the reference signed, read and written back: previous_checkpoint_id null is kept, the
        // absent observation_heads stays absent, and (v1.3.0) a present one is kept.
        var signed = JsonNode.Parse(Checkpoint())!.AsObject();
        var typed = JsonSerializer.Deserialize<RetentionCheckpoint>(signed.ToJsonString(), Auth.SerializerOptions)!;
        Assert.Equal((3L, 1L), (typed.RemovedThroughSequence, typed.ResourceHeads.Single().Value.ResourceSequence));
        Assert.True(JsonNode.DeepEquals(signed, JsonNode.Parse(JsonSerializer.Serialize(typed, Auth.SerializerOptions))));
        signed["observation_heads"] = new JsonObject { ["kv:vault/a"] = 2 };
        typed = JsonSerializer.Deserialize<RetentionCheckpoint>(signed.ToJsonString(), Auth.SerializerOptions)!;
        Assert.True(JsonNode.DeepEquals(signed, JsonNode.Parse(JsonSerializer.Serialize(typed, Auth.SerializerOptions))));
    }

    [Fact]
    public async Task LatestCheckpoint_FollowsThePages()
    {
        var later = JsonNode.Parse(Checkpoint())!.AsObject();
        later["checkpoint_id"] = "later";
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Json(200, req.RequestUri!.Query.Contains("after_sequence=0")
            ? Page(3, (3, "retention_checkpoint", Checkpoint()))
            : Page(null, (9, "retention_checkpoint", later.ToJsonString()))));
        Assert.Equal("later", (await gm.EvidenceStore.LatestCheckpoint())!.CheckpointId);
        Assert.Equal(2, sent.Count);
    }

    /// <summary>Each 1.9.0 store call, as an operator makes it.</summary>
    public static TheoryData<string> StoreCalls() => new(StoreCall.Keys);

    private static readonly Dictionary<string, Func<GenesisMeshClient, Task>> StoreCall = new()
    {
        ["Status"] = gm => gm.EvidenceStore.Status(),
        ["Verify"] = gm => gm.EvidenceStore.Verify(),
        ["ResourceHistory"] = gm => gm.EvidenceStore.ResourceHistory("kv:vault/secret"),
        ["VendorHistory"] = gm => gm.EvidenceStore.VendorHistory("vendor-1"),
        ["ExportText"] = gm => gm.EvidenceStore.ExportText(),
        ["Export"] = gm => gm.EvidenceStore.Export(),
        ["ExportAll"] = async gm =>
        {
            await foreach (var _ in gm.EvidenceStore.ExportAll()) { }
        },
        ["ListExecutorKeys"] = gm => gm.EvidenceStore.ListExecutorKeys(),
        ["RetireExecutorKey"] = gm => gm.EvidenceStore.RetireExecutorKey("portal-executor"),
        ["ApplyRetention"] = gm => gm.EvidenceStore.ApplyRetention(30),
        ["LatestCheckpoint"] = gm => gm.EvidenceStore.LatestCheckpoint(),
    };

    [Theory]
    [MemberData(nameof(StoreCalls))]
    public async Task EveryStoreCall_NeedsASigningKey(string call)
    {
        var sent = new List<Sent>();
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, req.RequestUri!.PathAndQuery, null, false));
                return Json(200, "{}");
            }),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreCall[call](gm));
        Assert.Empty(sent);
    }

    [Theory]
    [MemberData(nameof(StoreCalls))]
    public async Task EveryStoreCall_ThrowsTheNAsRefusal(string call)
    {
        // With the store off, every call but Status gets 404 evidence_store_disabled; Status, a 403.
        var (status, code) = call == "Status" ? (403, "insufficient_operator_tier") : (404, "evidence_store_disabled");
        using var gm = Client([], _ => Error(status, code));
        var refused = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => StoreCall[call](gm));
        Assert.Equal((status, code), (refused.Status, refused.Code));
        Assert.IsType(call == "Status" ? typeof(ForbiddenException) : typeof(NotFoundException), refused);
    }

    [Fact]
    public async Task ApplyRetention_ThrowsTheNAsRefusals()
    {
        using var invalid = Client([], _ => Error(400, "invalid_retention"));
        Assert.Equal("invalid_retention", (await Assert.ThrowsAsync<BadRequestException>(() => invalid.EvidenceStore.ApplyRetention(1))).Code);
        using var refused = Client([], _ => Error(409, "evidence_retention_refused"));
        Assert.Equal("evidence_retention_refused",
            (await Assert.ThrowsAsync<ConflictException>(() => refused.EvidenceStore.ApplyRetention(1))).Code);
    }

    [Theory]
    [InlineData("RetireExecutorKey", 1)]
    [InlineData("ApplyRetention", 1)]
    [InlineData("Status", 3)]
    [InlineData("ExportText", 3)]
    public async Task OnlyReadsAreSentAgain(string call, int attempts)
    {
        // A retirement or a retention run changes the store: an answer lost on the way may mean it happened.
        var sent = 0;
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = "TEST",
            Retry = new RetrySettings { Attempts = 2, BaseDelay = TimeSpan.Zero },
            HttpHandler = new FuncHandler(_ =>
            {
                sent++;
                return Error(503, "unavailable");
            }),
        });
        await Assert.ThrowsAsync<ServerException>(() => StoreCall[call](gm));
        Assert.Equal(attempts, sent);
    }

    [Fact]
    public async Task LatestCheckpoint_IsTheLastCheckpointEntry_OrNull()
    {
        var later = JsonNode.Parse(Checkpoint())!.AsObject();
        later["checkpoint_id"] = "later";
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200,
            Page(null, (3, "retention_checkpoint", Checkpoint()), (9, "retention_checkpoint", later.ToJsonString()))));
        Assert.Equal("later", (await gm.EvidenceStore.LatestCheckpoint())!.CheckpointId);
        Assert.Contains("entry_kind=retention_checkpoint", sent[0].Path);
        using var none = Client([], _ => Json(200, Page(null)));
        Assert.Null(await none.EvidenceStore.LatestCheckpoint());
    }
}
