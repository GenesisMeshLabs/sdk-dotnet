using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Pre-issued grants offline (1.9.0): the number rule, scalars, envelope parameters and the envelope, the typed
/// verifiers on the records the Python reference signed (the <c>grants</c> suite), and uses and time token requests
/// signed as the reference signs them.
/// </summary>
public class GrantRulesTests
{
    private static readonly JsonElement Suite = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "grants.json"))).RootElement;

    private static JsonElement Record(string id) =>
        Suite.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id).GetProperty("record");

    private static string Key(string id) =>
        Suite.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id).GetProperty("public_key").GetString()!;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // The reference's test key "b" (seed bytes 32 to 63): the grant's bearer key.
    private static readonly SeedSigner Bearer = new(Convert.ToBase64String(Enumerable.Range(32, 32).Select(i => (byte)i).ToArray()), "executor-b");

    // ── Numbers and scalars ─────────────────────────────────────────────────

    [Theory]
    [InlineData("5", "5")]
    [InlineData("5.0", "5")]
    [InlineData("-0.0", "0")]
    [InlineData("1E2", "100")]
    [InlineData("0.5", "0.5")]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("9007199254740993.0", "9007199254740992")]
    public void ANumberIsReadAsTheReferenceReadsIt(string json, string read)
    {
        Assert.True(GrantNumber.TryRead(Json(json), out var n));
        Assert.Equal(read, n.ToString());
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("-9223372036854775809")]
    [InlineData("1e20")]
    [InlineData("true")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    public void ANumberBeyond64BitsOrNoNumberIsNotRead(string json) => Assert.False(GrantNumber.TryRead(Json(json), out _));

    [Fact]
    public void NumbersCompareExactly()
    {
        GrantNumber Read(string json)
        {
            Assert.True(GrantNumber.TryRead(Json(json), out var n));
            return n;
        }
        Assert.True(Read("9007199254740993") > Read("9007199254740992.0"));
        Assert.True(Read("1") == Read("1.0"));
        Assert.True(Read("1") < Read("1.5"));
        Assert.True(Read("2") > Read("1.5"));
        Assert.True(Read("-2") < Read("-1.5"));
        Assert.True(Read("-1") > Read("-1.5"));
        Assert.True(Read("18446744073709551615") > Read("1.5e19"));
        Assert.Equal(0, Read("0.1").CompareTo(Read("0.1")));
        Assert.False(Read("0.1") == Read("0.10000000000000002"));
    }

    [Theory]
    [InlineData("true", "1", false)]
    [InlineData("\"1\"", "1", false)]
    [InlineData("1", "1.0", true)]
    [InlineData("null", "null", true)]
    [InlineData("false", "false", true)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("[1]", "[1]", false)]
    [InlineData("{}", "{}", false)]
    public void ScalarsCompareTypeStrictly(string a, string b, bool equal) => Assert.Equal(equal, GrantRules.ScalarEquals(Json(a), Json(b)));

    [Theory]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 9}""", "1", true)]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 9}""", "9.0", true)]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 9}""", "9.5", false)]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 9, "exclusive_minimum": true}""", "1", false)]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 18446744073709551615, "exclusive_maximum": true}""", "18446744073709551614", true)]
    [InlineData("""{"number_type": "integer", "minimum": 1, "maximum": 18446744073709551615, "exclusive_maximum": true}""", "18446744073709551615", false)]
    [InlineData("""{"number_type": "number", "minimum": 0.5, "maximum": 1}""", "0.5", true)]
    [InlineData("""{"number_type": "number", "minimum": 0.5, "maximum": 1, "exclusive_minimum": true}""", "0.5", false)]
    [InlineData("""{"number_type": "number", "minimum": 0, "maximum": 1}""", "true", false)]
    [InlineData("""{"number_type": "number", "minimum": 0, "maximum": 1}""", "\"1\"", false)]
    public void ARangeHoldsWhatItSays(string rangeJson, string value, bool inside)
    {
        var spec = GrantRules.Parameter(Json("""{"number_range": """ + rangeJson + "}"), out var problem);
        Assert.Null(problem);
        Assert.Equal(inside, GrantRules.InRange(Json(value), spec!.NumberRange!));
    }

    // ── Envelope parameters and the envelope ────────────────────────────────

    [Theory]
    [InlineData("[]", "an envelope parameter is an object")]
    [InlineData("""{"one_of": [1], "zeta": 1, "alpha": 2}""", "unknown field alpha, zeta")]
    [InlineData("""{"one_of": [1], "required": null}""", "required is a boolean")]
    [InlineData("""{"one_of": []}""", "one_of is a list of 1 to 256 scalars")]
    [InlineData("""{"one_of": [[1]]}""", "one_of holds scalars only")]
    [InlineData("""{"one_of": [1, 1.0]}""", "one_of repeats a value")]
    [InlineData("""{"one_of": [18446744073709551616]}""", "integers must fit in 64 bits")]
    [InlineData("""{"subset_of": ["a", 1]}""", "subset_of is a list of 1 to 256 strings")]
    [InlineData("""{"subset_of": ["a", "a"]}""", "subset_of repeats a value")]
    [InlineData("""{"number_range": {"number_type": "integer", "minimum": 1, "maximum": 2, "step": 1}}""", "number_range takes number_type, minimum, maximum, exclusive_minimum and exclusive_maximum")]
    [InlineData("""{"number_range": {"number_type": "float", "minimum": 1, "maximum": 2}}""", "number_type is integer or number")]
    [InlineData("""{"number_range": {"number_type": "integer", "maximum": 2}}""", "a number is expected")]
    [InlineData("""{"number_range": {"number_type": "integer", "minimum": true, "maximum": 2}}""", "a number is expected")]
    [InlineData("""{"number_range": {"number_type": "integer", "minimum": 1, "maximum": 2, "exclusive_minimum": null}}""", "exclusive_minimum is a boolean")]
    [InlineData("""{"number_range": {"number_type": "integer", "minimum": 0.5, "maximum": 2}}""", "an integer range has integer bounds")]
    [InlineData("""{"number_range": {"number_type": "integer", "minimum": 1, "maximum": 2, "exclusive_minimum": true, "exclusive_maximum": true}}""", "the range holds no integer")]
    [InlineData("""{"number_range": {"number_type": "number", "minimum": 1, "maximum": 1, "exclusive_maximum": true}}""", "the range holds no number")]
    [InlineData("""{"one_of": [1], "subset_of": ["a"]}""", "give exactly one of one_of, subset_of and number_range")]
    [InlineData("""{"required": false}""", "give exactly one of one_of, subset_of and number_range")]
    public void AParameterTheNAWouldRefuseIsNamed(string json, string why)
    {
        Assert.Null(GrantRules.Parameter(Json(json), out var problem));
        Assert.Equal("invalid envelope parameter: " + why, problem);
    }

    [Fact]
    public void AParameterIsNormalized()
    {
        var spec = GrantRules.Parameter(Json("""{"one_of": [5.0, "x", null, true], "number_range": null}"""), out _)!;
        Assert.Equal("""{"required":true,"one_of":[5,"x",null,true],"subset_of":null,"number_range":null}""", spec.ToJson());
    }

    [Fact]
    public void AnEnvelopeIsClosed()
    {
        var envelope = Json("""{"n": {"number_range": {"number_type": "integer", "minimum": 1, "maximum": 9}}, "tags": {"subset_of": ["a", "b"], "required": false}, "r": {"one_of": ["eu", 1]}}""");
        Assert.Null(GrantRules.EnvelopeProblem(envelope, Json("""{"n": 9, "r": "eu"}""")));
        Assert.Null(GrantRules.EnvelopeProblem(envelope, Json("""{"n": 1, "r": 1.0, "tags": ["b"]}""")));
        Assert.Equal("request_parameters is not an object", GrantRules.EnvelopeProblem(envelope, Json("[]")));
        // The first parameter outside it by code point order, then each of its own in that order.
        Assert.Equal("request_parameters.b is not in the envelope", GrantRules.EnvelopeProblem(envelope, Json("""{"z": 1, "b": 1, "n": 1, "r": 1}""")));
        Assert.Equal("request_parameters.n is required", GrantRules.EnvelopeProblem(envelope, Json("""{"r": 1}""")));
        Assert.Equal("request_parameters.n is outside its range", GrantRules.EnvelopeProblem(envelope, Json("""{"n": 10, "r": true}""")));
        Assert.Equal("request_parameters.r is not one of its values", GrantRules.EnvelopeProblem(envelope, Json("""{"n": 1, "r": true}""")));
        Assert.Equal("request_parameters.r is not a scalar", GrantRules.EnvelopeProblem(envelope, Json("""{"n": 1, "r": [1]}""")));
        Assert.Equal("request_parameters.tags is not a list of strings", GrantRules.EnvelopeProblem(envelope, Json("""{"n": 1, "r": 1, "tags": "a"}""")));
        Assert.Equal("request_parameters.tags has an item outside its set", GrantRules.EnvelopeProblem(envelope, Json("""{"n": 1, "r": 1, "tags": ["c"]}""")));
        Assert.Equal("parameter_envelope.x is not an envelope parameter",
            GrantRules.EnvelopeProblem(Json("""{"x": {"one_of": []}}"""), Json("{}")));
    }

    [Fact]
    public void AnEnvelopesShapeIsChecked()
    {
        Assert.Null(GrantRules.EnvelopeShapeProblem(Json("{}")));
        Assert.Equal("parameter_envelope is an object of parameter names", GrantRules.EnvelopeShapeProblem(Json("[]")));
        Assert.Equal("envelope parameter names match [A-Za-z0-9_-]{1,64}", GrantRules.EnvelopeShapeProblem(Json("""{"a.b": {"one_of": [1]}}""")));
        Assert.Equal("x: invalid envelope parameter: one_of repeats a value", GrantRules.EnvelopeShapeProblem(Json("""{"x": {"one_of": [1, 1]}}""")));
        var many = new JsonObject();
        for (var i = 0; i < 65; i++) many["p" + i] = new JsonObject { ["one_of"] = new JsonArray(1) };
        Assert.Equal("at most 64 envelope parameters", GrantRules.EnvelopeShapeProblem(JsonSerializer.SerializeToElement(many)));
    }

    // ── The typed verifiers, on the records the reference signed ────────────

    [Fact]
    public void TheNAsRecordsVerify()
    {
        var na = Key("signature-TimeToken-signer");
        var other = Key("signature-TimeToken-other-key");
        var token = Record("signature-TimeToken-signer").GetRawText();
        Assert.True(OfflineVerifier.VerifyTimeToken(token, [na]));
        Assert.True(OfflineVerifier.VerifyTimeToken(token, NAKeyHistory.ValidAlways([na])));
        Assert.False(OfflineVerifier.VerifyTimeToken(token, [other]));
        Assert.True(OfflineVerifier.TokenSignatureValid(Record("signature-TimeToken-signer"), [na]));
        Assert.False(OfflineVerifier.TokenSignatureValid(Record("signature-TimeToken-signer"), [other]));
        Assert.False(OfflineVerifier.TokenSignatureValid(Json("[]"), [na]));
        var revocation = Record("signature-GrantRevocationRecord-signer").GetRawText();
        Assert.True(OfflineVerifier.VerifyGrantRevocation(revocation, [na]));
        Assert.True(OfflineVerifier.VerifyGrantRevocation(revocation, NAKeyHistory.ValidAlways([na])));
        Assert.False(OfflineVerifier.VerifyGrantRevocation(revocation, [other]));
        Assert.Equal("accepted", OfflineVerifier.VerifyCapabilityGrant(Record("signature-CapabilityGrant-signer").GetRawText(), [na]).Reason);
        // Changed after signing, with a field this SDK does not know, or not in the reference's form: no record.
        Assert.False(OfflineVerifier.VerifyTimeToken(Changed(token, t => t["use_sequence"] = 2), [na]));
        Assert.False(OfflineVerifier.VerifyGrantRevocation(Changed(revocation, r => r["reason"] = "operator2"), [na]));
    }

    [Fact]
    public void TheHoldersRecordsVerify()
    {
        var bearer = Key("signature-GrantExecutionEvidence-signer");
        var other = Key("signature-GrantExecutionEvidence-other-key");
        var use = Record("signature-GrantExecutionEvidence-signer").GetRawText();
        Assert.True(OfflineVerifier.VerifyGrantUse(use, bearer));
        Assert.False(OfflineVerifier.VerifyGrantUse(use, other));
        Assert.False(OfflineVerifier.VerifyGrantUse(Changed(use, u => u["use_sequence"] = 3), bearer));
        // The first use names no previous one; a later one must.
        Assert.False(OfflineVerifier.VerifyGrantUse(Changed(use, u => u.Remove("prev_use_digest")), bearer));
        var request = Record("signature-TimeTokenRequest-signer").GetRawText();
        Assert.True(OfflineVerifier.VerifyTimeTokenRequest(request, bearer));
        Assert.False(OfflineVerifier.VerifyTimeTokenRequest(request, other));
    }

    private static string Changed(string json, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    // ── Signing, as the reference signs ─────────────────────────────────────

    [Fact]
    public async Task AUseIsSignedAsTheReferenceSignsIt()
    {
        var reference = Record("signature-GrantExecutionEvidence-signer");
        var grant = Record("signature-CapabilityGrant-signer").Deserialize<CapabilityGrant>()!;
        var use = await GrantRecords.SignUse(new GrantUseInput
        {
            Grant = grant,
            UseSequence = 2,
            PreviousDigest = reference.GetProperty("prev_use_digest").GetString(),
            Capability = "secret.rotate",
            RequestParameters = reference.GetProperty("request_parameters").Deserialize<Dictionary<string, object?>>(),
            ResourceId = "kv:vault/api-key",
            ResourceAction = ResourceActions.Rotate,
            ExecutedAt = new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero),
            TimeToken = reference.GetProperty("time_token"),
            Outcome = reference.GetProperty("outcome").GetString()!,
            OutcomeDetail = reference.TryGetProperty("outcome_detail", out var d) ? d.GetString() : null,
            ExecutionParameters = reference.GetProperty("execution_parameters").Deserialize<Dictionary<string, object?>>(),
            UseId = reference.GetProperty("use_id").GetString(),
        }, Bearer);
        Assert.Equal(reference.GetProperty("grant_digest").GetString(), use.GrantDigest);
        Assert.Equal(Canonical.FromJson(reference.GetRawText()), Canonical.FromJson(JsonSerializer.Serialize(use)));
        Assert.Equal(GrantRules.RecordDigest("GrantExecutionEvidence", reference.GetRawText()), GrantRules.UseDigest(use));
        Assert.Equal(new PreviousUse("2026-01-01T00:10:00Z", "2026-01-01T00:05:00Z"), GrantRules.NextPreviousUse(use));
        Assert.True(GrantRules.ContinuesChain(use, new UseChainHead(1, use.PrevUseDigest)));
        Assert.False(GrantRules.ContinuesChain(use, new UseChainHead(1, new string('e', 64))));
        Assert.False(GrantRules.ContinuesChain(use, new UseChainHead(2, use.PrevUseDigest)));
    }

    [Fact]
    public async Task ATimeTokenRequestIsSignedAsTheReferenceSignsIt()
    {
        var reference = Record("signature-TimeTokenRequest-signer");
        var request = await GrantRecords.SignTimeTokenRequest(new TimeTokenRequestInput
        {
            GrantId = reference.GetProperty("grant_id").GetString()!,
            UseSequence = 1,
            UseHeadDigest = reference.GetProperty("use_head_digest").GetString(),
            RequestedAt = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero),
        }, Bearer);
        Assert.Equal(Canonical.FromJson(reference.GetRawText()), Canonical.FromJson(JsonSerializer.Serialize(request)));
        // A head is given exactly from the first use on.
        var refused = await Assert.ThrowsAsync<GrantRecordException>(() => GrantRecords.SignTimeTokenRequest(
            new TimeTokenRequestInput { GrantId = "g", UseSequence = 0, UseHeadDigest = new string('f', 64) }, Bearer));
        Assert.Equal("time_token_request_malformed", refused.Code);
    }

    private static GrantUseInput First(Action<Dictionary<string, object?>>? parameters = null)
    {
        var request = new Dictionary<string, object?> { ["lifetime_days"] = 30, ["region"] = "eu-west", ["scopes"] = new[] { "read" } };
        parameters?.Invoke(request);
        return new GrantUseInput
        {
            Grant = Record("signature-CapabilityGrant-signer").Deserialize<CapabilityGrant>()!,
            UseSequence = 1,
            Capability = "secret.rotate",
            RequestParameters = request,
            TimeToken = Record("canonical-TimeToken-first"),
            Outcome = GrantUseOutcomes.Success,
        };
    }

    [Fact]
    public async Task AUseTheNAWouldRefuseIsRefusedBeforeSigning()
    {
        var first = await GrantRecords.SignUse(First(), Bearer);
        Assert.True(OfflineVerifier.VerifyGrantUse(JsonSerializer.Serialize(first), Key("signature-GrantExecutionEvidence-signer")));
        Assert.Null(first.PrevUseDigest);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", first.UseId);
        async Task<string> Code(GrantUseInput input) => (await Assert.ThrowsAsync<GrantRecordException>(() => GrantRecords.SignUse(input, Bearer))).Code;
        Assert.Equal("grant_use_malformed", await Code(First() with { UseId = "NOT-A-UUID" }));
        Assert.Equal("grant_use_malformed", await Code(First() with { PreviousDigest = new string('f', 64) }));
        Assert.Equal("grant_use_malformed", await Code(First() with { UseSequence = 2 }));
        Assert.Equal("grant_use_malformed", await Code(First() with { ResourceId = "kv:x" }));
        Assert.Equal("grant_use_malformed", await Code(First() with { TimeToken = Json("{}") }));
        Assert.Equal("grant_use_malformed", await Code(First() with { OutcomeDetail = new string('x', 1025) }));
        Assert.Equal("grant_use_secret_material", await Code(First(p => p["client_secret"] = "hunter2")));
        Assert.Equal("grant_use_secret_material", await Code(First() with { OutcomeDetail = "-----BEGIN PRIVATE KEY-----" }));
        // A value the NA cannot read is refused first, whatever else is wrong.
        await Assert.ThrowsAsync<StrictJsonException>(() => GrantRecords.SignUse(First(p => p["note"] = "\ud800") with { UseId = "NOT-A-UUID" }, Bearer));
    }

    [Fact]
    public void AUsesSecretMaterialIsNamed()
    {
        var use = new GrantExecutionEvidence
        {
            RequestParameters = Json("""{"region": "eu"}"""),
            ExecutionParameters = Json("""{"version_id": "v3"}"""),
        };
        Assert.Null(GrantRules.UseSecretProblem(use));
        use.ExecutionParameters = Json("""{"password": "x"}""");
        Assert.NotNull(GrantRules.UseSecretProblem(use));
    }
}
