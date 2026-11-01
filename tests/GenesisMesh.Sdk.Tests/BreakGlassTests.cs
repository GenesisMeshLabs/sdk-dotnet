using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Governed actions with break-glass (1.9.0), as the TypeScript SDK's <c>governedAction</c> with <c>breakGlass</c>
/// runs them: the checks before anything runs, which evaluation failures break the glass, and the record kept once the
/// action ran.
/// </summary>
public class BreakGlassTests
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

    private static HttpResponseMessage Error(int status, string code) =>
        Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");

    private static HttpResponseMessage Recorded() => Json(201, """{"status":"recorded","entry":{},"entry_digest":"d","payload":{}}""");

    private static HttpResponseMessage Offline() => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Text(object value) => JsonSerializer.Serialize(value, value.GetType(), Auth.SerializerOptions);

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(request, ct);
    }

    /// <summary>
    /// The NA: it evaluates with the reference's allowed (or denied) decision, signed again for the request's context,
    /// unless told to fail, and records what it is sent.
    /// </summary>
    private sealed class Na
    {
        public bool Denied { get; set; }
        /// <summary>Applied to the signed decision, as an attacker would.</summary>
        public Action<JsonObject>? Tamper { get; set; }
        /// <summary>The answer to an evaluation, instead of a decision; it may throw.</summary>
        public Func<CancellationToken, Task<HttpResponseMessage>>? Evaluate { get; set; }
        /// <summary>The answer to the key lookup.</summary>
        public Func<HttpResponseMessage> Sovereign { get; set; } = () => Json(200, $$$"""{"network_authority":{"public_key":"{{{NaKey}}}"}}""");
        /// <summary>The answer to a break-glass record.</summary>
        public Func<HttpResponseMessage> Glass { get; set; } = Recorded;
        public List<string> Paths { get; } = [];
        public List<string> Evaluated { get; } = [];
        public List<JsonObject> Glasses { get; } = [];
        public List<JsonObject> Executions { get; } = [];

        public async Task<HttpResponseMessage> Answer(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(CancellationToken.None);
            switch (path)
            {
                case "/sovereign.json":
                    return Sovereign();
                case "/admin/boundary/evaluate":
                    Evaluated.Add(text!);
                    if (Evaluate is { } evaluate) return await evaluate(ct);
                    var evaluation = Vector(Denied ? "denied" : "allowed");
                    var decision = evaluation["decision"]!.AsObject();
                    decision["context_id"] = (string?)JsonNode.Parse(text!)!["context"]?["context_id"];
                    decision["decision_made_at"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddSeconds(-1));
                    decision["decision_valid_until"] = Canonical.Timestamp(DateTimeOffset.UtcNow.AddMinutes(1));
                    decision.Remove("signature");
                    var canonical = Canonical.Of(JsonSerializer.SerializeToElement(decision), ["signature"], OfflineVerifier.DecisionOmittedWhenAbsent);
                    var sig = await Signer.SignAsync(Encoding.UTF8.GetBytes(canonical), CancellationToken.None);
                    decision["signature"] = new JsonObject { ["key_id"] = "test", ["sig"] = Convert.ToBase64String(sig) };
                    Tamper?.Invoke(decision);
                    return Json(200, evaluation.ToJsonString());
                case "/evidence/break-glass":
                    Glasses.Add(JsonNode.Parse(text!)!["record"]!.AsObject());
                    return Glass();
                case "/evidence/execution":
                    Executions.Add(JsonNode.Parse(text!)!["evidence"]!.AsObject());
                    return Json(201, """
                        {"status":"recorded","entry":{"store_sequence":7,"entry_kind":"execution","recorded_at":"2026-10-30T12:00:03Z",
                        "payload_digest":"00","prev_entry_digest":null,"decision_id":null,"context_id":null,"vendor_id":null,
                        "attestation_id":null,"capability":null,"outcome":null,"evidence_id":null,"executor_sovereign_id":null,
                        "exec_sequence_no":null,"resource_id":null,"resource_action":null,"resource_sequence":null},"entry_digest":"bb"}
                        """);
                default:
                    return path.StartsWith("/admin/evidence/resource-heads/", StringComparison.Ordinal)
                        ? Error(404, "resource_not_found")
                        : Error(404, "not_found");
            }
        }

        public BreakGlassRecord Sent(int i) => JsonSerializer.Deserialize<BreakGlassRecord>(Glasses[i].ToJsonString(), Auth.SerializerOptions)!;
    }

    /// <summary>A memory record outbox whose reads and additions can fail.</summary>
    private sealed class Records : IOutbox<RecordOutboxEntry>
    {
        private readonly MemoryOutbox<RecordOutboxEntry> _inner = new();
        public bool FailList { get; set; }
        public bool FailAdd { get; set; }

        public Task Add(RecordOutboxEntry entry, CancellationToken ct = default) =>
            FailAdd ? throw new IOException("disk full") : _inner.Add(entry, ct);

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
        public List<BoundaryDecision?> Decisions { get; } = [];
        /// <summary>What the action reports; it may throw.</summary>
        public Func<ActionReport<string>?> Report { get; set; } = () => new ActionReport<string>
        {
            Value = "rotated",
            ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v9" },
        };

        public Setup(bool recordOutbox = true, bool lookup = false)
        {
            Gm = new GenesisMeshClient(new ClientOptions
            {
                BaseUrl = "http://localhost",
                SigningKey = TestHelpers.ZeroSeedB64,
                KeyId = "ops",
                Audience = lookup ? null : "TEST",
                Retry = new RetrySettings { Attempts = 0 },
                HttpHandler = new AsyncHandler(Na.Answer),
                RecordOutbox = recordOutbox ? Records : null,
            });
        }

        public static GovernedActionRequest Request() => new()
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
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

        public Task<GovernedOutcome<string>> Run(GovernedActionRequest? request = null, string justification = "incident 42: rotate the leaked key now",
            CancellationToken ct = default) =>
            Governance.GovernedAction(Gm, Recorder, request ?? Request(), new BreakGlassOptions { Justification = justification },
                (decision, _) =>
                {
                    Decisions.Add(decision);
                    return Task.FromResult(Report());
                }, ct);

        public async Task<BreakGlassResult<string>> Broken(GovernedActionRequest? request = null, string justification = "incident 42: rotate the leaked key now")
        {
            var outcome = await Run(request, justification);
            Assert.True(outcome.BrokeGlass);
            Assert.Null(outcome.Governed);
            return outcome.BreakGlass;
        }

        /// <summary>Asserts nothing ran and nothing was kept.</summary>
        public async Task NothingRan()
        {
            Assert.Empty(Decisions);
            Assert.Empty(Na.Glasses);
            Assert.Empty(await Records.List());
        }
    }

    // ── Which failures break the glass ────────────────────────────────────────

    public static TheoryData<string, Func<CancellationToken, Task<HttpResponseMessage>>> Outages => new()
    {
        { EvaluationFailures.NetworkError, _ => Task.FromResult(Offline()) },
        { EvaluationFailures.Timeout, _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out", new TimeoutException())) },
        { EvaluationFailures.ServerError, _ => Task.FromResult(Error(503, "service_unavailable")) },
        { EvaluationFailures.RateLimited, _ => Task.FromResult(Error(429, "rate_limited")) },
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task AnEvaluationThatFailsTransiently_RunsTheAction_AndKeepsItsBreakGlassRecord(
        string failure, Func<CancellationToken, Task<HttpResponseMessage>> evaluate)
    {
        var x = new Setup();
        x.Na.Evaluate = evaluate;
        var result = await x.Broken();
        Assert.Null(Assert.Single(x.Decisions));
        Assert.Equal((failure, "rotated", "recorded"), (result.Failure, result.Value, result.Submission!.Status));
        Assert.Equal(failure, Governance.EvaluationFailure(result.EvaluationError!));
        Assert.Null(result.Queued);
        Assert.Empty(result.Dropped);
        var record = result.Record;
        Assert.Equal(
            ("executor", "kv:v/s", ResourceActions.Rotate, "sp-secret.rotate", AttestationId, failure, ExecutionOutcomes.Success),
            (record.ExecutorSovereignId, record.ResourceId, record.ResourceAction, record.Capability, record.AttestationId,
                record.EvaluationFailure, record.Outcome));
        Assert.Equal("incident 42: rotate the leaked key now", record.Justification);
        Assert.Equal("""{"app_id":"app-1"}""", record.RequestParameters.GetRawText());
        Assert.Equal("""{"owner":"team-a"}""", record.Attributes.GetRawText());
        Assert.Equal("""{"version_id":"v9"}""", record.ExecutionParameters.GetRawText());
        Assert.Null(record.OutcomeDetail);
        // The digest is of the evaluation request as it was sent, with its context ID.
        var sent = Assert.Single(x.Na.Evaluated);
        Assert.Equal(Sha256(Canonical.FromJson(sent)), record.EvaluationRequestDigest);
        Assert.NotNull((string?)JsonNode.Parse(sent)!["context"]!["context_id"]);
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(Text(record), [NaKey]));
        Assert.Equal(OfflineVerifier.OutOfBandCanonical(Text(record)), OfflineVerifier.OutOfBandCanonical(x.Na.Glasses[0].ToJsonString()));
        Assert.Empty(await x.Records.List());
    }

    [Fact]
    public async Task AnAnswer_IsGovernedAsWithoutBreakGlass()
    {
        var x = new Setup();
        var outcome = await x.Run();
        Assert.False(outcome.BrokeGlass);
        Assert.Null(outcome.BreakGlass);
        Assert.True(outcome.Governed!.Authorized);
        Assert.Equal("rotated", outcome.Governed.Value);
        Assert.NotNull(Assert.Single(x.Decisions));
        Assert.Single(x.Na.Executions);
        Assert.Empty(x.Na.Glasses);

        x.Na.Denied = true;
        var denied = await x.Run();
        Assert.False(denied.BrokeGlass);
        Assert.False(denied.Governed!.Authorized);
        Assert.Single(x.Decisions);
        Assert.Empty(x.Na.Glasses);
        Assert.Empty(await x.Records.List());
    }

    [Fact]
    public async Task ARefusal_OrADecisionThatFailsVerification_NeverBreaksTheGlass()
    {
        var refused = new Setup();
        refused.Na.Evaluate = _ => Task.FromResult(Error(400, "bad_request"));
        Assert.Equal("bad_request", (await Assert.ThrowsAsync<BadRequestException>(() => refused.Run())).Code);
        await refused.NothingRan();

        var tampered = new Setup();
        tampered.Na.Tamper = d => d["capability"] = "sp-secret.delete";
        await Assert.ThrowsAsync<DecisionVerificationException>(() => tampered.Run());
        await tampered.NothingRan();
    }

    [Theory]
    [InlineData(429, "admin_auth_throttled")]
    [InlineData(503, "evidence_store_unavailable")]
    public async Task ARefusalThatLooksTransient_NeverBreaksTheGlass(int status, string code)
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Error(status, code));
        var error = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => x.Run());
        Assert.Equal((status, code), (error.Status, error.Code));
        Assert.Null(Governance.EvaluationFailure(error));
        await x.NothingRan();
    }

    [Fact]
    public async Task AnAnswerThatCannotBeRead_NeverBreaksTheGlass()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Json(200, "not json"));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => x.Run());
        Assert.Null(Governance.EvaluationFailure(error));
        await x.NothingRan();
    }

    [Theory]
    [InlineData(403, "<html>blocked</html>")]
    [InlineData(404, """{"error":"not found"}""")]
    [InlineData(200, """{"hello":1}""")]
    public async Task AKeyLookupThatGetsAnAnswer_KeepsItsStatus_AndNeverBreaksTheGlass(int status, string body)
    {
        var x = new Setup(lookup: true);
        x.Na.Sovereign = () => Json(status, body);
        var error = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => x.Run());
        Assert.Equal(("na_public_key_unavailable", status), (error.Code, error.Status));
        Assert.Null(Governance.EvaluationFailure(error));
        Assert.Equal(["/sovereign.json"], x.Na.Paths);
        await x.NothingRan();
    }

    [Fact]
    public async Task AKeyLookupThatGetsNoAnswer_OrA503_BreaksTheGlass()
    {
        var unreachable = new Setup(lookup: true);
        unreachable.Na.Sovereign = Offline;
        Assert.Equal(EvaluationFailures.NetworkError, (await unreachable.Broken()).Failure);
        var down = new Setup(lookup: true);
        down.Na.Sovereign = () => Error(503, "service_unavailable");
        Assert.Equal(EvaluationFailures.ServerError, (await down.Broken()).Failure);
        Assert.Empty(down.Na.Evaluated);
    }

    [Fact]
    public async Task ACancelledEvaluation_NeverBreaksTheGlass()
    {
        var x = new Setup();
        using var cts = new CancellationTokenSource();
        x.Na.Evaluate = async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => x.Run(ct: cts.Token));
        await x.NothingRan();
    }

    [Fact]
    public async Task WithoutBreakGlass_AnOutageIsTheCallers()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        await Assert.ThrowsAsync<NetworkException>(() => Governance.GovernedAction(x.Gm, x.Recorder, Setup.Request(),
            (decision, _) => Task.FromResult<ActionReport<string>?>(null)));
        await x.NothingRan();
    }

    [Fact]
    public void EvaluationFailure_NamesOnlyTheFailuresALaterAttemptCanOvercome()
    {
        Assert.Equal(EvaluationFailures.NetworkError, Governance.EvaluationFailure(new NetworkException(new HttpRequestException("refused"))));
        Assert.Equal(EvaluationFailures.Timeout, Governance.EvaluationFailure(new NetworkException(new TaskCanceledException())));
        Assert.Equal(EvaluationFailures.Timeout,
            Governance.EvaluationFailure(new NetworkException(new HttpRequestException("x", new TimeoutException()))));
        // A request not sent because the key lookup timed out.
        Assert.Equal(EvaluationFailures.Timeout,
            Governance.EvaluationFailure(new NetworkException(new NetworkException(new TaskCanceledException()))));
        Assert.Equal(EvaluationFailures.ServerError, Governance.EvaluationFailure(new ServerException(500, "internal_error", "x")));
        Assert.Equal(EvaluationFailures.ServerError, Governance.EvaluationFailure(new GenesisMeshException(599, "x", "x")));
        Assert.Equal(EvaluationFailures.RateLimited, Governance.EvaluationFailure(new RateLimitException(429, "rate_limited", "x")));
        foreach (var other in new Exception[]
        {
            new GenesisMeshException(499, "x", "x"),
            new GenesisMeshException(600, "x", "x"),
            new GenesisMeshException(0, "x", "x"),
            new DecisionVerificationException("invalid_signature"),
            new StrictJsonException("response_body_unreadable", "x"),
            new OperationCanceledException(),
            new InvalidOperationException(),
        })
        {
            Assert.Null(Governance.EvaluationFailure(other));
        }
        Assert.Throws<ArgumentNullException>(() => Governance.EvaluationFailure(null!));
    }

    // ── What is checked before anything runs ──────────────────────────────────

    [Fact]
    public async Task WhatBreakGlassNeeds_IsCheckedBeforeAnythingIsEvaluatedOrRun()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        GovernedActionRequest With(Func<GovernedActionRequest, GovernedActionRequest> change) => change(Setup.Request());
        var noResource = With(r => new GovernedActionRequest
        {
            AttestationId = r.AttestationId,
            RequestedCapability = r.RequestedCapability,
            Verify = r.Verify,
        });
        var agreement = With(r => new GovernedActionRequest
        {
            Agreement = new AgreementRecord { AgreementId = "agr-1" },
            RequestedCapability = r.RequestedCapability,
            ResourceId = r.ResourceId,
            ResourceAction = r.ResourceAction,
            Verify = r.Verify,
        });
        GovernedActionRequest Context(EvaluationContext context) => new()
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
            Context = context,
            Verify = Setup.Request().Verify,
        };
        var noCapability = new GovernedActionRequest
        {
            AttestationId = AttestationId,
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
            Verify = Setup.Request().Verify,
        };
        var otherAction = new GovernedActionRequest
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = "explode",
            Verify = Setup.Request().Verify,
        };

        var without = new Setup(recordOutbox: false);
        Assert.Equal("record_outbox_required", (await Assert.ThrowsAsync<OutboxException>(() => without.Run(noResource))).Code);
        await without.NothingRan();
        Assert.Contains("ResourceId", (await Assert.ThrowsAsync<ArgumentException>(() => x.Run(noResource))).Message);
        foreach (var (request, justification, code) in new (GovernedActionRequest, string, string)[]
        {
            (Setup.Request(), "", "break_glass_malformed"),
            (Setup.Request(), new string('j', 1025), "break_glass_malformed"),
            (agreement, "outage", "break_glass_malformed"),
            (noCapability, "outage", "break_glass_malformed"),
            (otherAction, "outage", "break_glass_malformed"),
            (Setup.Request(), "see -----BEGIN PRIVATE KEY-----", "break_glass_secret_material"),
            (Context(new EvaluationContext { Attributes = new Dictionary<string, object?> { ["client_secret"] = "s" } }), "outage", "break_glass_secret_material"),
            (Context(new EvaluationContext { RequesterSovereignId = "-----BEGIN PRIVATE KEY-----" }), "outage", "break_glass_secret_material"),
            // Sized escaped, as the reference sizes metadata: 7200 bytes as UTF-8 are 16800 escaped, over the limit, and
            // 6300 bytes are 14700, under it but with no room left for the report.
            (Context(new EvaluationContext { Attributes = new Dictionary<string, object?> { ["note"] = string.Concat(Enumerable.Repeat("\u00e9 ", 2400)) } }),
                "outage", "break_glass_secret_material"),
            (Context(new EvaluationContext { Attributes = new Dictionary<string, object?> { ["note"] = string.Concat(Enumerable.Repeat("\u00e9 ", 2100)) } }),
                "outage", "break_glass_malformed"),
        })
        {
            var error = await Assert.ThrowsAsync<OutOfBandRecordException>(() => x.Run(request, justification));
            Assert.Equal(code, error.Code);
        }
        x.Records.FailList = true;
        Assert.Equal("unreadable", (await Assert.ThrowsAsync<IOException>(() => x.Run())).Message);
        x.Records.FailList = false;
        Assert.Empty(x.Na.Evaluated);
        await x.NothingRan();
    }

    [Fact]
    public async Task AContextThatLeavesRoomForTheReport_IsTakenToTheByte()
    {
        // The carried metadata is {"attributes":{"note":"..."},"execution_parameters":{},"justification":"j",
        // "outcome_detail":"","request_parameters":{}}: 116 bytes besides the note, so a note of 14220 leaves 2048.
        GovernedActionRequest Note(int length) => new()
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
            Context = new EvaluationContext
            {
                Attributes = new Dictionary<string, object?> { ["note"] = string.Concat(Enumerable.Repeat("a short note, ", 1100))[..length] },
            },
            Verify = Setup.Request().Verify,
        };
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Report = () => null;
        Assert.True((await x.Run(Note(14220), "j")).BrokeGlass);
        Assert.Equal("break_glass_malformed", (await Assert.ThrowsAsync<OutOfBandRecordException>(() => x.Run(Note(14221), "j"))).Code);
    }

    // ── The record once the action ran ─────────────────────────────────────────

    [Fact]
    public async Task AFailedAction_IsRecordedAsAFailure_AndItsErrorRethrown()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Report = () => throw new InvalidOperationException("cloud refused");
        Assert.Equal("cloud refused", (await Assert.ThrowsAsync<InvalidOperationException>(() => x.Run())).Message);
        var sent = x.Na.Sent(0);
        Assert.Equal((ExecutionOutcomes.Failure, "action failed", "{}"), (sent.Outcome, sent.OutcomeDetail, sent.ExecutionParameters.GetRawText()));
        Assert.Equal(EvaluationFailures.NetworkError, sent.EvaluationFailure);
        Assert.Empty(await x.Records.List());
    }

    [Fact]
    public async Task AFailedActionWhoseRecordCannotBeKept_ThrowsBothErrors()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Report = () => throw new InvalidOperationException("cloud refused");
        x.Records.FailAdd = true;
        var error = await Assert.ThrowsAsync<GovernedActionException>(() => x.Run());
        Assert.Equal(("governed_action_unrecorded", "cloud refused", "disk full"),
            (error.Code, error.InnerException!.Message, error.EvidenceError.Message));
        Assert.Null(error.Evidence);
    }

    [Fact]
    public async Task ARecordThatCannotBeKept_ThrowsEvidenceNotKept_WithTheValue()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Records.FailAdd = true;
        var error = await Assert.ThrowsAsync<EvidenceNotKeptException>(() => x.Run());
        Assert.Equal(("governed_action_evidence_unkept", "rotated", "disk full"), (error.Code, error.Value, error.InnerException!.Message));
        Assert.Null(error.Evidence);
        Assert.Single(x.Decisions);
    }

    [Fact]
    public async Task ARecordTheNACannotTakeYet_IsPending_AndRefusedMetadataIsLeftOut()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Na.Glass = Offline;
        x.Report = () => new ActionReport<string>
        {
            Value = "rotated",
            Outcome = ExecutionOutcomes.Partial,
            ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v9", ["client_secret"] = "s3cr3t" },
            OutcomeDetail = "rotated in one region",
        };
        var result = await x.Broken();
        Assert.Equal((OutboxStates.Pending, EvidenceOutbox.NetworkError), (result.Queued!.State, result.Queued.LastError!.Code));
        Assert.Null(result.Submission);
        Assert.Equal(["client_secret"], result.Dropped);
        Assert.Equal("""{"version_id":"v9"}""", result.Record.ExecutionParameters.GetRawText());
        Assert.Equal(("rotated", ExecutionOutcomes.Partial), (result.Value, result.Record.Outcome));
        Assert.Equal("rotated in one region [secret guard dropped: client_secret]", result.Record.OutcomeDetail);
        Assert.Equal(result.Record.BreakGlassId, Assert.Single(await x.Records.List()).Id);
        Assert.True(OfflineVerifier.VerifyOutOfBandRecord(Text(result.Record), [NaKey]));
    }

    [Fact]
    public async Task AReportTheNAWouldNotReadAlike_IsLeftOut()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Report = () => new ActionReport<string>
        {
            ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v9", ["lone"] = "\ud800" },
        };
        var result = await x.Broken();
        Assert.Equal(["lone"], result.Dropped);
        Assert.Equal("""{"version_id":"v9"}""", result.Record.ExecutionParameters.GetRawText());
    }

    [Fact]
    public async Task ARefusedDetailWithoutParameters_IsLeftOut()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Report = () => new ActionReport<string> { OutcomeDetail = "see -----BEGIN PRIVATE KEY-----" };
        var result = await x.Broken();
        Assert.Equal(["outcome_detail"], result.Dropped);
        Assert.Equal(("{}", "[secret guard dropped: outcome_detail]"), (result.Record.ExecutionParameters.GetRawText(), result.Record.OutcomeDetail));
    }

    [Fact]
    public async Task ARequestWithoutAContext_IsRecordedWithEmptyParametersAndAttributes()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        var request = new GovernedActionRequest
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
            Verify = Setup.Request().Verify,
        };
        var result = await x.Broken(request);
        Assert.Equal(("{}", "{}"), (result.Record.RequestParameters.GetRawText(), result.Record.Attributes.GetRawText()));
    }

    [Fact]
    public async Task ARecordIsAlwaysKept_OnceTheActionRan()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Na.Glass = Offline;
        var big = Enumerable.Range(0, 300).ToDictionary(i => $"k{i:D3}", i => (object?)$"item {i} of the report");
        x.Report = () => new ActionReport<string> { ExecutionParameters = big, OutcomeDetail = string.Concat(Enumerable.Repeat("a long detail, ", 100)) };
        var request = new GovernedActionRequest
        {
            AttestationId = AttestationId,
            RequestedCapability = "sp-secret.rotate",
            ResourceId = "kv:v/s",
            ResourceAction = ResourceActions.Rotate,
            Context = new EvaluationContext
            {
                Attributes = new Dictionary<string, object?> { ["note"] = string.Concat(Enumerable.Repeat("a short note, ", 650)) },
            },
            Verify = Setup.Request().Verify,
        };
        var result = await x.Broken(request, "outage");
        Assert.Equal(OutboxStates.Pending, result.Queued!.State);
        Assert.Equal("{}", result.Record.ExecutionParameters.GetRawText());
        Assert.Equal("[secret guard dropped the report]", result.Record.OutcomeDetail);
        Assert.Equal(big.Keys.Append("outcome_detail").Order(StringComparer.Ordinal), result.Dropped);
    }

    [Fact]
    public async Task AnOutcomeDetail_IsCutToWhatTheNATakes()
    {
        static string Words(int length) => string.Concat(Enumerable.Repeat("one more detail, ", 70))[..length];
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        foreach (var (detail, expected) in new (string, string)[]
        {
            (string.Concat(Enumerable.Repeat("one more detail, ", 90)), string.Concat(Enumerable.Repeat("one more detail, ", 90))[..1023] + "\u2026"),
            (string.Concat(Enumerable.Repeat("\ud83d\udea8", 1100)), string.Concat(Enumerable.Repeat("\ud83d\udea8", 1023)) + "\u2026"),
            (Words(1024), Words(1024)),
            (Words(1025), Words(1023) + "\u2026"),
        })
        {
            x.Report = () => new ActionReport<string> { OutcomeDetail = detail };
            var result = await x.Broken();
            Assert.Equal(expected, result.Record.OutcomeDetail);
        }
    }

    [Fact]
    public async Task AJustificationEndingInEmoji_IsKept()
    {
        var x = new Setup();
        x.Na.Evaluate = _ => Task.FromResult(Offline());
        x.Na.Glass = Offline;
        // 1024 characters, 1049 UTF-16 code units: the reference counts characters.
        var justification = string.Concat(Enumerable.Repeat("Rotate the leaked key now. ", 37)) + string.Concat(Enumerable.Repeat("\ud83d\udea8", 25));
        var result = await x.Broken(justification: justification);
        Assert.Equal((OutboxStates.Pending, justification), (result.Queued!.State, result.Record.Justification));
        await Assert.ThrowsAsync<OutOfBandRecordException>(() => x.Run(justification: justification + "\ud83d\udea8"));
    }

    [Fact]
    public async Task ACancellationAfterTheEvaluationFailed_RunsNoAction()
    {
        var x = new Setup();
        using var cts = new CancellationTokenSource();
        x.Na.Evaluate = async _ =>
        {
            await cts.CancelAsync();
            return Offline();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => x.Run(ct: cts.Token));
        await x.NothingRan();
    }
}
