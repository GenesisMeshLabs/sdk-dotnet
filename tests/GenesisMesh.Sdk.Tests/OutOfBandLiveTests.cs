using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Sdk.Tests;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Changes outside the governed path against a running NA with its out-of-band records on (1.9.0): observations and a
/// break-glass record signed here, submitted, judged at admission and read back as the resource's changes; a refusal
/// kept as a quarantine entry; a holder change proposed by one holder and approved by another; records kept in a
/// record outbox while the NA cannot be reached, among them a governed action's break-glass record. Each typed result
/// is compared with the JSON the NA sent, at every depth, and the records verify offline.
/// </summary>
[Trait("Category", "Live")]
public class OutOfBandLiveTests
{
    private sealed class Recorder : DelegatingHandler
    {
        public JsonNode? Json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            try { Json = JsonNode.Parse(text); } catch (JsonException) { Json = null; }
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    /// <summary>The NA's record routes and its evaluation cannot be reached while <see cref="Offline"/> is set.</summary>
    private sealed class Outage : DelegatingHandler
    {
        public bool Offline { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Offline && request.RequestUri!.AbsolutePath is "/evidence/observations" or "/evidence/break-glass" or "/admin/boundary/evaluate"
                ? throw new HttpRequestException("connection refused")
                : base.SendAsync(request, ct);
    }

    private static (string Seed, string PublicKey) NewKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Auth.SerializerOptions);

    [Fact]
    public async Task ChangesOutsideTheGovernedPathAreRecordedAndJudged()
    {
        using var na = await LiveNa.Start("--out-of-band", "on");
        if (na is null) return;
        var inputs = na.Inputs;
        var naKey = na.NaPublicKey;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        GenesisMeshClient Operator(string seed, string keyId, HttpMessageHandler? handler = null) => new(new ClientOptions
        {
            BaseUrl = na.BaseUrl,
            SigningKey = seed,
            KeyId = keyId,
            HttpHandler = handler ?? recorder,
        });
        using var ops = Operator((string)inputs["seed"]!, "ops");
        using var second = Operator((string)inputs["secondSeed"]!, "ops2", new HttpClientHandler());
        using var controller = Operator((string)inputs["controllerSeed"]!, "controller", new HttpClientHandler());

        // The NA's response to the call just made (or the part of it at `field`), against its typed result: every key
        // at every depth is modeled, and every key a model requires was sent.
        void Check(string name, object typed, string? field = null)
        {
            var raw = recorder.Json ?? throw new InvalidOperationException(name + ": no JSON response");
            var problems = ContractTypesTests.Unmodeled(field is null ? raw : raw[field], typed.GetType(), name);
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }

        var status = await ops.EvidenceStore.Status();
        Check("evidence_status_out_of_band", status);
        Assert.NotNull(status.PolicyHistoryStarted);

        var (observerSeed, observerKey) = NewKey();
        var (executorSeed, executorKey) = NewKey();
        await ops.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
        {
            KeyId = "observer-key",
            PublicKey = observerKey,
            ExecutorSovereignId = "observer-a",
            Role = "observer",
            ResourcePrefix = "partner:",
        });
        await ops.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
        {
            KeyId = "executor-key",
            PublicKey = executorKey,
            ExecutorSovereignId = "executor-a",
        });

