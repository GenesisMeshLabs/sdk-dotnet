using System.Globalization;
using System.Text.Json;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace GenesisMesh.Tests;

/// <summary>
/// A partner credential controller on a standard operator key, against a live NA that refuses the policy-free decide
/// route (<c>scripts/contract_na.py --policy-enforcement required</c>; it runs as <see cref="LiveContractTests"/> does).
/// The platform team's setup uses the privileged key directly: the policies, the partner's and the security team's
/// attestations, and the controller's executor key, limited to <c>partner:</c>. Everything else uses the standard key:
/// create, rotate, retire and revoke through governed actions with an outbox, the denials and refusals, emergency
/// removal under the security team's attestation once the partner's is revoked, and reconciliation.
/// </summary>
[Trait("Category", "Live")]
public class PartnerLifecycleTests(ITestOutputHelper output)
{
    private const string Orders = "partner:org-1/app-orders/secret";
    private const string Billing = "partner:org-1/app-billing/secret";
    private const string Reports = "partner:org-1/app-orders/reports";
    private const string OldBilling = "partner:org-1/app-billing/old-secret";
    private const string Internal = "internal:vault-1/db-password";

    /// <summary>Fails the record submissions while <see cref="Down"/> is set, as a lost connection does.</summary>
    private sealed class Outage : DelegatingHandler
    {
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down && request.RequestUri!.AbsolutePath == "/evidence/execution"
                ? throw new HttpRequestException("connection refused")
                : base.SendAsync(request, ct);
    }

