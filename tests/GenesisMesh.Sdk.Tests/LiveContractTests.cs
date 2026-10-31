using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Live contract check: every client method against a running Network Authority, each
/// typed result compared with the JSON the NA sent. Runs when GM_E2E_PYTHON names a Python
/// with the genesis-mesh core installed (<see cref="LiveNa"/> starts a disposable NA):
/// <code>GM_E2E_PYTHON=python dotnet test --filter Category=Live</code>
/// CI runs it in its own job, against the core branch of the same name or main, with
/// <c>GM_E2E_REQUIRED=1</c>: then a run without <c>GM_E2E_PYTHON</c> fails rather than passes.
/// </summary>
[Trait("Category", "Live")]
public class LiveContractTests
{
    private sealed class Recorder : DelegatingHandler
    {
        public JsonObject? Json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/sovereign.json") return response;
            var text = await response.Content.ReadAsStringAsync(ct);
            try { Json = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { Json = null; }
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    [Fact]
    public async Task EveryCallReturnsWhatTheNASent()
    {
        var na = await LiveNa.Start();
        if (na is null) return;
        try
        {
            var inputs = na.Inputs;
            string baseUrl = (string)inputs["baseUrl"]!, seed = (string)inputs["seed"]!, keyId = (string)inputs["keyId"]!;
            string naKey = (string)inputs["naPublicKey"]!, network = (string)inputs["networkName"]!;
            var proofInput = JsonSerializer.Deserialize<object>(inputs["justificationProof"]!.ToJsonString());

            var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
            using var gm = new GenesisMeshClient(new ClientOptions
            {
                BaseUrl = baseUrl,
                SigningKey = seed,
                KeyId = keyId,
                HttpHandler = recorder,
            });
            var now = DateTimeOffset.UtcNow;
            string Iso(TimeSpan d) => now.Add(d).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);
            static Dictionary<string, object?> D(params (string, object?)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);
            object? Raw(JsonObject response) => JsonSerializer.Deserialize<object>(response.ToJsonString());

            // The NA's response to the call just made, checked against the typed result: every
            // key the NA sent is modeled, and every required property was sent.
            JsonObject Check<T>(string name, T typed)
            {
                var raw = recorder.Json ?? throw new InvalidOperationException(name + ": no JSON response");
                if (typed is null) return raw;
                var declared = ContractTypesTests.JsonFields(typed.GetType());
                Assert.True(raw.All(k => declared.ContainsKey(k.Key)),
                    $"{name}: unmodeled keys {string.Join(", ", raw.Select(k => k.Key).Where(k => !declared.ContainsKey(k)))}");
                if (typed is not VerifyResult)
                {
                    var missing = declared.Where(f => f.Value && !raw.ContainsKey(f.Key)).Select(f => f.Key).ToList();
                    Assert.True(missing.Count == 0, $"{name}: declared but not sent: {string.Join(", ", missing)}");
                }
                return raw;
            }
            void Accepted(string name, VerifyResult result) =>
                Assert.True(result.Valid && result.Accepted, $"{name}: the NA answered {recorder.Json?.ToJsonString()}");

            var offer = await gm.Agreement.Offer(new CapabilityOffer
            {
                ResponderSovereignId = "sovereign-b",
                Capabilities = ["read", "write"],
                ValidFrom = Iso(TimeSpan.Zero),
                ValidUntil = Iso(TimeSpan.FromHours(24)),
                ExpiresAt = Iso(TimeSpan.FromHours(1)),
            });
            var rawOffer = Check("agreement_offer", offer);
            var counter = await gm.Agreement.Counter(D(("offer", Raw(rawOffer)), ("capabilities", new[] { "read" }),
                ("scope", new Dictionary<string, object?>()), ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(12)))));
            var rawCounter = Check("agreement_counter", counter);
            var counterAgreement = Check("agreement_accept_counter", await gm.Agreement.AcceptCounter(Raw(rawCounter)!, Raw(rawOffer)!));
            Assert.Equal(new JsonArray("read").ToJsonString(), counterAgreement["agreed_terms"]!["capabilities"]!.ToJsonString());
            var rawAgreement = Check("agreement_accept", await gm.Agreement.Accept(offer));
            var agreementCheck = await gm.Agreement.Verify(D(("agreement", Raw(rawAgreement))));
            Check("agreement_verify", agreementCheck);
            Accepted("agreement_verify", agreementCheck);

