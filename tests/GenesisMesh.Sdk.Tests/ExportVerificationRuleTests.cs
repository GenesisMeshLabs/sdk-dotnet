using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Each rule of export verification (1.9.0) that a wrong implementation would otherwise pass: the reference export with
/// its records signed again by test keys, then changed one rule at a time. Each case gives what the TypeScript SDK and
/// the Python reference give.
/// </summary>
public class ExportVerificationRuleTests
{
    private static Key NewKey() =>
        Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static readonly Key NA0 = NewKey();
    private static readonly Key NA1 = NewKey();
    private static readonly Key Executor = NewKey();

    private static string Pub(Key key) => Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static readonly Lazy<JsonObject> Reference = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    // The reference export: D1 J1 E1 (resource sequence 1), D2 J2 E2 (sequence 2), D3 (denied) J3.
    private const int D1 = 0, J1 = 1, E1 = 2, D2 = 3, E2 = 5;

    private static readonly ExecutorKeyRecord ExecutorKey = new()
    {
        KeyId = "ex",
        PublicKey = Pub(Executor),
        ExecutorSovereignId = "secrets-controller",
    };

    /// <summary>A record's <c>signature</c>, by <paramref name="key"/> over its signed form.</summary>
    private static void Sign(JsonObject record, string model, Key key, string keyId)
    {
        record.Remove("signature");
        var body = OfflineVerifier.ReceivedCanonical(model, JsonSerializer.SerializeToElement(record));
        record["signature"] = new JsonObject
        {
            ["key_id"] = keyId,
            ["sig"] = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(body))),
        };
    }

    /// <summary>The event's record changed by <paramref name="change"/>, then signed again: by <paramref name="na"/> for the NA's records.</summary>
    private static void Resign(EvidenceEvent e, Action<JsonObject>? change = null, Key? na = null, bool sign = true)
    {
        var payload = JsonNode.Parse(e.Payload.GetRawText())!.AsObject();
        var record = e.Entry.EntryKind == "decision" ? payload["decision"]!.AsObject() : payload;
        change?.Invoke(record);
        if (sign)
        {
            switch (e.Entry.EntryKind)
            {
                case "decision": Sign(record, "BoundaryDecision", na ?? NA0, "na"); break;
                case "justification": Sign(record, "JustificationProof", na ?? NA0, "na"); break;
                case "execution": Sign(record, "ExecutionEvidence", Executor, "ex"); break;
                case "retention_checkpoint": Sign(record, "RetentionCheckpoint", na ?? NA0, "na"); break;
            }
        }
        e.Payload = JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>The reference export, signed again by NA0 and the executor key.</summary>
    private static List<EvidenceEvent> Store()
    {
        var events = OfflineVerifier.ParseExportLines((string)Reference.Value["export"]!).ToList();
        foreach (var e in events) Resign(e);
        return Relink(events);
    }

    /// <summary>Positions from <paramref name="start"/>, each envelope's digests, and its link to the one before (the first to <paramref name="first"/>).</summary>
    private static List<EvidenceEvent> Relink(List<EvidenceEvent> events, long? start = null, string? first = null)
    {
        var previous = first;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (start is { } s) e.Entry.StoreSequence = s + i;
            e.Entry.PrevEntryDigest = previous;
            e.Entry.PayloadDigest = OfflineVerifier.PayloadDigest(e.Payload);
            e.EntryDigest = OfflineVerifier.EntryDigest(e.Entry);
            previous = e.EntryDigest;
        }
        return events;
    }

    private static EvidenceVerifyOptions Options(bool contiguous = true, string? checkpoint = null, NAKeyHistory? history = null,
        IReadOnlyList<ExecutorKeyRecord>? keys = null) => new()
        {
            NAPublicKeys = history is null ? [Pub(NA0)] : [],
            KeyHistory = history,
            ExecutorKeys = keys ?? [ExecutorKey],
            Contiguous = contiguous,
            Checkpoint = checkpoint,
        };

    private static List<(long?, string, string)> Failures(EvidenceStoreVerification v) =>
        v.Failures.Select(f => (f.StoreSequence, f.Reason, f.Detail)).ToList();

    private static DateTimeOffset At(EvidenceEvent e) => OfflineVerifier.ParseTimestamp(e.Entry.RecordedAt);

    private static string ExecutionDigest(EvidenceEvent e) => OfflineVerifier.ExecutionDigest(e.Payload.GetRawText());

    private static string Resource(EvidenceEvent e) => e.Payload.GetProperty("resource_id").GetString()!;

    /// <summary>A copy of an event, to store again elsewhere.</summary>
    private static EvidenceEvent Copy(EvidenceEvent e) => JsonSerializer.Deserialize<EvidenceEvent>(JsonSerializer.Serialize(e))!;

    private static JsonObject Checkpoint(long through, string last, JsonObject? heads = null, long? count = null, string? previous = null,
        string id = "cp", Key? key = null)
    {
        var checkpoint = new JsonObject
        {
            ["checkpoint_id"] = id,
            ["created_at"] = "2026-10-01T09:56:14.643875Z",
            ["cutoff"] = "2026-10-01T09:56:14.350335Z",
            ["removed_through_sequence"] = through,
            ["last_removed_entry_digest"] = last,
            ["removed_count"] = count ?? through,
            ["resource_heads"] = heads ?? new JsonObject(),
            ["previous_checkpoint_id"] = previous,
            ["issued_by"] = "na",
        };
        Sign(checkpoint, "RetentionCheckpoint", key ?? NA0, "na");
        return checkpoint;
    }

    private static JsonObject Head(long sequence, string digest) => new() { ["resource_sequence"] = sequence, ["record_digest"] = digest };

    private static EvidenceEvent Entry(string kind, JsonNode payload, string recordedAt) => new()
    {
        Schema = "gm.evidence.event",
        SchemaVersion = 1,
        Entry = new EvidenceStoreEntry { EntryKind = kind, RecordedAt = recordedAt },
        Payload = JsonSerializer.SerializeToElement(payload),
    };

    [Fact]
    public void TheStoreSignedAgain_Verifies() =>
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Store(), Options()).Verified);

    // ── The NA's key history ────────────────────────────────────────────────────

    [Fact]
    public void AnNAKey_MustAlsoBeValidAtTheLatestEarlierTime()
    {
        // NA0 hands over to NA1 at R; a proof recorded 4 minutes after R, signed by NA0 within the hand-over, follows a
        // decision recorded 5 minutes after it, when NA0 no longer signs.
        var events = Store().Take(2).ToList();
        var r = At(events[D1]);
        events[D1].Entry.RecordedAt = Canonical.Timestamp(r.AddMinutes(5));
        events[J1].Entry.RecordedAt = Canonical.Timestamp(r.AddMinutes(4));
        Resign(events[D1], na: NA1);
        var history = new NAKeyHistory("na", null,
            [new NAKey(1, "na0", Pub(NA0), null, r, null, false), new NAKey(2, "na1", Pub(NA1), r, null, null, false)]);
        Assert.Equal([(2L, "na_key_not_valid_at_time", "justification")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options(history: history))));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ADecision_MayNotOutliveItsKey(int beyondSeconds, bool outlives)
    {
        // NA0 ends at S: a decision it signs may stay valid for the hand-over and the longest decision lifetime after.
        var events = Store().Take(1).ToList();
        var end = At(events[D1]).AddMinutes(1);
        Resign(events[D1], d => d["decision_valid_until"] = Canonical.Timestamp(
            end + NAKeyHistory.SuccessionHandover + NAKeyHistory.MaxDecisionLifetime + TimeSpan.FromSeconds(beyondSeconds)));
        var history = new NAKeyHistory("na", null,
            [new NAKey(1, "na0", Pub(NA0), null, end, null, false), new NAKey(2, "na1", Pub(NA1), end, null, null, false)]);
        var failures = Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options(history: history)));
        Assert.Equal(outlives, failures.Any(f => f.Item2 == "decision_outlives_key"));
    }

    [Fact]
    public void ADecisionSignedByAKeyNotValidThen_IsNotJudgedAsOutlivingIt()
    {
        // Only a signature by a key valid at the time names the key a decision may not outlive.
        var events = Store().Take(1).ToList();
        var end = At(events[D1]).AddMinutes(-10);
        Resign(events[D1], d => d["decision_valid_until"] = Canonical.Timestamp(At(events[D1]).AddHours(1)));
        var history = new NAKeyHistory("na", null,
            [new NAKey(1, "na0", Pub(NA0), null, end, null, false), new NAKey(2, "na1", Pub(NA1), end, null, null, false)]);
        Assert.Equal([(1L, "na_key_not_valid_at_time", "decision")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options(history: history))));
    }

    [Fact]
    public void ALateSignatureOverAFormTheReferenceDoesNotWrite_IsNonCanonical()
    {
        var events = Store().Take(2).ToList();
        var r = At(events[D1]);
        Resign(events[D1], na: NA1);
        Resign(events[J1], p => p["proof_issued_at"] = ((string)p["proof_issued_at"]!).Replace("Z", "+00:00"));
        var history = new NAKeyHistory("na", null,
        [
            new NAKey(1, "na0", Pub(NA0), null, r.AddHours(-1), null, false),
            new NAKey(2, "na1", Pub(NA1), r.AddHours(-1), null, null, false),
        ]);
        Assert.Equal([(2L, "non_canonical_form", "justification")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options(history: history))));
    }

    [Fact]
    public void ACheckpointOption_IsCheckedUnderTheKeyValidWhenItWasCreated()
    {
        var events = Store();
        var checkpoint = Checkpoint(3, events[E1].EntryDigest).ToJsonString();
        var rest = events.Skip(3);
        var created = OfflineVerifier.ParseTimestamp("2026-10-01T09:56:14.643875Z");
        var later = new NAKeyHistory("na", null, [new NAKey(1, "na0", Pub(NA0), created.AddDays(1), null, null, false)]);
        Assert.Equal([((long?)null, "invalid_signature", "retention_checkpoint")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(rest, Options(checkpoint: checkpoint, history: later))));
        Assert.DoesNotContain(Failures(OfflineVerifier.VerifyEvidenceEvents(rest,
            Options(checkpoint: checkpoint, history: NAKeyHistory.ValidAlways([Pub(NA0)])))), f => f.Item2 == "invalid_signature");
    }

    // ── Executor keys ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("observer", "secrets-controller")]
    [InlineData(null, "another-controller")]
    public void OnlyTheExecutorsOwnExecutorKey_SignsItsRecords(string? role, string sovereign)
    {
        var key = new ExecutorKeyRecord { KeyId = "ex", PublicKey = Pub(Executor), ExecutorSovereignId = sovereign, Role = role };
        Assert.Equal([(3L, "invalid_signature", "execution"), (6L, "invalid_signature", "execution")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Store(), Options(keys: [key]))));
    }

    // ── Where each chain starts ─────────────────────────────────────────────────

    [Fact]
    public void ARunFromALaterPosition_TakesEachResourcesFirstRecordAsItsStart() =>
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Store().Skip(3), Options()).Verified);

    [Fact]
    public void ARunFromEntry1_ChecksEachChainFromItsFirstRecord()
    {
        var events = Store();
        Resign(events[E1], e => e["resource_sequence"] = 2);
        Assert.Contains((3L, "resource_chain_break", Resource(events[E1])),
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
    }

    [Fact]
    public void ARunRightAfterTheCheckpoint_ContinuesFromItsHeads()
    {
        var events = Store();
        var resource = Resource(events[E1]);
        var rest = events.Skip(3).ToList();
        var withHead = Checkpoint(3, events[E1].EntryDigest, new JsonObject { [resource] = Head(1, ExecutionDigest(events[E1])) });
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(rest, Options(checkpoint: withHead.ToJsonString())).Verified);
        // Without the resource's head, the run starts the store again, and the resource's chain with it.
        var withoutHead = Checkpoint(3, events[E1].EntryDigest);
        Assert.Equal([(6L, "resource_chain_break", resource)],
            Failures(OfflineVerifier.VerifyEvidenceEvents(rest, Options(checkpoint: withoutHead.ToJsonString()))));
        // A head with another digest breaks the chain.
        var wrongHead = Checkpoint(3, events[E1].EntryDigest, new JsonObject { [resource] = Head(1, new string('1', 64)) });
        Assert.Equal([(6L, "resource_chain_break", resource)],
            Failures(OfflineVerifier.VerifyEvidenceEvents(rest, Options(checkpoint: wrongHead.ToJsonString()))));
        // The run's first entry continues from the last entry the checkpoint removed.
        var otherLast = Checkpoint(3, new string('0', 64), new JsonObject { [resource] = Head(1, ExecutionDigest(events[E1])) });
        Assert.Equal([(4L, "store_chain_break", "does not continue from the checkpoint")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(rest, Options(checkpoint: otherLast.ToJsonString()))));
    }

    [Fact]
    public void ACheckpointEntry_SeedsTheHeadsOfTheChainsItRemovedRecordsFrom()
    {
        var events = Store();
        var resource = Resource(events[E1]);
        List<EvidenceEvent> Run(JsonObject checkpoint) =>
            Relink([Entry("retention_checkpoint", checkpoint, events[E2].Entry.RecordedAt), Copy(events[E2])], start: 30, first: new string('x', 64));
        var seeded = Checkpoint(3, "anchor", new JsonObject { [resource] = Head(1, ExecutionDigest(events[E1])) });
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Run(seeded), Options()).Verified);
        var wrong = Checkpoint(3, "anchor", new JsonObject { [resource] = Head(1, new string('1', 64)) });
        Assert.Equal([(31L, "resource_chain_break", resource)], Failures(OfflineVerifier.VerifyEvidenceEvents(Run(wrong), Options())));
        // An unsigned checkpoint seeds nothing: the run, not from the start, takes the record as the chain's start.
        wrong["signature"] = null;
        Assert.Equal([(30L, "invalid_signature", "retention_checkpoint")], Failures(OfflineVerifier.VerifyEvidenceEvents(Run(wrong), Options())));
    }

    [Fact]
    public void ACheckpointEntry_DoesNotMoveAHeadTheRunHoldsFurther()
    {
        var events = Store();
        var resource = Resource(events[E1]);
        // E2 (sequence 2), a checkpoint naming sequence 1, then a third record continuing from E2.
        var third = Copy(events[E2]);
        Resign(third, e =>
        {
            e["evidence_id"] = "00000000-0000-4000-8000-0000000000e3";
            e["sequence_no"] = 2;
            e["prev_evidence_digest"] = ExecutionDigest(events[E2]);
            e["resource_sequence"] = 3;
            e["prev_resource_digest"] = ExecutionDigest(events[E2]);
        });
        foreach (var held in new[] { 1L, 2L })
        {
            var checkpoint = Checkpoint(3, "anchor", new JsonObject { [resource] = Head(held, ExecutionDigest(events[E1])) });
            var run = Relink([Copy(events[E2]), Entry("retention_checkpoint", checkpoint, events[E2].Entry.RecordedAt), third],
                start: 30, first: new string('x', 64));
            Assert.True(OfflineVerifier.VerifyEvidenceEvents(run, Options()).Verified, string.Join("; ", Failures(OfflineVerifier.VerifyEvidenceEvents(run, Options()))));
        }
    }

    [Fact]
    public void RecordsUnderOneDecision_ChainInOrder()
    {
        var events = Store();
        var second = Copy(events[E1]);
        Resign(second, e =>
        {
            e["evidence_id"] = "00000000-0000-4000-8000-0000000000e2";
            e["sequence_no"] = 2;
            e["prev_evidence_digest"] = ExecutionDigest(events[E1]);
            foreach (var field in new[] { "resource_id", "resource_action", "resource_sequence", "prev_resource_digest" }) e.Remove(field);
        });
        var run = events.Take(3).Append(second).ToList();
        var chained = OfflineVerifier.VerifyEvidenceEvents(Relink(run, start: 1), Options());
        Assert.True(chained.Verified, string.Join("; ", Failures(chained)));
        Resign(second, e => e["prev_evidence_digest"] = new string('0', 64));
        Assert.Equal([(4L, "evidence_chain_break", "")], Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(run, start: 1), Options())));
    }

    // ── Record times ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AnEntryMayBeRecordedUpToTheToleranceBeforeAnEarlierOne(int beyondMicros, bool outOfOrder)
    {
        var events = Store();
        var latest = events.SkipLast(1).Max(At);
        events[^1].Entry.RecordedAt = Canonical.Timestamp(latest.AddMinutes(-5).AddTicks(-10 * beyondMicros));
        var failures = Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options()));
        Assert.Equal(outOfOrder, failures.Any(f => f.Item2 == "recorded_at_out_of_order"));
    }

    [Fact]
    public void EntriesRetentionCarriedForward_KeepTheirTime_AndDoNotMoveTheClock()
    {
        var events = Store();
        var now = events[D1].Entry.RecordedAt;
        var dayBefore = Canonical.Timestamp(At(events[D1]).AddDays(-1));
        var checkpoint = Checkpoint(3, "anchor");
        List<string> OutOfOrder(params EvidenceEvent[] run) =>
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink([.. run], start: 10, first: new string('x', 64)), Options()))
                .Where(f => f.Item2 == "recorded_at_out_of_order").Select(f => f.Item1!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        EvidenceEvent Registry(string at) => Entry("registry", new JsonObject(), at);
        EvidenceEvent Decision(string at)
        {
            var d = Copy(events[D1]);
            d.Entry.RecordedAt = at;
            return d;
        }
        // A registry record right after the checkpoint, recorded a day before: carried, not out of order.
        Assert.Empty(OutOfOrder(Entry("retention_checkpoint", checkpoint, now), Registry(dayBefore), Decision(now)));
        // Not after a checkpoint, it is.
        Assert.Equal(["11"], OutOfOrder(Decision(now), Registry(dayBefore)));
        // A carried block does not set the clock back: an old entry after it is out of order.
        Assert.Equal(["12"], OutOfOrder(Entry("retention_checkpoint", checkpoint, now), Registry(dayBefore), Decision(dayBefore)));
    }

    [Fact]
    public void TimesOutsideTheRangeOfDateTimeOffset_AreCompared_NotThrown()
    {
        var events = Store().Take(3).ToList();
        events[D1].Entry.RecordedAt = "0001-01-01T00:00:00Z";
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options()).Verified);
        // Before the year 1 in UTC.
        events[D1].Entry.RecordedAt = events[J1].Entry.RecordedAt;
        events[J1].Entry.RecordedAt = "0001-01-01T00:00:00+23:00";
        Assert.Contains((2L, "recorded_at_out_of_order", "recorded at 0001-01-01T00:00:00Z, after an entry recorded at "
            + Canonical.Timestamp(At(events[D1]))), Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
        // After 9999 in UTC: the record is outside its decision's window.
        events = Store().Take(3).ToList();
        Resign(events[D1], d =>
        {
            d["decision_made_at"] = "9999-12-31T22:00:00-23:00";
            d["decision_valid_until"] = "9999-12-31T23:00:00-23:00";
        });
        Resign(events[E1], e => e["executed_at"] = "9999-12-31T23:30:00-23:00");
        Assert.Contains((3L, "evidence_outside_decision_window", ""), Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ARecordMayBeMadeUntilItsDecisionExpires(int beyondMicros, bool outside)
    {
        var events = Store().Take(3).ToList();
        var until = events[D1].Payload.GetProperty("decision").GetProperty("decision_valid_until").GetString()!;
        Resign(events[E1], e => e["executed_at"] = Canonical.Timestamp(OfflineVerifier.ParseTimestamp(until).AddTicks(10 * beyondMicros)));
        var failures = Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options()));
        Assert.Equal(outside, failures.Contains((3L, "evidence_outside_decision_window", "")));
    }

    // ── Retention counts ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a", 4, false)]
    [InlineData("a", 7, true)]
    [InlineData("", 7, false)]
    public void ACheckpointRemovesAsManyEntriesAsPositionsAfterTheOneBefore(string id, long count, bool mismatch)
    {
        // An empty ID names no checkpoint, so the count is not checked, as in TypeScript.
        var first = Checkpoint(3, "anchor", id: id);
        var second = Checkpoint(7, "anchor2", count: count, previous: id, id: "b");
        var run = Relink([Entry("retention_checkpoint", first, "2026-10-01T09:56:14Z"), Entry("retention_checkpoint", second, "2026-10-01T09:56:15Z")],
            start: 10, first: new string('x', 64));
        var failures = Failures(OfflineVerifier.VerifyEvidenceEvents(run, Options()));
        var expected = new List<(long?, string, string)>();
        if (mismatch) expected.Add((11, "retention_count_mismatch", $"removed {count} of 4 entries after #3 through #7"));
        Assert.Equal(expected, failures);
    }

    // ── Unknown fields ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(D1, "decision.x")]
    [InlineData(J1, "x")]
    [InlineData(E1, "x")]
    public void ASignedUnknownField_IsRefusedByName(int index, string path)
    {
        var events = Store();
        Resign(events[index], r => r["x"] = 1);
        Assert.Contains(((long?)(index + 1), "unknown_field", path), Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
    }

    [Fact]
    public void ASignedUnknownFieldInACheckpointEntry_IsRefusedByName()
    {
        var checkpoint = Checkpoint(3, "anchor");
        checkpoint["x"] = 1;
        Sign(checkpoint, "RetentionCheckpoint", NA0, "na");
        var run = Relink([Entry("retention_checkpoint", checkpoint, "2026-10-01T09:56:14Z")], start: 10, first: new string('x', 64));
        Assert.Equal([(10L, "unknown_field", "x")], Failures(OfflineVerifier.VerifyEvidenceEvents(run, Options())));
    }

    [Fact]
    public void AnUnsignedUnknownFieldInADecision_IsSetAsideBeforeItsSignatureIsChecked()
    {
        var events = Store();
        Resign(events[D1], d => d["x"] = 1, sign: false);
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options());
        Assert.True(result.Verified, string.Join("; ", Failures(result)));
        Assert.Equal((1L, "unsigned_field", "decision.x"),
            (Assert.Single(result.Warnings!).StoreSequence!.Value, result.Warnings![0].Reason, result.Warnings[0].Detail));
    }

    [Fact]
    public void ACheckpointOptionsHeads_HoldExactlyTheirFields()
    {
        var events = Store();
        var head = Head(1, ExecutionDigest(events[E1]));
        head["x"] = 1;
        var checkpoint = Checkpoint(3, events[E1].EntryDigest, new JsonObject { [Resource(events[E1])] = head });
        Assert.Equal([((long?)null, "invalid_signature", "retention_checkpoint")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(events.Skip(3), Options(checkpoint: checkpoint.ToJsonString()))));
    }

    // ── Input that is no export ─────────────────────────────────────────────────

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"x\"")]
    [InlineData("true")]
    public void ACheckpointOptionThatIsNoObject_IsNotTheNAs(string json) =>
        Assert.Equal([((long?)null, "invalid_signature", "retention_checkpoint")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Store(), Options(checkpoint: json))));

    [Theory]
    [InlineData("""{"a":1e400}""")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"decision":{"x":1,"x":2}}""")]
    [InlineData("""{"a":"\ud800"}""")]
    public void AnEventBuiltByHandFromJsonThatIsNotStrict_IsNoRecord(string payload)
    {
        var e = Store()[D1];
        using var doc = JsonDocument.Parse(payload);
        e.Payload = doc.RootElement.Clone();
        Assert.Equal([((long?)null, "payload_invalid", "event envelope")], Failures(OfflineVerifier.VerifyEvidenceEvents([e], Options())));
    }

    [Fact]
    public void OptionsWithoutKeys_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyEvidenceEvents([], new EvidenceVerifyOptions { NAPublicKeys = null! }));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyEvidenceEvents([], new EvidenceVerifyOptions { ExecutorKeys = null! }));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyEvidenceEvents([], new EvidenceVerifyOptions { ExecutorKeys = [null!] }));
    }
}