    /// <summary>One step of the lifecycle, named in the failure and in the test output.</summary>
    private async Task Step(string name, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{name}: {e.GetType().Name}: {e.Message}", e);
        }
        output.WriteLine("ok  " + name);
    }

    private static Dictionary<string, object?> D(params (string, object?)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    private static string Iso(TimeSpan offset) =>
        DateTimeOffset.UtcNow.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);

    private static string Json<T>(T record) => JsonSerializer.Serialize(record, Auth.SerializerOptions);

    private static BoundaryPolicyIntent Policy(string id, string capabilities, params GateSpec[] gates) => new()
    {
        PolicyId = id,
        Description = id,
        ValidFrom = Iso(TimeSpan.FromMinutes(-5)),
        ValidUntil = Iso(TimeSpan.FromDays(1)),
        Selector = new PolicySelector { ParentKinds = ["attestation"], Capabilities = [capabilities] },
        Gates = gates,
    };

    private static GateSpec Gate(string id, string type, int order, params (string, object?)[] config) =>
        new() { GateId = id, GateType = type, Order = order, Config = D(config) };

    private static string Applied(BoundaryPolicy policy) => policy.PolicyId + "@" + policy.Version.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task APartnerControllerRunsTheLifecycleOnAStandardKey()
    {
        using var na = await LiveNa.Start("--policy-enforcement", "required");
        if (na is null) return;
        var naKey = na.NaPublicKey;
        GenesisMeshClient Client(string seed, string keyId, IOutbox<OutboxEntry>? outbox = null, HttpMessageHandler? handler = null) =>
            new(new ClientOptions
            {
                BaseUrl = na.BaseUrl,
                SigningKey = (string)na.Inputs[seed]!,
                KeyId = (string)na.Inputs[keyId]!,
                Audience = naKey,
                Outbox = outbox,
                HttpHandler = handler,
            });
        // The platform team holds the privileged key and sets up; the controller holds a standard key.
        using var platform = Client("seed", "keyId");
        var outbox = new MemoryOutbox<OutboxEntry>();
        var outage = new Outage { InnerHandler = new HttpClientHandler() };
        using var controller = Client("controllerSeed", "controllerKeyId", outbox, outage);
        using var executorKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var executorPublicKey = Convert.ToBase64String(executorKey.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var executor = new ExecutionRecorder("partner-controller",
            new SeedSigner(Convert.ToBase64String(executorKey.Export(KeyBlobFormat.RawPrivateKey)), "partner-executor"));

        var intents = new[]
        {
            Policy("partner-secret-lifecycle", "partner-secret.*",
                Gate("app", "attestation_claim.v1", 0, ("path", "request_parameters.app_id"), ("claim", "apps")),
                Gate("owner", "required_parameter.v1", 1, ("path", "attributes.owner")),
                Gate("max-lifetime", "max_value.v1", 2, ("path", "request_parameters.lifetime_days"), ("max", 90))),
            // Emergency removal: the incident is required; nothing limits the lifetime or the overlap.
            Policy("partner-secret-emergency", "partner-secret-emergency.*",
                Gate("incident", "required_parameter.v1", 0, ("path", "request_parameters.incident_id"))),
            // The other controller's.
            Policy("internal-secret-lifecycle", "internal-secret.*",
                Gate("max-lifetime", "max_value.v1", 0, ("path", "request_parameters.lifetime_days"), ("max", 90))),
        };

        // ── The platform team's setup, with the privileged key ──────────────────────────────────────────────

        await Step("the NA requires policies: the legacy decide is refused", async () =>
        {
            var refused = await Assert.ThrowsAsync<ConflictException>(() => controller.Boundary.Decide(
                D(("agreement", D()), ("requested_capability", "partner-secret.create"))));
            Assert.Equal("boundary_policy_required", refused.Code);
        });

        var policies = new List<BoundaryPolicy>();
        await Step("the platform team publishes and activates the policies, which the controller's key validates", async () =>
        {
            foreach (var intent in intents)
            {
                Assert.True((await controller.Policy.Validate(intent)).Valid);
                var policy = await platform.Policy.Publish(intent);
                Assert.True((await platform.Policy.Activate(policy.PolicyId, policy.Version)).Active);
                policies.Add(policy);
            }
            var active = await controller.Policy.Active();
            Assert.Equal(("required", true, 3), (active.Enforcement, active.PolicySetHealthy, active.Active.Count));
        });
        var (lifecycle, emergency, internalPolicy) = (policies[0], policies[1], policies[2]);

        await Step("the platform team registers the controller's executor key, limited to partner:", async () =>
        {
            var state = await platform.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
            {
                KeyId = "partner-executor",
                PublicKey = executorPublicKey,
                ExecutorSovereignId = "partner-controller",
                ResourcePrefix = "partner:",
            });
            Assert.Equal(("partner:", true), (state.ResourcePrefix, state.Active));
        });

        MembershipAttestation partner = null!, security = null!;
        await Step("the platform team issues the partner's attestation, and the security team's ahead of any incident", async () =>
        {
            partner = await platform.Attestation.Issue(D(("subject_id", "partner-org-1"), ("roles", new[] { "role:client" }),
                ("validity_hours", 720), ("claims", D(
                    ("capabilities", new[] { "partner-secret.create", "partner-secret.rotate", "partner-secret.retire", "partner-secret.revoke" }),
                    ("apps", new[] { "app-orders", "app-billing" })))));
            security = await platform.Attestation.Issue(D(("subject_id", "security-team"), ("roles", new[] { "role:client" }),
                ("validity_hours", 720), ("claims", D(("capabilities", new[] { "partner-secret-emergency.revoke" })))));
        });

        await Step("the controller's standard key cannot publish a policy, issue or revoke an attestation, register or retire a key, or apply retention", async () =>
        {
            async Task Refused(Func<Task> call) =>
                Assert.Equal("insufficient_operator_tier", (await Assert.ThrowsAsync<ForbiddenException>(call)).Code);
            await Refused(() => controller.Policy.Publish(intents[0]));
            await Refused(() => controller.Attestation.Issue(D(("subject_id", "partner-org-2"), ("roles", new[] { "role:client" }))));
            await Refused(() => controller.Attestation.Revoke(partner.AttestationId, D(("reason", "none"))));
            await Refused(() => controller.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
            {
                KeyId = "partner-executor-2",
                PublicKey = executorPublicKey,
                ExecutorSovereignId = "partner-controller",
                ResourcePrefix = "internal:",
            }));
            await Refused(() => controller.EvidenceStore.RetireExecutorKey("partner-executor"));
            await Refused(() => controller.EvidenceStore.ApplyRetention(1));
        });

        // ── The controller, with the standard key ───────────────────────────────────────────────────────────

        GovernedVerification Expect(BoundaryPolicy policy, MembershipAttestation basis) =>
            new() { OperatorPublicKeys = [naKey], ExpectedPolicies = [policy], ExpectedAttestation = basis };
        GovernedActionRequest Partner(string capability, string resource, string action, string app = "app-orders", int days = 30,
            string? owner = "team-orders", GovernedVerification? verify = null) => new()
            {
                AttestationId = partner.AttestationId,
                RequestedCapability = capability,
                Context = new EvaluationContext
                {
                    RequestParameters = D(("app_id", app), ("lifetime_days", days)),
                    Attributes = owner is null ? D() : D(("owner", owner)),
                },
                ResourceId = resource,
                ResourceAction = action,
                Verify = verify ?? Expect(lifecycle, partner),
            };
        GovernedActionRequest Emergency(string? incident) => new()
        {
            AttestationId = security.AttestationId,
            RequestedCapability = "partner-secret-emergency.revoke",
            Context = new EvaluationContext { RequestParameters = incident is null ? D() : D(("incident_id", incident)) },
            ResourceId = Orders,
            ResourceAction = ResourceActions.Revoke,
            Verify = Expect(emergency, security),
        };
        // The cloud change: counted, so a denial or a refusal can show it did not run.
        var actions = 0;
        Func<BoundaryDecision, CancellationToken, Task<ActionReport<string>?>> Cloud(params (string, object?)[] report) => (_, _) =>
        {
            actions++;
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string>
            {
                Value = "credential-" + actions.ToString(CultureInfo.InvariantCulture),
                ExecutionParameters = D(report),
            });
        };
        Task<GovernedActionResult<string>> Act(GovernedActionRequest request, Func<BoundaryDecision, CancellationToken, Task<ActionReport<string>?>> action) =>
            Governance.GovernedAction(controller, executor, request, action);
        async Task<BoundaryDecision> Decide(GovernedActionRequest request) => (await controller.Boundary.Evaluate(new EvaluateRequest
        {
            AttestationId = request.AttestationId,
            RequestedCapability = request.RequestedCapability,
            Context = request.Context,
        })).Decision;

        ExecutionEvidence created = null!, rotated = null!;
        await Step("create: a verified ALLOW runs the action once, and the NA admits its record", async () =>
        {
            var result = await Act(Partner("partner-secret.create", Orders, ResourceActions.Create),
                Cloud(("secret_version", "v1"), ("expires_on", Iso(TimeSpan.FromDays(30)))));
            Assert.True(result.Authorized);
            Assert.Equal((1, "credential-1", "recorded"), (actions, result.Value, result.Submission!.Status));
            created = result.Evidence!;
            Assert.Equal(1L, created.ResourceSequence);
            Assert.Null(created.PrevResourceDigest);
            Assert.True(OfflineVerifier.VerifyExecutionSignature(Json(created), executorPublicKey));
            Assert.Equal(new[] { Applied(lifecycle) }, result.Summary.AppliedPolicies);
        });

        await Step("rotate: the record chains from the create", async () =>
        {
            var result = await Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate),
                Cloud(("secret_version", "v2"), ("overlap_until", Iso(TimeSpan.FromDays(1)))));
            rotated = result.Evidence!;
            Assert.Equal((2L, ExecutionRecorder.Digest(created)), (rotated.ResourceSequence!.Value, rotated.PrevResourceDigest));
            Assert.Equal("recorded", result.Submission!.Status);
        });

        await Step("retire the old version once the overlap ends", async () =>
        {
            var result = await Act(Partner("partner-secret.retire", Orders, ResourceActions.Update),
                Cloud(("secret_version", "v2"), ("retired_version", "v1")));
            Assert.Equal((3L, "recorded"), (result.Evidence!.ResourceSequence!.Value, result.Submission!.Status));
        });

        await Step("create three more of the partner's credentials", async () =>
        {
            foreach (var (resource, app) in new[] { (Billing, "app-billing"), (Reports, "app-orders"), (OldBilling, "app-billing") })
            {
                var result = await Act(Partner("partner-secret.create", resource, ResourceActions.Create, app), Cloud(("secret_version", "v1")));
                Assert.Equal("recorded", result.Submission!.Status);
            }
        });

        await Step("revoke one at the partner's request, under its own attestation", async () =>
        {
            var result = await Act(Partner("partner-secret.revoke", OldBilling, ResourceActions.Revoke, "app-billing"),
                Cloud(("revoked_version", "v1")));
            Assert.True(result.Authorized);
            Assert.Equal((2L, "recorded"), (result.Evidence!.ResourceSequence!.Value, result.Submission!.Status));
        });

        await Step("an app outside the partner's claims, for longer than the limit: a signed DENY, and nothing runs", async () =>
        {
            var before = actions;
            var result = await Act(Partner("partner-secret.create", "partner:org-1/app-payroll/secret", ResourceActions.Create,
                "app-payroll", 365), Cloud());
            Assert.False(result.Authorized);
            Assert.Equal(before, actions);
            Assert.Null(result.Evidence);
            var gates = result.Summary.EnforcedFailures.Select(f => f.Gate).ToList();
            Assert.Contains("partner-secret-lifecycle/app", gates);
            Assert.Contains("partner-secret-lifecycle/max-lifetime", gates);
            // The typed decision, serialized, verifies offline as the NA signed it.
            var offline = OfflineVerifier.VerifyBoundaryDecision(Json(result.Evaluation.Decision), new DecisionVerifyOptions
            {
                OperatorPublicKeys = [naKey],
                ExpectedPolicies = [Json(lifecycle)],
            });
            Assert.Equal((true, false, "unauthorized_policy_gate_failure"), (offline.Accepted, offline.Authorized, offline.Reason));
        });

        await Step("no owner: denied", async () =>
        {
            var result = await Act(Partner("partner-secret.create", "partner:org-1/app-orders/unowned", ResourceActions.Create, owner: null), Cloud());
            Assert.False(result.Authorized);
            Assert.Contains(result.Summary.EnforcedFailures, f => f.Gate == "partner-secret-lifecycle/owner");
        });

        await Step("a capability the partner's attestation does not claim: denied by the capability check", async () =>
        {
            var before = actions;
            var result = await Act(Partner("partner-secret.delete", Orders, ResourceActions.Delete), Cloud());
            Assert.False(result.Authorized);
            Assert.Equal(before, actions);
            Assert.Contains(result.Summary.EnforcedFailures, f => f.Gate == "capability_check");
        });

        // The NA authorizes this request; only the expectations differ.
        var authorizedRotation = Partner("partner-secret.rotate", Orders, ResourceActions.Rotate);
        await Step("an ALLOW bound to other policies than expected: refused before the action", async () =>
        {
            Assert.True((await Decide(authorizedRotation)).Authorized);
            var before = actions;
            var refused = await Assert.ThrowsAsync<DecisionVerificationException>(() =>
                Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate, verify: Expect(emergency, partner)), Cloud()));
            Assert.Equal("policy_binding_mismatch", refused.Code);
            Assert.Equal(before, actions);
        });

        await Step("an ALLOW on another attestation than expected: refused before the action", async () =>
        {
            Assert.True((await Decide(authorizedRotation)).Authorized);
            var before = actions;
            var refused = await Assert.ThrowsAsync<DecisionVerificationException>(() =>
                Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate, verify: Expect(lifecycle, security)), Cloud()));
            Assert.Equal("attestation_binding_mismatch", refused.Code);
            Assert.Equal(before, actions);
        });

        await Step("the partner's attestation cannot authorize emergency removal", async () =>
        {
            var before = actions;
            var result = await Act(Partner("partner-secret-emergency.revoke", Orders, ResourceActions.Revoke,
                verify: Expect(emergency, partner)), Cloud());
            Assert.False(result.Authorized);
            Assert.Equal(before, actions);
            Assert.Contains(result.Summary.EnforcedFailures, f => f.Gate == "capability_check");
        });

        await Step("a failing cloud action: recorded as a failure and rethrown; the last good version stays v2", async () =>
        {
            await Assert.ThrowsAsync<TimeoutException>(() => Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate),
                (_, _) => throw new TimeoutException("the cloud did not answer")));
            var state = (await controller.EvidenceStore.ResourceStates())[Orders];
            Assert.Equal((4L, ExecutionOutcomes.Failure), (state.ResourceSequence, state.LastOutcome));
            Assert.Equal("v2", state.LastSuccessParameters.GetProperty("secret_version").GetString());
        });

        await Step("the NA unreachable after the action: the record stays in the outbox, and a flush admits it without acting again", async () =>
        {
            var before = actions;
            outage.Down = true;
            GovernedActionResult<string> result;
            try
            {
                result = await Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate), Cloud(("secret_version", "v3")));
            }
            finally
            {
                outage.Down = false;
            }
            Assert.Equal(before + 1, actions);
            Assert.Null(result.Submission);
            Assert.Equal((OutboxStates.Pending, EvidenceOutbox.NetworkError), (result.Queued!.State, result.Queued.LastError!.Code));
            var flushed = await controller.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
            Assert.Equal(result.Evidence!.EvidenceId, Assert.Single(flushed.Admitted).Id);
            Assert.Empty(await outbox.List());
            Assert.Equal(before + 1, actions);
            Assert.Equal(5L, result.Evidence.ResourceSequence);
        });

        await Step("a record on a stale head: refused, so the chain cannot fork", async () =>
        {
            var fork = await executor.Record(new RecordExecutionRequest
            {
                Decision = await Decide(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate)),
                ExecutedCapability = "partner-secret.rotate",
                Outcome = ExecutionOutcomes.Success,
                ResourceId = Orders,
                ResourceAction = ResourceActions.Rotate,
                PriorResource = ExecutionRecorder.HeadOf(rotated),
            });
            var refused = await Assert.ThrowsAsync<ConflictException>(() => controller.EvidenceStore.Submit(fork));
            Assert.Equal("evidence_conflict", refused.Code);
        });

        await Step("the resource's chain verifies offline from the store", async () =>
        {
            var chain = (await controller.EvidenceStore.Search(new EvidenceSearchParams { ResourceId = Orders, EntryKind = "execution" })).Entries;
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, chain.Select(e => e.Payload.GetProperty("resource_sequence").GetInt64()));
            string? previous = null;
            foreach (var entry in chain)
            {
                var record = entry.Payload.GetRawText();
                Assert.True(OfflineVerifier.VerifyExecutionSignature(record, executorPublicKey));
                // The first record has no link: the NA keeps it without the field.
                var link = entry.Payload.TryGetProperty("prev_resource_digest", out var digest) ? digest.GetString() : null;
                Assert.Equal(previous, link);
                previous = OfflineVerifier.ExecutionDigest(record);
            }
        });

        await Step("the resource's history and the partner's decisions, denials included, verify on the NA and offline", async () =>
        {
            var history = await controller.EvidenceStore.ResourceHistory(Orders);
            Assert.True(history.Verification.Verified);
            var executions = history.Entries.Where(e => e.Entry.EntryKind == "execution").ToList();
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, executions.Select(e => e.Entry.ResourceSequence!.Value));
            var decisions = history.Entries.Where(e => e.Entry.EntryKind == "decision").Select(e => e.Entry.DecisionId).ToHashSet();
            Assert.All(executions, e => Assert.Contains(e.Entry.DecisionId, decisions));
            var vendor = await controller.EvidenceStore.VendorHistory("partner-org-1");
            Assert.True(vendor.Verification.Verified);
            Assert.Contains(vendor.Entries, e => e.Entry.EntryKind == "decision" && e.Entry.Outcome == "denied");
            // Offline, as histories: the NA's word is not needed.
            var keys = await controller.EvidenceStore.ListExecutorKeys();
            foreach (var (entries, verification) in new[] { (history.Entries, history.Verification), (vendor.Entries, vendor.Verification) })
            {
                var offline = OfflineVerifier.VerifyEvidenceEvents(entries,
                    new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = keys, Contiguous = false });
                Assert.Equal((true, verification.CheckedEntries, verification.Decisions, verification.Executions),
                    (offline.Verified, offline.CheckedEntries, offline.Decisions, offline.Executions));
            }
        });

        await Step("a secret in the request: refused before anything is sent", async () =>
        {
            var before = actions;
            var request = Partner("partner-secret.rotate", Orders, ResourceActions.Rotate);
            request.Context!.Attributes!["client_secret"] = "never-sent";
            await Assert.ThrowsAsync<SecretMaterialException>(() => Act(request, Cloud()));
            Assert.Equal(before, actions);
        });

        await Step("a secret in the action's report: recorded without it, and the action's value kept", async () =>
        {
            var refused = await Assert.ThrowsAsync<MetadataRefusedException>(() => Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate),
                Cloud(("secret_version", "v4"), ("client_secret", "kept-out"))));
            Assert.Equal(("recorded", "client_secret"), (refused.Submission!.Status, Assert.Single(refused.Dropped)));
            Assert.Equal("credential-" + actions.ToString(CultureInfo.InvariantCulture), refused.Value);
            Assert.DoesNotContain("kept-out", Json(refused.Evidence), StringComparison.Ordinal);
            Assert.Equal(6L, refused.Evidence.ResourceSequence);
        });

        await Step("the other controller's capability, under the partner's attestation: a signed DENY", async () =>
        {
            var before = actions;
            var result = await Act(Partner("internal-secret.rotate", Internal, ResourceActions.Rotate, verify: Expect(internalPolicy, partner)), Cloud());
            Assert.False(result.Authorized);
            Assert.Equal(before, actions);
        });

        await Step("acting on the other controller's resource: the action runs, its record is refused for good and kept as a dead letter", async () =>
        {
            // The decision does not name the resource, so the NA authorizes the change; the executor key's prefix
            // stops only its record. A controller keeps to its own resources itself.
            var before = actions;
            var result = await Act(Partner("partner-secret.rotate", Internal, ResourceActions.Rotate), Cloud(("secret_version", "v9")));
            Assert.True(result.Authorized);
            Assert.Equal(before + 1, actions);
            Assert.Null(result.Submission);
            Assert.Equal((OutboxStates.DeadLetter, "evidence_out_of_scope"), (result.Queued!.State, result.Queued.LastError!.Code));
            Assert.Equal(result.Queued.Id, Assert.Single(await outbox.List()).Id);
        });

        BoundaryDecision denied = null!;
        await Step("the platform team revokes the partner's attestation: the next request is a signed DENY", async () =>
        {
            await platform.Attestation.Revoke(partner.AttestationId, D(("reason", "partner offboarded")));
            var before = actions;
            var result = await Act(Partner("partner-secret.rotate", Orders, ResourceActions.Rotate), Cloud());
            Assert.False(result.Authorized);
            Assert.Equal(before, actions);
            denied = result.Evaluation.Decision;
            var offline = OfflineVerifier.VerifyBoundaryDecision(Json(denied), new DecisionVerifyOptions { OperatorPublicKeys = [naKey] });
            Assert.Equal((true, "unauthorized_attestation_basis"), (offline.Accepted, offline.Reason));
        });

        await Step("a record under the denied decision: refused", async () =>
        {
            // On the resource's head: a key limited to partner: signs records for its resources only.
            var late = await executor.Record(new RecordExecutionRequest
            {
                Decision = denied,
                ExecutedCapability = "partner-secret.rotate",
                Outcome = ExecutionOutcomes.Success,
                ResourceId = Orders,
                ResourceAction = ResourceActions.Rotate,
                PriorResource = await controller.EvidenceStore.ResourceHead(Orders),
            });
            var refused = await Assert.ThrowsAsync<ValidationException>(() => controller.EvidenceStore.Submit(late));
            Assert.Equal("evidence_decision_denied", refused.Code);
        });

        await Step("emergency removal without an incident: denied", async () =>
        {
            var result = await Act(Emergency(null), Cloud());
            Assert.False(result.Authorized);
            Assert.Contains(result.Summary.EnforcedFailures, f => f.Gate == "partner-secret-emergency/incident");
        });

        await Step("emergency removal under the security team's attestation, after the partner's revocation: allowed and recorded on the chain", async () =>
        {
            var result = await Act(Emergency("INC-1042"), Cloud(("revoked_version", "v4"), ("overlap", "none")));
            Assert.True(result.Authorized);
            Assert.Equal(("recorded", 7L), (result.Submission!.Status, result.Evidence!.ResourceSequence!.Value));
            Assert.Equal(new[] { Applied(emergency) }, result.Summary.AppliedPolicies);
            Assert.Equal(security.AttestationId, result.Summary.AttestationId);
        });

        await Step("reconciliation: present after its revocation, in sync, absent as revoked, unmanaged, missing, drifted", async () =>
        {
            var states = await controller.EvidenceStore.ResourceStates();
            Assert.DoesNotContain(Internal, states.Keys);
            var findings = Governance.ReconcileResources(
            [
                new ObservedResource { ResourceId = Orders, Exists = true, Version = "v4" },
                new ObservedResource { ResourceId = Billing, Exists = true, Version = "v1" },
                new ObservedResource { ResourceId = OldBilling, Exists = false },
                new ObservedResource { ResourceId = "partner:org-1/app-orders/made-by-hand", Exists = true, Version = "v1" },
            ], states, new ReconcileOptions { CompleteInventory = true, ScopePrefix = "partner:" });
            Assert.Equal(new Dictionary<string, string>
            {
                [Orders] = ReconciliationStatuses.PresentAfterRevoke,
                [Billing] = ReconciliationStatuses.InSync,
                [OldBilling] = ReconciliationStatuses.InSync,
                ["partner:org-1/app-orders/made-by-hand"] = ReconciliationStatuses.Unmanaged,
                [Reports] = ReconciliationStatuses.Missing,
            }, findings.ToDictionary(f => f.ResourceId, f => f.Status));
            var drift = Governance.ReconcileResources([new ObservedResource { ResourceId = Billing, Exists = true, Version = "v7" }], states);
            Assert.Equal(ReconciliationStatuses.Drifted, Assert.Single(drift).Status);
        });

        await Step("offboarding: the platform team retires the executor key; it signs no new record, and the store still verifies", async () =>
        {
            Assert.False((await platform.EvidenceStore.RetireExecutorKey("partner-executor")).Active);
            Assert.NotNull((await controller.EvidenceStore.ListExecutorKeys()).Single(k => k.KeyId == "partner-executor").RetiredAt);
            var late = await executor.Record(new RecordExecutionRequest
            {
                Decision = await Decide(Emergency("INC-1043")),
                ExecutedCapability = "partner-secret-emergency.revoke",
                Outcome = ExecutionOutcomes.Success,
                ResourceId = Orders,
                ResourceAction = ResourceActions.Revoke,
                PriorResource = await controller.EvidenceStore.ResourceHead(Orders),
            });
            var refused = await Assert.ThrowsAsync<ValidationException>(() => controller.EvidenceStore.Submit(late));
            Assert.Equal("evidence_executor_key_retired", refused.Code);
            var store = await controller.EvidenceStore.Verify();
            Assert.True(store.Verified, string.Join("; ", store.Failures.Select(f => $"{f.StoreSequence} {f.Reason} {f.Detail}")));
            Assert.Equal((await controller.EvidenceStore.Status()).Entries, store.CheckedEntries);
            // The whole store, exported, verifies offline with the keys retired ones included.
            var export = new List<EvidenceEvent>();
            await foreach (var e in controller.EvidenceStore.ExportAll()) export.Add(e);
            var offline = OfflineVerifier.VerifyEvidenceEvents(export,
                new EvidenceVerifyOptions { NAPublicKeys = [naKey], ExecutorKeys = await controller.EvidenceStore.ListExecutorKeys() });
            Assert.True(offline.Verified, string.Join("; ", offline.Failures.Select(f => $"{f.StoreSequence} {f.Reason} {f.Detail}")));
            Assert.Equal((store.CheckedEntries, store.Decisions, store.Executions), (offline.CheckedEntries, offline.Decisions, offline.Executions));
        });
    }
}
