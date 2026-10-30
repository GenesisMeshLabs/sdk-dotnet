using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>The evidence store client (1.9.0): submission, search, resource heads, resource states and executor keys.</summary>
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
}