            var decision = await gm.Boundary.Decide(D(("agreement", Raw(rawAgreement)), ("requested_capability", "read")));
            var rawDecision = Check("boundary_decide", decision);
            Assert.Equal((bool)rawDecision["authorized"]!, decision.Authorized);
            var decisionCheck = await gm.Boundary.Verify(D(("decision", Raw(rawDecision))));
            Check("boundary_verify", decisionCheck);
            Accepted("boundary_verify", decisionCheck);

            var evidence = await gm.Evidence.Build(new TrustDecision
            {
                SourceSovereignId = network,
                TargetSovereignId = "sovereign-b",
                Verdict = "allow",
                Reason = "direct recognition",
                Trusted = true,
                HopCount = 1,
            });
            var rawEvidence = Check("evidence_build", evidence);
            Assert.True(evidence.Trusted);
            Assert.Equal(1, evidence.HopCount);
            var evidenceCheck = await gm.Evidence.Verify(D(("evidence", Raw(rawEvidence))));
            Check("evidence_verify", evidenceCheck);
            Accepted("evidence_verify", evidenceCheck);

            var rawCommitment = Check("disclosure_commit",
                await gm.Disclosure.Commit(D(("capabilities", new[] { "read", "write" }), ("agreement", Raw(rawAgreement)))));
            var rawProof = Check("disclosure_prove", await gm.Disclosure.Prove(D(("capability", "read"),
                ("capabilities", new[] { "read", "write" }), ("commitment", Raw(rawCommitment)), ("prover_sovereign_id", "sovereign-b"))));
            var proofCheck = await gm.Disclosure.Verify(D(("proof", Raw(rawProof)), ("commitment", Raw(rawCommitment))));
            Check("disclosure_verify", proofCheck);
            Accepted("disclosure_verify", proofCheck);

            var policy = await gm.DataUsage.CreatePolicy(D(("licensee_sovereign_id", "sovereign-b"),
                ("allowed_source_ids", new[] { "src-1" }), ("allowed_access_types", new[] { "read" }),
                ("valid_from", Iso(TimeSpan.Zero)), ("valid_until", Iso(TimeSpan.FromHours(720)))));
            var rawPolicy = Check("data_usage_policy", policy);
            Check("data_usage_get_policy", await gm.DataUsage.GetPolicy());
            var intent = await gm.DataUsage.CreateIntent(D(("sources", new[] { D(("source_id", "src-1"),
                ("source_type", "public"), ("owner_sovereign_id", network), ("classification_tags", Array.Empty<string>())) }),
                ("access_types", new[] { "read" }), ("decision_id", "dec-001")));
            var rawIntent = Check("data_usage_intent", intent);
            var usageCheck = await gm.DataUsage.Verify(D(("intent", Raw(rawIntent)), ("policy", Raw(rawPolicy))));
            Check("data_usage_verify", usageCheck);
            Accepted("data_usage_verify", usageCheck);
            // 1.8.0: the typed records, passed back as they are, keep the form the NA signed.
            Accepted("data_usage_verify_typed", await gm.DataUsage.Verify(D(("intent", intent), ("policy", policy))));

            var rawVote = Check("consensus_vote",
                await gm.Consensus.Vote(D(("justification_proof", proofInput), ("vote", true), ("reason", "contract"))));
            var rawConsensus = Check("consensus_proof", await gm.Consensus.Proof(D(("justification_proof", proofInput),
                ("votes", new[] { Raw(rawVote) }), ("required_threshold", 1), ("validator_sovereign_ids", new[] { network }))));
            var consensusCheck = await gm.Consensus.Verify(D(("proof", Raw(rawConsensus)),
                ("validator_public_keys", new Dictionary<string, object?> { [network] = naKey })));
            Check("consensus_verify", consensusCheck);
            Assert.True(consensusCheck.Valid);

            var attestation = await gm.Attestation.Issue(D(("subject_id", "vendor-contract"), ("roles", new[] { "role:client" })));
            Check("attestation_issue", attestation);
            Assert.Equal("vendor-contract", attestation.SubjectId);

            Check("health_liveness", await gm.Health.Liveness());
            var readiness = await gm.Health.Readiness();
            Check("health_readiness", readiness);
            Assert.True(readiness.Ready);
            Check("health_status", await gm.Health.Status());
            Assert.True((await gm.Health.Endpoints()).Single() is { Reachable: true, Ready: true });

            Check("na_key_history", await gm.NA.KeyHistory());
            var history = await gm.NA.VerifiedKeyHistory(naKey);
            Assert.Equal(naKey, history.Keys.Single().PublicKey);

