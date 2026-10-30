using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Execution evidence (1.9.0): <see cref="ExecutionRecorder"/> against records the Python reference signed with the
/// same key, the offline checks of execution records and justification proofs, and <see cref="Canonical.Timestamp"/>.
/// </summary>
public class ExecutionTests
{
    // The Python reference (genesis_mesh.models.execution.ExecutionEvidence), signed with the zero seed as
    // "executor-key-1": the recorder must write the same records, so the same canonical forms and signatures.
    private const string ExecutorPublicKey = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=";
    private const string FirstCanonical = "{\"agreement_id\":\"att-1\",\"context_id\":\"ctx-1\",\"decision_id\":\"dec-1\",\"evidence_id\":\"00000000-0000-4000-8000-000000000001\",\"executed_at\":\"2026-10-30T12:00:00.123456Z\",\"executed_capability\":\"partner-secret.rotate\",\"execution_parameters\":{\"app_id\":\"app-1\",\"lifetime_days\":90,\"note\":\"caf\\u00e9\",\"ratio\":0.5},\"executor_sovereign_id\":\"executor-a\",\"outcome\":\"success\",\"outcome_detail\":null,\"prev_evidence_digest\":null,\"resource_action\":\"rotate\",\"resource_id\":\"partner:app-1/secret\",\"resource_sequence\":1,\"sequence_no\":1}";
    private const string FirstDigest = "f17308c1723d35d0c7abfdfa49779146b02c50a3c3411eaf5aad6f9f872e6995";
    private const string FirstSig = "L8Pzt1q8XHvsyAxu0C33cfShyP0CEcIqJXmjO31IEId2Z6HgJZS1ULXD6gxv0v1fIga/PYZaDVaW9gW8hKn3Cg==";
    private const string SecondDigest = "e82e42ba7147d2e8946792b51eee91d6ff4b69b50e48a840bed2e31d22f668fe";
    private const string SecondSig = "xK4H22uXMTJxTx8Mz+mLXr8YJT37vQYjar+7nyFaR984/Zx7sXL1ZvZGSPl2ewnY9D2XPVqL5Hzq1jrkALyRCQ==";
    private const string PlainCanonical = "{\"agreement_id\":\"att-1\",\"context_id\":\"ctx-2\",\"decision_id\":\"dec-2\",\"evidence_id\":\"00000000-0000-4000-8000-000000000003\",\"executed_at\":\"2026-10-30T12:00:02.500000Z\",\"executed_capability\":\"partner-secret.create\",\"execution_parameters\":{\"app_id\":\"app-2\"},\"executor_sovereign_id\":\"executor-a\",\"outcome\":\"failure\",\"outcome_detail\":\"graph_unavailable\",\"prev_evidence_digest\":null,\"sequence_no\":1}";
    private const string PlainDigest = "9fd1e5504449320a494f622d67dd7efa7b99833aa4037cd22fd04cdd6947bd2d";
    private const string PlainSig = "lFHbhO4iTqDDVZMyJ5x62fsONBj1LsHF1/kSVxwK0FvfPKEi/yJrBFWtpTlWvwk4DXyedEKt0MMqc1UVuhNbAg==";

    private static ExecutionRecorder Recorder(ISigner? signer = null) =>
        new("executor-a", signer ?? new SeedSigner(TestHelpers.ZeroSeedB64, "executor-key-1"));

    private static BoundaryDecision Decision(string id = "dec-1", string context = "ctx-1", string madeAt = "2026-10-30T11:59:59Z") =>
        new() { DecisionId = id, ContextId = context, AgreementId = "att-1", Authorized = true, DecisionMadeAt = madeAt };

