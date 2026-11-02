using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// The signature verifiers of NA-signed records (1.9.0), as the TypeScript SDK's <c>verifyDecisionSignature</c>,
/// <c>verifyAttestationSignature</c>, <c>verifyPolicySignature</c>, <c>verifyRevocationFeedSignature</c>,
/// <c>revocationFeedSigner</c> and <c>verifyTreatySignature</c>: records the Python reference signed, records signed
/// again after a change, and the records of the shared <c>vouching</c> suite, whose verdicts they must match.
/// </summary>
public class TypedVerifierTests
{
    private static readonly Lazy<JsonObject> Reference = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    private static readonly string VouchingPath = Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "vouching.json");

    private static readonly Key Zero = Key.Import(SignatureAlgorithm.Ed25519, new byte[32], KeyBlobFormat.RawPrivateKey);
    private static readonly Key Other = Key.Create(SignatureAlgorithm.Ed25519);

    private static string Pub(Key key) => Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static string NaKey => (string)Reference.Value["na_public_key"]!;

    /// <summary>The field each kind is placed at in a key history.</summary>
    private static readonly Dictionary<string, string> TimeFields = new()
    {
        ["BoundaryDecision"] = "decision_made_at",
        ["MembershipAttestation"] = "issued_at",
        ["BoundaryPolicy"] = "issued_at",
        ["SovereignRevocationFeed"] = "issued_at",
        ["RecognitionTreaty"] = "issued_at",
    };

    /// <summary>The kinds a key history judges for the present too.</summary>
    private static readonly HashSet<string> JudgedNow = ["MembershipAttestation", "SovereignRevocationFeed", "RecognitionTreaty"];

    private static bool Verify(string model, string json, IReadOnlyList<string> keys) => model switch
    {
        "BoundaryDecision" => OfflineVerifier.VerifyDecisionSignature(json, keys),
        "MembershipAttestation" => OfflineVerifier.VerifyAttestationSignature(json, keys),
        "BoundaryPolicy" => OfflineVerifier.VerifyPolicySignature(json, keys),
        "SovereignRevocationFeed" => OfflineVerifier.VerifyRevocationFeedSignature(json, keys),
        "RecognitionTreaty" => OfflineVerifier.VerifyTreatySignature(json, keys),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    private static bool Verify(string model, string json, NAKeyHistory history, DateTimeOffset? now = null)
    {
        Assert.True(now is null || JudgedNow.Contains(model), $"{model} is not judged for the present");
        return model switch
        {
            "BoundaryDecision" => OfflineVerifier.VerifyDecisionSignature(json, history),
            "MembershipAttestation" => OfflineVerifier.VerifyAttestationSignature(json, history, now),
            "BoundaryPolicy" => OfflineVerifier.VerifyPolicySignature(json, history),
            "SovereignRevocationFeed" => OfflineVerifier.VerifyRevocationFeedSignature(json, history, now),
            "RecognitionTreaty" => OfflineVerifier.VerifyTreatySignature(json, history, now),
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
    }

    // ── the vouching suite ─────────────────────────────────────────────────

    private static IEnumerable<JsonElement> VouchingVectors() =>
        JsonDocument.Parse(File.ReadAllText(VouchingPath)).RootElement.GetProperty("vectors").EnumerateArray()
            .Where(v => JudgedNow.Contains(v.GetProperty("model").GetString()!));

    public static TheoryData<string> VouchingIds() => new(VouchingVectors().Select(v => v.GetProperty("id").GetString()!));

    private static JsonElement VouchingVector(string id) => VouchingVectors().Single(v => v.GetProperty("id").GetString() == id);

    private static NAKeyHistory History(JsonElement input)
    {
        var result = OfflineVerifier.VerifyKeyHistory(input.GetProperty("genesis").GetRawText(),
            input.GetProperty("successions").GetRawText(), input.GetProperty("root_public_key").GetString()!);
        Assert.True(result.Verified, result.Reason);
        return input.TryGetProperty("grace_seconds", out var grace)
            ? result.History!.WithVouchingGrace(TimeSpan.FromSeconds(grace.GetDouble()))
            : result.History!;
    }

    /// <summary>A record of the suite or the reference, as an object to change.</summary>
    private static JsonObject Record(string model) => model switch
    {
        "BoundaryDecision" => Reference.Value["allowed"]!["decision"]!.DeepClone().AsObject(),
        "MembershipAttestation" => Reference.Value["attestation"]!.DeepClone().AsObject(),
        "BoundaryPolicy" => Reference.Value["policy"]!.DeepClone().AsObject(),
        _ => JsonNode.Parse(VouchingVectors().First(v => v.GetProperty("model").GetString() == model)
            .GetProperty("input").GetProperty("record").GetRawText())!.AsObject(),
    };

    /// <summary><paramref name="record"/> of <paramref name="model"/> after <paramref name="change"/>, signed by <paramref name="key"/> over its signed form.</summary>
    private static string Signed(string model, JsonObject record, Key key, Action<JsonObject>? change = null) =>
        Signed(model, record, [key], change);

    /// <summary>As the other overload, with a signature by each of <paramref name="keys"/> in order (the first for a single <c>signature</c>).</summary>
    private static string Signed(string model, JsonObject record, Key[] keys, Action<JsonObject>? change = null)
    {
        change?.Invoke(record);
        var field = StrictFields.Model(model).SignatureField!;
        record.Remove(field);
        var body = OfflineVerifier.ReceivedCanonical(model, JsonSerializer.SerializeToElement(record));
        var signatures = keys.Select(key => new JsonObject
        {
            ["key_id"] = "na",
            ["sig"] = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(body))),
        }).ToArray();
        record[field] = field == "signatures" ? new JsonArray(signatures) : signatures[0];
        return record.ToJsonString();
    }

    public static TheoryData<string> Models() => new(TimeFields.Keys);

    [Theory]
    [MemberData(nameof(VouchingIds))]
    public void ARecordOfTheVouchingSuite_VerifiesAsTheReferenceJudgesIt(string id)
    {
        // Only the signature: no record of these kinds in the suite is refused for its times (record_malformed).
        var v = VouchingVector(id);
        var input = v.GetProperty("input");
        var model = v.GetProperty("model").GetString()!;
        var record = input.GetProperty("record").GetRawText();
        var history = History(input);
        var now = OfflineVerifier.ParseTimestamp(input.GetProperty("now").GetString()!);
        var expected = v.GetProperty("expected");
        var atRecordTime = expected.GetProperty("at_record_time").ValueKind == JsonValueKind.Null;
        Assert.Equal(atRecordTime, Verify(model, record, history));
        Assert.Equal(expected.GetProperty("now").ValueKind == JsonValueKind.Null, Verify(model, record, history, now));
        Assert.Equal(atRecordTime ? null : "refused", OfflineVerifier.NARecordRefusal(model, record, history) is null ? null : "refused");
        if (model != "SovereignRevocationFeed") return;
        var signer = OfflineVerifier.RevocationFeedSigner(record, history);
        Assert.Equal(atRecordTime, signer is not null);
        if (signer is not null) Assert.True(Verify(model, record, [signer]));
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    public void TheReferenceDecisions_Verify_UnderTheNAsKeyOnly(string name)
    {
        var decision = Reference.Value[name]!["decision"]!.ToJsonString();
        Assert.True(OfflineVerifier.VerifyDecisionSignature(decision, [Pub(Other), NaKey]));
        Assert.True(OfflineVerifier.VerifyDecisionSignature(decision, NAKeyHistory.ValidAlways([NaKey])));
        Assert.False(OfflineVerifier.VerifyDecisionSignature(decision, [Pub(Other)]));
        Assert.False(OfflineVerifier.VerifyDecisionSignature(decision, NAKeyHistory.ValidAlways([Pub(Other)])));
    }

    [Theory]
    [InlineData("MembershipAttestation", "attestation")]
    [InlineData("BoundaryPolicy", "policy")]
    public void TheReferenceRecords_Verify_UnderTheNAsKeyOnly(string model, string name)
    {
        var record = Reference.Value[name]!.ToJsonString();
        Assert.True(Verify(model, record, [NaKey]));
        Assert.True(Verify(model, record, NAKeyHistory.ValidAlways([NaKey])));
        Assert.False(Verify(model, record, [Pub(Other)]));
        Assert.False(Verify(model, record, NAKeyHistory.ValidAlways([Pub(Other)])));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ARecordSignedAgain_Verifies_AndAnyChangeAfterwardsDoesNot(string model)
    {
        var json = Signed(model, Record(model), Zero);
        Assert.True(Verify(model, json, [Pub(Zero)]));
        Assert.True(Verify(model, json, NAKeyHistory.ValidAlways([Pub(Zero)])));

        var changed = JsonNode.Parse(json)!.AsObject();
        var field = TimeFields[model];
        changed[field] = "2020-01-01T00:00:00Z";
        Assert.False(Verify(model, changed.ToJsonString(), [Pub(Zero)]));

        var unsigned = JsonNode.Parse(json)!.AsObject();
        unsigned.Remove(StrictFields.Model(model).SignatureField!);
        Assert.False(Verify(model, unsigned.ToJsonString(), [Pub(Zero)]));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ASignedFieldThisSdkDoesNotKnow_NeverVerifies(string model)
    {
        // As every verifier here, a treaty's too: the TypeScript SDK's verifyTreatySignature does not check it.
        var json = Signed(model, Record(model), Zero, r => r["added_later"] = "x");
        Assert.False(Verify(model, json, [Pub(Zero)]));
        Assert.False(Verify(model, json, NAKeyHistory.ValidAlways([Pub(Zero)])));
        Assert.True(OfflineVerifier.SignedAsReceived(model, JsonDocument.Parse(json).RootElement, [Pub(Zero)]));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void WithAKeyHistory_TheKeyMustBeValidAtTheRecordsTime(string model)
    {
        var record = Record(model);
        var json = Signed(model, record, Zero);
        var at = OfflineVerifier.ParseTimestamp((string)record[TimeFields[model]]!);
        NAKeyHistory From(DateTimeOffset start) => new("TEST", null, [new NAKey(1, "na", Pub(Zero), start, null, null, false)]);
        Assert.True(Verify(model, json, From(at)));
        Assert.False(Verify(model, json, From(at.AddTicks(10))));
        // Without the time, no key of a history is valid. Nor does a list of keys take it: a record without a field the
        // reference always writes is not in canonical form (the TypeScript SDK checks unknown fields only).
        var timeless = Signed(model, Record(model), Zero, r => r.Remove(TimeFields[model]));
        Assert.False(Verify(model, timeless, From(at)));
        Assert.False(Verify(model, timeless, [Pub(Zero)]));
        Assert.Contains(TimeFields[model], StrictFields.NonCanonicalFields(model, JsonDocument.Parse(timeless).RootElement));
    }

    [Theory]
    [InlineData("MembershipAttestation")]
    [InlineData("SovereignRevocationFeed")]
    [InlineData("RecognitionTreaty")]
    public void JudgedNow_ASucceededKeyVouchesOnlyUntilItsHorizon(string model)
    {
        var record = Record(model);
        var json = Signed(model, record, Zero);
        var at = OfflineVerifier.ParseTimestamp((string)record[TimeFields[model]]!);
        var succeeded = at.AddDays(1);
        var history = new NAKeyHistory("TEST", null,
        [
            new NAKey(1, "na-1", Pub(Zero), null, succeeded, null, false),
            new NAKey(2, "na-2", Pub(Other), succeeded, null, null, false),
        ]);
        // The horizon: the key's end, the hand-over and the grace.
        var horizon = succeeded + NAKeyHistory.SuccessionHandover + NAKeyHistory.SuccessionVouchingGrace;
        Assert.Equal(horizon, history.VouchesUntil(Pub(Zero)));
        Assert.True(Verify(model, json, history));
        Assert.True(Verify(model, json, history, horizon.AddTicks(-10)));
        Assert.False(Verify(model, json, history, horizon));
    }

    [Theory]
    [InlineData("MembershipAttestation")]
    [InlineData("SovereignRevocationFeed")]
    [InlineData("RecognitionTreaty")]
    public void JudgedNow_ARecordBothKeysOfAHandOverSigned_VouchesWhileEitherDoes(string model)
    {
        // As the TypeScript SDK: any key valid at the record's time that still vouches. NARecordRefusal, as the
        // reference, judges the first key of the history that signed it, so the two differ past the old key's horizon.
        var record = Record(model);
        var at = OfflineVerifier.ParseTimestamp((string)record[TimeFields[model]]!);
        var succeeded = at.AddSeconds(-100);
        var history = new NAKeyHistory("TEST", null,
        [
            new NAKey(1, "na-1", Pub(Zero), null, succeeded, null, false),
            new NAKey(2, "na-2", Pub(Other), succeeded, null, null, false),
        ]);
        var json = Signed(model, record, [Other, Zero]);
        var horizon = history.VouchesUntil(Pub(Zero))!.Value;
        Assert.True(Verify(model, json, history, horizon));
        Assert.Equal("outlives_key", OfflineVerifier.NARecordRefusal(model, json, history, horizon));
        Assert.Null(OfflineVerifier.NARecordRefusal(model, json, history, horizon.AddTicks(-10)));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void AListOfKeys_AnyOfThemMaySign(string model)
    {
        var json = Signed(model, Record(model), Zero);
        Assert.True(Verify(model, json, [Pub(Other), Pub(Zero)]));
        Assert.True(Verify(model, json, NAKeyHistory.ValidAlways([Pub(Other), Pub(Zero)])));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ANullTime_PlacesTheRecordNowhereInAHistory(string model)
    {
        // The TypeScript SDK takes every key of the history for a record without its time.
        var json = Signed(model, Record(model), Zero, r => r[TimeFields[model]] = null);
        Assert.False(Verify(model, json, NAKeyHistory.ValidAlways([Pub(Zero)])));
    }

    [Theory]
    [InlineData("BoundaryDecision")]
    [InlineData("BoundaryPolicy")]
    public void ADecisionOrAPolicy_IsJudgedAtItsOwnTimeOnly(string model)
    {
        // Signed by a key that has since been succeeded and is past its horizon: no time to judge at is taken.
        var record = Record(model);
        var at = OfflineVerifier.ParseTimestamp((string)record[TimeFields[model]]!);
        var json = Signed(model, record, Zero);
        var history = new NAKeyHistory("TEST", null,
        [
            new NAKey(1, "na-1", Pub(Zero), null, at.AddDays(1), null, false),
            new NAKey(2, "na-2", Pub(Other), at.AddDays(1), null, null, false),
        ]);
        Assert.True(history.RecordOutlivesKey(Pub(Zero), DateTimeOffset.UtcNow));
        Assert.True(Verify(model, json, history));
    }

    [Theory]
    [InlineData("MembershipAttestation")]
    [InlineData("RecognitionTreaty")]
    public void ARecord_IsPlacedAtItsIssuedAt_NotItsValidFrom(string model)
    {
        var record = Record(model);
        var issued = OfflineVerifier.ParseTimestamp((string)record["issued_at"]!);
        var json = Signed(model, record, Zero, r => r["valid_from"] = Canonical.Timestamp(issued.AddDays(-1)));
        var history = new NAKeyHistory("TEST", null, [new NAKey(1, "na", Pub(Zero), issued, null, null, false)]);
        Assert.True(Verify(model, json, history));
    }

    [Fact]
    public void AFeedBothKeysOfAHandOverSigned_IsSignedByTheFirstKeyOfTheHistory()
    {
        var feed = Record("SovereignRevocationFeed");
        var at = OfflineVerifier.ParseTimestamp((string)feed["issued_at"]!);
        var history = new NAKeyHistory("TEST", null,
        [
            new NAKey(1, "na-1", Pub(Zero), null, at.AddSeconds(-100), null, false),
            new NAKey(2, "na-2", Pub(Other), at.AddSeconds(-100), null, null, false),
        ]);
        var json = Signed("SovereignRevocationFeed", feed, [Other, Zero]);
        Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(json, history));
    }

    [Fact]
    public void TheFeedsSigner_IsTheKeyThatSignedIt_WhateverItsFields()
    {
        var json = Signed("SovereignRevocationFeed", Record("SovereignRevocationFeed"), Zero);
        Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(json, [Pub(Other), Pub(Zero)]));
        Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(json, NAKeyHistory.ValidAlways([Pub(Other), Pub(Zero)])));
        Assert.Null(OfflineVerifier.RevocationFeedSigner(json, [Pub(Other)]));
        // It names the signer only: a feed with a field this SDK does not know still has one, though it does not verify.
        var unknown = Signed("SovereignRevocationFeed", Record("SovereignRevocationFeed"), Zero, r => r["added_later"] = 1);
        Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(unknown, [Pub(Zero)]));
        Assert.False(OfflineVerifier.VerifyRevocationFeedSignature(unknown, [Pub(Zero)]));
        // A key history places it at its issued_at: without one, no key of the history signed it.
        foreach (var issued in new JsonNode?[] { null, "yesterday" })
        {
            var timeless = Signed("SovereignRevocationFeed", Record("SovereignRevocationFeed"), Zero, r => r["issued_at"] = issued);
            Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(timeless, [Pub(Zero)]));
            Assert.Null(OfflineVerifier.RevocationFeedSigner(timeless, NAKeyHistory.ValidAlways([Pub(Zero)])));
        }
        // And at its issued_at, by the key valid then only.
        var feed = Record("SovereignRevocationFeed");
        var at = OfflineVerifier.ParseTimestamp((string)feed["issued_at"]!);
        var history = new NAKeyHistory("TEST", null,
        [
            new NAKey(1, "na-1", Pub(Other), null, at, null, false),
            new NAKey(2, "na-2", Pub(Zero), at, null, null, false),
        ]);
        Assert.Equal(Pub(Zero), OfflineVerifier.RevocationFeedSigner(Signed("SovereignRevocationFeed", feed, Zero), history));
        var early = Signed("SovereignRevocationFeed", Record("SovereignRevocationFeed"), Zero,
            r => r["issued_at"] = Canonical.Timestamp(at.AddDays(-1)));
        Assert.Null(OfflineVerifier.RevocationFeedSigner(early, history));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void NotAnObject_NeverVerifies_AndInputThatIsNotStrictJson_IsRefused(string model)
    {
        foreach (var json in new[] { "[]", "null", "\"x\"", "1" })
        {
            Assert.False(Verify(model, json, [Pub(Zero)]));
            Assert.False(Verify(model, json, NAKeyHistory.ValidAlways([Pub(Zero)])));
        }
        Assert.Throws<StrictJsonException>(() => Verify(model, "{\"a\":1,\"a\":2}", [Pub(Zero)]));
        Assert.Throws<StrictJsonException>(() => Verify(model, "{", NAKeyHistory.ValidAlways([Pub(Zero)])));
        Assert.Throws<ArgumentNullException>(() => Verify(model, "{}", (NAKeyHistory)null!));
    }

    [Fact]
    public void TheFeedsSigner_OfAnythingButAnObject_IsNone()
    {
        Assert.Null(OfflineVerifier.RevocationFeedSigner("[]", [Pub(Zero)]));
        Assert.Null(OfflineVerifier.RevocationFeedSigner("null", NAKeyHistory.ValidAlways([Pub(Zero)])));
        Assert.Throws<StrictJsonException>(() => OfflineVerifier.RevocationFeedSigner("{", [Pub(Zero)]));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.RevocationFeedSigner("{}", (NAKeyHistory)null!));
    }
}