            // 1.9.0: the policy lifecycle and policy-aware evaluation.
            var policyIntent = new BoundaryPolicyIntent
            {
                PolicyId = "contract-policy",
                Description = "contract",
                ValidFrom = Iso(TimeSpan.FromMinutes(-5)),
                ValidUntil = Iso(TimeSpan.FromHours(24)),
                Selector = new PolicySelector { ParentKinds = ["attestation"], Capabilities = ["contract.*"] },
                Gates =
                [
                    new GateSpec
                    {
                        GateId = "max-days",
                        GateType = "max_value.v1",
                        Order = 0,
                        Config = new Dictionary<string, object?> { ["path"] = "request_parameters.days", ["max"] = 90 },
                    },
                ],
            };
            Assert.True(Check("policy_validate", await gm.Policy.Validate(policyIntent))["valid"]!.GetValue<bool>());
            var boundaryPolicy = await gm.Policy.Publish(policyIntent);
            Check("policy_publish", boundaryPolicy);
            Check("policy_activate", await gm.Policy.Activate("contract-policy", boundaryPolicy.Version));
            var listed = await gm.Policy.List();
            var listedFields = ContractTypesTests.JsonFields(typeof(PolicyVersionSummary));
            Assert.All(recorder.Json!["policies"]!.AsArray(), item =>
                Assert.True(item!.AsObject().All(k => listedFields.ContainsKey(k.Key)), "policy_list: unmodeled keys"));
            Assert.Contains(listed, v => v.PolicyId == "contract-policy" && v.Active);
            Check("policy_active", await gm.Policy.Active());
            Check("policy_history", await gm.Policy.History("contract-policy"));
            var policyCheck = await gm.Policy.Verify(boundaryPolicy);
            Check("policy_verify", policyCheck);
            Assert.True(policyCheck.Valid, recorder.Json?.ToJsonString());

