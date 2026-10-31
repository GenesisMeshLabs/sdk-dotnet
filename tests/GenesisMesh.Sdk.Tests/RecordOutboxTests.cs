using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>The record outbox (1.9.0): its file store, and how the evidence store keeps and flushes its records.</summary>
public sealed class RecordOutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gm-records-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir => Path.Combine(_root, "records");

    private static readonly DateTimeOffset At = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly ObservationRecorder Observer = new("observer-a", new SeedSigner(TestHelpers.ZeroSeedB64, "observer-key-1"));

    private static Task<ObservationRecord> Observe(int n) => Observer.Record(new ObservationInput
    {
        ResourceId = $"kv:prod/s{n}",
        Action = ResourceActions.Rotate,
        Capability = "secret.rotate",
        ChangedAt = At,
        ObservedAt = At.AddMinutes(1),
        Source = "cloud-activity-log",
        SourceEventId = $"event-{n}",
    });

    private static Task<BreakGlassRecord> Glass() =>
        new ExecutionRecorder("executor-a", new SeedSigner(TestHelpers.ZeroSeedB64, "executor-key-1")).SignBreakGlass(new BreakGlassInput
        {
            ResourceId = "kv:prod/s1",
            ResourceAction = ResourceActions.Rotate,
            Capability = "secret.rotate",
            AttestationId = "att-1",
            Justification = "NA unreachable during an incident",
            EvaluationRequest = new Dictionary<string, object?> { ["requested_capability"] = "secret.rotate" },
            EvaluationFailure = EvaluationFailures.NetworkError,
            ExecutedAt = At,
            Outcome = ExecutionOutcomes.Success,
        });

    /// <summary>An entry of <paramref name="kind"/> whose record is only an ID: the fake NA does not read it.</summary>
    private static RecordOutboxEntry Raw(string id, string kind = RecordOutboxKinds.Observation, string? nextAttemptAt = null) => new()
    {
        Id = id,
        Kind = kind,
        Record = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["id"] = id }),
        QueuedAt = "2026-10-30T12:00:00.000Z",
        NextAttemptAt = nextAttemptAt,
    };

    private static string Canonical(JsonElement record) => GenesisMesh.Canonical.FromJson(record.GetRawText());

    // ── The NA ───────────────────────────────────────────────────────────────

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }

    /// <summary>The NA's record routes, answering as told; by default it records every record.</summary>
    private sealed class Na
    {
        public List<(string Path, JsonObject Body)> Sent { get; } = [];

        /// <summary>The answer to a request; null for the default.</summary>
        public Func<string, JsonObject, HttpResponseMessage?> Answer { get; set; } = (_, _) => null;

        /// <summary>Awaited before answering, with the request's token.</summary>
        public Func<CancellationToken, Task>? During { get; set; }

        /// <summary>The paths of what was sent, in order.</summary>
        public IEnumerable<string> Paths => Sent.Select(s => s.Path);

        /// <summary>The sizes of the batches sent, in order.</summary>
        public IEnumerable<int> Batches => Sent.Where(s => s.Path.EndsWith("/batch", StringComparison.Ordinal))
            .Select(s => s.Body["observations"]!.AsArray().Count);

        public async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            lock (Sent) Sent.Add((path, body));
            if (During is { } during) await during(ct);
            return Answer(path, body) ?? (path.EndsWith("/batch", StringComparison.Ordinal)
                ? Batch(body["observations"]!.AsArray().Select((_, i) => Result(i)).ToArray())
                : Recorded());
        }
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Recorded(string status = "recorded") =>
        Json(201, $$$"""{"status":"{{{status}}}","entry":{},"entry_digest":"d","payload":{}}""");

    private static HttpResponseMessage Refused(int status, string code, string? details = null, int? retryAfter = null)
    {
        var answer = Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"refused"{{{(details is null ? "" : ",\"details\":" + details)}}}}}""");
        if (retryAfter is { } wait) answer.Headers.Add("Retry-After", wait.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return answer;
    }

    private static string Result(int index, string status = "recorded") =>
        $$$"""{"index":{{{index}}},"status":"{{{status}}}","entry":{},"entry_digest":"d","payload":{}}""";

    private static HttpResponseMessage Batch(params string[] results) => Json(200, $$"""{"results":[{{string.Join(",", results)}}]}""");

    private static GenesisMeshClient Client(Na na, IOutbox<RecordOutboxEntry>? outbox, int retries = 0) => new(new ClientOptions
    {
        BaseUrl = "http://localhost",
        SigningKey = TestHelpers.ZeroSeedB64,
        KeyId = "ops",
        Audience = "TEST",
        Retry = new RetrySettings { Attempts = retries, BaseDelay = TimeSpan.Zero },
        RecordOutbox = outbox,
        HttpHandler = new AsyncHandler(na.Respond),
    });

    /// <summary>A memory record outbox whose reads and writes can fail or wait.</summary>
    private sealed class HookedOutbox : IOutbox<RecordOutboxEntry>
    {
        private readonly MemoryOutbox<RecordOutboxEntry> _inner = new();
        public bool FailList { get; set; }
        public bool FailUpdates { get; set; }
        public bool FailRemoves { get; set; }
        /// <summary>Updates and removals fail as a store's own timeout does, though the caller did not cancel.</summary>
        public bool CancelWrites { get; set; }
        public Func<Task>? DuringList { get; set; }
        public int Lists { get; private set; }

        public Task Add(RecordOutboxEntry entry, CancellationToken ct = default) => _inner.Add(entry, ct);

        public Task Update(RecordOutboxEntry entry, CancellationToken ct = default) =>
            CancelWrites ? Task.FromException(new TaskCanceledException("store timeout"))
            : FailUpdates ? Task.FromException(new IOException("disk")) : _inner.Update(entry, ct);

        public Task Remove(string id, CancellationToken ct = default) =>
            CancelWrites ? Task.FromException(new TaskCanceledException("store timeout"))
            : FailRemoves ? Task.FromException(new IOException("disk")) : _inner.Remove(id, ct);

        public async Task<IReadOnlyList<RecordOutboxEntry>> List(CancellationToken ct = default)
        {
            Lists++;
            if (DuringList is { } during) await during();
            if (FailList) throw new IOException("unreadable");
            return await _inner.List(ct);
        }
    }

    // ── FileRecordOutbox ─────────────────────────────────────────────────────

    [Fact]
    public async Task FileRecordOutbox_KeepsRecordsInTheirOwnFormat_InOrder()
    {
        var first = EvidenceOutbox.RecordEntry(await Observe(1));
        var glass = EvidenceOutbox.RecordEntry(await Glass());
        await new FileRecordOutbox(Dir).Add(first);
        await new FileRecordOutbox(Dir).Add(glass);
        var listed = await new FileRecordOutbox(Dir).List();
        Assert.Equal([(RecordOutboxKinds.Observation, first.Id), (RecordOutboxKinds.BreakGlass, glass.Id)], listed.Select(e => (e.Kind, e.Id)));
        Assert.Equal(Canonical(first.Record), Canonical(listed[0].Record));
        var file = Directory.GetFiles(Dir).Order(StringComparer.Ordinal).First();
        var body = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("gm.evidence.record-outbox.v1", (string?)body["format"]);
        Assert.Equal("observation", (string?)body["entry"]!["kind"]);
        var outbox = new FileRecordOutbox(Dir);
        Assert.Equal(Dir, outbox.DirectoryPath);
        await outbox.Update(listed[0] with { Attempts = 3 });
        await outbox.Remove(glass.Id);
        Assert.Equal(3, Assert.Single(await new FileRecordOutbox(Dir).List()).Attempts);
        Assert.Throws<ArgumentException>(() => new FileRecordOutbox(""));
    }

    [Fact]
    public async Task FileRecordOutbox_AndFileOutbox_RefuseEachOthersDirectory()
    {
        await new FileOutbox(Dir).Add(EvidenceOutbox.Entry(new ExecutionEvidence { EvidenceId = "e", Outcome = "success" }));
        var error = await Assert.ThrowsAsync<OutboxException>(() => new FileRecordOutbox(Dir).List());
        Assert.Equal("outbox_file_other_format", error.Code);
        Assert.Contains("gm.evidence.record-outbox.v1", error.Message, StringComparison.Ordinal);

        var records = Path.Combine(_root, "other");
        await new FileRecordOutbox(records).Add(EvidenceOutbox.RecordEntry(await Observe(1)));
        Assert.Equal("outbox_file_other_format", (await Assert.ThrowsAsync<OutboxException>(() => new FileOutbox(records).List())).Code);
    }

    [Fact]
    public async Task FileRecordOutbox_KeepsARecordAsAnotherSdkWroteIt_AndSubmitsItSo()
    {
        Directory.CreateDirectory(Dir);
        // Signed over 1.0, as a Rust or Python signer writes an integral float, with a field this SDK does not know.
        File.WriteAllText(Path.Combine(Dir, "000000000001-o-1.json"),
            """{"entry":{"id":"o-1","kind":"observation","record":{"observation_id":"o-1","metadata":{"ratio":1.0},"later_field":"x"},"state":"pending","attempts":0,"queued_at":"2026-10-30T12:00:00.000Z","next_attempt_at":null,"last_error":null},"format":"gm.evidence.record-outbox.v1"}""");
        File.WriteAllText(Path.Combine(Dir, "000000000002-r-1.json"),
            """{"entry":{"id":"r-1","kind":"remediation","record":{"remediation_id":"r-1"},"state":"pending","attempts":0,"queued_at":"2026-10-30T12:00:00.000Z","next_attempt_at":null,"last_error":null},"format":"gm.evidence.record-outbox.v1"}""");
        File.WriteAllText(Path.Combine(Dir, "000000000003-u-1.json"),
            """{"entry":{"id":"u-1","kind":"grant_execution","record":{"use_id":"u-1"},"state":"pending","attempts":0,"queued_at":"2026-10-30T12:00:00.000Z","next_attempt_at":null,"last_error":null},"format":"gm.evidence.record-outbox.v1"}""");
        var outbox = new FileRecordOutbox(Dir);
        var listed = await outbox.List();
        Assert.Equal(["observation", "remediation", "grant_execution"], listed.Select(e => e.Kind));
        Assert.Contains("\"ratio\":1.0", listed[0].Record.GetRawText(), StringComparison.Ordinal);
        await outbox.Update(listed[0] with { Attempts = 1 });
        Assert.Contains("\"ratio\":1.0", File.ReadAllText(Path.Combine(Dir, "000000000001-o-1.json")), StringComparison.Ordinal);

        var na = new Na();
        var flushed = await Client(na, outbox).EvidenceStore.FlushRecords();
        Assert.Equal(3, flushed.Admitted.Count);
        var observation = na.Sent.Single(s => s.Path == "/evidence/observations/batch").Body;
        var sent = observation["observations"]![0]!.ToJsonString();
        Assert.Equal(GenesisMesh.Canonical.FromJson("""{"observation_id":"o-1","metadata":{"ratio":1.0},"later_field":"x"}"""), GenesisMesh.Canonical.FromJson(sent));
        Assert.Contains("\"ratio\":1.0", sent, StringComparison.Ordinal);
        Assert.Empty(await outbox.List());
    }

    [Fact]
    public async Task FileRecordOutbox_MovesAsideARecordOfAnotherKind()
    {
        await new FileRecordOutbox(Dir).Add(EvidenceOutbox.RecordEntry(await Observe(1)));
        File.WriteAllText(Path.Combine(Dir, "000000000002-d-1.json"),
            """{"entry":{"id":"d-1","kind":"decision","record":{},"state":"pending","attempts":0,"queued_at":"2026-10-30T12:00:00.000Z","next_attempt_at":null,"last_error":null},"format":"gm.evidence.record-outbox.v1"}""");
        File.WriteAllText(Path.Combine(Dir, "000000000003-n-1.json"),
            """{"entry":{"id":"n-1","kind":"observation","record":null},"format":"gm.evidence.record-outbox.v1"}""");
        File.WriteAllText(Path.Combine(Dir, "000000000004-k-1.json"),
            """{"entry":{"id":"k-1","kind":7,"record":{}},"format":"gm.evidence.record-outbox.v1"}""");
        var outbox = new FileRecordOutbox(Dir);
        var error = await Assert.ThrowsAsync<OutboxException>(() => outbox.List());
        Assert.Equal("outbox_file_unreadable", error.Code);
        Assert.Contains("000000000002-d-1.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("000000000003-n-1.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("000000000004-k-1.json", error.Message, StringComparison.Ordinal);
        Assert.Single(await outbox.List());
    }

    // ── The rules ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordEntry_IsPendingWithNoAttempts_QueuedNow_HoldingTheRecordAsSigned()
    {
        var observation = await Observe(1);
        var entry = EvidenceOutbox.RecordEntry(observation, At);
        Assert.Equal((observation.ObservationId, RecordOutboxKinds.Observation, OutboxStates.Pending, 0, "2026-10-30T12:00:00.000Z", null, null),
            (entry.Id, entry.Kind, entry.State, entry.Attempts, entry.QueuedAt, entry.NextAttemptAt, entry.LastError));
        Assert.Equal(GenesisMesh.Canonical.FromJson(JsonSerializer.Serialize(observation, Auth.SerializerOptions)), Canonical(entry.Record));
        Assert.Equal(observation.ObservationId, entry.Record.Deserialize<ObservationRecord>(Auth.SerializerOptions)!.ObservationId);
        var glass = await Glass();
        Assert.Equal((glass.BreakGlassId, RecordOutboxKinds.BreakGlass), (EvidenceOutbox.RecordEntry(glass).Id, EvidenceOutbox.RecordEntry(glass).Kind));
        Assert.InRange(EvidenceOutbox.ParseTime(EvidenceOutbox.RecordEntry(glass).QueuedAt)!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Throws<ArgumentNullException>(() => EvidenceOutbox.RecordEntry((ObservationRecord)null!));
        Assert.Throws<ArgumentNullException>(() => EvidenceOutbox.RecordEntry((BreakGlassRecord)null!));
    }

    [Theory]
    [InlineData("observation_invalid_signature", 422, false)]
    [InlineData("break_glass_conflict", 409, false)]
    [InlineData("remediation_not_due", 422, false)]
    [InlineData("grant_budget_exhausted", 422, false)]
    [InlineData("invalid_json", 400, false)]
    // Refused by this SDK before sending.
    [InlineData("observation_malformed", 0, false)]
    // An observer key not registered yet, a grant use behind one the NA does not hold yet, an execution refusal.
    [InlineData("observation_unknown_key", 422, true)]
    [InlineData("grant_use_chain_gap", 422, true)]
    [InlineData("evidence_decision_denied", 422, true)]
    [InlineData("observation_malformed", 503, true)]
    public void ClassifyRecord_TellsTransientFailuresFromFinalRefusals(string code, int status, bool transient)
    {
        var (failure, isTransient) = EvidenceOutbox.ClassifyRecord(new GenesisMeshException(status, code, "refused"));
        Assert.Equal((code, status, transient), (failure.Code, failure.Status, isTransient));
        // The evidence outbox's rules are its own.
        Assert.True(EvidenceOutbox.Classify(new GenesisMeshException(422, "observation_invalid_signature", "x")).Transient);
        Assert.Contains("invalid_json", EvidenceOutbox.RecordPermanentRefusals);
    }

    [Fact]
    public void RecordPermanentRefusals_AreTheTypeScriptSdks_AndLeaveOutWhatIsRetried()
    {
        string[] typescript =
        [
            "invalid_json", "invalid_observation", "observation_malformed", "observation_invalid_signature", "observation_key_retired",
            "observation_out_of_scope", "observation_secret_material", "observation_conflict", "invalid_break_glass", "break_glass_malformed",
            "break_glass_invalid_signature", "break_glass_key_retired", "break_glass_out_of_scope", "break_glass_secret_material",
            "break_glass_conflict", "invalid_remediation", "remediation_malformed", "remediation_invalid_signature", "remediation_key_retired",
            "remediation_out_of_scope", "remediation_secret_material", "remediation_subject_mismatch", "remediation_steps_mismatch",
            "remediation_rotation_mismatch", "remediation_rotation_before_change", "remediation_rotation_other_executor",
            "remediation_rotation_reused", "remediation_not_due", "remediation_already_recorded", "remediation_conflict", "invalid_grant_use",
            "grant_use_malformed", "grant_not_found", "grant_use_invalid_signature", "grant_bearer_mismatch", "grant_use_secret_material",
            "grant_bearer_key_retired", "grant_mismatch", "grant_use_conflict", "grant_use_chain_mismatch", "grant_capability_mismatch",
            "grant_envelope_mismatch", "grant_budget_exhausted", "grant_time_token_invalid", "grant_time_token_mismatch",
            "grant_time_token_regressed", "grant_time_token_exhausted", "grant_use_before_time_token", "grant_use_out_of_order",
            "grant_use_outside_validity", "grant_use_uploaded_late", "grant_revoked", "grant_use_out_of_scope",
        ];
        Assert.Equal(typescript.Order(StringComparer.Ordinal), EvidenceOutbox.RecordPermanentRefusals.Order(StringComparer.Ordinal));
        foreach (var retried in new[]
        {
            "observation_unknown_key", "break_glass_unknown_key", "remediation_subject_not_found", "remediation_rotation_missing",
            "remediation_disabled", "grant_use_chain_gap", "grant_use_in_future", "grants_disabled", "out_of_band_disabled",
        })
        {
            Assert.DoesNotContain(retried, EvidenceOutbox.RecordPermanentRefusals);
        }
    }

    // ── EnqueueRecord ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueRecord_AndFlushRecords_NeedARecordOutbox()
    {
        using var bare = Client(new Na(), null);
        Assert.Null(bare.EvidenceStore.RecordOutbox);
        var error = await Assert.ThrowsAsync<OutboxException>(async () => await bare.EvidenceStore.EnqueueRecord(await Observe(1)));
        Assert.Equal(("record_outbox_required", 0), (error.Code, error.Status));
        await Assert.ThrowsAsync<OutboxException>(async () => await bare.EvidenceStore.EnqueueRecord(await Glass()));
        await Assert.ThrowsAsync<OutboxException>(() => bare.EvidenceStore.FlushRecords());
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        using var gm = Client(new Na(), outbox);
        Assert.Same(outbox, gm.EvidenceStore.RecordOutbox);
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.EvidenceStore.EnqueueRecord((ObservationRecord)null!));
    }

    [Fact]
    public async Task EnqueueRecord_RemovesAnAdmittedRecord_AndKeepsATransientFailurePending()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        using var gm = Client(na, outbox);
        var admitted = await gm.EvidenceStore.EnqueueRecord(await Observe(1));
        Assert.Equal("recorded", admitted.Submission!.Status);
        Assert.Null(admitted.Queued);
        Assert.Empty(await outbox.List());
        Assert.Equal(("/evidence/observations", (await Observe(1)).ObservationId),
            (na.Sent[0].Path, (string?)na.Sent[0].Body["observation"]!["observation_id"]));

        na.Answer = (_, _) => throw new HttpRequestException("connection refused");
        var before = DateTimeOffset.UtcNow;
        var queued = (await gm.EvidenceStore.EnqueueRecord(await Observe(2))).Queued!;
        Assert.Equal((OutboxStates.Pending, 1, EvidenceOutbox.NetworkError), (queued.State, queued.Attempts, queued.LastError!.Code));
        Assert.True(EvidenceOutbox.ParseTime(queued.NextAttemptAt) >= before.AddSeconds(4));
        var stored = Assert.Single(await outbox.List());
        Assert.Equal((queued.Id, queued.Attempts, queued.NextAttemptAt), (stored.Id, stored.Attempts, stored.NextAttemptAt));
    }

    [Theory]
    [InlineData(204)]
    [InlineData(200)]
    public async Task AnAnswerWithoutABody_LeavesTheRecordPending(int status)
    {
        // A success without a body, such as a proxy's 204: the NA may or may not hold the record.
        var na = new Na { Answer = (_, _) => new HttpResponseMessage((HttpStatusCode)status) };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        using var gm = Client(na, outbox);
        var glass = await Glass();
        var queued = (await gm.EvidenceStore.EnqueueRecord(glass)).Queued!;
        Assert.Equal((OutboxStates.Pending, "response_body_unreadable"), (queued.State, queued.LastError!.Code));
        await outbox.Add(Raw("o-1"));
        var flushed = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        Assert.Empty(flushed.Admitted);
        Assert.Equal(["/evidence/break-glass", "/evidence/break-glass"], na.Paths);
        // A batch answered so is pending too.
        na.Answer = (path, _) => path.EndsWith("/batch", StringComparison.Ordinal) ? new HttpResponseMessage((HttpStatusCode)status) : null;
        var batch = await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(glass.BreakGlassId, Assert.Single(batch.Admitted).Id);
        Assert.Equal(("o-1", "response_body_unreadable"), (Assert.Single(batch.Pending).Id, batch.Pending[0].LastError!.Code));
        Assert.Equal("o-1", Assert.Single(await outbox.List()).Id);
    }

    [Fact]
    public async Task EnqueueRecord_DeadLettersAFinalRefusal_KeepingItsQuarantine_AndRetriesAnUnknownKey()
    {
        var na = new Na { Answer = (_, _) => Refused(422, "break_glass_invalid_signature", """{"quarantine_id":"q-1"}""") };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        using var gm = Client(na, outbox);
        var glass = await Glass();
        var dead = (await gm.EvidenceStore.EnqueueRecord(glass)).Queued!;
        Assert.Equal((OutboxStates.DeadLetter, 1, "break_glass_invalid_signature", "q-1", null),
            (dead.State, dead.Attempts, dead.LastError!.Code, dead.LastError.QuarantineId, dead.NextAttemptAt));
        Assert.Equal("/evidence/break-glass", na.Sent[0].Path);
        Assert.Equal(glass.BreakGlassId, (string?)na.Sent[0].Body["record"]!["break_glass_id"]);
        // A dead letter enqueued again is returned as it is, unsent.
        var again = (await gm.EvidenceStore.EnqueueRecord(glass)).Queued!;
        Assert.Equal((dead.Id, OutboxStates.DeadLetter, "q-1"), (again.Id, again.State, again.LastError!.QuarantineId));
        Assert.Single(na.Sent);

        na.Answer = (_, _) => Refused(422, "observation_unknown_key");
        Assert.Equal(OutboxStates.Pending, (await gm.EvidenceStore.EnqueueRecord(await Observe(1))).Queued!.State);
    }

    [Fact]
    public async Task EnqueueRecord_SubmitsAQueuedRecordAgain_AndRefusesADifferentOneUnderItsId()
    {
        var na = new Na { Answer = (_, _) => Refused(503, "unavailable") };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        using var gm = Client(na, outbox);
        var record = await Observe(1);
        await gm.EvidenceStore.EnqueueRecord(record);
        var before = DateTimeOffset.UtcNow;
        var twice = (await gm.EvidenceStore.EnqueueRecord(record)).Queued!;
        // 5 seconds after one failure, 10 after two.
        Assert.Equal(2, twice.Attempts);
        Assert.True(EvidenceOutbox.ParseTime(twice.NextAttemptAt) >= before.AddSeconds(9));
        var other = await Observer.Record(new ObservationInput
        {
            ResourceId = "kv:prod/other",
            Action = ResourceActions.Delete,
            Capability = "secret.rotate",
            ChangedAt = At,
            Source = "cloud-activity-log",
            SourceEventId = "event-1",
        });
        Assert.Equal(record.ObservationId, other.ObservationId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gm.EvidenceStore.EnqueueRecord(other));
        Assert.Contains("a different record", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, na.Sent.Count);
    }

    // ── FlushRecords ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FlushRecords_SendsBreakGlassFirst_ThenGrantUses_ThenObservations_ThenRemediation_EachToItsRoute()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        foreach (var entry in new[]
        {
            Raw("r-2", RecordOutboxKinds.Remediation), Raw("o-1"), Raw("u-1", RecordOutboxKinds.GrantExecution),
            Raw("b-1", RecordOutboxKinds.BreakGlass), Raw("o-2"), Raw("r-1", RecordOutboxKinds.Remediation),
        })
        {
            await outbox.Add(entry);
        }
        na.Answer = (path, _) => path == "/evidence/break-glass" ? Recorded("quarantined") : null;
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal(["b-1", "u-1", "o-1", "o-2", "r-2", "r-1"], result.Admitted.Select(e => e.Id));
        Assert.Equal("b-1", Assert.Single(result.Quarantined).Id);
        Assert.Equal(
            [("/evidence/break-glass", "record"), ("/evidence/grant-executions", "evidence"), ("/evidence/observations/batch", "observations"),
             ("/evidence/remediations", "record"), ("/evidence/remediations", "record")],
            na.Sent.Select(s => (s.Path, s.Body.Single().Key)));
        Assert.Equal("u-1", (string?)na.Sent[1].Body["evidence"]!["id"]);
        Assert.Empty(result.Pending);
        Assert.Empty(await outbox.List());
    }

    [Fact]
    public async Task FlushRecords_SendsObservationsInBatches_AndSettlesEachAnswer()
    {
        var na = new Na
        {
            Answer = (path, _) => Batch(
                Result(0),
                """{"index":1,"status":"refused","error":{"code":"observation_out_of_scope","message":"out of scope","details":{"quarantine_id":"q-1"}}}""",
                Result(2, "quarantined"),
                """{"index":4,"status":"refused","error":{"code":"observation_conflict","message":"taken"}}""",
                """{"index":5,"status":"refused","error":{"code":"observation_unknown_key","message":"not yet"}}""",
                """{"index":6,"status":"refused"}""",
                """{"index":7,"status":"refused","error":{} }"""),
        };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        for (var n = 0; n < 8; n++) await outbox.Add(Raw($"o-{n}"));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal([8], na.Batches);
        Assert.Equal(["o-0", "o-2"], result.Admitted.Select(e => e.Id));
        Assert.Equal("o-2", Assert.Single(result.Quarantined).Id);
        Assert.Equal([("o-1", "observation_out_of_scope", "out of scope", 422, "q-1"), ("o-4", "observation_conflict", "taken", 409, null)],
            result.DeadLettered.Select(e => (e.Id, e.LastError!.Code, e.LastError.Message, e.LastError.Status, e.LastError.QuarantineId)));
        // Unanswered, refused for a key not registered yet, or refused without a reason: still pending.
        Assert.Equal([("o-3", null, null), ("o-5", "observation_unknown_key", "not yet"), ("o-6", "unknown", "unknown"), ("o-7", "unknown", "unknown")],
            result.Pending.Select(e => (e.Id, e.LastError?.Code, e.LastError?.Message)));
        Assert.Equal(["o-1", "o-3", "o-4", "o-5", "o-6", "o-7"], (await outbox.List()).Select(e => e.Id));
    }

    [Fact]
    public async Task FlushRecords_StopsAtATransientFailure_AndSkipsRecordsInBackoff()
    {
        var na = new Na { Answer = (_, _) => Refused(503, "unavailable") };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass));
        await outbox.Add(Raw("o-1"));
        await outbox.Add(Raw("o-later", nextAttemptAt: EvidenceOutbox.Time(DateTimeOffset.UtcNow.AddHours(1))));
        await outbox.Add(Raw("o-due", nextAttemptAt: EvidenceOutbox.Time(DateTimeOffset.UtcNow.AddHours(-1))));
        using var gm = Client(na, outbox);
        var first = await gm.EvidenceStore.FlushRecords();
        Assert.Equal(["/evidence/break-glass"], na.Paths);
        Assert.Equal(["b-1", "o-1", "o-later", "o-due"], first.Pending.Select(e => e.Id));
        Assert.Equal(1, first.Pending[0].Attempts);
        // The break-glass record waits out its backoff: the observations go, and the one in backoff waits too.
        na.Answer = (_, _) => null;
        var second = await gm.EvidenceStore.FlushRecords();
        Assert.Equal(["o-1", "o-due"], second.Admitted.Select(e => e.Id));
        Assert.Equal(["b-1", "o-later"], second.Pending.Select(e => e.Id));
        // A record in backoff ends a batch: the observations after it go in the next.
        Assert.Equal([1, 1], na.Batches);
        Assert.Equal(["o-1", "o-due"], na.Sent.Skip(1).Select(s => (string?)s.Body["observations"]![0]!["id"]));
        Assert.Equal(["b-1", "o-later"], (await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true })).Admitted.Select(e => e.Id));
    }

    [Fact]
    public async Task FlushRecords_LeavesDeadLettersUnsent_AndAFinalRefusalHoldsNothingBack()
    {
        var na = new Na();
        na.Answer = (path, _) => path == "/evidence/break-glass" ? Refused(422, "break_glass_malformed", retryAfter: 600) : null;
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-dead") with { State = OutboxStates.DeadLetter, Attempts = 1 });
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass, nextAttemptAt: EvidenceOutbox.Time(DateTimeOffset.UtcNow.AddMinutes(-1))) with { Attempts = 2 });
        await outbox.Add(Raw("o-1"));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        var dead = Assert.Single(result.DeadLettered);
        Assert.Equal(("b-1", 3, null), (dead.Id, dead.Attempts, dead.NextAttemptAt));
        Assert.Equal("o-1", Assert.Single(result.Admitted).Id);
        Assert.Empty(result.Pending);
        Assert.Equal(["/evidence/break-glass", "/evidence/observations/batch"], na.Paths);
        await outbox.Add(Raw("o-2"));
        Assert.Equal("o-2", Assert.Single((await gm.EvidenceStore.FlushRecords()).Admitted).Id);
        Assert.Equal(["o-dead", "b-1"], (await outbox.List()).Select(e => e.Id));
    }

    [Fact]
    public async Task FlushRecords_SplitsABatchTheNARefusesWhole_UntilItFindsTheRecord()
    {
        static bool Unreadable(JsonObject body, string field) => body[field] is JsonArray all
            ? all.Any(r => (string?)r!["id"] == "o-3")
            : (string?)body[field]!["id"] == "o-3";
        var na = new Na();
        na.Answer = (path, body) => path.EndsWith("/batch", StringComparison.Ordinal)
            ? Unreadable(body, "observations") ? Refused(400, "invalid_json") : null
            : Unreadable(body, "observation") ? Refused(400, "invalid_json") : null;
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        for (var n = 0; n < 5; n++) await outbox.Add(Raw($"o-{n}"));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal(["o-0", "o-1", "o-2", "o-4"], result.Admitted.Select(e => e.Id));
        Assert.Equal(("o-3", "invalid_json"), (Assert.Single(result.DeadLettered).Id, result.DeadLettered[0].LastError!.Code));
        // 5, then 3 and 2; the 2 alone, each.
        Assert.Equal([5, 3, 2], na.Batches);
        Assert.Equal(2, na.Paths.Count(p => p == "/evidence/observations"));
    }

    [Fact]
    public async Task FlushRecords_LeavesTheSecondHalfPending_WhenTheFirstFailsTransiently()
    {
        var na = new Na();
        na.Answer = (_, body) => body["observations"]!.AsArray().Count == 4 ? Refused(400, "invalid_json") : Refused(503, "unavailable");
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        for (var n = 0; n < 4; n++) await outbox.Add(Raw($"o-{n}"));
        await outbox.Add(Raw("r-1", RecordOutboxKinds.Remediation));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal([4, 2], na.Batches);
        Assert.Equal([("o-0", 1), ("o-1", 1), ("o-2", 0), ("o-3", 0), ("r-1", 0)], result.Pending.Select(e => (e.Id, e.Attempts)));
        Assert.Empty(result.Admitted);
        Assert.DoesNotContain("/evidence/remediations", na.Paths);
    }

    [Fact]
    public async Task FlushRecords_KeepsABatchPending_WhenTheNARefusesItForAReasonARetryMayOvercome()
    {
        var na = new Na { Answer = (_, _) => Refused(400, "request_not_ready") };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-0"));
        await outbox.Add(Raw("o-1"));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal([("o-0", 1, "request_not_ready"), ("o-1", 1, "request_not_ready")], result.Pending.Select(e => (e.Id, e.Attempts, e.LastError!.Code)));
        Assert.Equal(["/evidence/observations/batch"], na.Paths);
    }

    [Fact]
    public async Task FlushRecords_RetriesTheRecordRoutes_AsResubmittingARecordIsSafe()
    {
        var na = new Na();
        var answered = new HashSet<string>();
        na.Answer = (path, _) => answered.Add(path) ? Refused(503, "unavailable") : null;
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass));
        await outbox.Add(Raw("o-1"));
        using var gm = Client(na, outbox, retries: 1);
        Assert.Equal(2, (await gm.EvidenceStore.FlushRecords()).Admitted.Count);
        Assert.Equal(["/evidence/break-glass", "/evidence/break-glass", "/evidence/observations/batch", "/evidence/observations/batch"], na.Paths);
    }

    [Fact]
    public async Task FlushRecords_SendsABatchTooLargeForTheNAOneObservationAtATime()
    {
        var na = new Na();
        na.Answer = (path, _) => path.EndsWith("/batch", StringComparison.Ordinal) ? Refused(413, "request_entity_too_large") : null;
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-0"));
        await outbox.Add(Raw("o-1"));
        using var gm = Client(na, outbox);
        Assert.Equal(2, (await gm.EvidenceStore.FlushRecords()).Admitted.Count);
        Assert.Equal(["/evidence/observations/batch", "/evidence/observations", "/evidence/observations"], na.Paths);
        // A batch of one goes alone, too.
        await outbox.Add(Raw("o-2"));
        Assert.Equal("o-2", Assert.Single((await gm.EvidenceStore.FlushRecords()).Admitted).Id);
        Assert.Equal(["/evidence/observations/batch", "/evidence/observations"], na.Paths.Skip(3));
    }

    [Fact]
    public async Task FlushRecords_KeepsABatchPending_WhenTheNACannotBeReached_WithoutHalvingTheBatches()
    {
        var na = new Na { Answer = (_, _) => throw new HttpRequestException("connection refused") };
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        for (var n = 0; n < 3; n++) await outbox.Add(Raw($"o-{n}"));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal([EvidenceOutbox.NetworkError, EvidenceOutbox.NetworkError, EvidenceOutbox.NetworkError], result.Pending.Select(e => e.LastError!.Code));
        na.Answer = (_, _) => null;
        Assert.Equal(3, (await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true })).Admitted.Count);
        Assert.Equal([3, 3], na.Batches);
    }

    [Fact]
    public async Task FlushRecords_HalvesItsBatchesAfterA429_AndWaitsAsLongAsRetryAfterAsks()
    {
        var na = new Na();
        na.Answer = (_, body) => body["observations"]!.AsArray().Count > 60 ? Refused(429, "rate_limit_exceeded", retryAfter: 60) : null;
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        for (var n = 0; n < 150; n++) await outbox.Add(Raw($"o-{n}"));
        using var gm = Client(na, outbox);
        var before = DateTimeOffset.UtcNow;
        var first = await gm.EvidenceStore.FlushRecords();
        Assert.Empty(first.Admitted);
        Assert.Equal(150, first.Pending.Count);
        Assert.True(EvidenceOutbox.ParseTime(first.Pending[0].NextAttemptAt) >= before.AddSeconds(59));
        Assert.Equal(0, first.Pending[100].Attempts);
        // The NA asked for a minute: even a record not tried yet waits.
        Assert.Empty((await gm.EvidenceStore.FlushRecords()).Admitted);
        Assert.Equal(150, (await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true })).Admitted.Count);
        Assert.Equal([100, 50, 50, 50], na.Batches);
    }

    [Fact]
    public async Task FlushRecords_KeepsBatchesOfOne_AfterABatchOfOneIsThrottled()
    {
        var na = new Na();
        na.Answer = (_, _) => Refused(429, "rate_limit_exceeded");
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-0"));
        using var gm = Client(na, outbox);
        Assert.Single((await gm.EvidenceStore.FlushRecords()).Pending);
        na.Answer = (_, _) => null;
        await outbox.Add(Raw("o-1"));
        await outbox.Add(Raw("o-2"));
        Assert.Equal(3, (await gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true })).Admitted.Count);
        Assert.Equal([1, 1, 1, 1], na.Batches);
    }

    [Fact]
    public async Task FlushRecords_SettlesWhatTheNAAnswered_WhenTheOutboxCannotStoreIt()
    {
        var na = new Na();
        var outbox = new HookedOutbox { FailRemoves = true, FailUpdates = true };
        await outbox.Add(Raw("o-1"));
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass));
        await outbox.Add(Raw("b-2", RecordOutboxKinds.BreakGlass));
        na.Answer = (path, body) => (string?)body["record"]?["id"] == "b-2" ? Refused(422, "break_glass_malformed") : null;
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        Assert.Equal(["b-1", "o-1"], result.Admitted.Select(e => e.Id));
        Assert.Equal("b-2", Assert.Single(result.DeadLettered).Id);
        // Still stored as they were: the next flush resubmits the admitted ones and gets duplicates.
        Assert.Equal([OutboxStates.Pending, OutboxStates.Pending, OutboxStates.Pending], (await outbox.List()).Select(e => e.State));

        // A store that cancels its own writes, though the caller did not, is no different.
        outbox.CancelWrites = true;
        Assert.Equal("recorded", (await gm.EvidenceStore.EnqueueRecord(await Observe(1))).Submission!.Status);
        na.Answer = (_, _) => Refused(503, "unavailable");
        Assert.Equal(OutboxStates.Pending, (await gm.EvidenceStore.EnqueueRecord(await Observe(2))).Queued!.State);
    }

    [Fact]
    public async Task FlushRecords_DeadLettersARecordOfAnUnknownKind_AndSendsTheRecordsAfterIt()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("x-1", "decision"));
        await outbox.Add(Raw("r-1", RecordOutboxKinds.Remediation));
        using var gm = Client(na, outbox);
        var result = await gm.EvidenceStore.FlushRecords();
        var dead = Assert.Single(result.DeadLettered);
        Assert.Equal(("x-1", EvidenceOutbox.UnknownRecordKind, 0, 1), (dead.Id, dead.LastError!.Code, dead.LastError.Status, dead.Attempts));
        Assert.Contains("unknown kind, decision", dead.LastError.Message, StringComparison.Ordinal);
        Assert.Equal("r-1", Assert.Single(result.Admitted).Id);
        Assert.Equal(["/evidence/remediations"], na.Paths);
        Assert.Equal(OutboxStates.DeadLetter, Assert.Single(await outbox.List()).State);
    }

    [Fact]
    public async Task FlushRecords_SharesOneRunBetweenConcurrentFlushes()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-1"));
        var release = new TaskCompletionSource();
        na.During = ct => release.Task.WaitAsync(ct);
        using var gm = Client(na, outbox);
        var a = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        var b = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        // A run that keeps the backoff joins one that ignores it.
        var c = gm.EvidenceStore.FlushRecords();
        release.SetResult();
        Assert.Single((await a).Admitted);
        Assert.Same(await a, await b);
        Assert.Same(await a, await c);
        Assert.Single(na.Sent);
    }

    [Fact]
    public async Task FlushRecords_StartsItsOwnRun_AfterOneThatTriesLess()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass));
        await outbox.Add(Raw("o-later", nextAttemptAt: EvidenceOutbox.Time(DateTimeOffset.UtcNow.AddHours(1))));
        var release = new TaskCompletionSource();
        na.During = ct => release.Task.WaitAsync(ct);
        using var gm = Client(na, outbox);
        var keepsBackoff = gm.EvidenceStore.FlushRecords();
        var ignoresBackoff = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        release.SetResult();
        Assert.Equal(("b-1", "o-later"), (Assert.Single((await keepsBackoff).Admitted).Id, Assert.Single((await keepsBackoff).Pending).Id));
        Assert.Equal("o-later", Assert.Single((await ignoresBackoff).Admitted).Id);
        Assert.Empty(await outbox.List());
    }

    [Fact]
    public async Task FlushRecords_JoinersSeeTheRunsFailure_AndOneThatWaitedStartsItsOwn()
    {
        var outbox = new HookedOutbox { FailList = true };
        var release = new TaskCompletionSource();
        outbox.DuringList = () => release.Task;
        using var gm = Client(new Na(), outbox);
        var a = gm.EvidenceStore.FlushRecords();
        var b = gm.EvidenceStore.FlushRecords();
        var c = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => a);
        await Assert.ThrowsAsync<IOException>(() => b);
        // Not the first run's failure: its own, from the run it started after.
        await Assert.ThrowsAsync<IOException>(() => c);
        Assert.Equal(2, outbox.Lists);
    }

    [Fact]
    public async Task FlushRecords_AFlushWaitingOnAnotherRunStops_WhenItsCallerCancels()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-1"));
        var release = new TaskCompletionSource();
        // The run holds the slot until released, whatever its token says.
        na.During = _ => release.Task;
        using var gm = Client(na, outbox);
        var running = gm.EvidenceStore.FlushRecords();
        using var cts = new CancellationTokenSource();
        var waiting = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true }, cts.Token);
        await Task.Run(cts.Cancel).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        release.SetResult();
        Assert.Single((await running).Admitted);
    }

    [Fact]
    public async Task FlushRecords_AJoinerThatCancelsStopsWaiting_AndTheRunGoesOn()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-1"));
        var release = new TaskCompletionSource();
        na.During = _ => release.Task;
        using var gm = Client(na, outbox);
        var running = gm.EvidenceStore.FlushRecords();
        using var cts = new CancellationTokenSource();
        var joiner = gm.EvidenceStore.FlushRecords(null, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner.WaitAsync(TimeSpan.FromSeconds(10)));
        release.SetResult();
        Assert.Single((await running).Admitted);
    }

    [Fact]
    public async Task FlushRecords_AWaiterWhoseRunItsOwnerCancelled_StartsItsOwn()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-1"));
        var hold = new TaskCompletionSource();
        na.During = ct => hold.Task.WaitAsync(ct);
        using var gm = Client(na, outbox);
        using var owner = new CancellationTokenSource();
        var cancelled = gm.EvidenceStore.FlushRecords(null, owner.Token);
        // It asks for more than the run tries, so it waits for the run to end rather than joining it.
        var waiter = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        na.During = null;
        await owner.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal("o-1", Assert.Single((await waiter.WaitAsync(TimeSpan.FromSeconds(10))).Admitted).Id);
    }

    [Fact]
    public async Task FlushRecords_AFlushWhoseCallerCancelsLetsTheNextOneRun()
    {
        var na = new Na();
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        await outbox.Add(Raw("o-1"));
        await outbox.Add(Raw("b-1", RecordOutboxKinds.BreakGlass));
        var release = new TaskCompletionSource();
        na.During = ct => release.Task.WaitAsync(ct);
        using var gm = Client(na, outbox);
        using var cts = new CancellationTokenSource();
        var cancelled = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true }, cts.Token);
        var joiner = gm.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        na.During = null;
        release.SetResult();
        // The joiner started a run of its own and admitted both records.
        Assert.Equal(2, (await joiner).Admitted.Count);

        // Cancelled while a batch is in flight, too.
        await outbox.Add(Raw("o-2"));
        using var batchCts = new CancellationTokenSource();
        var hold = new TaskCompletionSource();
        na.During = ct => hold.Task.WaitAsync(ct);
        var inBatch = gm.EvidenceStore.FlushRecords(null, batchCts.Token);
        await batchCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inBatch);
        Assert.Equal(0, Assert.Single(await outbox.List()).Attempts);
    }
}
