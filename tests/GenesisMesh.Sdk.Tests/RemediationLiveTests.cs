using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Sdk.Tests;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Remediation and reviews against a running NA with its remediation records on (1.9.0): a change judged DENY under a
/// policy with remediation guidance is due; the NA's plan and due time are the rules' offline; a notice is recorded; a
/// review requested by one holder is refused to that holder and decided by another, and verifies offline with its
/// embedded requests; the remediation record is signed, checked as the NA admits it, submitted once, and settles the
/// change. Each typed result is compared with the JSON the NA sent, at every depth.
/// </summary>
[Trait("Category", "Live")]
public class RemediationLiveTests
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

    private static (string Seed, string PublicKey) NewKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType(), Auth.SerializerOptions);

    [Fact]
    public async Task ADeniedChangeIsRemediatedAndReviewed()
    {
        using var na = await LiveNa.Start("--out-of-band", "on", "--remediation", "on");
        if (na is null) return;
        var inputs = na.Inputs;
        var naKey = na.NaPublicKey;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        var secondRecorder = new Recorder { InnerHandler = new HttpClientHandler() };
        GenesisMeshClient Operator(string seed, string keyId, HttpMessageHandler handler) => new(new ClientOptions
        {
            BaseUrl = na.BaseUrl,
            SigningKey = seed,
            KeyId = keyId,
            HttpHandler = handler,
        });
        using var ops = Operator((string)inputs["seed"]!, "ops", recorder);
        using var second = Operator((string)inputs["secondSeed"]!, "ops2", secondRecorder);
        using var controller = Operator((string)inputs["controllerSeed"]!, "controller", new HttpClientHandler());

        void Check(Recorder from, string name, object typed)
        {
            var raw = from.Json ?? throw new InvalidOperationException(name + ": no JSON response");
            var problems = ContractTypesTests.Unmodeled(raw, typed.GetType(), name);
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }

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
            ResourcePrefix = "partner:",
        });

        // A policy whose gate a change may break, and what a DENY by it leads to: cancel within the hour.
        var now = DateTimeOffset.UtcNow;
        var intent = new BoundaryPolicyIntent
        {
            PolicyId = "partner-secret-lifetime",
            ValidFrom = Canonical.Timestamp(now.AddDays(-1)),
            ValidUntil = Canonical.Timestamp(now.AddDays(1)),
            Selector = new PolicySelector { Capabilities = ["partner-secret.*"] },
            Gates =
            [
                new GateSpec
                {
                    GateId = "max-lifetime", GateType = "max_value.v1", Order = 0,
                    Config = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = 90 },
                },
            ],
            Remediation = new Dictionary<string, GateRemediation> { ["max-lifetime"] = new() { Mode = RemediationModes.Cancel, GraceSeconds = 3600 } },
        };
        Assert.Empty(RemediationRules.PolicySectionIssues(intent));
        var policy = await ops.Policy.Publish(intent);
        Assert.True((await ops.Policy.Activate(policy.PolicyId, policy.Version)).Active);
        Assert.Empty(RemediationRules.PolicySectionIssues(policy));

        // A secret created in the console with a lifetime beyond the policy: judged DENY at admission.
        var observer = new ObservationRecorder("observer-a", new SeedSigner(observerSeed, "observer-key"));
        var observation = await observer.Record(new ObservationInput
        {
            ResourceId = "partner:app-9/secret",
            Action = ResourceActions.Create,
            Capability = "partner-secret.create",
            ChangedAt = DateTimeOffset.UtcNow,
            Actor = "principal-7f3a",
            Source = "cloud-activity-log",
            SourceEventId = "event-r1",
            VersionId = "v1",
            Metadata = new Dictionary<string, object?> { ["lifetime_days"] = 400 },
        });
        var judgement = (await controller.EvidenceStore.SubmitObservation(observation)).Judgement!.Payload;
        Assert.Equal("deny", judgement.Verdict);

        // Due: the NA's plan and due time are the rules' offline.
        var page = await ops.EvidenceStore.Remediations();
        Check(recorder, "remediation_queue", page);
        var due = Assert.Single(page.Remediations, s => s.SubjectId == judgement.JudgementId);
        Assert.Equal((RemediationStates.RemediationDue, ChangeKinds.Observation, observation.ObservationId, "principal-7f3a"),
            (due.State, due.ChangeKind, due.ChangeId, due.Actor));
        var evaluations = judgement.PolicyBinding.GetProperty("gate_evaluations").Deserialize<List<PolicyGateEvaluation>>()!;
        var plan = RemediationRules.Plan(judgement.Verdict, [policy], evaluations,
            RemediationRules.FactsOf(ChangeKinds.Observation, JsonSerializer.SerializeToElement(observation, Auth.SerializerOptions)));
        Assert.Equal(new RemediationPlan(RemediationModes.Cancel, RemediationSteps.Disable, null, 3600, GuidanceSources.Gate), plan);
        Assert.Equal((plan.Mode, plan.Steps, plan.Fallback, plan.GraceSeconds, plan.Source),
            (due.Mode, due.Steps, due.Fallback, due.GraceSeconds, due.GuidanceSource));
        Assert.Equal(OfflineVerifier.ParseTimestamp(RemediationRules.DueAt(judgement.JudgedAt, plan.GraceSeconds)),
            OfflineVerifier.ParseTimestamp(due.RemediationDueAt!));
        Assert.Equal(RemediationStates.RemediationDue,
            RemediationRules.NextState(RemediationRules.JudgementState(judgement.Verdict, judgement.GovernedBy), RemediationEvents.Arm).State);

        // The actor is told.
        var notified = await ops.EvidenceStore.MarkNotified(due.SubjectId, "email");
        Check(recorder, "remediation_notified", notified);
        Assert.Equal("ops", notified.NotifiedBy);
        Assert.NotNull(notified.NotifiedAt);

        // One holder asks for a review; it cannot decide it, another holder does.
        var requested = await ops.EvidenceStore.RequestReview(new ReviewRequest
        {
            SubjectKind = RemediationSubjectKinds.Judgement,
            SubjectId = due.SubjectId,
            Justification = "The lifetime was agreed with the vendor",
        });
        Check(recorder, "review_requested", requested);
        Assert.True(requested.ReviewPending);
        var reviewId = requested.ReviewId!;
        var pending = new List<RemediationStatus>();
        await foreach (var s in ops.EvidenceStore.AllRemediations("review_pending")) pending.Add(s);
        Assert.Equal(reviewId, Assert.Single(pending, s => s.SubjectId == due.SubjectId).Review!.ReviewId);
        var open = await ops.EvidenceStore.Review(reviewId);
        Check(recorder, "review_pending", open);
        Assert.Null(open.Record);
        Assert.Equal("platform-team", open.RequesterHolder);
        var refused = await Assert.ThrowsAsync<ConflictException>(() => ops.EvidenceStore.DecideReview(reviewId, new ReviewDecision { Verdict = ReviewVerdicts.Uphold }));
        Assert.Equal("review_same_holder_as_requester", refused.Code);

        var decided = await second.EvidenceStore.DecideReview(reviewId, new ReviewDecision { Verdict = ReviewVerdicts.Deny, Note = "Keep the policy's lifetime" });
        Check(secondRecorder, "review_decided", decided);
        Assert.Equal((RemediationStates.RemediationDue, false), (decided.State, decided.ReviewPending));
        var review = decided.Review.Payload;
        var reviewJson = Json(review);
        Assert.True(OfflineVerifier.VerifyReviewRecord(reviewJson, [naKey]));
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(reviewJson, [naKey]));
        Assert.Null(RemediationRules.ReviewRecordProblem(review));
        Assert.Null(RemediationRules.ReviewApprovalProblem(review, [naKey]));
        Assert.Null(RemediationRules.ReviewHolderProblem(review));
        Assert.Equal(("ops2", "security-team", "ops", "platform-team"), (review.DecidedBy, review.DeciderHolder, review.RequestedBy, review.RequesterHolder));
        var read = await ops.EvidenceStore.Review(reviewId);
        Check(recorder, "review_read", read);
        Assert.Equal(OfflineVerifier.OutOfBandCanonical(reviewJson), OfflineVerifier.OutOfBandCanonical(Json(read.Record!.Payload)));

        // The controller disables the secret and records it, as the NA's plan says.
        var state = await ops.EvidenceStore.Remediation(due.SubjectId);
        Check(recorder, "remediation_detail", state);
        Assert.Equal(ReviewVerdicts.Deny, Assert.Single(state.Reviews).Verdict);
        var executor = new ExecutionRecorder("executor-a", new SeedSigner(executorSeed, "executor-key"));
        var record = await executor.SignRemediation(RemediationInput.For(state, state.Steps) with
        {
            DisabledVersionId = "v1",
            Detail = "disabled before any reader used it",
        });
        Assert.Null(RemediationRules.StepsProblem(state.Mode, state.Steps, record));
        var registered = (await ops.EvidenceStore.ListExecutorKeys()).Single(k => k.KeyId == "executor-key");
        Assert.Null(RemediationRules.RecordProblem(Json(record), registered));
        Assert.True(OfflineVerifier.VerifyRemediationRecord(Json(record), [executorKey]));
        var recorded = await ops.EvidenceStore.SubmitRemediation(record);
        Check(recorder, "remediation_recorded", recorded);
        Assert.Equal(("recorded", RemediationStates.Remediated, record.RemediationId), (recorded.Status, recorded.Remediation!.State, recorded.Remediation.RemediationId));
        Assert.Equal("duplicate", (await controller.EvidenceStore.SubmitRemediation(record)).Status);

        // Settled: out of the open queue, in the remediated one.
        Assert.DoesNotContain((await ops.EvidenceStore.Remediations()).Remediations, s => s.SubjectId == due.SubjectId);
        var remediated = new List<RemediationStatus>();
        await foreach (var s in ops.EvidenceStore.AllRemediations(RemediationStates.Remediated, 1)) remediated.Add(s);
        Assert.Equal(record.RemediationId, Assert.Single(remediated, s => s.SubjectId == due.SubjectId).RemediationId);
        Assert.Equal("remediation_already_recorded", RemediationRules.NextState(RemediationStates.Remediated, RemediationEvents.RecordRemediation).Refusal);
    }

    [Fact]
    public async Task ADeniedRotationIsCancelledByRotatingForward()
    {
        using var na = await LiveNa.Start("--out-of-band", "on", "--remediation", "on");
        if (na is null) return;
        var inputs = na.Inputs;
        var naKey = na.NaPublicKey;
        GenesisMeshClient Operator(string seed, string keyId) => new(new ClientOptions { BaseUrl = na.BaseUrl, SigningKey = seed, KeyId = keyId });
        using var ops = Operator((string)inputs["seed"]!, "ops");
        using var controller = Operator((string)inputs["controllerSeed"]!, "controller");

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
            ResourcePrefix = "partner:",
        });
        var now = DateTimeOffset.UtcNow;
        var policy = await ops.Policy.Publish(new BoundaryPolicyIntent
        {
            PolicyId = "partner-secret-lifetime",
            ValidFrom = Canonical.Timestamp(now.AddDays(-1)),
            ValidUntil = Canonical.Timestamp(now.AddDays(1)),
            Selector = new PolicySelector { Capabilities = ["partner-secret.*"] },
            Gates =
            [
                new GateSpec
                {
                    GateId = "max-lifetime", GateType = "max_value.v1", Order = 0,
                    Config = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = 90 },
                },
            ],
            Remediation = new Dictionary<string, GateRemediation> { ["max-lifetime"] = new() { Mode = RemediationModes.Cancel, GraceSeconds = 3600 } },
        });
        Assert.True((await ops.Policy.Activate(policy.PolicyId, policy.Version)).Active);
        var attestation = await ops.Attestation.Issue(new Dictionary<string, object?>
        {
            ["subject_id"] = "partner-org-1",
            ["roles"] = new[] { "role:client" },
            ["validity_hours"] = 24,
            ["claims"] = new Dictionary<string, object?> { ["capabilities"] = new[] { "partner-secret.rotate" } },
        });

        // A rotation in the console with a lifetime beyond the policy: its cancel is a forward rotation.
        var observer = new ObservationRecorder("observer-a", new SeedSigner(observerSeed, "observer-key"));
        var observation = await observer.Record(new ObservationInput
        {
            ResourceId = "partner:app-9/secret",
            Action = ResourceActions.Rotate,
            Capability = "partner-secret.rotate",
            ChangedAt = DateTimeOffset.UtcNow,
            Actor = "principal-7f3a",
            Source = "cloud-activity-log",
            SourceEventId = "event-r2",
            VersionId = "v2",
            Metadata = new Dictionary<string, object?> { ["lifetime_days"] = 400 },
        });
        var judgement = (await controller.EvidenceStore.SubmitObservation(observation)).Judgement!.Payload;
        var due = await ops.EvidenceStore.Remediation(judgement.JudgementId);
        Assert.Equal((RemediationStates.RemediationDue, RemediationModes.Cancel, RemediationSteps.RotateForwardThenDisable, "v2"),
            (due.State, due.Mode, due.Steps, due.ChangeVersionId));

        // The controller rotates forward under a decision of its own, disables the denied version and records both.
        var disabled = new List<string>();
        var executor = new ExecutionRecorder("executor-a", new SeedSigner(executorSeed, "executor-key"));
        var options = new RotateForwardOptions<string>
        {
            Remediation = due,
            DeniedVersionId = due.ChangeVersionId!,
            Rotation = new GovernedActionRequest
            {
                RequestedCapability = "partner-secret.rotate",
                AttestationId = attestation.AttestationId,
                Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 } },
                Verify = new GovernedVerification { OperatorPublicKeys = [naKey], ExpectedPolicies = [policy], ExpectedAttestation = attestation },
            },
            Rotate = (_, _) => Task.FromResult(new RotatedVersion<string> { VersionId = "v3", Value = "new secret handed to its readers" }),
            Disable = (version, _) =>
            {
                disabled.Add(version);
                return Task.CompletedTask;
            },
            Detail = "rotated forward, then the denied version disabled",
        };
        var result = await Governance.CancelByRotatingForward(controller, executor, options);
        Assert.True(result.Rotation.Authorized);
        Assert.Equal(["v2"], disabled);
        Assert.Equal("v3", result.Rotation.Evidence!.ExecutionParameters.GetProperty("version_id").GetString());
        Assert.Equal(("recorded", RemediationStates.Remediated), (result.Submission!.Status, result.Submission.Remediation!.State));
        var record = result.Remediation!;
        Assert.Equal((result.Rotation.Evidence.EvidenceId, "v3", "v2"), (record.RotationEvidenceId, record.VersionId, record.DisabledVersionId));
        var settled = await ops.EvidenceStore.Remediation(judgement.JudgementId);
        Assert.Equal((RemediationStates.Remediated, record.RemediationId), (settled.State, settled.RemediationId));

        // Again: the state takes no second record, and nothing runs.
        var again = await Assert.ThrowsAsync<TransitionRefusedException>(() => Governance.CancelByRotatingForward(controller, executor,
            new RotateForwardOptions<string>
            {
                Remediation = settled,
                DeniedVersionId = "v2",
                Rotation = options.Rotation,
                Rotate = options.Rotate,
                Disable = options.Disable,
            }));
        Assert.Equal("remediation_already_recorded", again.Code);
        Assert.Equal(["v2"], disabled);
    }
}