            var vendor = await gm.Attestation.Issue(D(("subject_id", "vendor-evaluate"), ("roles", new[] { "role:client" }),
                ("claims", D(("capabilities", new[] { "contract.read" }))), ("validity_hours", 24)));
            EvaluateRequest Evaluation(int days) => new()
            {
                AttestationId = vendor.AttestationId,
                RequestedCapability = "contract.read",
                Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["days"] = days } },
            };
            var allowed = await gm.Boundary.Evaluate(Evaluation(30));
            Check("boundary_evaluate", allowed);
            Assert.True(allowed.Decision.Authorized, recorder.Json?.ToJsonString());
            var denied = await gm.Boundary.Evaluate(Evaluation(120));
            Assert.False(denied.Decision.Authorized);
            // The typed decisions, passed back as they are, keep the form the NA signed (1.9.0: denial_reason null).
            Accepted("boundary_verify_typed_allowed", await gm.Boundary.Verify(D(("decision", allowed.Decision))));
            Accepted("boundary_verify_typed_denied", await gm.Boundary.Verify(D(("decision", denied.Decision))));
            await Assert.ThrowsAsync<SecretMaterialException>(() => gm.Boundary.Evaluate(new EvaluateRequest
            {
                AttestationId = vendor.AttestationId,
                RequestedCapability = "contract.read",
                Context = new EvaluationContext { Attributes = new Dictionary<string, object?> { ["password"] = "x" } },
            }));
            // 1.9.0: the justification proof verifies offline under the NA's key, and under its key history.
            Assert.True(OfflineVerifier.VerifyJustificationSignature(allowed.JustificationProof.GetRawText(), [naKey]));
            Assert.True(OfflineVerifier.VerifyJustificationSignature(allowed.JustificationProof.GetRawText(), history));

            // 1.9.0: execution evidence a controller submits, with an executor key limited to "contract:".
            using var executorKey = Key.Create(SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var executorPublicKey = Convert.ToBase64String(executorKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
            var executorState = await gm.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
            {
                KeyId = "contract-executor",
                PublicKey = executorPublicKey,
                ExecutorSovereignId = "contract-executor",
                ResourcePrefix = "contract:",
            });
            Check("evidence_register_executor_key", executorState);
            Assert.Equal(("contract:", true), (executorState.ResourcePrefix, executorState.Active));
            var executor = new ExecutionRecorder("contract-executor",
                new SeedSigner(Convert.ToBase64String(executorKey.Export(KeyBlobFormat.RawPrivateKey)), "contract-executor"));
            const string resource = "contract:app-1/secret";
            Assert.Null(await gm.EvidenceStore.ResourceHead(resource));
            RecordExecutionRequest Execution(BoundaryDecision decision, string resourceId, string action, ResourceHead? head) => new()
            {
                Decision = decision,
                ExecutedCapability = "contract.read",
                Outcome = ExecutionOutcomes.Success,
                ExecutionParameters = new Dictionary<string, object?> { ["app_id"] = "app-1", ["lifetime_days"] = 30 },
                ResourceId = resourceId,
                ResourceAction = action,
                PriorResource = head,
            };
            var created = await executor.Record(Execution(allowed.Decision, resource, ResourceActions.Create, null));
            var submission = await gm.EvidenceStore.Submit(created);
            Check("evidence_submit", submission);
            Assert.Equal("recorded", submission.Status);
            Assert.Equal("duplicate", (await gm.EvidenceStore.Submit(created)).Status);
            var storedHead = await gm.EvidenceStore.StoredResourceHead(resource);
            Check("evidence_resource_head", storedHead);
            Assert.Equal((1L, ExecutionRecorder.Digest(created), "execution", created.EvidenceId),
                (storedHead.ResourceSequence, storedHead.RecordDigest, storedHead.EntryKind, storedHead.RecordId));

            var rotateDecision = (await gm.Boundary.Evaluate(Evaluation(60))).Decision;
            var rotated = await executor.Record(Execution(rotateDecision, resource, ResourceActions.Rotate,
                await gm.EvidenceStore.ResourceHead(resource)));
            Assert.Equal("recorded", (await gm.EvidenceStore.Submit(rotated)).Status);
            Assert.Equal(2, rotated.ResourceSequence);

            // A record built on a head read before the rotation was stored: its position is taken (409).
            var staleDecision = (await gm.Boundary.Evaluate(Evaluation(45))).Decision;
            var stale = await Assert.ThrowsAsync<ConflictException>(async () => await gm.EvidenceStore.Submit(
                await executor.Record(Execution(staleDecision, resource, ResourceActions.Rotate, ExecutionRecorder.HeadOf(created)))));
            Assert.Equal("evidence_conflict", stale.Code);

            // Refused by the NA: a resource outside the key's prefix, and a denied decision.
            var outside = await Assert.ThrowsAsync<ValidationException>(async () => await gm.EvidenceStore.Submit(
                await executor.Record(Execution(allowed.Decision, "other:app-2/secret", ResourceActions.Create, null))));
            Assert.Equal("evidence_out_of_scope", outside.Code);
            var underDenial = await Assert.ThrowsAsync<ValidationException>(async () => await gm.EvidenceStore.Submit(
                await executor.Record(Execution(denied.Decision, "contract:app-3/secret", ResourceActions.Create, null))));
            Assert.Equal("evidence_decision_denied", underDenial.Code);
            await Assert.ThrowsAsync<SecretMaterialException>(() => executor.Record(new RecordExecutionRequest
            {
                Decision = allowed.Decision,
                ExecutedCapability = "contract.read",
                Outcome = ExecutionOutcomes.Success,
                ExecutionParameters = new Dictionary<string, object?> { ["client_secret"] = "x" },
            }));

            var stored = await gm.EvidenceStore.Search(new EvidenceSearchParams { ResourceId = resource, EntryKind = "execution" });
            Check("evidence_search", stored);
            var eventFields = ContractTypesTests.JsonFields(typeof(EvidenceEvent));
            var entryFields = ContractTypesTests.JsonFields(typeof(EvidenceStoreEntry));
            Assert.All(recorder.Json!["entries"]!.AsArray(), item =>
            {
                Assert.True(item!.AsObject().All(k => eventFields.ContainsKey(k.Key)), "evidence_search: unmodeled event keys");
                Assert.True(item["entry"]!.AsObject().All(k => entryFields.ContainsKey(k.Key)), "evidence_search: unmodeled entry keys");
            });
            Assert.Equal([created.EvidenceId, rotated.EvidenceId], stored.Entries.Select(e => e.Entry.EvidenceId));
            Assert.All(stored.Entries, e => Assert.True(OfflineVerifier.VerifyExecutionSignature(e.Payload.GetRawText(), executorPublicKey)));
            Assert.Equal(ExecutionRecorder.Digest(rotated), OfflineVerifier.ExecutionDigest(stored.Entries[1].Payload.GetRawText()));
            var state = (await gm.EvidenceStore.ResourceStates())[resource];
            Assert.Equal((2L, ExecutionRecorder.Digest(rotated), "rotate", "success", rotateDecision.DecisionId),
                (state.ResourceSequence, state.RecordDigest, state.LastAction, state.LastOutcome, state.LastDecisionId));

            // 1.9.0: governed actions through a file outbox. The decision is verified against the published policy and
            // the attestation as typed, so their typed forms must keep the digests the NA bound.
            var outboxDir = Path.Combine(Path.GetTempPath(), "gm-contract-outbox-" + Guid.NewGuid().ToString("N"));
            try
            {
                GenesisMeshClient Governed(string url) => new(new ClientOptions
                {
                    BaseUrl = url,
                    SigningKey = seed,
                    KeyId = keyId,
                    Audience = naKey,
                    Outbox = new FileOutbox(outboxDir),
                });
                using var governed = Governed(baseUrl);
                var verify = new GovernedVerification
                {
                    OperatorPublicKeys = [naKey],
                    ExpectedPolicies = [boundaryPolicy],
                    ExpectedAttestation = vendor,
                };
                GovernedActionRequest Change(int days, string action) => new()
                {
                    AttestationId = vendor.AttestationId,
                    RequestedCapability = "contract.read",
                    Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["days"] = days } },
                    ResourceId = "contract:app-4/secret",
                    ResourceAction = action,
                    Verify = verify,
                };
                Func<BoundaryDecision, CancellationToken, Task<ActionReport<string>?>> Issue(string version, params (string, object?)[] more) =>
                    (_, _) => Task.FromResult<ActionReport<string>?>(new ActionReport<string>
                    {
                        Value = version,
                        ExecutionParameters = D([("secret_version", version), .. more]),
                    });

                var made = await Governance.GovernedAction(governed, executor, Change(30, ResourceActions.Create), Issue("v1"));
                Assert.True(made.Authorized);
                Assert.Equal(("recorded", "v1", 1L), (made.Submission!.Status, made.Value, made.Evidence!.ResourceSequence!.Value));
                Assert.Equal(["contract-policy@" + boundaryPolicy.Version.ToString(CultureInfo.InvariantCulture)], made.Summary.AppliedPolicies);
                Assert.Empty(await governed.EvidenceStore.Outbox!.List());

                var ran = 0;
                var refusal = await Governance.GovernedAction<string>(governed, executor, Change(120, ResourceActions.Rotate), (_, _) =>
                {
                    ran++;
                    return Task.FromResult<ActionReport<string>?>(null);
                });
                Assert.False(refusal.Authorized);
                Assert.Equal(0, ran);
                Assert.Contains(refusal.Summary.EnforcedFailures, f => f.Gate == "contract-policy/max-days");

                var rotatedGoverned = await Governance.GovernedAction(governed, executor, Change(60, ResourceActions.Rotate), Issue("v2"));
                Assert.Equal((2L, ExecutionRecorder.Digest(made.Evidence)),
                    (rotatedGoverned.Evidence!.ResourceSequence!.Value, rotatedGoverned.Evidence.PrevResourceDigest));
                // A secret in the report: the change is recorded without it, and the NA admits the record.
                var guarded = await Assert.ThrowsAsync<MetadataRefusedException>(() =>
                    Governance.GovernedAction(governed, executor, Change(45, ResourceActions.Update), Issue("v3", ("client_secret", "x"))));
                Assert.Equal(("recorded", "client_secret"), (guarded.Submission!.Status, guarded.Dropped.Single()));

                // Kept while the NA cannot be reached (nothing listens on port 9), then admitted by a flush from another
                // instance of the outbox, as after a restart.
                var offlineDecision = (await gm.Boundary.Evaluate(Evaluation(30))).Decision;
                var kept = await executor.Record(Execution(offlineDecision, "contract:app-5/secret", ResourceActions.Create, null));
                using (var offline = Governed("http://127.0.0.1:9"))
                {
                    var queued = (await offline.EvidenceStore.Enqueue(kept)).Queued!;
                    Assert.Equal((OutboxStates.Pending, EvidenceOutbox.NetworkError), (queued.State, queued.LastError!.Code));
                }
                using (var restarted = Governed(baseUrl))
                {
                    var flushed = await restarted.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
                    Assert.Equal(kept.EvidenceId, Assert.Single(flushed.Admitted).Id);
                    Assert.Empty(await restarted.EvidenceStore.Outbox!.List());
                }

                var findings = Governance.ReconcileResources(
                [
                    new ObservedResource { ResourceId = "contract:app-4/secret", Exists = true, Version = "v3" },
                    new ObservedResource { ResourceId = "contract:app-5/secret", Exists = false },
                    new ObservedResource { ResourceId = "contract:app-6/secret", Exists = true },
                ], await governed.EvidenceStore.ResourceStates());
                Assert.Equal(new[] { ReconciliationStatuses.InSync, ReconciliationStatuses.Missing, ReconciliationStatuses.Unmanaged },
                    findings.Select(f => f.Status));
            }
            finally
            {
                if (Directory.Exists(outboxDir)) Directory.Delete(outboxDir, recursive: true);
            }

            // 1.9.0: the store's status, the NA's verification of it, histories, the export, executor keys and
            // retention. Each response is checked at every depth: a history holds events and a verification.
            void Modeled<T>(string name, T typed, JsonNode? raw = null)
            {
                if (raw is null) Check(name, typed);
                var problems = ContractTypesTests.Unmodeled(raw ?? recorder.Json, typeof(T), name);
                Assert.True(problems.Count == 0, string.Join("; ", problems));
            }
            static string Failures(EvidenceStoreVerification v) =>
                string.Join("; ", v.Failures.Select(f => $"{f.StoreSequence} {f.Reason} {f.Detail}"));

            var status = await gm.EvidenceStore.Status();
            Modeled("evidence_status", status);
            Assert.Equal(("on", (long?)null), (status.EvidenceStore, status.RetentionCheckpoint));
            Assert.Equal(status.Entries, status.LastStoreSequence);
            var storeCheck = await gm.EvidenceStore.Verify();
            Modeled("evidence_verify_store", storeCheck);
            Assert.True(storeCheck.Verified, Failures(storeCheck));
            Assert.Equal((status.Entries, true), (storeCheck.CheckedEntries, storeCheck.Anchors!.Chain!.Verified));

            var resourceHistory = await gm.EvidenceStore.ResourceHistory(resource);
            Modeled("evidence_resource_history", resourceHistory);
            Assert.True(resourceHistory.Verification.Verified, Failures(resourceHistory.Verification));
            Assert.False(resourceHistory.Truncated);
            Assert.Equal([created.EvidenceId, rotated.EvidenceId],
                resourceHistory.Entries.Where(e => e.Entry.EntryKind == "execution").Select(e => e.Entry.EvidenceId));
            Assert.Contains(resourceHistory.Entries, e => e.Entry.EntryKind == "decision" && e.Entry.DecisionId == rotateDecision.DecisionId);
            var vendorHistory = await gm.EvidenceStore.VendorHistory(vendor.SubjectId);
            Modeled("evidence_vendor_history", vendorHistory);
            Assert.True(vendorHistory.Verification.Verified, Failures(vendorHistory.Verification));
            Assert.Contains(vendorHistory.Entries, e => e.Entry.EntryKind == "decision" && e.Entry.DecisionId == allowed.Decision.DecisionId);
            Assert.Equal("resource_not_found",
                (await Assert.ThrowsAsync<NotFoundException>(() => gm.EvidenceStore.ResourceHistory("contract:none/secret"))).Code);
            Assert.Equal("vendor_not_found", (await Assert.ThrowsAsync<NotFoundException>(() => gm.EvidenceStore.VendorHistory("vendor-none"))).Code);

            // The export, page by page, holds the whole store in order, as search and the first page hold it.
            var exported = new List<EvidenceEvent>();
            await foreach (var e in gm.EvidenceStore.ExportAll(pageSize: 3)) exported.Add(e);
            Assert.Equal(Enumerable.Range(1, (int)status.Entries!.Value).Select(i => (long)i), exported.Select(e => e.Entry.StoreSequence));
            Assert.Equal(stored.Entries.Select(e => e.EntryDigest),
                exported.Where(e => e.Entry.ResourceId == resource && e.Entry.EntryKind == "execution").Select(e => e.EntryDigest));
            var firstPage = await gm.EvidenceStore.ExportText(new EvidenceExportParams { Limit = 2 });
            Assert.Equal(exported.Take(2).Select(e => e.EntryDigest), OfflineVerifier.ParseExportLines(firstPage).Select(e => e.EntryDigest));
            foreach (var line in firstPage.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Modeled("evidence_export_event", OfflineVerifier.ParseExportLines(line)[0], JsonNode.Parse(line));
            Assert.Equal(exported.Skip(2).Select(e => e.EntryDigest),
                (await gm.EvidenceStore.Export(new EvidenceExportParams { SinceSequence = 2 })).Select(e => e.EntryDigest));

            var keys = await gm.EvidenceStore.ListExecutorKeys();
            Modeled("evidence_executor_keys", keys.ToList(), recorder.Json!["executor_keys"]);
            var contractKey = keys.Single(k => k.KeyId == "contract-executor");
            Assert.Equal((executorPublicKey, "contract:", (string?)null, "executor"), (contractKey.PublicKey, contractKey.ResourcePrefix, contractKey.RetiredAt, contractKey.Role));

            // 1.9.0: the export verifies offline as the NA verified the store, under its key or its key history, and
            // the resource's history as a history.
            static (bool, long, long, long) Counts(EvidenceStoreVerification v) => (v.Verified, v.CheckedEntries, v.Decisions, v.Executions);
            var exportCheck = OfflineVerifier.VerifyEvidenceEvents(exported, new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = keys });
            Assert.True(exportCheck.Verified, Failures(exportCheck));
            Assert.Equal(Counts(storeCheck), Counts(exportCheck));
            Assert.Equal(Counts(storeCheck), Counts(OfflineVerifier.VerifyEvidenceEvents(exported,
                new EvidenceVerifyOptions { KeyHistory = history, ExecutorKeys = keys })));
            Assert.Equal(Counts(resourceHistory.Verification), Counts(OfflineVerifier.VerifyEvidenceEvents(resourceHistory.Entries,
                new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = keys, Contiguous = false })));

            // A retired key signs no new record, and the records it signed still verify.
            using var retiringKey = Key.Create(SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            await gm.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
            {
                KeyId = "contract-retired",
                PublicKey = Convert.ToBase64String(retiringKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                ExecutorSovereignId = "contract-executor",
                ResourcePrefix = "contract:",
            });
            var retiring = new ExecutionRecorder("contract-executor",
                new SeedSigner(Convert.ToBase64String(retiringKey.Export(KeyBlobFormat.RawPrivateKey)), "contract-retired"));
            Assert.Equal("recorded", (await gm.EvidenceStore.Submit(await retiring.Record(Execution(
                (await gm.Boundary.Evaluate(Evaluation(30))).Decision, "contract:app-7/secret", ResourceActions.Create, null)))).Status);
            var retired = await gm.EvidenceStore.RetireExecutorKey("contract-retired");
            Modeled("evidence_retire_executor_key", retired);
            Assert.Equal(("contract-retired", false, (string?)null), (retired.KeyId, retired.Active, retired.ExecutorSovereignId));
            Assert.NotNull((await gm.EvidenceStore.ListExecutorKeys()).Single(k => k.KeyId == "contract-retired").RetiredAt);
            var afterRetirement = await Assert.ThrowsAsync<ValidationException>(async () => await gm.EvidenceStore.Submit(
                await retiring.Record(Execution((await gm.Boundary.Evaluate(Evaluation(30))).Decision, "contract:app-8/secret",
                    ResourceActions.Create, null))));
            Assert.Equal("evidence_executor_key_retired", afterRetirement.Code);
            Assert.Equal("executor_key_retired",
                (await Assert.ThrowsAsync<ConflictException>(() => gm.EvidenceStore.RetireExecutorKey("contract-retired"))).Code);
            Assert.Equal("executor_key_not_found",
                (await Assert.ThrowsAsync<NotFoundException>(() => gm.EvidenceStore.RetireExecutorKey("contract-none"))).Code);
            var afterKeys = await gm.EvidenceStore.Verify();
            Assert.True(afterKeys.Verified, Failures(afterKeys));
            // Offline too, with the retired key among the keys; without it, its record is not authentic.
            var whole = new List<EvidenceEvent>();
            await foreach (var e in gm.EvidenceStore.ExportAll()) whole.Add(e);
            var allKeys = await gm.EvidenceStore.ListExecutorKeys();
            Assert.Equal(Counts(afterKeys), Counts(OfflineVerifier.VerifyEvidenceEvents(whole,
                new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = allKeys })));
            var withoutRetired = OfflineVerifier.VerifyEvidenceEvents(whole,
                new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = allKeys.Where(k => k.KeyId != "contract-retired").ToList() });
            Assert.Equal(("invalid_signature", "execution"), (Assert.Single(withoutRetired.Failures).Reason, withoutRetired.Failures[0].Detail));

            // Retention removes nothing recorded within the day; the store has no checkpoint yet.
            var retention = await gm.EvidenceStore.ApplyRetention(1);
            Modeled("evidence_retention", retention);
            Assert.Equal((0L, (RetentionCheckpoint?)null, (RetentionHeldBack?)null), (retention.RemovedCount, retention.Checkpoint, retention.HeldBack));
            Assert.Null(await gm.EvidenceStore.LatestCheckpoint());
            Assert.Null((await gm.EvidenceStore.Status()).RetentionCheckpoint);

            // 1.9.0: an attestation as stored and listed, the recognition policy, verification, and the revocation feed.
            var member = await gm.Attestation.Issue(D(("subject_id", "vendor-feed"), ("roles", new[] { "role:client" })));
            var memberRecord = await gm.Attestation.Get(member.AttestationId);
            Modeled("attestation_get", memberRecord);
            Assert.Equal(("active", member.AttestationId), (memberRecord.Status, memberRecord.Attestation.AttestationId));
            var members = await gm.Attestation.List(new ListAttestationsParams { SubjectId = "vendor-feed" });
            Modeled("attestation_list", members);
            Assert.Equal(member.AttestationId, Assert.Single(members.Attestations!).Attestation.AttestationId);
            var anonymousRecorder = new Recorder { InnerHandler = new HttpClientHandler() };
            using (var anonymous = new GenesisMeshClient(new ClientOptions { BaseUrl = baseUrl, HttpHandler = anonymousRecorder }))
            {
                var counted = await anonymous.Attestation.List(new ListAttestationsParams { Status = "active" });
                Modeled("attestation_count", counted, anonymousRecorder.Json);
                Assert.Null(counted.Attestations);
                Assert.True(counted.Count >= 3, $"{counted.Count} active attestations");
                Assert.Equal("admin_auth_failed", (await Assert.ThrowsAsync<UnauthorizedException>(() =>
                    anonymous.Attestation.List(new ListAttestationsParams { SubjectId = "vendor-feed" }))).Code);
                Assert.Equal("recognition_policy_not_configured",
                    (await Assert.ThrowsAsync<NotFoundException>(() => anonymous.Attestation.GetPolicy())).Code);
            }
            await gm.Attestation.SavePolicy(D(("policy_id", "contract-recognition"), ("recognition_policy", D(
                ("local_sovereign_id", network),
                ("recognized_issuers", new[] { D(("sovereign_id", network), ("public_keys", new[] { naKey }), ("allowed_roles", new[] { "role:client" })) })))));
            var recognition = await gm.Attestation.GetPolicy();
            Modeled("recognition_policy", recognition);
            Assert.Equal((network, naKey), (recognition.LocalSovereignId, recognition.RecognizedIssuers.Single().PublicKeys.Single()));
            var accepted = await gm.Attestation.Verify(new VerifyAttestationRequest { Attestation = member });
            Modeled("attestation_verification", accepted);
            Assert.True(accepted.Accepted, accepted.Reason);
            // The record's typed attestation and the typed policy, passed back, are what the NA signed and stored.
            Assert.True((await gm.Attestation.Verify(new VerifyAttestationRequest
            {
                Attestation = memberRecord.Attestation,
                RecognitionPolicy = recognition,
            })).Accepted);
            // A policy built in code: its issuer accepts active attestations by default, as the NA reads one.
            Assert.True((await gm.Attestation.Verify(new VerifyAttestationRequest
            {
                Attestation = memberRecord.Attestation,
                RecognitionPolicy = new RecognitionPolicy
                {
                    LocalSovereignId = network,
                    RecognizedIssuers = [new RecognizedIssuer { SovereignId = network, PublicKeys = [naKey], AllowedRoles = ["role:client"] }],
                },
            })).Accepted);

            await gm.Attestation.Revoke(member.AttestationId, D(("reason", "offboarded")));
            var revokedMember = await gm.Attestation.Get(member.AttestationId);
            Assert.Equal(("revoked", "offboarded"), (revokedMember.Status, revokedMember.RevocationReason));
            Assert.NotNull(revokedMember.RevokedAt);
            var afterRevocation = await gm.Attestation.Verify(new VerifyAttestationRequest { Attestation = member });
            Assert.Equal((false, "locally_revoked"), (afterRevocation.Accepted, afterRevocation.Reason));
            var feed = await gm.Attestation.RevocationFeed();
            Modeled("revocation_feed", feed);
            Assert.Equal(("offboarded", network), (feed.RevocationReasons[member.AttestationId], feed.IssuerSovereignId));
            Assert.Contains(member.AttestationId, (await gm.Attestation.RevocationFeed(network)).RevokedAttestationIds);
            Assert.Equal("feed_issuer_mismatch",
                (await Assert.ThrowsAsync<BadRequestException>(() => gm.Attestation.RevocationFeed("sovereign-other"))).Code);

            Check("policy_deactivate", await gm.Policy.Deactivate("contract-policy", boundaryPolicy.Version));
        }
        finally
        {
            na.Dispose();
        }
    }
}