        // An observation: recorded, judged at admission, and the same record again a duplicate.
        var now = DateTimeOffset.UtcNow;
        var observer = new ObservationRecorder("observer-a", new SeedSigner(observerSeed, "observer-key"));
        var observation = await observer.Record(new ObservationInput
        {
            ResourceId = "partner:app-1/secret",
            Action = ResourceActions.Rotate,
            Capability = "partner-secret.rotate",
            ChangedAt = now.AddMinutes(-2),
            Actor = "principal-7f3a",
            Source = "cloud-activity-log",
            SourceEventId = "event-1",
            VersionId = "v2",
            Metadata = new Dictionary<string, object?> { ["vault"] = "kv-a", ["lifetime_days"] = 90 },
        });
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(Json(observation), [observerKey]));
        var submitted = await controller.EvidenceStore.SubmitObservation(observation);
        Assert.Equal("recorded", submitted.Status);
        var judgement = submitted.Judgement!;
        Assert.Equal(("judged", "observation", observation.ObservationId), (judgement.Status, judgement.Payload.SubjectKind, judgement.Payload.SubjectId));
        Assert.Equal("after_the_fact", judgement.Payload.GovernedBy);
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(Json(judgement.Payload), [naKey]));
        // The judgement names its subject as stored: the entry's payload digest and position.
        Assert.Equal((submitted.Entry.PayloadDigest, submitted.Entry.StoreSequence),
            (judgement.Payload.SubjectDigest, judgement.Payload.SubjectStoreSequence));
        var again = await ops.EvidenceStore.SubmitObservation(observation);
        Check("observation_duplicate", again);
        Assert.Equal(("duplicate", submitted.Entry.StoreSequence), (again.Status, again.Entry.StoreSequence));
        Assert.Null(again.Judgement);

        // A batch: one recorded, one refused for good (outside the key's prefix) and kept as a quarantine entry.
        var later = await observer.Record(new ObservationInput
        {
            ResourceId = "partner:app-1/secret",
            Action = ResourceActions.Update,
            Capability = "partner-secret.update",
            ChangedNotBefore = now.AddMinutes(-5),
            ChangedNotAfter = now.AddMinutes(-1),
            ObservedAt = now,
            Source = "reconciliation",
            SourceEventId = "scan-1",
        });
        var outside = await observer.Record(new ObservationInput
        {
            ResourceId = "other:app-9/secret",
            Action = ResourceActions.Delete,
            Capability = "other.delete",
            ChangedAt = now.AddMinutes(-1),
            Source = "cloud-activity-log",
            SourceEventId = "event-9",
        });
        var batch = await ops.EvidenceStore.SubmitObservations([later, outside]);
        Check("observation_batch", batch.ToList(), "results");
        Assert.Equal(("recorded", "refused"), (batch[0].Status, batch[1].Status));
        Assert.Equal("observation_out_of_scope", batch[1].Error!.Code);
        var quarantineId = batch[1].Error!.Details.GetProperty("quarantine_id").GetString();
        Assert.False(string.IsNullOrEmpty(quarantineId));

        // Judged once: judging again returns the judgement made at admission.
        var existing = await ops.EvidenceStore.JudgeObservation(observation.ObservationId);
        Check("observation_judgement", existing);
        Assert.Equal(("existing", judgement.Payload.JudgementId), (existing.Status, existing.Payload.JudgementId));
        Assert.Equal("judgement_subject_not_found",
            (await Assert.ThrowsAsync<NotFoundException>(() => ops.EvidenceStore.JudgeObservation("no-such-observation"))).Code);

        // A break-glass record, signed by the executor key while evaluation failed.
        var executor = new ExecutionRecorder("executor-a", new SeedSigner(executorSeed, "executor-key"));
        var glass = await executor.SignBreakGlass(new BreakGlassInput
        {
            ResourceId = "partner:app-2/secret",
            ResourceAction = ResourceActions.Rotate,
            Capability = "partner-secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["app_id"] = "app-2" },
            Justification = "NA unreachable during an incident; rotating the leaked secret",
            EvaluationRequest = new Dictionary<string, object?> { ["requested_capability"] = "partner-secret.rotate" },
            EvaluationFailure = EvaluationFailures.NetworkError,
            ExecutedAt = now.AddSeconds(-30),
            Outcome = ExecutionOutcomes.Success,
            ExecutionParameters = new Dictionary<string, object?> { ["secret_version"] = "v3" },
        });
        var glassSubmitted = await ops.EvidenceStore.SubmitBreakGlass(glass);
        Check("break_glass_submission", glassSubmitted);
        Assert.Equal(("recorded", "break_glass"), (glassSubmitted.Status, glassSubmitted.Judgement!.Payload.SubjectKind));
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(glassSubmitted.Payload.GetRawText(), [executorKey]));
        Assert.Equal("existing", (await ops.EvidenceStore.JudgeBreakGlass(glass.BreakGlassId)).Status);
        Assert.Equal("duplicate", (await ops.EvidenceStore.SubmitBreakGlass(glass)).Status);

        // The resource's changes, a page and all of them; the quarantine entry on its own resource.
        var changes = await ops.EvidenceStore.ResourceChanges("partner:app-1/secret");
        Check("resource_changes", changes);
        Assert.Equal(["observation", "observation"], changes.Changes.Select(c => c.Kind));
        Assert.All(changes.Changes, c => Assert.NotNull(c.GovernedBy));
        var paged = new List<ResourceChange>();
        await foreach (var c in ops.EvidenceStore.AllResourceChanges("partner:app-1/secret", limit: 1)) paged.Add(c);
        Assert.Equal(changes.Changes.Select(c => c.StoreSequence), paged.Select(c => c.StoreSequence));
        var quarantined = await ops.EvidenceStore.ResourceChanges("other:app-9/secret");
        Check("resource_changes_quarantine", quarantined);
        Assert.Equal(("quarantine", "quarantined", "observation_out_of_scope", quarantineId),
            (quarantined.Changes[0].Kind, quarantined.Changes[0].State, quarantined.Changes[0].RejectionCode, quarantined.Changes[0].RecordId));
        var glassChanges = await ops.EvidenceStore.ResourceChanges("partner:app-2/secret");
        Assert.Equal(("break_glass", glass.Justification), (glassChanges.Changes[0].Kind, glassChanges.Changes[0].Justification));

        // Holders: the controller key is its own holder until a holder change, proposed by one holder and approved by
        // another, names one. One holder cannot approve its own proposal.
        var holders = await ops.EvidenceStore.OperatorHolders();
        Check("operator_holders", holders.ToList(), "holders");
        Assert.Equal(("platform-team", "security-team", "controller"),
            (holders.Single(h => h.KeyId == "ops").Holder, holders.Single(h => h.KeyId == "ops2").Holder, holders.Single(h => h.KeyId == "controller").Holder));
        var proposal = await ops.EvidenceStore.ProposeHolder("controller", "controller-team");
        Check("holder_proposal", proposal);
        Assert.Equal(("controller", "controller-team", "ops", false), (proposal.KeyId, proposal.Holder, proposal.ProposedBy, proposal.Approved));
        Assert.Equal("holder_change_needs_second_holder",
            (await Assert.ThrowsAsync<ConflictException>(() => ops.EvidenceStore.ApproveHolder(proposal.ProposalId))).Code);
        var approval = await second.EvidenceStore.ApproveHolder(proposal.ProposalId);
        Assert.Equal((true, "ops2"), (approval.Approved, approval.ApprovedBy));
        Assert.False(string.IsNullOrEmpty(approval.RegistryRecordId));
        Assert.Equal("controller-team", (await ops.EvidenceStore.OperatorHolders()).Single(h => h.KeyId == "controller").Holder);
        Assert.Equal("holder_change_already_approved",
            (await Assert.ThrowsAsync<ConflictException>(() => second.EvidenceStore.ApproveHolder(proposal.ProposalId))).Code);
        Assert.Equal("unknown_operator_key",
            (await Assert.ThrowsAsync<NotFoundException>(() => ops.EvidenceStore.ProposeHolder("no-such-key", "x"))).Code);
        Assert.Equal("insufficient_operator_tier",
            (await Assert.ThrowsAsync<ForbiddenException>(() => controller.EvidenceStore.ProposeHolder("controller", "x"))).Code);

        // The record outbox: an observation and break-glass records kept in files while the NA cannot be reached, one of
        // them a governed action's that ran when its evaluation failed, and submitted by a restarted controller once it
        // is back, the break-glass records first. A record refused for good is a dead letter that names its quarantine
        // entry.
        var directory = Path.Combine(Path.GetTempPath(), "gm-live-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            var outage = new Outage { InnerHandler = new HttpClientHandler() };
            using (var down = new GenesisMeshClient(new ClientOptions
            {
                BaseUrl = na.BaseUrl,
                SigningKey = (string)inputs["controllerSeed"]!,
                KeyId = "controller",
                HttpHandler = outage,
                Retry = new RetrySettings { Attempts = 0 },
                RecordOutbox = new FileRecordOutbox(directory),
            }))
            {
                var kept = await observer.Record(new ObservationInput
                {
                    ResourceId = "partner:app-3/secret",
                    Action = ResourceActions.Rotate,
                    Capability = "partner-secret.rotate",
                    ChangedAt = now.AddSeconds(-20),
                    Source = "cloud-activity-log",
                    SourceEventId = "event-3",
                });
                var keptGlass = await executor.SignBreakGlass(new BreakGlassInput
                {
                    ResourceId = "partner:app-3/secret",
                    ResourceAction = ResourceActions.Rotate,
                    Capability = "partner-secret.rotate",
                    Justification = "NA unreachable during an incident drill",
                    EvaluationRequest = new Dictionary<string, object?> { ["requested_capability"] = "partner-secret.rotate" },
                    EvaluationFailure = EvaluationFailures.NetworkError,
                    ExecutedAt = now.AddSeconds(-25),
                    Outcome = ExecutionOutcomes.Success,
                });
                var queued = (await down.EvidenceStore.EnqueueRecord(kept)).Queued!;
                Assert.Equal((OutboxStates.Pending, EvidenceOutbox.NetworkError), (queued.State, queued.LastError!.Code));
                Assert.Equal(OutboxStates.Pending, (await down.EvidenceStore.EnqueueRecord(keptGlass)).Queued!.State);

                var outcome = await Governance.GovernedAction(down, executor, new GovernedActionRequest
                {
                    RequestedCapability = "partner-secret.rotate",
                    AttestationId = "att-incident-drill",
                    Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["app_id"] = "app-4" } },
                    ResourceId = "partner:app-4/secret",
                    ResourceAction = ResourceActions.Rotate,
                    Verify = new GovernedVerification { OperatorPublicKeys = [naKey], ExpectedPolicies = [] },
                }, new BreakGlassOptions { Justification = "NA unreachable during an incident drill; rotating app-4 now" }, (decision, _) =>
                {
                    Assert.Null(decision);
                    return Task.FromResult<ActionReport<string>?>(new ActionReport<string>
                    {
                        Value = "rotated",
                        ExecutionParameters = new Dictionary<string, object?> { ["secret_version"] = "v5" },
                    });
                });
                var brokeGlass = outcome.BreakGlass!;
                Assert.Equal((EvaluationFailures.NetworkError, "rotated", OutboxStates.Pending),
                    (brokeGlass.Failure, brokeGlass.Value, brokeGlass.Queued!.State));
                Assert.True(OfflineVerifier.VerifyOutOfBandRecord(Json(brokeGlass.Record), [executorKey]));

                using var restarted = new GenesisMeshClient(new ClientOptions { BaseUrl = na.BaseUrl, RecordOutbox = new FileRecordOutbox(directory) });
                var flushed = await restarted.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = true });
                Assert.Equal([keptGlass.BreakGlassId, brokeGlass.Record.BreakGlassId, kept.ObservationId], flushed.Admitted.Select(e => e.Id));
                Assert.Empty(flushed.Pending);
                Assert.Empty(await restarted.EvidenceStore.RecordOutbox!.List());
                Assert.Equal(["break_glass", "observation"], (await ops.EvidenceStore.ResourceChanges("partner:app-3/secret")).Changes.Select(c => c.Kind));
                var governedGlass = Assert.Single((await ops.EvidenceStore.ResourceChanges("partner:app-4/secret")).Changes);
                Assert.Equal(("break_glass", brokeGlass.Record.Justification, brokeGlass.Record.BreakGlassId),
                    (governedGlass.Kind, governedGlass.Justification, governedGlass.RecordId));

                var elsewhere = await observer.Record(new ObservationInput
                {
                    ResourceId = "other:app-8/secret",
                    Action = ResourceActions.Delete,
                    Capability = "other.delete",
                    ChangedAt = now.AddSeconds(-10),
                    Source = "cloud-activity-log",
                    SourceEventId = "event-8",
                });
                var dead = (await restarted.EvidenceStore.EnqueueRecord(elsewhere)).Queued!;
                Assert.Equal((OutboxStates.DeadLetter, "observation_out_of_scope"), (dead.State, dead.LastError!.Code));
                Assert.False(string.IsNullOrEmpty(dead.LastError.QuarantineId));
                Assert.Equal(dead.Id, Assert.Single(await new FileRecordOutbox(directory).List()).Id);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        // Offline: every out-of-band record of the export verifies under its signer's key.
        var signers = new Dictionary<string, string[]>
        {
            ["observation"] = [observerKey],
            ["break_glass"] = [executorKey],
            ["judgement"] = [naKey],
            ["quarantine"] = [naKey],
            ["registry"] = [naKey],
        };
        var kinds = new HashSet<string>();
        var events = new List<EvidenceEvent>();
        await foreach (var e in ops.EvidenceStore.ExportAll())
        {
            events.Add(e);
            if (!signers.TryGetValue(e.Entry.EntryKind, out var keys)) continue;
            kinds.Add(e.Entry.EntryKind);
            Assert.True(OfflineVerifier.VerifyOutOfBandRecord(e.Payload.GetRawText(), keys), $"{e.Entry.EntryKind} {e.Entry.StoreSequence}");
            var typed = e.Entry.EntryKind switch
            {
                "observation" => (object)e.Payload.Deserialize<ObservationRecord>(Auth.SerializerOptions)!,
                "break_glass" => e.Payload.Deserialize<BreakGlassRecord>(Auth.SerializerOptions)!,
                "judgement" => e.Payload.Deserialize<JudgementRecord>(Auth.SerializerOptions)!,
                "quarantine" => e.Payload.Deserialize<QuarantineRecord>(Auth.SerializerOptions)!,
                _ => e.Payload.Deserialize<RegistryRecord>(Auth.SerializerOptions)!,
            };
            // A record read from the store and passed back keeps the form its signer signed.
            Assert.Equal(OfflineVerifier.OutOfBandCanonical(e.Payload.GetRawText()), OfflineVerifier.OutOfBandCanonical(Json(typed)));
            Assert.True(ContractTypesTests.Unmodeled(JsonNode.Parse(e.Payload.GetRawText()), typed.GetType(), e.Entry.EntryKind).Count == 0);
        }
        Assert.Equal(signers.Keys.Order(), kinds.Order());
        var server = await ops.EvidenceStore.Verify();
        Assert.True(server.Verified);
        // The whole export verifies offline too, as the NA verifies it: every entry, and as many of each kind.
        var offline = OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
        {
            NAPublicKeys = [naKey],
            ExecutorKeys = await ops.EvidenceStore.ListExecutorKeys(),
        });
        Assert.True(offline.Verified, string.Join("; ", offline.Failures.Select(f => $"{f.StoreSequence} {f.Reason} {f.Detail}")));
        Assert.Equal((server.CheckedEntries, server.Observations, server.BreakGlass, server.Judgements, server.Quarantined),
            (offline.CheckedEntries, offline.Observations, offline.BreakGlass, offline.Judgements, offline.Quarantined));
    }
}
