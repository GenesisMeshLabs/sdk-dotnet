using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Tests;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Remediation and reviews (1.9.0): the store calls that submit remediation records, read the due queue and a subject's
/// state, record a notice, and request, read and decide reviews; remediation records in the record outbox; and the
/// subject a remediation record names, from its state.
/// </summary>
public class RemediationCallTests
{
    private sealed record Sent(string Method, string Path, string? Body, bool Admin);

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refused(int status, string code) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "refused" } }.ToJsonString());

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

    private static JsonElement Vector(string id) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "remediation.json")))
            .RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id);

    private static string Record(string id) => Vector(id).GetProperty("record").GetRawText();

    /// <summary>A remediation state as the NA's routes return it.</summary>
    private static string State(string subject = "j-1", string state = "remediation_due", bool reviewPending = false, string extra = "") =>
        $$"""
        {"subject_kind":"judgement","subject_id":"{{subject}}","change_kind":"observation","change_id":"o-1","change_version_id":"v2",
         "resource_id":"kv:vault/api-key","capability":"secret.rotate","actor":"principal-7f3a","actor_holder":null,"verdict":"deny",
         "state":"{{state}}","review_pending":{{(reviewPending ? "true" : "false")}},"mode":"cancel","steps":"rotate_forward_then_disable",
         "fallback":null,"grace_seconds":600,"guidance_source":"gate","remediation_due_at":"2026-01-01T00:11:00.000000+00:00",
         "lifetime_ends_at":null,"waived_until":null,"review_id":null,"remediation_id":null,"notified_at":null,"notified_by":null,
         "overdue_since":null,"created_at":"2026-01-01T00:01:00.000000+00:00","updated_at":"2026-01-01T00:01:00.000000+00:00"{{extra}}}
        """;

    private const string Envelope = """{"store_sequence":12,"entry_kind":"remediation","recorded_at":"2026-01-01T00:05:01Z","payload_digest":"00","prev_entry_digest":"11","decision_id":null,"context_id":null,"vendor_id":null,"attestation_id":null,"capability":null,"outcome":"cancelled","evidence_id":null,"executor_sovereign_id":"sovereign-b","exec_sequence_no":null,"resource_id":"kv:vault/api-key","resource_action":null,"resource_sequence":null,"record_id":"00000000-0000-4000-8000-000000000041"}""";

    // ── Submitting a remediation record ────────────────────────────────────

    [Fact]
    public async Task SubmitRemediation_IsPublicAndRetried_AndReadsTheSubjectsState()
    {
        var sent = new List<Sent>();
        var calls = 0;
        using var gm = Client(sent, _ => ++calls == 1
            ? Refused(503, "unavailable")
            : Json(201, $$"""{"status":"recorded","entry":{{Envelope}},"entry_digest":"33","payload":{{Record("canonical-remediation")}},"remediation":{{State(state: "remediated")}}}"""),
            retries: 1);
        var record = JsonSerializer.Deserialize<RemediationRecord>(Record("canonical-remediation"))!;
        var result = await gm.EvidenceStore.SubmitRemediation(record);
        Assert.Equal(("recorded", 12L, RemediationStates.Remediated), (result.Status, result.Entry.StoreSequence, result.Remediation!.State));
        Assert.Equal(2, sent.Count);
        Assert.Equal(("POST", "/evidence/remediations", false), (sent[1].Method, sent[1].Path, sent[1].Admin));
        Assert.Equal(Canonical.FromJson(Record("canonical-remediation")), Canonical.FromJson(JsonNode.Parse(sent[1].Body!)!["record"]!.ToJsonString()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.EvidenceStore.SubmitRemediation(null!));
    }

    [Fact]
    public async Task ARefusedRemediationKeepsTheNAsCode()
    {
        using var gm = Client([], _ => Refused(422, "remediation_steps_mismatch"));
        var e = await Assert.ThrowsAsync<ValidationException>(() =>
            gm.EvidenceStore.SubmitRemediation(JsonSerializer.Deserialize<RemediationRecord>(Record("canonical-remediation"))!));
        Assert.Equal("remediation_steps_mismatch", e.Code);
        using var off = Client([], _ => Refused(404, "remediation_disabled"));
        Assert.Equal("remediation_disabled", (await Assert.ThrowsAsync<NotFoundException>(() => off.EvidenceStore.Remediations())).Code);
    }

    // ── The due queue ──────────────────────────────────────────────────────

    [Fact]
    public async Task Remediations_SendsTheFilterAndReadsAPage()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, $$"""{"state":"overdue","count":1,"remediations":[{{State(state: "overdue")}}],"next_after":"2026-01-01T00:01:00Z|j-1"}"""));
        var page = await gm.EvidenceStore.Remediations(new RemediationQueueParams { State = RemediationStates.Overdue, Limit = 1, After = "2025-12-31T00:00:00Z|j-0" });
        Assert.Equal(("GET", "/admin/evidence/remediations?state=overdue&limit=1&after=2025-12-31T00:00:00Z|j-0", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal((RemediationStates.Overdue, 1, "2026-01-01T00:01:00Z|j-1"), (page.State, page.Count, page.NextAfter));
        var status = Assert.Single(page.Remediations);
        Assert.Equal(("j-1", "o-1", "kv:vault/api-key", 600, "2026-01-01T00:11:00.000000+00:00"),
            (status.SubjectId, status.ChangeId, status.ResourceId, status.GraceSeconds, status.RemediationDueAt));
        Assert.Null(status.Review);
        await gm.EvidenceStore.Remediations();
        Assert.Equal("/admin/evidence/remediations", sent[1].Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ALimitOutOfRangeSendsNothing(int limit)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.EvidenceStore.Remediations(new RemediationQueueParams { Limit = limit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => gm.EvidenceStore.AllRemediations(limit: limit));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task AllRemediations_FollowsThePages()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Json(200, req.RequestUri!.Query.Contains("after=")
            ? $$"""{"state":"review_pending","count":1,"remediations":[{{State("j-2", reviewPending: true, extra: ",\"review\":{\"review_id\":\"r-1\",\"subject_id\":\"j-2\",\"opened_for\":\"requested\",\"requested_by\":\"ops\",\"requester_holder\":\"platform-team\",\"justification\":\"why\",\"requested_at\":\"2026-01-01T00:02:00.000000+00:00\",\"decided_at\":null,\"verdict\":null}")}}],"next_after":null}"""
            : $$"""{"state":"review_pending","count":1,"remediations":[{{State("j-1", reviewPending: true)}}],"next_after":"c1"}"""));
        var states = new List<RemediationStatus>();
        await foreach (var s in gm.EvidenceStore.AllRemediations("review_pending", 1)) states.Add(s);
        Assert.Equal(["j-1", "j-2"], states.Select(s => s.SubjectId));
        Assert.Equal(("r-1", "requested", null), (states[1].Review!.ReviewId, states[1].Review!.OpenedFor, states[1].Review!.Verdict));
        Assert.Equal(["/admin/evidence/remediations?state=review_pending&limit=1", "/admin/evidence/remediations?state=review_pending&limit=1&after=c1"],
            sent.Select(s => s.Path));
    }

    [Fact]
    public async Task ACursorThatDoesNotAdvanceStopsTheReading()
    {
        using var gm = Client([], _ => Json(200, $$"""{"state":null,"count":1,"remediations":[{{State()}}],"next_after":"same"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in gm.EvidenceStore.AllRemediations()) { }
        });
    }

    [Fact]
    public async Task Remediation_ReadsTheStateWithItsReviewsAndRecords()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, State(extra: $$""","reviews":[{"review_id":"r-1","subject_id":"j-1","opened_for":"flagged_for_review","requested_by":null,"requester_holder":null,"justification":null,"requested_at":"2026-01-01T00:02:00.000000+00:00","decided_at":"2026-01-01T00:03:00.000000+00:00","verdict":"deny"}],"reviews_truncated":false,"records":[{"entry":{{Envelope}},"entry_digest":"33","payload":{{Record("canonical-remediation")}}}],"records_truncated":true""")));
        var detail = await gm.EvidenceStore.Remediation("j-1");
        Assert.Equal(("GET", "/admin/evidence/remediations/j-1", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal((RemediationStates.RemediationDue, "deny", false, true), (detail.State, Assert.Single(detail.Reviews).Verdict, detail.ReviewsTruncated, detail.RecordsTruncated));
        Assert.Equal("00000000-0000-4000-8000-000000000041", Assert.Single(detail.Records).Payload.GetProperty("remediation_id").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.Remediation("a/b"));
        Assert.Single(sent);
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("email", """{"channel":"email"}""")]
    public async Task MarkNotified_SendsTheChannelAndIsNotRetried(string? channel, string body)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, State(extra: ",\"x\":1")), retries: 2);
        var status = await gm.EvidenceStore.MarkNotified("j-1", channel);
        Assert.Equal(("POST", "/admin/evidence/remediations/j-1/notified", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(Canonical.FromJson(body), Canonical.FromJson(sent[0].Body!));
        Assert.Equal("j-1", status.SubjectId);
        using var down = Client(sent, _ => Refused(503, "unavailable"), retries: 2);
        await Assert.ThrowsAnyAsync<GenesisMeshException>(() => down.EvidenceStore.MarkNotified("j-1"));
        Assert.Equal(2, sent.Count);
    }

    // ── Reviews ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestReview_SendsExactlyTheSubjectAndJustification()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(201, State(reviewPending: true).Replace("\"review_id\":null", "\"review_id\":\"r-9\"")), retries: 2);
        var status = await gm.EvidenceStore.RequestReview(new ReviewRequest
        {
            SubjectKind = RemediationSubjectKinds.Judgement,
            SubjectId = "j-1",
            Justification = "Planned with the vendor",
        });
        Assert.Equal(("POST", "/admin/evidence/reviews", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(Canonical.FromJson("""{"subject_kind":"judgement","subject_id":"j-1","justification":"Planned with the vendor"}"""),
            Canonical.FromJson(sent[0].Body!));
        Assert.Equal(("r-9", true), (status.ReviewId, status.ReviewPending));
        using var conflict = Client(sent, _ => Refused(409, "review_already_pending"), retries: 2);
        Assert.Equal("review_already_pending", (await Assert.ThrowsAsync<ConflictException>(() =>
            conflict.EvidenceStore.RequestReview(new ReviewRequest { SubjectKind = "judgement", SubjectId = "j-1", Justification = "x" }))).Code);
        Assert.Equal(2, sent.Count);
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.EvidenceStore.RequestReview(null!));
    }

    [Fact]
    public async Task Review_ReadsAPendingReviewAndADecidedOneWithItsRecord()
    {
        var sent = new List<Sent>();
        const string pending = """{"review_id":"r-1","subject_id":"j-1","opened_for":"requested","requested_by":"ops","requester_holder":"platform-team","justification":"why","requested_at":"2026-01-01T00:02:00.000000+00:00","decided_at":null,"verdict":null}""";
        var decided = pending.Replace("\"decided_at\":null,\"verdict\":null}",
            "\"decided_at\":\"2026-01-01T00:03:00.000000+00:00\",\"verdict\":\"waive_until\",\"record\":{\"entry\":" + Envelope
                + ",\"entry_digest\":\"44\",\"payload\":" + Record("canonical-review") + "}}");
        var calls = 0;
        using var gm = Client(sent, _ => Json(200, ++calls == 1 ? pending : decided));
        var first = await gm.EvidenceStore.Review("r-1");
        Assert.Equal(("GET", "/admin/evidence/reviews/r-1"), (sent[0].Method, sent[0].Path));
        Assert.Null(first.Record);
        Assert.Null(first.Verdict);
        var second = await gm.EvidenceStore.Review("r-1");
        Assert.Equal(ReviewVerdicts.WaiveUntil, second.Verdict);
        Assert.Equal(ReviewVerdicts.WaiveUntil, second.Record!.Payload.Verdict);
        // The typed record written back keeps its signed form.
        Assert.Equal(OfflineVerifier.OutOfBandCanonical(Record("canonical-review")),
            OfflineVerifier.OutOfBandCanonical(JsonSerializer.Serialize(second.Record.Payload, Auth.SerializerOptions)));
    }

    [Fact]
    public async Task DecideReview_SendsTheDecisionAsSignedAndReadsTheStoredRecord()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, State(state: "waived", extra: $$""","review":{"entry":{{Envelope}},"entry_digest":"44","payload":{{Record("canonical-review")}}}""")),
            retries: 2);
        var decided = await gm.EvidenceStore.DecideReview("r-1", new ReviewDecision
        {
            Verdict = ReviewVerdicts.WaiveUntil,
            WaiveUntil = new DateTimeOffset(2026, 1, 8, 1, 9, 0, TimeSpan.FromHours(1)),
            Note = "Rotated on Friday",
        });
        Assert.Equal(("POST", "/admin/evidence/reviews/r-1/decide", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(Canonical.FromJson("""{"verdict":"waive_until","waive_until":"2026-01-08T00:09:00Z","note":"Rotated on Friday"}"""),
            Canonical.FromJson(sent[0].Body!));
        Assert.Equal((RemediationStates.Waived, "44", ReviewVerdicts.WaiveUntil), (decided.State, decided.Review.EntryDigest, decided.Review.Payload.Verdict));
        await gm.EvidenceStore.DecideReview("r-1", new ReviewDecision { Verdict = ReviewVerdicts.Deny });
        Assert.Equal("""{"verdict":"deny"}""", Canonical.FromJson(sent[1].Body!));
        await gm.EvidenceStore.DecideReview("r-1", new ReviewDecision
        {
            Verdict = ReviewVerdicts.WaiveUntil,
            WaiveUntil = new DateTimeOffset(2026, 1, 8, 0, 9, 0, TimeSpan.Zero).AddTicks(12_345_678),
        });
        Assert.Equal("2026-01-08T00:09:01.234567Z", JsonNode.Parse(sent[2].Body!)!["waive_until"]!.GetValue<string>());
        Assert.NotNull(RemediationRules.Rfc3339(JsonNode.Parse(sent[2].Body!)!["waive_until"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AWaiversEndIsGivenExactlyForAWaiver()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.DecideReview("r-1", new ReviewDecision { Verdict = ReviewVerdicts.WaiveUntil }));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.DecideReview("r-1",
            new ReviewDecision { Verdict = ReviewVerdicts.Uphold, WaiveUntil = DateTimeOffset.UtcNow }));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.DecideReview("../r-1", new ReviewDecision { Verdict = ReviewVerdicts.Deny }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.EvidenceStore.DecideReview("r-1", null!));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task ARequestOrADecisionIsNotRetriedWhenTheNAFails()
    {
        // A second request would find the review pending, a second decision find it decided.
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Refused(503, "evidence_store_unavailable"), retries: 2);
        await Assert.ThrowsAnyAsync<GenesisMeshException>(() =>
            gm.EvidenceStore.RequestReview(new ReviewRequest { SubjectKind = "judgement", SubjectId = "j-1", Justification = "why" }));
        Assert.Equal(["/admin/evidence/reviews"], sent.Select(s => s.Path));
        await Assert.ThrowsAnyAsync<GenesisMeshException>(() => gm.EvidenceStore.DecideReview("r-1", new ReviewDecision { Verdict = ReviewVerdicts.Deny }));
        Assert.Equal(["/admin/evidence/reviews", "/admin/evidence/reviews/r-1/decide"], sent.Select(s => s.Path));
    }

    [Fact]
    public async Task ADecisionTheNARefusesIsNotRetried()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Refused(409, "review_same_holder_as_requester"), retries: 2);
        var e = await Assert.ThrowsAsync<ConflictException>(() => gm.EvidenceStore.DecideReview("r-1", new ReviewDecision { Verdict = ReviewVerdicts.Uphold }));
        Assert.Equal("review_same_holder_as_requester", e.Code);
        Assert.Single(sent);
    }

    // ── The record outbox ──────────────────────────────────────────────────

    private static ExecutionRecorder Recorder() => new("sovereign-b", new SeedSigner(TestHelpers.ZeroSeedB64, "executor-b"));

    private static RemediationStatus Status(string json) => JsonSerializer.Deserialize<RemediationStatus>(json)!;

    [Fact]
    public async Task ARemediationRecordIsKeptInTheRecordOutboxAndSubmitted()
    {
        var sent = new List<Sent>();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        var record = await Recorder().SignRemediation(RemediationInput.For(Status(State()), RemediationSteps.Disable) with { DisabledVersionId = "v2" });
        var entry = EvidenceOutbox.RecordEntry(record);
        Assert.Equal((RecordOutboxKinds.Remediation, record.RemediationId, OutboxStates.Pending), (entry.Kind, entry.Id, entry.State));
        using var gm = Client(sent, _ => Json(201, $$"""{"status":"recorded","entry":{{Envelope}},"entry_digest":"33","payload":{{JsonSerializer.Serialize(record, Auth.SerializerOptions)}}}"""),
            outbox: outbox);
        var delivered = await gm.EvidenceStore.EnqueueRecord(record);
        Assert.Equal("recorded", delivered.Submission!.Status);
        Assert.Equal(("POST", "/evidence/remediations"), (sent[0].Method, sent[0].Path));
        Assert.Equal(record.RemediationId, JsonNode.Parse(sent[0].Body!)!["record"]!["remediation_id"]!.GetValue<string>());
        Assert.Empty(await outbox.List());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.FromResult(EvidenceOutbox.RecordEntry((RemediationRecord)null!)));
    }

    [Fact]
    public async Task WithoutARecordOutboxAnEnqueueIsRefused()
    {
        using var gm = Client([], _ => Json(201, "{}"));
        var record = await Recorder().SignRemediation(RemediationInput.For(Status(State()), RemediationSteps.None));
        Assert.Equal("record_outbox_required", (await Assert.ThrowsAsync<OutboxException>(() => gm.EvidenceStore.EnqueueRecord(record))).Code);
    }

    // ── The subject of a remediation ───────────────────────────────────────

    [Fact]
    public void AStateGivesTheSubjectChangeAndResource()
    {
        var input = RemediationInput.For(Status(State()), RemediationSteps.Disable);
        Assert.Equal((RemediationSubjectKinds.Judgement, "j-1", ChangeKinds.Observation, "o-1", "kv:vault/api-key", RemediationSteps.Disable),
            (input.SubjectKind, input.SubjectId, input.ChangeKind, input.ChangeId, input.ResourceId, input.Steps));
        Assert.Null(input.DisabledVersionId);
    }

    [Fact]
    public void ADecisionNeverExecutedNamesItselfAsItsChange()
    {
        var never = Status(State("d-1").Replace("\"subject_kind\":\"judgement\"", "\"subject_kind\":\"decision\"")
            .Replace("\"change_kind\":\"observation\"", "\"change_kind\":null").Replace("\"change_id\":\"o-1\"", "\"change_id\":null")
            .Replace("\"resource_id\":\"kv:vault/api-key\"", "\"resource_id\":null").Replace("\"fallback\":null", "\"fallback\":\"not_executed\""));
        var input = RemediationInput.For(never, RemediationSteps.None, "kv:vault/other");
        Assert.Equal((RemediationSubjectKinds.Decision, "d-1", ChangeKinds.Execution, "d-1", "kv:vault/other"),
            (input.SubjectKind, input.SubjectId, input.ChangeKind, input.ChangeId, input.ResourceId));
        var e = Assert.Throws<OutOfBandRecordException>(() => RemediationInput.For(never, RemediationSteps.None));
        Assert.Equal("remediation_subject_not_found", e.Code);
        var executed = never;
        executed.Fallback = null;
        Assert.Equal("remediation_subject_not_found", Assert.Throws<OutOfBandRecordException>(() => RemediationInput.For(executed, RemediationSteps.None, "kv:vault/other")).Code);
        Assert.Throws<ArgumentNullException>(() => RemediationInput.For(null!, RemediationSteps.None));
    }

    [Fact]
    public void TheStatesResourceWinsAndAnEmptyValueNamesNothing()
    {
        var never = Status(State("d-1").Replace("\"subject_kind\":\"judgement\"", "\"subject_kind\":\"decision\"")
            .Replace("\"fallback\":null", "\"fallback\":\"not_executed\""));
        Assert.Equal("kv:vault/api-key", RemediationInput.For(never, RemediationSteps.None, "kv:vault/other").ResourceId);
        never.ResourceId = "";
        Assert.Equal("kv:vault/other", RemediationInput.For(never, RemediationSteps.None, "kv:vault/other").ResourceId);
        Assert.Equal("remediation_subject_not_found", Assert.Throws<OutOfBandRecordException>(() => RemediationInput.For(never, RemediationSteps.None, "")).Code);
        var judged = Status(State());
        judged.ChangeId = "";
        Assert.Equal("remediation_subject_not_found", Assert.Throws<OutOfBandRecordException>(() => RemediationInput.For(judged, RemediationSteps.None)).Code);
    }
}
