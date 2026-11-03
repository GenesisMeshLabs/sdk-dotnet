using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Tests;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The remediation side of governed changes (1.9.0): a denied change cancelled by rotating forward, then disabling the
/// denied version, with the two linked records; and an urgent change, under the ordinary capability, the emergency one
/// or break-glass.
/// </summary>
public class RemediationGovernanceTests
{
    private const string NaKey = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=";
    private static readonly SeedSigner Signer = new(TestHelpers.ZeroSeedB64, "test");

    private static readonly Lazy<JsonObject> VectorData = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    private static JsonObject Vector(string name) => VectorData.Value[name]!.DeepClone().AsObject();

    private static T Typed<T>(string name) => JsonSerializer.Deserialize<T>(Vector(name).ToJsonString(), Auth.SerializerOptions)!;

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status, string code) => Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");

    private static HttpResponseMessage Offline() => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

    private static HttpResponseMessage Recorded() => Json(201, """{"status":"recorded","entry":{},"entry_digest":"d","payload":{}}""");

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }

    /// <summary>The NA: it evaluates with the reference's allowed or denied decision, per capability, and records what it is sent.</summary>
    private sealed class Na
    {
        /// <summary>The capabilities it denies.</summary>
        public HashSet<string> Denied { get; } = [];
        /// <summary>The capabilities it cannot be reached for.</summary>
        public HashSet<string> Unreachable { get; } = [];
        /// <summary>An answer to an evaluation of a capability, instead of a decision.</summary>
        public Dictionary<string, Func<HttpResponseMessage>> Answers { get; } = [];
        public Func<HttpResponseMessage> Remediation { get; set; } = Recorded;
        public Func<HttpResponseMessage>? Execution { get; set; }
        public List<string> Paths { get; } = [];
        public Dictionary<string, Action<JsonObject>> Tamper { get; } = [];
        public List<JsonObject> Evaluated { get; } = [];
        public List<JsonObject> Executions { get; } = [];
        public List<JsonObject> Remediations { get; } = [];
        public List<JsonObject> Glasses { get; } = [];

        public async Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(CancellationToken.None);
            Paths.Add(path);
            switch (path)
            {
                case "/admin/boundary/evaluate":
                    var asked = JsonNode.Parse(text!)!.AsObject();
                    Evaluated.Add(asked);
                    var capability = (string)asked["requested_capability"]!;
                    if (Unreachable.Contains(capability)) return Offline();
                    if (Answers.TryGetValue(capability, out var answer)) return answer();
                    var evaluation = Vector(Denied.Contains(capability) ? "denied" : "allowed");
                    var decision = evaluation["decision"]!.AsObject();
                    if (Tamper.TryGetValue(capability, out var tamper))
                    {
                        tamper(decision);
                        var sorted = new JsonObject();
                        foreach (var key in decision.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList())
                            sorted[key] = decision[key]?.DeepClone();
                        evaluation["decision"] = sorted;
                        decision = sorted;
                    }
                    decision["context_id"] = (string?)asked["context"]?["context_id"];
                    decision["decision_made_at"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddSeconds(-1));
                    decision["decision_valid_until"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddMinutes(1));
                    decision.Remove("signature");
                    var canonical = Canonical.Of(JsonSerializer.SerializeToElement(decision), ["signature"], OfflineVerifier.DecisionOmittedWhenAbsent);
                    var sig = await Signer.SignAsync(Encoding.UTF8.GetBytes(canonical), CancellationToken.None);
                    decision["signature"] = new JsonObject { ["key_id"] = "test", ["sig"] = Convert.ToBase64String(sig) };
                    return Json(200, evaluation.ToJsonString());
                case "/evidence/execution":
                    Executions.Add(JsonNode.Parse(text!)!["evidence"]!.AsObject());
                    if (Execution is not null) return Execution();
                    return Json(201, """
                        {"status":"recorded","entry":{"store_sequence":7,"entry_kind":"execution","recorded_at":"2026-10-30T12:00:03Z",
                        "payload_digest":"00","prev_entry_digest":null,"decision_id":null,"context_id":null,"vendor_id":null,
                        "attestation_id":null,"capability":null,"outcome":null,"evidence_id":null,"executor_sovereign_id":null,
                        "exec_sequence_no":null,"resource_id":null,"resource_action":null,"resource_sequence":null},"entry_digest":"bb"}
                        """);
                case "/evidence/remediations":
                    Remediations.Add(JsonNode.Parse(text!)!["record"]!.AsObject());
                    return Remediation();
                case "/evidence/break-glass":
                    Glasses.Add(JsonNode.Parse(text!)!["record"]!.AsObject());
                    return Recorded();
                default:
                    return path.StartsWith("/admin/evidence/resource-heads/", StringComparison.Ordinal)
                        ? Error(404, "resource_not_found")
                        : Error(404, "not_found");
            }
        }
    }

    /// <summary>A memory record outbox whose reads can fail.</summary>
    private sealed class Records : IOutbox<RecordOutboxEntry>
    {
        private readonly MemoryOutbox<RecordOutboxEntry> _inner = new();
        public bool FailList { get; set; }

        public Task Add(RecordOutboxEntry entry, CancellationToken ct = default) => _inner.Add(entry, ct);

        public Task Update(RecordOutboxEntry entry, CancellationToken ct = default) => _inner.Update(entry, ct);

        public Task Remove(string id, CancellationToken ct = default) => _inner.Remove(id, ct);

        public Task<IReadOnlyList<RecordOutboxEntry>> List(CancellationToken ct = default) =>
            FailList ? throw new IOException("unreadable") : _inner.List(ct);
    }

    private sealed class Setup
    {
        public Na Na { get; } = new();
        public Records Records { get; } = new();
        public GenesisMeshClient Gm { get; }
        public ExecutionRecorder Recorder { get; } = new("executor", new SeedSigner(TestHelpers.ZeroSeedB64, "test"));
        public List<string> Disabled { get; } = [];
        public List<string> Steps { get; } = [];

        public Setup(bool recordOutbox = true, bool evidenceOutbox = false) => Gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = "TEST",
            Retry = new RetrySettings { Attempts = 0 },
            HttpHandler = new AsyncHandler(Na.Answer),
            RecordOutbox = recordOutbox ? Records : null,
            Outbox = evidenceOutbox ? new MemoryOutbox<OutboxEntry>() : null,
        });

        public static GovernedActionRequest Request(string capability = "sp-secret.rotate", string? resource = null) => new()
        {
            AttestationId = (string)VectorData.Value["attestation"]!["attestation_id"]!,
            RequestedCapability = capability,
            ResourceId = resource,
            ResourceAction = resource is null ? null : ResourceActions.Rotate,
            Context = new EvaluationContext
            {
                RequestParameters = new Dictionary<string, object?> { ["app_id"] = "app-1" },
                Attributes = new Dictionary<string, object?> { ["owner"] = "team-a" },
            },
            Verify = new GovernedVerification
            {
                OperatorPublicKeys = [NaKey],
                ExpectedPolicies = [Typed<BoundaryPolicy>("policy")],
                ExpectedAttestation = Typed<MembershipAttestation>("attestation"),
            },
        };

        public static RemediationStatus Due(string state = RemediationStates.RemediationDue, string mode = RemediationModes.Cancel,
            string steps = RemediationSteps.RotateForwardThenDisable) => new()
            {
                SubjectKind = RemediationSubjectKinds.Judgement,
                SubjectId = "j-1",
                ChangeKind = ChangeKinds.Observation,
                ChangeId = "o-1",
                ChangeVersionId = "v2",
                ResourceId = "kv:v/s",
                State = state,
                Mode = mode,
                Steps = steps,
            };

        public RotateForwardOptions<string> Options(Action<Dictionary<string, object?>>? change = null)
        {
            var o = new Dictionary<string, object?>
            {
                ["Remediation"] = Due(),
                ["DeniedVersionId"] = "v2",
                ["Rotation"] = Request(),
                ["NewVersion"] = "v3",
                ["Detail"] = null,
                ["DisableThrows"] = null,
            };
            change?.Invoke(o);
            return new RotateForwardOptions<string>
            {
                Remediation = (RemediationStatus?)o["Remediation"],
                DeniedVersionId = (string?)o["DeniedVersionId"],
                Rotation = (GovernedActionRequest?)o["Rotation"],
                Detail = (string?)o["Detail"],
                Rotate = (_, _) =>
                {
                    Steps.Add("rotate");
                    return Task.FromResult(new RotatedVersion<string>
                    {
                        VersionId = (string)o["NewVersion"]!,
                        Value = "rotated",
                        ExecutionParameters = new Dictionary<string, object?> { ["vault"] = "kv-a" },
                    });
                },
                Disable = (version, _) =>
                {
                    Steps.Add("disable");
                    if (o["DisableThrows"] is Exception e) throw e;
                    Disabled.Add(version);
                    return Task.CompletedTask;
                },
            };
        }

        public Task<RotateForwardResult<string>> Cancel(RotateForwardOptions<string>? options = null, CancellationToken ct = default) =>
            Governance.CancelByRotatingForward(Gm, Recorder, options ?? Options(), ct);

        public async Task NothingRan()
        {
            Assert.Empty(Na.Evaluated);
            Assert.Empty(Steps);
            Assert.Empty(Na.Remediations);
            Assert.Empty(await Records.List());
        }
    }

    // ── The cancel pair ────────────────────────────────────────────────────

    [Fact]
    public async Task ADeniedChangeIsRotatedForwardThenDisabledThenRecorded()
    {
        var s = new Setup();
        var result = await s.Cancel(s.Options(o => o["Detail"] = "rotated past the leak"));
        Assert.Equal(["rotate", "disable"], s.Steps);
        Assert.Equal(["v2"], s.Disabled);
        Assert.True(result.Rotation.Authorized);
        Assert.Equal("rotated", result.Rotation.Value);
        // The rotation's evidence names the new version, so observers match it.
        var evidence = Assert.Single(s.Na.Executions);
        Assert.Equal(("kv:v/s", "rotate", "v3", "kv-a"), ((string)evidence["resource_id"]!, (string)evidence["resource_action"]!,
            (string)evidence["execution_parameters"]!["version_id"]!, (string)evidence["execution_parameters"]!["vault"]!));
        var record = result.Remediation!;
        Assert.Equal((RemediationSteps.RotateForwardThenDisable, RemediationOutcomes.Cancelled, result.Rotation.Evidence!.EvidenceId, "v3", "v2", "rotated past the leak"),
            (record.Steps, record.Outcome, record.RotationEvidenceId, record.VersionId, record.DisabledVersionId, record.Detail));
        Assert.Equal((RemediationSubjectKinds.Judgement, "j-1", ChangeKinds.Observation, "o-1", "kv:v/s", "executor"),
            (record.SubjectKind, record.SubjectId, record.ChangeKind, record.ChangeId, record.ResourceId, record.ExecutorSovereignId));
        Assert.True(OfflineVerifier.VerifyRemediationRecord(JsonSerializer.Serialize(record, Auth.SerializerOptions), [NaKey]));
        Assert.Equal(record.RemediationId, (string)Assert.Single(s.Na.Remediations)["remediation_id"]!);
        Assert.Equal("recorded", result.Submission!.Status);
        Assert.Null(result.Queued);
        Assert.Empty(await s.Records.List());
    }

    [Fact]
    public async Task WithoutARecordOutboxTheRecordIsSubmitted()
    {
        var s = new Setup(recordOutbox: false);
        var result = await s.Cancel();
        Assert.Equal("recorded", result.Submission!.Status);
        Assert.Single(s.Na.Remediations);
    }

    [Fact]
    public async Task ARecordTheNACannotTakeYetIsKeptInTheRecordOutbox()
    {
        var s = new Setup();
        s.Na.Remediation = () => Error(409, "remediation_rotation_missing");
        var result = await s.Cancel();
        Assert.Null(result.Submission);
        Assert.Equal((OutboxStates.Pending, RecordOutboxKinds.Remediation, result.Remediation!.RemediationId), (result.Queued!.State, result.Queued.Kind, result.Queued.Id));
        Assert.Single(await s.Records.List());
    }

    [Fact]
    public async Task ADeniedRotationChangesNothing()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        var result = await s.Cancel();
        Assert.False(result.Rotation.Authorized);
        Assert.Empty(s.Steps);
        Assert.Null(result.Remediation);
        Assert.Empty(s.Na.Remediations);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "remediated", "remediation_already_recorded" },
        { "judged_allowed", "remediation_not_due" },
        { "waived", "remediation_not_due" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AStateThatTakesNoRecordRefusesBeforeAnythingRuns(string state, string code)
    {
        var s = new Setup();
        var e = await Assert.ThrowsAsync<TransitionRefusedException>(() => s.Cancel(s.Options(o => o["Remediation"] = Setup.Due(state))));
        Assert.Equal(code, e.Code);
        await s.NothingRan();
    }

    [Fact]
    public async Task AnOverdueChangeOrOneUnderReviewIsCancelled()
    {
        var s = new Setup();
        var overdue = Setup.Due(RemediationStates.Overdue);
        overdue.ReviewPending = true;
        Assert.NotNull((await s.Cancel(s.Options(o => o["Remediation"] = overdue))).Remediation);
    }

    [Theory]
    [InlineData(RemediationModes.AlertOnly, RemediationSteps.None)]
    [InlineData(RemediationModes.Cancel, RemediationSteps.Disable)]
    public async Task APlanThatIsNoForwardRotationIsRefused(string mode, string steps)
    {
        var s = new Setup();
        var e = await Assert.ThrowsAsync<OutOfBandRecordException>(() => s.Cancel(s.Options(o => o["Remediation"] = Setup.Due(mode: mode, steps: steps))));
        Assert.Equal("remediation_steps_mismatch", e.Code);
        await s.NothingRan();
    }

    [Fact]
    public async Task WhatTheRecordWouldCarryIsCheckedBeforeAnythingRuns()
    {
        var s = new Setup();
        Assert.Equal("remediation_secret_material", (await Assert.ThrowsAsync<OutOfBandRecordException>(() =>
            s.Cancel(s.Options(o => o["Detail"] = "the old value -----BEGIN PRIVATE KEY-----")))).Code);
        var unknown = Setup.Due();
        unknown.ChangeId = null;
        Assert.Equal("remediation_subject_not_found", (await Assert.ThrowsAsync<OutOfBandRecordException>(() =>
            s.Cancel(s.Options(o => o["Remediation"] = unknown)))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Cancel(s.Options(o => o["Rotation"] = Setup.Request(resource: "kv:v/other"))));
        var deleting = Setup.Request();
        await Assert.ThrowsAsync<ArgumentException>(() => s.Cancel(s.Options(o => o["Rotation"] = new GovernedActionRequest
        {
            RequestedCapability = deleting.RequestedCapability,
            AttestationId = deleting.AttestationId,
            Verify = deleting.Verify,
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Delete,
        })));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Cancel(s.Options(o => o["Remediation"] = null)));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Cancel(s.Options(o => o["Rotation"] = null)));
        s.Records.FailList = true;
        Assert.Equal("unreadable", (await Assert.ThrowsAsync<IOException>(() => s.Cancel())).Message);
        s.Records.FailList = false;
        await s.NothingRan();
    }

    [Fact]
    public async Task ARotationThatIssuesTheDeniedVersionIsNotDisabled()
    {
        var s = new Setup();
        var e = await Assert.ThrowsAsync<RemediationNotRecordedException>(() => s.Cancel(s.Options(o => o["NewVersion"] = "v2")));
        Assert.Equal(["rotate"], s.Steps);
        Assert.Equal(("remediation_not_recorded", "remediation_malformed"), (e.Code, ((OutOfBandRecordException)e.InnerException!).Code));
        Assert.NotNull(e.RotationEvidence);
        Assert.Equal("rotated", e.Value);
        Assert.False(e.Disabled);
        Assert.Null(e.Record);
    }

    [Fact]
    public async Task AFailedDisableIsNotRecorded()
    {
        var s = new Setup();
        var e = await Assert.ThrowsAsync<RemediationNotRecordedException>(() => s.Cancel(s.Options(o => o["DisableThrows"] = new InvalidOperationException("vault busy"))));
        Assert.Equal("vault busy", e.InnerException!.Message);
        Assert.False(e.Disabled);
        Assert.Null(e.Record);
        Assert.Empty(s.Na.Remediations);
        Assert.Single(s.Na.Executions);
    }

    [Fact]
    public async Task ARecordThatCannotBeSubmittedComesBackSigned()
    {
        var s = new Setup(recordOutbox: false);
        s.Na.Remediation = () => Error(503, "unavailable");
        var e = await Assert.ThrowsAsync<RemediationNotRecordedException>(() => s.Cancel());
        Assert.Equal(["v2"], s.Disabled);
        Assert.True(e.Disabled);
        Assert.True(OfflineVerifier.VerifyRemediationRecord(JsonSerializer.Serialize(e.Record!, Auth.SerializerOptions), [NaKey]));
    }

    [Fact]
    public async Task OnceDisabledTheRecordIsKeptWhateverTheTokenSays()
    {
        var s = new Setup();
        using var cancel = new CancellationTokenSource();
        var options = s.Options();
        var disable = options.Disable!;
        var cancelling = new RotateForwardOptions<string>
        {
            Remediation = options.Remediation,
            DeniedVersionId = options.DeniedVersionId,
            Rotation = options.Rotation,
            Rotate = options.Rotate,
            Disable = async (version, ct) =>
            {
                await disable(version, ct);
                await cancel.CancelAsync();
            },
        };
        var result = await s.Cancel(cancelling, cancel.Token);
        Assert.Equal("recorded", result.Submission!.Status);
        Assert.Single(s.Na.Remediations);
    }

    // ── Urgent changes ─────────────────────────────────────────────────────

    private const string EmergencyCapability = "sp-secret.emergency-rotate";

    private static EmergencyOptions Emergency(IReadOnlyList<BoundaryPolicy>? policies = null) => new()
    {
        Capability = EmergencyCapability,
        Justification = "incident 42: rotate the leaked key now",
        ExpectedPolicies = policies ?? [Typed<BoundaryPolicy>("policy")],
    };

    private static Task<UrgentActionResult<string>> Urgent(Setup s, EmergencyOptions? emergency = null, List<BoundaryDecision?>? decisions = null,
        Func<ActionReport<string>?>? report = null) =>
        Governance.UrgentAction(s.Gm, s.Recorder, Setup.Request(resource: "kv:v/s"), emergency ?? Emergency(), (decision, _) =>
        {
            decisions?.Add(decision);
            return Task.FromResult((report ?? (() => new ActionReport<string> { Value = "rotated" }))());
        });

    private static IEnumerable<string> Capabilities(Setup s) => s.Na.Evaluated.Select(e => (string)e["requested_capability"]!);

    [Fact]
    public async Task AnAllowedOrdinaryCapabilityRunsUnderIt()
    {
        var s = new Setup();
        var result = await Urgent(s);
        Assert.Equal(UrgentPaths.Ordinary, result.Path);
        Assert.True(result.Outcome.Governed!.Authorized);
        Assert.Null(result.OrdinaryDenial);
        Assert.Equal(["sp-secret.rotate"], Capabilities(s));
    }

    [Fact]
    public async Task ADeniedOrdinaryCapabilityRequestsTheEmergencyOneWithTheJustification()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        var decisions = new List<BoundaryDecision?>();
        var result = await Urgent(s, decisions: decisions);
        Assert.Equal(UrgentPaths.Emergency, result.Path);
        Assert.False(result.OrdinaryDenial!.Authorized);
        Assert.True(result.Outcome.Governed!.Authorized);
        Assert.Equal(["sp-secret.rotate", EmergencyCapability], Capabilities(s));
        var urgent = s.Na.Evaluated[1]["context"]!["request_parameters"]!;
        Assert.Equal(("incident 42: rotate the leaked key now", "app-1"), ((string)urgent["justification"]!, (string)urgent["app_id"]!));
        Assert.Null(s.Na.Evaluated[0]["context"]!["request_parameters"]!["justification"]);
        Assert.Single(decisions);
    }

    [Fact]
    public async Task AJustificationParameterOfItsOwnName()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        var emergency = new EmergencyOptions
        {
            Capability = EmergencyCapability,
            Justification = "incident 42",
            JustificationParameter = "incident",
            ExpectedPolicies = [Typed<BoundaryPolicy>("policy")],
        };
        await Urgent(s, emergency);
        Assert.Equal("incident 42", (string)s.Na.Evaluated[1]["context"]!["request_parameters"]!["incident"]!);
    }

    [Fact]
    public async Task WhenTheNACannotBeReachedTheChangeBreaksTheGlassNamingTheEmergencyCapability()
    {
        var s = new Setup();
        s.Na.Unreachable.Add("sp-secret.rotate");
        s.Na.Unreachable.Add(EmergencyCapability);
        var decisions = new List<BoundaryDecision?>();
        var result = await Urgent(s, decisions: decisions);
        Assert.Equal(UrgentPaths.BreakGlass, result.Path);
        Assert.Null(result.OrdinaryDenial);
        var record = result.Outcome.BreakGlass!.Record;
        Assert.Equal((EmergencyCapability, "incident 42: rotate the leaked key now"), (record.Capability, record.Justification));
        Assert.Equal("incident 42: rotate the leaked key now", record.RequestParameters.GetProperty("justification").GetString());
        Assert.Equal([null], decisions);
    }

    [Fact]
    public async Task AnUnreachableOrdinaryEvaluationGoesToTheEmergencyCapability()
    {
        var s = new Setup();
        s.Na.Answers["sp-secret.rotate"] = () => Error(503, "service_unavailable");
        var result = await Urgent(s);
        Assert.Equal(UrgentPaths.Emergency, result.Path);
        Assert.Null(result.OrdinaryDenial);
        Assert.True(result.Outcome.Governed!.Authorized);
    }

    [Fact]
    public async Task ADeniedEmergencyCapabilityRunsNothing()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        s.Na.Denied.Add(EmergencyCapability);
        var decisions = new List<BoundaryDecision?>();
        var result = await Urgent(s, decisions: decisions);
        Assert.Equal(UrgentPaths.Emergency, result.Path);
        Assert.False(result.Outcome.Governed!.Authorized);
        Assert.Empty(decisions);
    }

    [Fact]
    public async Task AnOrdinaryErrorThatIsNoOutageIsTheCallers()
    {
        var s = new Setup();
        s.Na.Answers["sp-secret.rotate"] = () => Error(403, "insufficient_operator_tier");
        await Assert.ThrowsAsync<ForbiddenException>(() => Urgent(s));
        Assert.Equal(["sp-secret.rotate"], Capabilities(s));
        var failing = new Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Urgent(failing, report: () => throw new InvalidOperationException("vault busy")));
        Assert.Equal(["sp-secret.rotate"], Capabilities(failing));
    }

    [Fact]
    public async Task TheEmergencyDecisionIsVerifiedAgainstTheEmergencyPolicies()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        await Assert.ThrowsAsync<DecisionVerificationException>(() => Urgent(s, Emergency(policies: [])));
        Assert.Equal(["sp-secret.rotate", EmergencyCapability], Capabilities(s));
    }

    [Fact]
    public async Task BreakGlassNeedsAreCheckedBeforeAnythingIsEvaluated()
    {
        var none = new Setup(recordOutbox: false);
        Assert.Equal("record_outbox_required", (await Assert.ThrowsAsync<OutboxException>(() => Urgent(none))).Code);
        Assert.Empty(none.Na.Evaluated);
        var s = new Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => Urgent(s, new EmergencyOptions { Justification = "why", ExpectedPolicies = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => Urgent(s, new EmergencyOptions { Capability = EmergencyCapability, Justification = "why" }));
        Assert.Equal("break_glass_malformed", (await Assert.ThrowsAsync<OutOfBandRecordException>(() =>
            Urgent(s, new EmergencyOptions { Capability = EmergencyCapability, Justification = "", ExpectedPolicies = [] }))).Code);
        Assert.Empty(s.Na.Evaluated);
    }

    // ── What each step passes on ───────────────────────────────────────────

    private static GovernedActionRequest Like(GovernedActionRequest r, EvaluationContext? context = null, GovernedVerification? verify = null,
        string? resource = "kv:v/s", string? action = ResourceActions.Rotate, bool newResource = false) => new()
        {
            RequestedCapability = r.RequestedCapability,
            AttestationId = r.AttestationId,
            Context = context ?? r.Context,
            Verify = verify ?? r.Verify,
            ResourceId = resource,
            ResourceAction = action,
            NewResource = newResource,
        };

    private static Func<BoundaryDecision?, CancellationToken, Task<ActionReport<string>?>> Nothing(List<BoundaryDecision?>? decisions = null) =>
        (d, _) =>
        {
            decisions?.Add(d);
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string> { Value = "done" });
        };

    [Fact]
    public async Task AFailureAfterTheOrdinaryActionRanIsTheCallers()
    {
        var s = new Setup();
        s.Na.Execution = () => Error(503, "service_unavailable");
        var decisions = new List<BoundaryDecision?>();
        var e = await Assert.ThrowsAnyAsync<GenesisMeshException>(() =>
            Governance.UrgentAction(s.Gm, s.Recorder, Setup.Request(resource: "kv:v/s"), Emergency(), Nothing(decisions)));
        Assert.Equal(503, e.Status);
        Assert.Single(decisions);
        Assert.Equal(["sp-secret.rotate"], Capabilities(s));
    }

    [Fact]
    public async Task TheEmergencyEvaluationKeepsTheWholeContext()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        var r = Setup.Request(resource: "kv:v/s");
        var context = new EvaluationContext
        {
            RequestParameters = r.Context!.RequestParameters,
            Attributes = r.Context.Attributes,
            RequesterSovereignId = "requester-a",
            ProviderSovereignId = "provider-b",
            ContextId = "ctx-1",
            ContextFreshnessSeq = 3,
        };
        await Governance.UrgentAction(s.Gm, s.Recorder, Like(r, context: context), Emergency(), Nothing());
        var asked = s.Na.Evaluated[1]["context"]!;
        Assert.Equal(("team-a", "requester-a", "provider-b", "ctx-1", 3L),
            ((string)asked["attributes"]!["owner"]!, (string)asked["requester_sovereign_id"]!, (string)asked["provider_sovereign_id"]!,
             (string)asked["context_id"]!, (long)asked["context_freshness_seq"]!));
    }

    [Fact]
    public async Task TheEmergencyDecisionIsVerifiedUnderTheOrdinaryKeyHistory()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        var r = Setup.Request(resource: "kv:v/s");
        var history = new GovernedVerification
        {
            KeyHistory = NAKeyHistory.ValidAlways([NaKey]),
            ExpectedPolicies = r.Verify!.ExpectedPolicies,
            ExpectedAttestation = r.Verify.ExpectedAttestation,
        };
        var result = await Governance.UrgentAction(s.Gm, s.Recorder, Like(r, verify: history), Emergency(), Nothing());
        Assert.True(result.Outcome.Governed!.Authorized);
    }

    [Fact]
    public async Task TheEmergencyDecisionsFreshnessProofIsVerifiedAsAsked()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        s.Na.Tamper[EmergencyCapability] = d => d["freshness_proof"] = new JsonObject
        {
            ["proof_id"] = "p-1",
            ["feed_digest"] = null,
            ["feed_sequence"] = null,
            ["feed_sovereign_id"] = null,
            ["issuer_sovereign_id"] = "feed-a",
            ["attested_at"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1)),
            ["proof_valid_until"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddDays(1)),
            ["signature"] = new JsonObject { ["key_id"] = "x", ["sig"] = Convert.ToBase64String(new byte[64]) },
        };
        var r = Setup.Request(resource: "kv:v/s");
        var fresh = new GovernedVerification
        {
            OperatorPublicKeys = [NaKey],
            FreshnessProofIssuerKeys = [NaKey],
            ExpectedPolicies = r.Verify!.ExpectedPolicies,
            ExpectedAttestation = r.Verify.ExpectedAttestation,
        };
        var decisions = new List<BoundaryDecision?>();
        var e = await Assert.ThrowsAsync<DecisionVerificationException>(() =>
            Governance.UrgentAction(s.Gm, s.Recorder, Like(r, verify: fresh), Emergency(), Nothing(decisions)));
        Assert.Equal("freshness_proof_invalid_signature", e.Code);
        Assert.Empty(decisions);
    }

    [Fact]
    public async Task TheEmergencyChangeKeepsANewResourceNew()
    {
        var s = new Setup();
        s.Na.Denied.Add("sp-secret.rotate");
        await Governance.UrgentAction(s.Gm, s.Recorder, Like(Setup.Request(), newResource: true), Emergency(), Nothing());
        Assert.DoesNotContain(s.Na.Paths, p => p.StartsWith("/admin/evidence/resource-heads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutARecordOutboxOnceDisabledTheRecordIsSubmittedWhateverTheTokenSays()
    {
        var s = new Setup(recordOutbox: false);
        using var cancel = new CancellationTokenSource();
        var o = s.Options();
        var cancelling = new RotateForwardOptions<string>
        {
            Remediation = o.Remediation,
            DeniedVersionId = o.DeniedVersionId,
            Rotation = o.Rotation,
            Rotate = o.Rotate,
            Disable = async (version, ct) =>
            {
                Assert.True(ct.CanBeCanceled); // the disable gets the caller's token
                await o.Disable!(version, ct);
                await cancel.CancelAsync();
            },
        };
        var result = await s.Cancel(cancelling, cancel.Token);
        Assert.Equal("recorded", result.Submission!.Status);
    }

    [Fact]
    public async Task AnUpdateIsRecordedAsAnUpdate()
    {
        var s = new Setup();
        await s.Cancel(s.Options(o => o["Rotation"] = Like(Setup.Request(), resource: null, action: ResourceActions.Update)));
        Assert.Equal("update", (string)Assert.Single(s.Na.Executions)["resource_action"]!);
    }

    [Fact]
    public async Task TheRotationIsEvaluatedWithItsContextAndBasis()
    {
        var s = new Setup();
        await s.Cancel();
        Assert.Equal("app-1", (string)s.Na.Evaluated[0]["context"]!["request_parameters"]!["app_id"]!);
        Assert.Equal((string)VectorData.Value["attestation"]!["attestation_id"]!, (string)s.Na.Evaluated[0]["attestation_id"]!);
    }

    [Fact]
    public async Task TheIssuedVersionIsTheOneTheEvidenceNames()
    {
        var s = new Setup();
        var o = s.Options();
        var overriding = new RotateForwardOptions<string>
        {
            Remediation = o.Remediation,
            DeniedVersionId = o.DeniedVersionId,
            Rotation = o.Rotation,
            Disable = o.Disable,
            Rotate = (_, _) => Task.FromResult(new RotatedVersion<string>
            {
                VersionId = "v3",
                ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v9" },
            }),
        };
        var result = await s.Cancel(overriding);
        Assert.Equal("v3", (string)Assert.Single(s.Na.Executions)["execution_parameters"]!["version_id"]!);
        Assert.Equal("v3", result.Remediation!.VersionId);
    }

    [Fact]
    public async Task ARotationOfANewResourceReadsNoHead()
    {
        var s = new Setup();
        await s.Cancel(s.Options(o => o["Rotation"] = Like(Setup.Request(), resource: null, action: null, newResource: true)));
        Assert.DoesNotContain(s.Na.Paths, p => p.StartsWith("/admin/evidence/resource-heads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARotateThatReturnsNoVersionIsAFailedAction()
    {
        var s = new Setup();
        var o = s.Options();
        var none = new RotateForwardOptions<string>
        {
            Remediation = o.Remediation,
            DeniedVersionId = o.DeniedVersionId,
            Rotation = o.Rotation,
            Disable = o.Disable,
            Rotate = (_, _) => Task.FromResult<RotatedVersion<string>>(null!),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Cancel(none));
        Assert.Equal("failure", (string)Assert.Single(s.Na.Executions)["outcome"]!);
        Assert.Empty(s.Disabled);
    }

    // ── A disable is never left without a record the NA will take ──────────

    [Fact]
    public async Task ARotationWhoseEvidenceIsADeadLetterIsNotFollowedByTheDisable()
    {
        var s = new Setup(evidenceOutbox: true);
        s.Na.Execution = () => Error(409, "evidence_chain_mismatch");
        var e = await Assert.ThrowsAsync<RemediationNotRecordedException>(() => s.Cancel());
        Assert.Equal(OutboxStates.DeadLetter, e.RotationQueued!.State);
        Assert.Contains("evidence_chain_mismatch", e.InnerException!.Message, StringComparison.Ordinal);
        Assert.False(e.Disabled);
        Assert.Null(e.Record);
        Assert.Empty(s.Disabled);
        Assert.Empty(s.Na.Remediations);
    }

    [Fact]
    public async Task TheDeniedVersionIsTheOneTheChangeMade()
    {
        var s = new Setup();
        var e = await Assert.ThrowsAsync<OutOfBandRecordException>(() => s.Cancel(s.Options(o => o["DeniedVersionId"] = "v1")));   // the state says v2
        Assert.Equal("remediation_subject_mismatch", e.Code);
        await s.NothingRan();
        var result = await s.Cancel(s.Options(o => o["DeniedVersionId"] = null));
        Assert.Equal(["v2"], s.Disabled);
        Assert.Equal("v2", result.Remediation!.DisabledVersionId);
    }

    [Fact]
    public async Task AStateWithoutTheChangesVersionTakesTheOneGiven()
    {
        var s = new Setup();
        var unversioned = Setup.Due();
        unversioned.ChangeVersionId = null;
        var result = await s.Cancel(s.Options(o => { o["Remediation"] = unversioned; o["DeniedVersionId"] = "v1"; }));
        Assert.Equal("v1", result.Remediation!.DisabledVersionId);
        var e = await Assert.ThrowsAsync<OutOfBandRecordException>(() => s.Cancel(s.Options(o => { o["Remediation"] = unversioned; o["DeniedVersionId"] = ""; })));
        Assert.Equal("remediation_malformed", e.Code);
    }
}