    private static Task<ExecutionEvidence> First() => Recorder().Record(new RecordExecutionRequest
    {
        Decision = Decision(),
        ExecutedCapability = "partner-secret.rotate",
        Outcome = ExecutionOutcomes.Success,
        ExecutionParameters = new Dictionary<string, object?> { ["app_id"] = "app-1", ["lifetime_days"] = 90, ["ratio"] = 0.5, ["note"] = "café" },
        ResourceId = "partner:app-1/secret",
        ResourceAction = ResourceActions.Rotate,
        ExecutedAt = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560),
        EvidenceId = "00000000-0000-4000-8000-000000000001",
    });

    private static string Json(ExecutionEvidence e) => JsonSerializer.Serialize(e, Auth.SerializerOptions);

    private static string Sig(ExecutionEvidence e) => e.Signature.GetProperty("sig").GetString()!;

    // ── The recorder against the reference ───────────────────────────────────

    [Fact]
    public async Task Record_WritesTheReferenceRecord()
    {
        var first = await First();
        Assert.Equal(FirstSig, Sig(first));
        Assert.Equal("executor-key-1", first.Signature.GetProperty("key_id").GetString());
        Assert.Equal(FirstDigest, ExecutionRecorder.Digest(first));
        using var doc = JsonDocument.Parse(Json(first));
        Assert.Equal(FirstCanonical, Canonical.Of(doc.RootElement, ["signature"], ["prev_resource_digest"]));
        Assert.True(OfflineVerifier.VerifyExecutionSignature(Json(first), ExecutorPublicKey));
    }

    [Fact]
    public async Task Record_ChainsUnderTheDecisionAndOnTheResource()
    {
        var first = await First();
        var second = await Recorder().Record(new RecordExecutionRequest
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Partial,
            OutcomeDetail = "old credential kept for overlap",
            PriorRecord = first,
            ResourceId = "partner:app-1/secret",
            ResourceAction = ResourceActions.Rotate,
            PriorResource = ExecutionRecorder.HeadOf(first),
            ExecutedAt = new DateTimeOffset(2026, 10, 30, 12, 0, 1, TimeSpan.Zero),
            EvidenceId = "00000000-0000-4000-8000-000000000002",
        });
        Assert.Equal((2L, FirstDigest, 2L, FirstDigest), (second.SequenceNo, second.PrevEvidenceDigest, second.ResourceSequence, second.PrevResourceDigest));
        Assert.Equal("2026-10-30T12:00:01Z", second.ExecutedAt);
        Assert.Equal(SecondSig, Sig(second));
        Assert.Equal(SecondDigest, ExecutionRecorder.Digest(second));
    }

    [Fact]
    public async Task Record_WithoutAResource_LeavesTheResourceFieldsOut()
    {
        var plain = await Recorder().Record(new RecordExecutionRequest
        {
            Decision = Decision("dec-2", "ctx-2"),
            ExecutedCapability = "partner-secret.create",
            Outcome = ExecutionOutcomes.Failure,
            OutcomeDetail = "graph_unavailable",
            ExecutionParameters = new Dictionary<string, object?> { ["app_id"] = "app-2" },
            ExecutedAt = new DateTimeOffset(2026, 10, 30, 12, 0, 2, 500, TimeSpan.Zero),
            EvidenceId = "00000000-0000-4000-8000-000000000003",
        });
        Assert.Equal(PlainSig, Sig(plain));
        Assert.Equal(PlainDigest, ExecutionRecorder.Digest(plain));
        var sent = JsonNode.Parse(Json(plain))!.AsObject();
        Assert.DoesNotContain(sent, p => p.Key.StartsWith("resource_", StringComparison.Ordinal) || p.Key == "prev_resource_digest");
        // Fields the NA signs as null are written as null.
        Assert.True(sent.ContainsKey("prev_evidence_digest") && sent["prev_evidence_digest"] is null);
        using var doc = JsonDocument.Parse(Json(plain));
        Assert.Equal(PlainCanonical, Canonical.Of(doc.RootElement, ["signature"]));
        // A record without a resource is no resource's head: a record linked to it would be refused.
        Assert.Throws<ArgumentException>(() => ExecutionRecorder.HeadOf(plain));
    }

    [Fact]
    public async Task Record_FirstOnTheResource_HasNoPreviousResourceDigest()
    {
        var first = await First();
        var sent = JsonNode.Parse(Json(first))!.AsObject();
        Assert.False(sent.ContainsKey("prev_resource_digest"));
        Assert.Equal(1, first.ResourceSequence);
        Assert.Null(first.OutcomeDetail);
        Assert.True(sent.ContainsKey("outcome_detail") && sent["outcome_detail"] is null);
    }

    [Fact]
    public async Task Record_Defaults_AnIdAndNowButNotBeforeTheDecision()
    {
        var later = DateTimeOffset.UtcNow.AddHours(1);
        var madeAt = Canonical.Timestamp(later);
        var record = await Recorder().Record(new RecordExecutionRequest
        {
            Decision = Decision(madeAt: madeAt),
            ExecutedCapability = "partner-secret.create",
            Outcome = ExecutionOutcomes.Success,
        });
        Assert.Equal(madeAt, record.ExecutedAt);
        Assert.True(Guid.TryParse(record.EvidenceId, out _));
        Assert.Equal("{}", record.ExecutionParameters.GetRawText());

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var now = await Recorder().Record(new RecordExecutionRequest
        {
            Decision = Decision(madeAt: "2026-01-01T00:00:00Z"),
            ExecutedCapability = "partner-secret.create",
            Outcome = ExecutionOutcomes.Success,
        });
        Assert.InRange(OfflineVerifier.ParseTimestamp(now.ExecutedAt), before, DateTimeOffset.UtcNow);
        Assert.True(StrictFields.CanonicalTimestamp(now.ExecutedAt));

        var unparsed = await Recorder().Record(new RecordExecutionRequest
        {
            Decision = Decision(madeAt: "not a time"),
            ExecutedCapability = "partner-secret.create",
            Outcome = ExecutionOutcomes.Success,
        });
        Assert.InRange(OfflineVerifier.ParseTimestamp(unparsed.ExecutedAt), before, DateTimeOffset.UtcNow);
    }

    // ── What the recorder refuses ────────────────────────────────────────────

    [Fact]
    public async Task Record_RefusesSecretMaterial_BeforeSigning()
    {
        var signer = new CountingSigner();
        var recorder = Recorder(signer);
        RecordExecutionRequest With(IDictionary<string, object?>? parameters = null, string? detail = null) => new()
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
            ExecutionParameters = parameters,
            OutcomeDetail = detail,
        };
        var named = await Assert.ThrowsAsync<SecretMaterialException>(() =>
            recorder.Record(With(new Dictionary<string, object?> { ["client_secret"] = "x" })));
        Assert.Equal("evidence_secret_material", named.Code);
        await Assert.ThrowsAsync<SecretMaterialException>(() =>
            recorder.Record(With(detail: "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJlLXZhbHVlLWhlcmU")));
        await Assert.ThrowsAsync<SecretMaterialException>(() =>
            recorder.Record(With(new Dictionary<string, object?> { ["note"] = new string('a', EvidenceMetadata.MaxMetadataBytes) })));
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task Record_RefusesValuesTheNAWouldNotRead()
    {
        var signer = new CountingSigner();
        RecordExecutionRequest With(object? value) => new()
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
            ExecutionParameters = new Dictionary<string, object?> { ["setting"] = value },
        };
        var nan = await Assert.ThrowsAsync<StrictJsonException>(() => Recorder(signer).Record(With(double.NaN)));
        Assert.Equal("invalid_json", nan.Reason);
        object deep = "leaf";
        for (var i = 0; i < 70; i++) deep = new[] { deep };
        await Assert.ThrowsAsync<StrictJsonException>(() => Recorder(signer).Record(With(deep)));
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task Record_RefusesALoneSurrogate_AsTheTypeScriptSdkDoes()
    {
        // The serializer would read an unpaired surrogate back as U+FFFD and sign another value than the caller's.
        var signer = new CountingSigner();
        RecordExecutionRequest With(string parameter = "ok", string? detail = null, string? resourceId = null) => new()
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
            ExecutionParameters = new Dictionary<string, object?> { ["setting"] = parameter },
            OutcomeDetail = detail,
            ResourceId = resourceId,
            ResourceAction = resourceId is null ? null : ResourceActions.Rotate,
        };
        foreach (var request in new[] { With("\ud800"), With(detail: "a\udc00"), With(resourceId: "partner:\ud800") })
        {
            var refused = await Assert.ThrowsAsync<StrictJsonException>(() => Recorder(signer).Record(request));
            Assert.Equal("lone_surrogate", refused.Reason);
        }
        var key = await Assert.ThrowsAsync<StrictJsonException>(() => Recorder(signer).Record(new RecordExecutionRequest
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
            ExecutionParameters = new Dictionary<string, object?> { ["key\ud800"] = 1 },
        }));
        Assert.Equal("lone_surrogate", key.Reason);
        Assert.Equal(0, signer.Calls);
        // A surrogate pair is a character like any other.
        var pair = await Recorder().Record(With("😀"));
        Assert.Equal("😀", pair.ExecutionParameters.GetProperty("setting").GetString());
        Assert.True(OfflineVerifier.VerifyExecutionSignature(Json(pair), ExecutorPublicKey));
    }

    [Fact]
    public async Task Record_RefusesAnIncompleteRequest()
    {
        var recorder = Recorder();
        await Assert.ThrowsAsync<ArgumentNullException>(() => recorder.Record(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.Record(new RecordExecutionRequest
        {
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.Record(new RecordExecutionRequest
        {
            Decision = Decision(),
            Outcome = ExecutionOutcomes.Success,
        }));
        var alone = await Assert.ThrowsAsync<ArgumentException>(() => recorder.Record(new RecordExecutionRequest
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
            ResourceId = "partner:app-1/secret",
        }));
        Assert.Contains("go together", alone.Message);
        Assert.Throws<ArgumentException>(() => new ExecutionRecorder("", new SeedSigner(TestHelpers.ZeroSeedB64, "k")));
        Assert.Throws<ArgumentNullException>(() => new ExecutionRecorder("executor-a", null!));
        Assert.Throws<ArgumentNullException>(() => ExecutionRecorder.Digest(null!));
        Assert.Throws<ArgumentNullException>(() => ExecutionRecorder.HeadOf(null!));
    }

    [Fact]
    public async Task Record_RefusesASignatureThatIsNot64Bytes()
    {
        var recorder = Recorder(new CountingSigner(length: 63));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.Record(new RecordExecutionRequest
        {
            Decision = Decision(),
            ExecutedCapability = "partner-secret.rotate",
            Outcome = ExecutionOutcomes.Success,
        }));
        Assert.Equal("executor-key-1", Recorder().KeyId);
        Assert.Equal("executor-a", Recorder().ExecutorSovereignId);
    }

    private sealed class CountingSigner(int length = 64) : ISigner
    {
        public int Calls;
        public string KeyId => "counting";

        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new byte[length]);
        }
    }

    // ── Offline checks ───────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyExecutionSignature_RefusesAnotherKey_ATamperedRecord_AndAnUnknownField()
    {
        var json = Json(await First());
        Assert.False(OfflineVerifier.VerifyExecutionSignature(json, "bPsnBlkvakRwfqL/Ctcg9Vf+7DN0ZlbjbkljtubXbE0="));
        var tampered = JsonNode.Parse(json)!.AsObject();
        tampered["outcome"] = "failure";
        Assert.False(OfflineVerifier.VerifyExecutionSignature(tampered.ToJsonString(), ExecutorPublicKey));
        var unknown = JsonNode.Parse(json)!.AsObject();
        unknown["surprise"] = 1;
        Assert.False(OfflineVerifier.VerifyExecutionSignature(unknown.ToJsonString(), ExecutorPublicKey));
        var unsigned = JsonNode.Parse(json)!.AsObject();
        unsigned.Remove("signature");
        Assert.False(OfflineVerifier.VerifyExecutionSignature(unsigned.ToJsonString(), ExecutorPublicKey));
        Assert.False(OfflineVerifier.VerifyExecutionSignature("[]", ExecutorPublicKey));
        Assert.Throws<StrictJsonException>(() => OfflineVerifier.VerifyExecutionSignature("{\"a\":1,\"a\":2}", ExecutorPublicKey));
    }

    [Fact]
    public async Task VerifyExecutionSignature_AcceptsTheResourceFieldsSentAsNull()
    {
        // The NA admits a record with its absent resource fields written as null: the signed form leaves them out.
        var json = JsonNode.Parse(Json(await First()))!.AsObject();
        json["prev_resource_digest"] = null;
        Assert.True(OfflineVerifier.VerifyExecutionSignature(json.ToJsonString(), ExecutorPublicKey));
        Assert.Equal(FirstDigest, OfflineVerifier.ExecutionDigest(json.ToJsonString()));
    }

    [Fact]
    public void ExecutionDigest_RefusesWhatIsNotAnObject() =>
        Assert.Throws<ArgumentException>(() => OfflineVerifier.ExecutionDigest("[1]"));

    /// <summary>
    /// A captured justification proof, signed again by the Python reference with the zero seed (its NA key is not in
    /// the captures); the live contract check verifies a proof under the NA's own key.
    /// </summary>
    private static (string Proof, string NAKey) SignedProof()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "contract", "na_responses.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var proof = JsonNode.Parse(doc.RootElement.GetProperty("boundary_evaluate").GetProperty("justification_proof").GetRawText())!;
        proof["signature"]!["sig"] = "811StNx26QuPuNjbjvCG07A/rvkYndZa5/pSRQP4atSuXaE0Xq6Zgfn3qUK4D6DO6kk/D/GddncJZ7BpIRgpDg==";
        return (proof.ToJsonString(), ExecutorPublicKey);
    }

    [Fact]
    public void VerifyJustificationSignature_ChecksTheNAKey_AndTheKeyValidAtTheProof()
    {
        var (proof, key) = SignedProof();
        Assert.True(OfflineVerifier.VerifyJustificationSignature(proof, [key]));
        Assert.False(OfflineVerifier.VerifyJustificationSignature(proof, ["bPsnBlkvakRwfqL/Ctcg9Vf+7DN0ZlbjbkljtubXbE0="]));
        Assert.True(OfflineVerifier.VerifyJustificationSignature(proof, NAKeyHistory.ValidAlways([key])));

        var issued = OfflineVerifier.ParseTimestamp(JsonNode.Parse(proof)!["proof_issued_at"]!.GetValue<string>());
        var later = new NAKeyHistory("contract-na", null, [new NAKey(1, "na-1", key, issued.AddSeconds(1), null, null, false)]);
        Assert.False(OfflineVerifier.VerifyJustificationSignature(proof, later));

        var tampered = JsonNode.Parse(proof)!.AsObject();
        tampered["decision_id"] = "another";
        Assert.False(OfflineVerifier.VerifyJustificationSignature(tampered.ToJsonString(), [key]));
        var unreadable = JsonNode.Parse(proof)!.AsObject();
        unreadable["proof_issued_at"] = "not a time";
        Assert.False(OfflineVerifier.VerifyJustificationSignature(unreadable.ToJsonString(), NAKeyHistory.ValidAlways([key])));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyJustificationSignature(proof, (NAKeyHistory)null!));
    }

    // ── Timestamps ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "2026-10-30T12:00:00Z")]
    [InlineData(5_000_000L, "2026-10-30T12:00:00.500000Z")]
    [InlineData(1_234_567L, "2026-10-30T12:00:00.123456Z")]
    [InlineData(9L, "2026-10-30T12:00:00Z")]
    [InlineData(10L, "2026-10-30T12:00:00.000001Z")]
    public void Timestamp_WritesMicrosecondsAsTheNADoes(long ticks, string expected)
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero).AddTicks(ticks);
        Assert.Equal(expected, Canonical.Timestamp(at));
        Assert.True(StrictFields.CanonicalTimestamp(expected));
    }

    [Fact]
    public void Timestamp_WritesUtc() =>
        Assert.Equal("2026-10-30T10:00:00Z", Canonical.Timestamp(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.FromHours(2))));
}
