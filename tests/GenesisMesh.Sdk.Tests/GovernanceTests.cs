using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Governed actions (1.9.0), with and without the evidence outbox, the decision summary and reconciliation: the
/// TypeScript SDK's governance tests, against an NA that answers with the Python reference's decisions, signed again
/// with the test key over fresh timestamps.
/// </summary>
public class GovernanceTests
{
    private const string NaKey = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=";
    private static readonly SeedSigner Signer = new(TestHelpers.ZeroSeedB64, "test");

    private static readonly Lazy<JsonObject> VectorData = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    private static JsonObject Vector(string name) => VectorData.Value[name]!.DeepClone().AsObject();

    private static T Typed<T>(string name) => JsonSerializer.Deserialize<T>(Vector(name).ToJsonString(), Auth.SerializerOptions)!;

    private static string AttestationId => (string)VectorData.Value["attestation"]!["attestation_id"]!;

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status, string code, string message = "refused") =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString());

    private static HttpResponseMessage Recorded(string status = "recorded") => Json(201, $$"""
        {"status":"{{status}}","entry":{"store_sequence":7,"entry_kind":"execution","recorded_at":"2026-10-30T12:00:03Z",
        "payload_digest":"00","prev_entry_digest":null,"decision_id":null,"context_id":null,"vendor_id":null,
        "attestation_id":null,"capability":null,"outcome":null,"evidence_id":null,"executor_sovereign_id":null,
        "exec_sequence_no":null,"resource_id":null,"resource_action":null,"resource_sequence":null},"entry_digest":"bb"}
        """);

    /// <summary>Signs a decision as the NA does, with the test key.</summary>
    private static void Sign(JsonObject decision)
    {
        decision.Remove("signature");
        var canonical = Canonical.Of(JsonSerializer.SerializeToElement(decision), ["signature"], OfflineVerifier.DecisionOmittedWhenAbsent);
        var sig = Signer.SignAsync(Encoding.UTF8.GetBytes(canonical)).AsTask().GetAwaiter().GetResult();
        decision["signature"] = new JsonObject { ["key_id"] = "test", ["sig"] = Convert.ToBase64String(sig) };
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }

    /// <summary>
    /// The NA: it evaluates with the reference's allowed (or denied) decision, for the request's context, and answers
    /// head reads and submissions as told.
    /// </summary>
    private sealed class FakeNa
    {
        public bool Denied { get; set; }
        /// <summary>The decision's context, when not the request's.</summary>
        public string? AnswerContext { get; set; }
        public TimeSpan Validity { get; set; } = TimeSpan.FromMinutes(1);
        /// <summary>Applied to the signed decision, as an attacker would.</summary>
        public Action<JsonObject>? Tamper { get; set; }
        /// <summary>The head the NA holds; null for a resource with no history.</summary>
        public (long Sequence, string Digest)? Head { get; set; }
        public Func<Task>? DuringHeadRead { get; set; }
        /// <summary>Answers to submissions, in turn; then <see cref="Recorded"/>.</summary>
        public Queue<Func<HttpResponseMessage>> Submissions { get; } = new();
        public Func<CancellationToken, Task>? DuringSubmit { get; set; }
        public List<JsonObject> Evaluated { get; } = [];
        public List<JsonObject> Submitted { get; } = [];
        public int HeadReads { get; private set; }

        public void Fail(int times, Func<HttpResponseMessage> answer)
        {
            for (var i = 0; i < times; i++) Submissions.Enqueue(answer);
        }

        public static Func<HttpResponseMessage> Offline => () => throw new HttpRequestException("offline");

        public async Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(CancellationToken.None))!.AsObject();
            if (path == "/admin/boundary/evaluate")
            {
                Evaluated.Add(body!);
                var evaluation = Vector(Denied ? "denied" : "allowed");
                var decision = evaluation["decision"]!.AsObject();
                decision["context_id"] = AnswerContext ?? (string?)body!["context"]?["context_id"];
                decision["decision_made_at"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddSeconds(-1));
                decision["decision_valid_until"] = Canonical.Timestamp(DateTimeOffset.UtcNow + Validity);
                Sign(decision);
                Tamper?.Invoke(decision);
                return Json(200, evaluation.ToJsonString());
            }
            if (path.StartsWith("/admin/evidence/resource-heads/", StringComparison.Ordinal))
            {
                HeadReads++;
                if (DuringHeadRead is not null) await DuringHeadRead();
                return Head is { } head
                    ? Json(200, new JsonObject
                    {
                        ["resource_id"] = path["/admin/evidence/resource-heads/".Length..],
                        ["resource_sequence"] = head.Sequence,
                        ["record_digest"] = head.Digest,
                    }.ToJsonString())
                    : Error(404, "resource_not_found", "no history");
            }
            if (path == "/evidence/execution")
            {
                Submitted.Add(body!["evidence"]!.AsObject());
                if (DuringSubmit is not null) await DuringSubmit(ct);
                return Submissions.TryDequeue(out var answer) ? answer() : Recorded();
            }
            return Error(404, "not_found", path);
        }

        public ExecutionEvidence SubmittedRecord(int i) =>
            JsonSerializer.Deserialize<ExecutionEvidence>(Submitted[i].ToJsonString(), Auth.SerializerOptions)!;
    }

    private sealed class Setup
    {
        public FakeNa Na { get; } = new();
        public GenesisMeshClient Gm { get; }
        public IOutbox<OutboxEntry>? Outbox { get; }
        public ExecutionRecorder Recorder { get; } = new("executor", new SeedSigner(TestHelpers.ZeroSeedB64, "test"));
        public int Actions { get; private set; }

        public Setup(IOutbox<OutboxEntry>? outbox = null)
        {
            Outbox = outbox;
            Gm = new GenesisMeshClient(new ClientOptions
            {
                BaseUrl = "http://localhost",
                SigningKey = TestHelpers.ZeroSeedB64,
                KeyId = "ops",
                Audience = "TEST",
                HttpHandler = new AsyncHandler(Na.Answer),
                Outbox = outbox,
            });
        }

        public static GovernedActionRequest Request(string resourceId = "kv:v/s", GovernedVerification? verify = null) => new()
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = resourceId,
            ResourceAction = ResourceActions.Rotate,
            Context = new EvaluationContext { RequestParameters = new Dictionary<string, object?> { ["app_id"] = "app-1" } },
            Verify = verify ?? Verification(),
        };

        public static GovernedVerification Verification() => new()
        {
            OperatorPublicKeys = [NaKey],
            ExpectedPolicies = [Typed<BoundaryPolicy>("policy")],
            ExpectedAttestation = Typed<MembershipAttestation>("attestation"),
        };

        public Task<ActionReport<string>?> Action(BoundaryDecision decision, CancellationToken ct)
        {
            Actions++;
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string>
            {
                Value = "returned only",
                ExecutionParameters = new Dictionary<string, object?> { ["secret_version"] = "v2" },
            });
        }

        public Task<GovernedActionResult<string>> Run(GovernedActionRequest? request = null) =>
            Governance.GovernedAction(Gm, Recorder, request ?? Request(), Action);

        public Task<ExecutionEvidence> Sign(ExecutionEvidence? prior = null, string resourceId = "kv:v/s", bool onDecisionChain = false) =>
            Recorder.Record(new RecordExecutionRequest
            {
                Decision = new BoundaryDecision
                {
                    DecisionId = "dec-1",
                    ContextId = "ctx-1",
                    DecisionMadeAt = "2026-10-30T11:59:59Z",
                },
                ExecutedCapability = "sp-secret.rotate",
                Outcome = ExecutionOutcomes.Success,
                ResourceId = onDecisionChain ? null : resourceId,
                ResourceAction = onDecisionChain ? null : ResourceActions.Rotate,
                PriorResource = prior is null || onDecisionChain ? null : ExecutionRecorder.HeadOf(prior),
                PriorRecord = onDecisionChain ? prior : null,
            });
    }

    private static Setup WithOutbox() => new(new MemoryOutbox<OutboxEntry>());

    private static string Params(ExecutionEvidence e) => e.ExecutionParameters.GetRawText();

    // ── Without an outbox ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifiesAnAllow_ReadsTheHead_AndRecordsTheActionsReport()
    {
        var x = new Setup();
        x.Na.Head = (3, "prior");
        var result = await x.Run();
        Assert.True(result.Authorized);
        Assert.Equal("returned only", result.Value);
        Assert.Equal((4L, "prior"), (result.Evidence!.ResourceSequence!.Value, result.Evidence.PrevResourceDigest));
        Assert.Equal("""{"secret_version":"v2"}""", Params(result.Evidence));
        Assert.Equal("recorded", result.Submission!.Status);
        Assert.Null(result.Queued);
        Assert.Equal(ExecutionOutcomes.Success, x.Na.SubmittedRecord(0).Outcome);
        Assert.True(OfflineVerifier.VerifyExecutionSignature(x.Na.Submitted[0].ToJsonString(), NaKey));
        Assert.Equal(1, x.Actions);
        // The evaluation went out with the request's context and a context ID the decision is checked against.
        var evaluated = Assert.Single(x.Na.Evaluated);
        Assert.Equal(AttestationId, (string?)evaluated["attestation_id"]);
        Assert.Equal("app-1", (string?)evaluated["context"]!["request_parameters"]!["app_id"]);
        Assert.Equal(result.Evaluation.Decision.ContextId, (string?)evaluated["context"]!["context_id"]);
        Assert.Matches("^[0-9a-f-]{36}$", result.Evaluation.Decision.ContextId);
        Assert.Equal(new[] { "vendor-sp-secret@1" }, result.Summary.AppliedPolicies);
    }

    [Fact]
    public async Task ReturnsAVerifiedDeny_WithoutTheAction_TheHeadOrEvidence()
    {
        var x = new Setup();
        x.Na.Denied = true;
        var result = await x.Run();
        Assert.False(result.Authorized);
        Assert.False(result.Summary.Authorized);
        Assert.Null(result.Evidence);
        Assert.Equal((0, 0, 0), (x.Actions, x.Na.HeadReads, x.Na.Submitted.Count));
    }

    [Fact]
    public async Task RefusesToEvaluateWithoutVerificationKeysOrPolicies()
    {
        var x = new Setup();
        foreach (var (verify, reason) in new (GovernedVerification?, string)[]
        {
            (null, "verification_keys_required"),
            (new GovernedVerification { ExpectedPolicies = [] }, "verification_keys_required"),
            (new GovernedVerification { OperatorPublicKeys = [NaKey] }, "policy_expectations_required"),
        })
        {
            var request = new GovernedActionRequest
            {
                AttestationId = AttestationId,
                RequestedCapability = "sp-secret.rotate",
                Verify = verify,
            };
            var error = await Assert.ThrowsAsync<DecisionVerificationException>(() => x.Run(request));
            Assert.Equal(reason, error.Code);
        }
        Assert.Empty(x.Na.Evaluated);
        Assert.Equal(0, x.Actions);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "unsigned", "missing_signature" },
        { "expired", "decision_expired" },
        { "tampered", "invalid_signature" },
        { "another attestation", "attestation_binding_mismatch" },
        { "other policies", "policy_binding_mismatch" },
        { "another context", "context_binding_mismatch" },
        { "unknown field", "unknown_field" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task BlocksAnAllowThatFailsVerification(string mode, string reason)
    {
        var x = new Setup();
        var request = Setup.Request();
        switch (mode)
        {
            case "unsigned": x.Na.Tamper = d => d["signature"] = null; break;
            case "expired": x.Na.Validity = TimeSpan.FromDays(-1); break;
            case "tampered": x.Na.Tamper = d => d["denial_reason"] = "changed"; break;
            case "another attestation":
                request = new GovernedActionRequest
                {
                    AttestationId = "different",
                    RequestedCapability = request.RequestedCapability,
                    Verify = request.Verify,
                };
                break;
            case "other policies":
                request = Setup.Request(verify: new GovernedVerification
                {
                    OperatorPublicKeys = [NaKey],
                    ExpectedPolicies = [],
                    ExpectedAttestation = Typed<MembershipAttestation>("attestation"),
                });
                break;
            case "another context": x.Na.AnswerContext = "different-context"; break;
            // Signed by the NA, but read into the typed decision the field would be dropped: verified as received.
            case "unknown field": x.Na.Tamper = d => { d["from_the_future"] = 1; Sign(d); }; break;
        }
        var error = await Assert.ThrowsAsync<DecisionVerificationException>(() => x.Run(request));
        Assert.Equal(reason, error.Code);
        Assert.Equal(0, error.Status);
        Assert.Equal(0, x.Actions);
        Assert.Empty(x.Na.Submitted);
    }

    [Fact]
    public async Task RequiresTheExpectedAttestation_ForAnAllowOnAnAttestation()
    {
        var x = new Setup();
        var verify = new GovernedVerification { OperatorPublicKeys = [NaKey], ExpectedPolicies = [Typed<BoundaryPolicy>("policy")] };
        var error = await Assert.ThrowsAsync<DecisionVerificationException>(() => x.Run(Setup.Request(verify: verify)));
        Assert.Equal("attestation_expectation_required", error.Code);
        // A denial needs none.
        x.Na.Denied = true;
        Assert.False((await x.Run(Setup.Request(verify: verify))).Authorized);
        Assert.Equal(0, x.Actions);
    }

    [Fact]
    public async Task VerifiesUnderTheKeyHistory_AndRefusesBothKindsOfKeys()
    {
        var x = new Setup();
        var history = NAKeyHistory.ValidAlways([NaKey]);
        var verify = new GovernedVerification
        {
            KeyHistory = history,
            ExpectedPolicies = [Typed<BoundaryPolicy>("policy")],
            ExpectedAttestation = Typed<MembershipAttestation>("attestation"),
        };
        Assert.True((await x.Run(Setup.Request(verify: verify))).Authorized);
        var both = new GovernedVerification
        {
            OperatorPublicKeys = [NaKey],
            KeyHistory = history,
            ExpectedPolicies = verify.ExpectedPolicies,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => x.Run(Setup.Request(verify: both)));
        Assert.Single(x.Na.Evaluated);
    }

    [Fact]
    public async Task ChecksTheExpiryAgainAfterReadingTheHead()
    {
        var x = new Setup();
        x.Na.Validity = TimeSpan.FromSeconds(1.5);
        x.Na.DuringHeadRead = () => Task.Delay(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<DecisionVerificationException>(() => x.Run());
        Assert.Equal("decision_expired", error.Code);
        Assert.Equal(0, x.Actions);
    }

    [Fact]
    public async Task UsesAGivenHead_OrNoneForANewResource_WithoutReadingIt()
    {
        var x = new Setup();
        x.Na.Submissions.Enqueue(() => Recorded("duplicate"));
        var request = Setup.Request();
        var given = await x.Run(new GovernedActionRequest
        {
            AttestationId = request.AttestationId,
            RequestedCapability = request.RequestedCapability,
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction,
            Verify = request.Verify,
            PriorResource = new ResourceHead { ResourceSequence = 8, RecordDigest = "given" },
        });
        Assert.Equal("duplicate", given.Submission!.Status);
        Assert.Equal((9L, "given"), (given.Evidence!.ResourceSequence!.Value, given.Evidence.PrevResourceDigest));
        var fresh = await x.Run(new GovernedActionRequest
        {
            AttestationId = request.AttestationId,
            RequestedCapability = request.RequestedCapability,
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction,
            Verify = request.Verify,
            NewResource = true,
        });
        Assert.Equal((1L, (string?)null), (fresh.Evidence!.ResourceSequence!.Value, fresh.Evidence.PrevResourceDigest));
        Assert.Equal(0, x.Na.HeadReads);
    }

    [Fact]
    public async Task RecordsTheActionsFailure_WithoutItsErrorText()
    {
        var x = new Setup();
        var failure = new InvalidOperationException("secret-value");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Governance.GovernedAction<string>(x.Gm, x.Recorder, Setup.Request(), (_, _) => throw failure));
        Assert.Same(failure, thrown);
        var record = x.Na.SubmittedRecord(0);
        Assert.Equal((ExecutionOutcomes.Failure, "action failed"), (record.Outcome, record.OutcomeDetail));
        Assert.DoesNotContain("secret-value", x.Na.Submitted[0].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsBothErrors_WhenTheFailureCannotBeRecorded()
    {
        var x = new Setup();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        var failure = new InvalidOperationException("action");
        var error = await Assert.ThrowsAsync<GovernedActionException>(() =>
            Governance.GovernedAction<string>(x.Gm, x.Recorder, Setup.Request(), (_, _) => throw failure));
        Assert.Equal("governed_action_unrecorded", error.Code);
        Assert.Same(failure, error.InnerException);
        Assert.IsType<NetworkException>(error.EvidenceError);
        Assert.Equal(ExecutionOutcomes.Failure, error.Evidence!.Outcome);
    }

    [Fact]
    public async Task DoesNotRunTheActionTwice_WhenTheSubmissionFails()
    {
        var x = new Setup();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await Assert.ThrowsAsync<NetworkException>(() => x.Run());
        Assert.Equal(1, x.Actions);
    }

    [Fact]
    public async Task RefusesSecretMetadata_BeforeAnythingIsSigned()
    {
        var x = new Setup();
        await Assert.ThrowsAsync<SecretMaterialException>(() => Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(),
            (_, _) => Task.FromResult<ActionReport<string>?>(new ActionReport<string>
            {
                ExecutionParameters = new Dictionary<string, object?> { ["client_secret"] = "s3cr3t" },
            })));
        Assert.Empty(x.Na.Submitted);
    }

    [Fact]
    public async Task RecordsAnEmptyReport_ForAnActionThatReturnsNone()
    {
        var x = new Setup();
        var result = await Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(),
            (_, _) => Task.FromResult<ActionReport<string>?>(null));
        Assert.Null(result.Value);
        Assert.Equal(("{}", ExecutionOutcomes.Success), (Params(result.Evidence!), result.Evidence!.Outcome));
    }

    [Fact]
    public async Task RecordsWithoutAResource_OnTheDecisionAlone()
    {
        var x = new Setup();
        var request = Setup.Request();
        var result = await x.Run(new GovernedActionRequest
        {
            AttestationId = request.AttestationId,
            RequestedCapability = request.RequestedCapability,
            Verify = request.Verify,
        });
        Assert.Null(result.Evidence!.ResourceId);
        Assert.Equal(0, x.Na.HeadReads);
    }

    [Fact]
    public async Task RefusesAnIncompleteRequest_BeforeEvaluating()
    {
        var x = new Setup();
        var r = Setup.Request();
        GovernedActionRequest With(string? resource, string? action, ResourceHead? prior = null, bool fresh = false) => new()
        {
            AttestationId = r.AttestationId,
            RequestedCapability = r.RequestedCapability,
            Verify = r.Verify,
            ResourceId = resource,
            ResourceAction = action,
            PriorResource = prior,
            NewResource = fresh,
        };
        var head = new ResourceHead { ResourceSequence = 1, RecordDigest = "d" };
        var go = await Assert.ThrowsAsync<ArgumentException>(() => x.Run(With("kv:v/s", null)));
        Assert.Contains("go together", go.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => x.Run(With("kv:v/s", ResourceActions.Rotate, head, fresh: true)));
        await Assert.ThrowsAsync<ArgumentException>(() => x.Run(With(null, null, head)));
        await Assert.ThrowsAsync<ArgumentException>(() => x.Run(With(null, null, fresh: true)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Governance.GovernedAction(null!, x.Recorder, r, x.Action));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Governance.GovernedAction(x.Gm, null!, r, x.Action));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Governance.GovernedAction(x.Gm, x.Recorder, null!, x.Action));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Governance.GovernedAction<string>(x.Gm, x.Recorder, r, null!));
        Assert.Empty(x.Na.Evaluated);
    }

    [Fact]
    public async Task UsesTheCallersContextId_AndBindsAnAgreementBasis()
    {
        var x = new Setup();
        var r = Setup.Request();
        var result = await x.Run(new GovernedActionRequest
        {
            AttestationId = r.AttestationId,
            RequestedCapability = r.RequestedCapability,
            Verify = r.Verify,
            Context = new EvaluationContext { ContextId = "ctx-given", Attributes = new Dictionary<string, object?> { ["owner"] = "team" } },
        });
        Assert.Equal("ctx-given", result.Evaluation.Decision.ContextId);
        Assert.Equal("team", (string?)x.Na.Evaluated[0]["context"]!["attributes"]!["owner"]);

        // The reference's decision names the attestation as its agreement: another agreement is refused.
        var agreement = new AgreementRecord { AgreementId = "another-agreement" };
        var error = await Assert.ThrowsAsync<DecisionVerificationException>(() => x.Run(new GovernedActionRequest
        {
            Agreement = agreement,
            RequestedCapability = r.RequestedCapability,
            Verify = r.Verify,
        }));
        Assert.Equal("agreement_binding_mismatch", error.Code);
    }

    [Fact]
    public async Task CancelsBeforeEvaluating()
    {
        var x = new Setup();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(), x.Action, cancelled.Token));
        Assert.Empty(x.Na.Evaluated);
        Assert.Equal(0, x.Actions);
    }

    [Fact]
    public async Task CancelsBeforeTheAction_AfterTheDecision()
    {
        var x = new Setup();
        using var cts = new CancellationTokenSource();
        x.Na.DuringHeadRead = () => cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(), x.Action, cts.Token));
        Assert.Single(x.Na.Evaluated);
        Assert.Equal(0, x.Actions);
        Assert.Empty(x.Na.Submitted);
    }

    [Fact]
    public async Task RecordsTheOutcome_WhenTheCallerCancelsDuringTheAction()
    {
        var x = new Setup();
        using var cts = new CancellationTokenSource();
        var result = await Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(), async (_, _) =>
        {
            await cts.CancelAsync();
            return new ActionReport<string> { Value = "done" };
        }, cts.Token);
        Assert.Equal("recorded", result.Submission!.Status);
    }

    // ── With the outbox ──────────────────────────────────────────────────────

    [Fact]
    public async Task Outbox_FailsBeforeEvaluating_WhenItCannotBeRead()
    {
        var outbox = new HookedOutbox { FailList = true };
        var x = new Setup(outbox);
        await Assert.ThrowsAsync<IOException>(() => x.Run());
        Assert.Empty(x.Na.Evaluated);
        Assert.Equal(0, x.Actions);
    }

    [Fact]
    public async Task Outbox_RemovesTheRecordOnceTheNaAdmitsIt()
    {
        var x = WithOutbox();
        var result = await x.Run();
        Assert.Equal("recorded", result.Submission!.Status);
        Assert.Null(result.Queued);
        Assert.Empty(await x.Outbox!.List());
    }

    [Fact]
    public async Task Outbox_KeepsTheRecordPendingWhileTheNaIsUnreachable_ThenAFlushSubmitsIt()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        var result = await x.Run();
        Assert.Equal("returned only", result.Value);
        Assert.Null(result.Submission);
        Assert.Equal((OutboxStates.Pending, 1, 0, "network_error"),
            (result.Queued!.State, result.Queued.Attempts, result.Queued.LastError!.Status, result.Queued.LastError.Code));
        var kept = Assert.Single(await x.Outbox!.List());
        Assert.Equal(JsonSerializer.Serialize(result.Evidence, Auth.SerializerOptions), JsonSerializer.Serialize(kept.Evidence, Auth.SerializerOptions));
        Assert.True(EvidenceOutbox.ParseTime(kept.NextAttemptAt) > DateTimeOffset.UtcNow);

        var waiting = await x.Gm.EvidenceStore.FlushPending();
        Assert.Empty(waiting.Admitted);
        Assert.Equal(kept.Id, Assert.Single(waiting.Pending).Id);
        var flushed = await x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(kept.Id, Assert.Single(flushed.Admitted).Id);
        Assert.Equal(result.Evidence!.EvidenceId, x.Na.SubmittedRecord(x.Na.Submitted.Count - 1).EvidenceId);
        Assert.Empty(await x.Outbox.List());
    }

    public static TheoryData<string> Transient => new()
    {
        "503", "429", "disabled store", "executor key not registered yet", "gap behind a record not admitted yet", "proxy page",
    };

    [Theory]
    [MemberData(nameof(Transient))]
    public async Task Outbox_KeepsTheRecordPending_AfterATransientFailure(string failure)
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(failure switch
        {
            "503" => () => Error(503, "service_unavailable"),
            "429" => () => Error(429, "rate_limited"),
            "disabled store" => () => Error(404, "evidence_store_disabled"),
            "executor key not registered yet" => () => Error(422, "evidence_unknown_executor"),
            "gap behind a record not admitted yet" => () => Error(422, "resource_chain_gap"),
            _ => () => Json(403, "<html>Forbidden</html>"),
        });
        Assert.Equal(OutboxStates.Pending, (await x.Run()).Queued!.State);
    }

    [Fact]
    public async Task Outbox_DeadLettersARefusedRecord_WithItsCode_AndKeepsIt()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(() => Error(409, "evidence_conflict", "taken"));
        var result = await x.Run();
        Assert.Equal((OutboxStates.DeadLetter, 409, "evidence_conflict"),
            (result.Queued!.State, result.Queued.LastError!.Status, result.Queued.LastError.Code));
        var flushed = await x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        Assert.Empty(flushed.Admitted.Concat(flushed.Pending).Concat(flushed.DeadLettered));
        Assert.Equal(OutboxStates.DeadLetter, Assert.Single(await x.Outbox!.List()).State);
        Assert.Single(x.Na.Submitted);
    }

    [Fact]
    public async Task Outbox_ChainsASecondActionFromThePendingHead_AndSubmitsBothInOrder()
    {
        var x = WithOutbox();
        x.Na.Head = (3, "prior");
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        var first = await x.Run();
        var second = await x.Run();
        Assert.Equal(1, x.Na.HeadReads);
        Assert.Equal((5L, ExecutionRecorder.Digest(first.Evidence!)), (second.Evidence!.ResourceSequence!.Value, second.Evidence.PrevResourceDigest));
        // The NA just answered the evaluation: the first record goes first, despite its backoff.
        Assert.Equal("recorded", second.Submission!.Status);
        Assert.Equal(new[] { first.Evidence!.EvidenceId, first.Evidence.EvidenceId, second.Evidence.EvidenceId },
            Enumerable.Range(0, 3).Select(i => x.Na.SubmittedRecord(i).EvidenceId));
        Assert.Empty(await x.Outbox!.List());
    }

    [Fact]
    public async Task Outbox_SendsARecordWhosePredecessorIsRefused_AndDeadLettersItForTheGap()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await x.Run();
        x.Na.Submissions.Enqueue(() => Error(409, "evidence_conflict", "taken"));
        x.Na.Submissions.Enqueue(() => Error(422, "resource_chain_gap", "gap"));
        var second = await x.Run();
        Assert.Equal((OutboxStates.DeadLetter, 422, EvidenceOutbox.PredecessorDeadLettered),
            (second.Queued!.State, second.Queued.LastError!.Status, second.Queued.LastError.Code));
        Assert.Contains("resource_chain_gap", second.Queued.LastError.Message, StringComparison.Ordinal);
        Assert.Equal(3, x.Na.Submitted.Count);
        Assert.Equal(second.Evidence!.EvidenceId, x.Na.SubmittedRecord(2).EvidenceId);
        Assert.Equal(new[] { OutboxStates.DeadLetter, OutboxStates.DeadLetter }, (await x.Outbox!.List()).Select(e => e.State));
        // A third action no longer chains from the dead records.
        x.Na.Head = (9, "na-head");
        var third = await x.Run();
        Assert.Equal((10L, "na-head"), (third.Evidence!.ResourceSequence!.Value, third.Evidence.PrevResourceDigest));
    }

    [Fact]
    public async Task Outbox_SendsANewRecordWhosePredecessorIsADeadLetter_SoTheNaCanQuarantineIt()
    {
        var x = WithOutbox();
        var store = x.Gm.EvidenceStore;
        static HttpResponseMessage Retired() => Error(422, "evidence_executor_key_retired", "signing key is retired");
        var a = await x.Sign();
        x.Na.Submissions.Enqueue(Retired);
        Assert.Equal(OutboxStates.DeadLetter, (await store.Enqueue(a)).Queued!.State);
        var b = await x.Sign(a);
        // The NA refuses it for its own reason (and quarantines it): that refusal is kept.
        x.Na.Submissions.Enqueue(Retired);
        var refused = (await store.Enqueue(b)).Queued!;
        Assert.Equal((OutboxStates.DeadLetter, "evidence_executor_key_retired"), (refused.State, refused.LastError!.Code));
        Assert.Equal(2, x.Na.Submitted.Count);
        // A record behind one that is merely pending still waits for it.
        var c = await x.Sign(b);
        var d = await x.Sign(c);
        x.Na.Fail(2, FakeNa.Offline);
        await store.Enqueue(c);
        var waiting = (await store.Enqueue(d)).Queued!;
        Assert.Equal((OutboxStates.Pending, 0), (waiting.State, waiting.Attempts));
        // A dead letter enqueued again stays one, unsent.
        Assert.Equal(OutboxStates.DeadLetter, (await store.Enqueue(a)).Queued!.State);
        Assert.Equal(4, x.Na.Submitted.Count);
    }

    [Fact]
    public async Task Outbox_WaitsOnTheDecisionChainAsOnTheResourceChain()
    {
        var x = WithOutbox();
        var store = x.Gm.EvidenceStore;
        var a = await x.Sign(onDecisionChain: true);
        var b = await x.Sign(a, onDecisionChain: true);
        x.Na.Fail(10, FakeNa.Offline);
        await store.Enqueue(a);
        var queued = (await store.Enqueue(b)).Queued!;
        Assert.Equal((OutboxStates.Pending, 0), (queued.State, queued.Attempts));
        // Inline, b waits behind a's failed retry.
        Assert.Equal(new[] { a.EvidenceId, a.EvidenceId }, Enumerable.Range(0, x.Na.Submitted.Count).Select(i => x.Na.SubmittedRecord(i).EvidenceId));
    }

    [Fact]
    public async Task Outbox_ResubmitsARecordAlreadyInIt_AndRefusesAnotherRecordWithItsId()
    {
        var x = WithOutbox();
        var store = x.Gm.EvidenceStore;
        var a = await x.Sign();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await store.Enqueue(a);
        Assert.Equal("recorded", (await store.Enqueue(a)).Submission!.Status);
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await store.Enqueue(a);
        var imposter = JsonSerializer.Deserialize<ExecutionEvidence>(JsonSerializer.Serialize(a, Auth.SerializerOptions), Auth.SerializerOptions)!;
        imposter.Outcome = ExecutionOutcomes.Failure;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.Enqueue(imposter));
        Assert.Contains("different record", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outbox_ReportsAnAdmittedRecordAsAdmitted_EvenWhenItCannotBeRemoved()
    {
        var outbox = new HookedOutbox { FailRemoves = 1 };
        var x = new Setup(outbox);
        Assert.Equal("recorded", (await x.Run()).Submission!.Status);
        // The leftover is resubmitted, found a duplicate and removed by the next flush.
        x.Na.Submissions.Enqueue(() => Recorded("duplicate"));
        Assert.Single((await x.Gm.EvidenceStore.FlushPending()).Admitted);
        Assert.Empty(await outbox.List());
    }

    [Fact]
    public async Task Outbox_KeepsTheNasAnswer_WhenTheEntryCannotBeUpdated()
    {
        var outbox = new HookedOutbox { FailUpdates = true };
        var x = new Setup(outbox);
        x.Na.Submissions.Enqueue(() => Error(409, "evidence_conflict"));
        var result = await x.Run();
        // The answer stands; the stored entry keeps its previous state for the next flush to settle.
        Assert.Equal(OutboxStates.DeadLetter, result.Queued!.State);
        Assert.Equal(OutboxStates.Pending, Assert.Single(await outbox.List()).State);
    }

    [Fact]
    public async Task Outbox_EndsAFlushAtTheFirstTransientFailure_AndSkipsRecordsInBackoff()
    {
        var x = WithOutbox();
        x.Na.Fail(10, FakeNa.Offline);
        await x.Run();
        await x.Run(Setup.Request("kv:v/other"));
        Assert.Equal(new[] { 1, 1 }, (await x.Outbox!.List()).Select(e => e.Attempts));
        var before = x.Na.Submitted.Count;
        var flushed = await x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(before + 1, x.Na.Submitted.Count);
        Assert.Equal(new[] { 2, 1 }, flushed.Pending.Select(e => e.Attempts));
        Assert.Equal(2, (await x.Gm.EvidenceStore.FlushPending()).Pending.Count);
        Assert.Equal(before + 1, x.Na.Submitted.Count);
    }

    [Fact]
    public async Task Outbox_SharesOneRunBetweenConcurrentFlushes()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await x.Run();
        var release = new TaskCompletionSource();
        x.Na.DuringSubmit = ct => release.Task.WaitAsync(ct);
        var a = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        var b = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        // A run that keeps the backoff joins one that ignores it; the reverse waits and starts its own.
        var c = x.Gm.EvidenceStore.FlushPending();
        release.SetResult();
        Assert.Single((await a).Admitted);
        Assert.Same(await a, await b);
        Assert.Same(await a, await c);
        Assert.Equal(2, x.Na.Submitted.Count);
    }

    [Fact]
    public async Task Outbox_StartsItsOwnRun_AfterOneThatTriesLess()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        var inBackoff = (await x.Run()).Evidence!;
        var due = await x.Sign(resourceId: "kv:v/other");
        await x.Outbox!.Add(EvidenceOutbox.Entry(due));
        var release = new TaskCompletionSource();
        x.Na.DuringSubmit = ct => release.Task.WaitAsync(ct);
        var keepsBackoff = x.Gm.EvidenceStore.FlushPending();
        var ignoresBackoff = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        release.SetResult();
        // The first run skipped the record in backoff; the second waited for it, then tried that record too.
        Assert.Equal(due.EvidenceId, Assert.Single((await keepsBackoff).Admitted).Id);
        Assert.Equal(inBackoff.EvidenceId, Assert.Single((await keepsBackoff).Pending).Id);
        Assert.Equal(inBackoff.EvidenceId, Assert.Single((await ignoresBackoff).Admitted).Id);
        Assert.Empty(await x.Outbox.List());
    }

    [Fact]
    public async Task Outbox_FlushJoinersSeeTheRunsFailure_AndAFlushWithoutAnOutboxFails()
    {
        var outbox = new HookedOutbox();
        var x = new Setup(outbox);
        var release = new TaskCompletionSource();
        outbox.DuringList = () => release.Task;
        outbox.FailList = true;
        var a = x.Gm.EvidenceStore.FlushPending();
        var b = x.Gm.EvidenceStore.FlushPending();
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => a);
        await Assert.ThrowsAsync<IOException>(() => b);

        var bare = new Setup();
        var error = await Assert.ThrowsAsync<OutboxException>(() => bare.Gm.EvidenceStore.FlushPending());
        Assert.Equal("outbox_required", error.Code);
        await Assert.ThrowsAsync<OutboxException>(() => bare.Gm.EvidenceStore.Enqueue(new ExecutionEvidence()));
        await Assert.ThrowsAsync<OutboxException>(() => bare.Gm.EvidenceStore.PendingHead("kv:v/s"));
        Assert.Null(bare.Gm.EvidenceStore.Outbox);
        Assert.Same(outbox, x.Gm.EvidenceStore.Outbox);
    }

    [Fact]
    public async Task Outbox_AFlushLeavesARecordInFlightToItsEnqueue()
    {
        var x = WithOutbox();
        var a = await x.Sign();
        var release = new TaskCompletionSource();
        x.Na.DuringSubmit = ct => release.Task.WaitAsync(ct);
        var enqueued = x.Gm.EvidenceStore.Enqueue(a);
        var flushed = await x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        Assert.Equal(a.EvidenceId, Assert.Single(flushed.Pending).Id);
        release.SetResult();
        Assert.Equal("recorded", (await enqueued).Submission!.Status);
        Assert.Single(x.Na.Submitted);
    }

    [Fact]
    public async Task Outbox_AFlushThatWaitedOnAFailedRunStartsItsOwn()
    {
        var outbox = new HookedOutbox();
        var x = new Setup(outbox);
        var release = new TaskCompletionSource();
        outbox.DuringList = () => release.Task;
        outbox.FailList = true;
        var keepsBackoff = x.Gm.EvidenceStore.FlushPending();
        var ignoresBackoff = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => keepsBackoff);
        // Not the first run's failure: its own, from the run it started after.
        await Assert.ThrowsAsync<IOException>(() => ignoresBackoff);
        Assert.Equal(2, outbox.Lists);
    }

    [Fact]
    public async Task Outbox_AFlushWhoseCallerCancelsLetsTheNextOneRun()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await x.Run();
        var release = new TaskCompletionSource();
        x.Na.DuringSubmit = ct => release.Task.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        var cancelled = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true }, cts.Token);
        var joiner = x.Gm.EvidenceStore.FlushPending(new FlushOptions { IgnoreBackoff = true });
        await cts.CancelAsync();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        // The joiner started a run of its own and admitted the record.
        Assert.Single((await joiner).Admitted);
    }

    [Fact]
    public async Task Outbox_RecordsTheOutcomeWithoutRefusedMetadata_AndReportsTheRefusal()
    {
        var x = WithOutbox();
        var actions = 0;
        var error = await Assert.ThrowsAsync<MetadataRefusedException>(() => Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(), (_, _) =>
        {
            actions++;
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string>
            {
                Value = "v",
                OutcomeDetail = "rotated",
                ExecutionParameters = new Dictionary<string, object?> { ["client_secret"] = "s3cr3t", ["secret_version"] = "v2" },
            });
        }));
        Assert.Equal(("governed_action_metadata_refused", "v"), (error.Code, error.Value));
        Assert.Equal(new[] { "client_secret" }, error.Dropped);
        Assert.Equal("recorded", error.Submission!.Status);
        Assert.Null(error.Queued);
        Assert.Equal(ExecutionOutcomes.Success, error.Evidence.Outcome);
        Assert.Equal("""{"secret_version":"v2"}""", Params(error.Evidence));
        Assert.Equal("rotated [secret guard dropped: client_secret]", error.Evidence.OutcomeDetail);
        Assert.IsType<SecretMaterialException>(error.InnerException);
        Assert.Equal(error.Evidence.EvidenceId, x.Na.SubmittedRecord(0).EvidenceId);
        Assert.DoesNotContain("s3cr3t", x.Na.Submitted[0].ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, actions);
    }

    [Fact]
    public async Task Outbox_RecordsWithoutAStringTheNaWouldNotReadAlike()
    {
        var x = WithOutbox();
        var error = await Assert.ThrowsAsync<MetadataRefusedException>(() => Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(),
            (_, _) => Task.FromResult<ActionReport<string>?>(new ActionReport<string>
            {
                ExecutionParameters = new Dictionary<string, object?> { ["note"] = "half \ud800", ["secret_version"] = "v2" },
            })));
        Assert.IsType<StrictJsonException>(error.InnerException);
        Assert.Equal(new[] { "note" }, error.Dropped);
        Assert.Equal("[secret guard dropped: note]", error.Evidence.OutcomeDetail);
    }

    [Fact]
    public async Task Outbox_KeepsTheValueAndSignedRecord_WhenTheOutboxFailsAfterTheAction()
    {
        var outbox = new HookedOutbox { FailAdds = true };
        var x = new Setup(outbox);
        var error = await Assert.ThrowsAsync<EvidenceNotKeptException>(() => x.Run());
        Assert.Equal(("governed_action_evidence_unkept", "returned only"), (error.Code, error.Value));
        Assert.Equal(ExecutionOutcomes.Success, error.Evidence!.Outcome);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Empty(x.Na.Submitted);
    }

    [Fact]
    public async Task Outbox_KeepsTheFailureRecordPending_AndRethrows()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        var failure = new InvalidOperationException("action");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Governance.GovernedAction<string>(x.Gm, x.Recorder, Setup.Request(), (_, _) => throw failure)));
        var kept = Assert.Single(await x.Outbox!.List());
        Assert.Equal((OutboxStates.Pending, 1, ExecutionOutcomes.Failure, "action failed"),
            (kept.State, kept.Attempts, kept.Evidence.Outcome, kept.Evidence.OutcomeDetail));
    }

    [Fact]
    public async Task Outbox_LetsAGivenHeadOrANewResourceOverrideThePendingHead()
    {
        var x = WithOutbox();
        x.Na.Submissions.Enqueue(FakeNa.Offline);
        await x.Run();
        var r = Setup.Request();
        var second = await x.Run(new GovernedActionRequest
        {
            AttestationId = r.AttestationId,
            RequestedCapability = r.RequestedCapability,
            Verify = r.Verify,
            ResourceId = r.ResourceId,
            ResourceAction = r.ResourceAction,
            NewResource = true,
        });
        Assert.Equal((1L, (string?)null), (second.Evidence!.ResourceSequence!.Value, second.Evidence.PrevResourceDigest));
    }

    [Fact]
    public async Task Outbox_PendingHeadIsTheNewestPendingRecordOfTheResource()
    {
        var x = WithOutbox();
        var store = x.Gm.EvidenceStore;
        Assert.Null(await store.PendingHead("kv:v/s"));
        x.Na.Fail(10, FakeNa.Offline);
        var a = await x.Sign();
        var other = await x.Sign(resourceId: "kv:v/other");
        var b = await x.Sign(a);
        foreach (var e in new[] { a, other, b }) await store.Enqueue(e);
        Assert.Equal(b.EvidenceId, (await store.PendingHead("kv:v/s"))!.EvidenceId);
        Assert.Equal(other.EvidenceId, (await store.PendingHead("kv:v/other"))!.EvidenceId);
    }

    /// <summary>An outbox that fails as told.</summary>
    private sealed class HookedOutbox : IOutbox<OutboxEntry>
    {
        private readonly MemoryOutbox<OutboxEntry> _inner = new();
        public bool FailList { get; set; }
        public bool FailAdds { get; set; }
        public bool FailUpdates { get; set; }
        public int FailRemoves { get; set; }
        public Func<Task>? DuringList { get; set; }

        public Task Add(OutboxEntry entry, CancellationToken ct = default) =>
            FailAdds ? Task.FromException(new IOException("disk full")) : _inner.Add(entry, ct);

        public Task Update(OutboxEntry entry, CancellationToken ct = default) =>
            FailUpdates ? Task.FromException(new IOException("disk")) : _inner.Update(entry, ct);

        public Task Remove(string id, CancellationToken ct = default)
        {
            if (FailRemoves <= 0) return _inner.Remove(id, ct);
            FailRemoves--;
            return Task.FromException(new IOException("disk"));
        }

        public int Lists { get; private set; }

        public async Task<IReadOnlyList<OutboxEntry>> List(CancellationToken ct = default)
        {
            Lists++;
            if (DuringList is not null) await DuringList();
            if (FailList) throw new IOException("outbox file 000000000001-a.json is unreadable");
            return await _inner.List(ct);
        }
    }

    // ── The decision summary ─────────────────────────────────────────────────

    [Fact]
    public void SummarizesObserveAndEnforceFailuresApart()
    {
        var summary = Governance.SummarizeDecision(Typed<BoundaryEvaluation>("denied").Decision);
        Assert.False(summary.Authorized);
        Assert.NotNull(summary.DenialReason);
        var observed = Assert.Single(summary.ObservedFailures);
        Assert.Equal(("vendor-sp-secret/owner", "required fact 'attributes.owner' is missing"), (observed.Gate, observed.Detail));
        Assert.Equal(2, summary.EnforcedFailures.Count);
        Assert.Equal(new[] { "vendor-sp-secret@1" }, summary.AppliedPolicies);
        Assert.Equal(AttestationId, summary.AttestationId);
        Assert.False(string.IsNullOrEmpty(summary.DecisionId));
        var bare = Governance.SummarizeDecision(new BoundaryDecision { DecisionId = "d", Authorized = true });
        Assert.Equal((0, 0, 0, (string?)null), (bare.EnforcedFailures.Count, bare.ObservedFailures.Count, bare.AppliedPolicies.Count, bare.AttestationId));
        Assert.Throws<ArgumentNullException>(() => Governance.SummarizeDecision(null!));
    }

    // ── The metadata guard's fallback ────────────────────────────────────────

    [Fact]
    public void WithoutRefusedMetadata_DropsOnlyTheRefusedParametersAndARefusedDetail()
    {
        var cleaned = Governance.WithoutRefusedMetadata(new Dictionary<string, object?>
        {
            ["password"] = "x",
            ["version"] = "v1",
            ["nested"] = new Dictionary<string, object?> { ["token"] = "y" },
        }, "-----BEGIN KEY");
        Assert.Equal(new Dictionary<string, object?> { ["version"] = "v1" }, cleaned.ExecutionParameters);
        Assert.Equal("[secret guard dropped: nested, outcome_detail, password]", cleaned.OutcomeDetail);
        Assert.Equal(new[] { "nested", "outcome_detail", "password" }, cleaned.Dropped);
    }

    [Fact]
    public void WithoutRefusedMetadata_NamesOnlyPlainFieldNamesInTheNote()
    {
        var cleaned = Governance.WithoutRefusedMetadata(new Dictionary<string, object?>
        {
            ["a b"] = new string('x', 130),
            [new string('y', 70)] = new string('z', 130),
            ["ok"] = 1,
        }, null);
        Assert.Equal(new Dictionary<string, object?> { ["ok"] = 1 }, cleaned.ExecutionParameters);
        Assert.Equal("[secret guard dropped: 2 other fields]", cleaned.OutcomeDetail);
        Assert.Equal("[secret guard dropped: 1 other field]",
            Governance.WithoutRefusedMetadata(new Dictionary<string, object?> { ["a b"] = new string('x', 130) }, null).OutcomeDetail);
    }

    [Fact]
    public void WithoutRefusedMetadata_DropsEverything_WhenTheRestIsStillTooLarge()
    {
        // Words, not one opaque run of characters, which the guard would refuse alone as key material.
        var big = Enumerable.Range(0, 4).ToDictionary(i => $"k{i}", _ => (object?)string.Join(' ', Enumerable.Repeat("word", 1000)));
        var cleaned = Governance.WithoutRefusedMetadata(big, null);
        Assert.Empty(cleaned.ExecutionParameters);
        Assert.Equal("[secret guard dropped: k0, k1, k2, k3]", cleaned.OutcomeDetail);
        Assert.Equal(new[] { "k0", "k1", "k2", "k3" }, cleaned.Dropped);
        // A detail kept with the rest is dropped with it, and a note too long to keep leaves the bare note.
        var withDetail = Governance.WithoutRefusedMetadata(big, "rotated");
        Assert.Equal(new[] { "k0", "k1", "k2", "k3", "outcome_detail" }, withDetail.Dropped);
        Assert.Equal("[secret guard dropped: k0, k1, k2, k3, outcome_detail]", withDetail.OutcomeDetail);
        var names = Enumerable.Range(0, 400).ToDictionary(i => $"field_with_a_long_name_{i:D4}_{new string('n', 20)}", _ => (object?)string.Join(' ', Enumerable.Repeat("word", 20)));
        var bare = Governance.WithoutRefusedMetadata(names, null);
        Assert.Equal("[secret guard dropped]", bare.OutcomeDetail);
        Assert.Equal(400, bare.Dropped.Count);
        Assert.Throws<ArgumentNullException>(() => Governance.WithoutRefusedMetadata(null!, null));
    }

    // ── Reconciliation ───────────────────────────────────────────────────────

    private static ResourceState State(string id, string? action = ResourceActions.Create, string parameters = """{"secret_version":"v1"}""") => new()
    {
        ResourceId = id,
        ResourceSequence = 1,
        RecordDigest = "digest",
        LastAction = action,
        LastOutcome = ExecutionOutcomes.Success,
        LastSuccessAction = action,
        LastSuccessParameters = JsonDocument.Parse(parameters).RootElement.Clone(),
        LastExecutedAt = "2026-10-01T00:00:00Z",
        LastDecisionId = "decision",
    };

    [Fact]
    public void Reconcile_ClassifiesUnmanagedDriftedRevokedMissingAndMatchingResources()
    {
        var states = new Dictionary<string, ResourceState>
        {
            ["drift"] = State("drift"),
            ["revoked"] = State("revoked", ResourceActions.Revoke),
            ["missing"] = State("missing"),
            ["match"] = State("match"),
            ["failed"] = State("failed", null),
        };
        var findings = Governance.ReconcileResources(
        [
            new ObservedResource { ResourceId = "new", Exists = true },
            new ObservedResource { ResourceId = "drift", Exists = true, Version = "v2" },
            new ObservedResource { ResourceId = "revoked", Exists = true },
            new ObservedResource { ResourceId = "missing", Exists = false },
            new ObservedResource { ResourceId = "match", Exists = true, Version = "v1" },
            new ObservedResource { ResourceId = "failed", Exists = true },
            new ObservedResource { ResourceId = "gone", Exists = false },
        ], states);
        Assert.Equal(new[]
        {
            ReconciliationStatuses.Unmanaged, ReconciliationStatuses.Drifted, ReconciliationStatuses.PresentAfterRevoke,
            ReconciliationStatuses.Missing, ReconciliationStatuses.InSync, ReconciliationStatuses.Unmanaged, ReconciliationStatuses.InSync,
        }, findings.Select(f => f.Status));
        Assert.Equal("cloud version v2, governed version v1", findings[1].Detail);
        Assert.Same(states["drift"], findings[1].Recorded);
        Assert.Equal("drift", findings[1].Observed!.ResourceId);
        Assert.Equal("absent and never governed", findings[6].Detail);
    }

    [Fact]
    public void Reconcile_InfersMissingResourcesOnlyForACompleteScopedInventory()
    {
        var states = new Dictionary<string, ResourceState> { ["kv:a"] = State("kv:a"), ["other:b"] = State("other:b"), ["kv:c"] = State("kv:c", ResourceActions.Delete) };
        Assert.Empty(Governance.ReconcileResources([], states));
        var missing = Assert.Single(Governance.ReconcileResources([], states, new ReconcileOptions { CompleteInventory = true, ScopePrefix = "kv:" }));
        Assert.Equal(("kv:a", ReconciliationStatuses.Missing, (ObservedResource?)null), (missing.ResourceId, missing.Status, missing.Observed));
        Assert.Equal(2, Governance.ReconcileResources([], states, new ReconcileOptions { CompleteInventory = true }).Count);
        Assert.Throws<ArgumentNullException>(() => Governance.ReconcileResources(null!, states));
        Assert.Throws<ArgumentNullException>(() => Governance.ReconcileResources([], null!));
    }

    [Fact]
    public void Reconcile_ReadsACustomVersionField_AndAbsenceAfterDeletion()
    {
        var deleted = State("deleted", ResourceActions.Delete);
        Assert.Equal(ReconciliationStatuses.InSync, Governance.ReconcileResources(
            [new ObservedResource { ResourceId = "deleted", Exists = false }],
            new Dictionary<string, ResourceState> { ["deleted"] = deleted })[0].Status);
        var custom = State("custom", parameters: """{"revision":2}""");
        var findings = Governance.ReconcileResources(
            [new ObservedResource { ResourceId = "custom", Exists = true, Version = "3" }, new ObservedResource { ResourceId = "same", Exists = true, Version = "2" }],
            new Dictionary<string, ResourceState> { ["custom"] = custom, ["same"] = State("same", parameters: """{"revision":2}""") },
            new ReconcileOptions { VersionKey = "revision" });
        Assert.Equal(new[] { ReconciliationStatuses.Drifted, ReconciliationStatuses.InSync }, findings.Select(f => f.Status));
        // No recorded version, or none observed: nothing to compare.
        Assert.Equal(ReconciliationStatuses.InSync, Governance.ReconcileResources(
            [new ObservedResource { ResourceId = "x", Exists = true, Version = "v9" }],
            new Dictionary<string, ResourceState> { ["x"] = State("x", parameters: "{}") })[0].Status);
    }
}
