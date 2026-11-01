using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Export verification of the records of changes outside the governed path (1.9.0), rule by rule where the shared
/// <c>out_of_band</c> vectors do not reach: each envelope field, the keys that may sign each kind, the judgement rules,
/// observation positions (from a retention checkpoint too), fields this SDK does not know, and the NA key valid when a
/// record was stored. Records are signed by test keys; each case gives what the TypeScript SDK gives, failures in its
/// order and with its details.
/// </summary>
public class OutOfBandExportTests
{
    private static Key NewKey() =>
        Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static readonly Key NA0 = NewKey();
    private static readonly Key NA1 = NewKey();
    private static readonly Key Observer = NewKey();
    private static readonly Key OtherObserver = NewKey();
    private static readonly Key Executor = NewKey();
    private static readonly Key OtherExecutor = NewKey();

    private static string Pub(Key key) => Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static readonly ExecutorKeyRecord ObserverKey = new()
    {
        KeyId = "obs",
        PublicKey = Pub(Observer),
        ExecutorSovereignId = "sovereign-a",
        Role = "observer",
    };

    /// <summary>An observer key of another sovereign, and an executor key of the observer's sovereign.</summary>
    private static readonly ExecutorKeyRecord OtherObserverKey = new()
    {
        KeyId = "obs-x",
        PublicKey = Pub(OtherObserver),
        ExecutorSovereignId = "sovereign-x",
        Role = "observer",
    };

    private static readonly ExecutorKeyRecord ExecutorKey = new()
    {
        KeyId = "ex",
        PublicKey = Pub(Executor),
        ExecutorSovereignId = "sovereign-a",
    };

    private static readonly ExecutorKeyRecord OtherExecutorKey = new()
    {
        KeyId = "ex-x",
        PublicKey = Pub(OtherExecutor),
        ExecutorSovereignId = "sovereign-x",
    };

    private const string Resource = "kv:app-1/secret";

    /// <summary>
    /// The envelope fields the NA derives from each kind's record, as the TypeScript SDK lists them
    /// (<c>ENVELOPE_FIELDS</c>): envelope field, record field.
    /// </summary>
    private static readonly Dictionary<string, (string Envelope, string Record)[]> Derived = new()
    {
        ["observation"] =
        [
            ("record_id", "observation_id"), ("resource_id", "resource_id"), ("resource_action", "action"),
            ("capability", "capability"), ("executor_sovereign_id", "observer_sovereign_id"),
        ],
        ["break_glass"] =
        [
            ("record_id", "break_glass_id"), ("resource_id", "resource_id"), ("resource_action", "resource_action"),
            ("capability", "capability"), ("executor_sovereign_id", "executor_sovereign_id"), ("attestation_id", "attestation_id"),
            ("outcome", "outcome"),
        ],
        ["judgement"] =
        [
            ("record_id", "judgement_id"), ("subject_id", "subject_id"), ("resource_id", "resource_id"), ("resource_action", "action"),
            ("capability", "capability"), ("outcome", "verdict"), ("matched_evidence_id", "matched_evidence_id"),
        ],
        ["quarantine"] = [("record_id", "quarantine_id"), ("resource_id", "resource_id"), ("outcome", "rejection_code")],
        ["registry"] = [("record_id", "registry_record_id"), ("outcome", "event")],
    };

    private static string Signature(string body, Key key) =>
        Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(body)));

    /// <summary>A record of a change outside the governed path, signed over its signed form.</summary>
    private static JsonObject Signed(JsonObject record, Key key, string keyId)
    {
        record.Remove("signature");
        record["signature"] = new JsonObject { ["key_id"] = keyId, ["sig"] = Signature(OfflineVerifier.OutOfBandCanonical(record.ToJsonString()), key) };
        return record;
    }

    private static JsonObject Observation(string id) => Signed(new JsonObject
    {
        ["observation_id"] = id,
        ["observer_sovereign_id"] = "sovereign-a",
        ["resource_id"] = Resource,
        ["action"] = "rotate",
        ["capability"] = "kv.rotate",
        ["changed_at"] = "2026-10-01T10:00:00Z",
        ["observed_at"] = "2026-10-01T10:00:01Z",
        ["source"] = "activity-log",
        ["source_event_id"] = id,
        ["metadata"] = new JsonObject(),
    }, Observer, "obs");

    private static JsonObject Observation(string id, Key key, string keyId)
    {
        var record = Observation(id);
        return Signed(record, key, keyId);
    }

    /// <summary>The NA's judgement of an observation, naming it by digest and store position, and the record it matched.</summary>
    private static JsonObject Judgement(string id, string subjectId, string subjectDigest, long subjectSequence, string? matched = null,
        Key? na = null) => Signed(new JsonObject
        {
            ["judgement_id"] = id,
            ["subject_kind"] = "observation",
            ["subject_id"] = subjectId,
            ["subject_digest"] = subjectDigest,
            ["subject_store_sequence"] = subjectSequence,
            ["resource_id"] = Resource,
            ["action"] = "rotate",
            ["capability"] = "kv.rotate",
            ["governed_by"] = "after_the_fact",
            ["verdict"] = "allow",
            ["evaluated_as_of"] = "2026-10-01T10:00:00Z",
            ["gate_results"] = new JsonArray(),
            ["judged_at"] = "2026-10-01T10:00:03Z",
            ["matched_evidence_id"] = matched,
            ["issuer_sovereign_id"] = "sovereign-c",
            ["issued_by"] = "na",
        }, na ?? NA0, "na");

    /// <summary>A break-glass record of the observer's sovereign, signed by the executor key unless another is given.</summary>
    private static JsonObject BreakGlass(string id, string? attestation = "att-1", Key? key = null, string keyId = "ex") => Signed(new JsonObject
    {
        ["break_glass_id"] = id,
        ["executor_sovereign_id"] = "sovereign-a",
        ["resource_id"] = Resource,
        ["resource_action"] = "rotate",
        ["capability"] = "kv.rotate",
        ["attestation_id"] = attestation,
        ["request_parameters"] = new JsonObject(),
        ["attributes"] = new JsonObject(),
        ["justification"] = "the NA is unreachable",
        ["evaluation_request_digest"] = new string('8', 64),
        ["evaluation_failure"] = "timeout",
        ["executed_at"] = "2026-10-01T10:00:01Z",
        ["outcome"] = "success",
        ["execution_parameters"] = new JsonObject(),
    }, key ?? Executor, keyId);

    /// <summary>The NA's quarantine entry for a refused observation.</summary>
    private static JsonObject Quarantine(string id, JsonObject refused, Key? na = null) => Signed(new JsonObject
    {
        ["quarantine_id"] = id,
        ["record_kind"] = "observation",
        ["record"] = refused,
        ["record_digest"] = DigestOf(refused),
        ["rejection_code"] = "observation_out_of_scope",
        ["detail"] = "outside the key's prefix",
        ["resource_id"] = Resource,
        ["quarantined_at"] = "2026-10-01T10:00:03Z",
        ["issuer_sovereign_id"] = "sovereign-c",
        ["issued_by"] = "na",
    }, na ?? NA0, "na");

    /// <summary>
    /// The reference export's first decision, signed again by NA0, with its context: the payload, its ID and the
    /// capability it allows until it expires.
    /// </summary>
    private static (JsonObject Payload, string Id, string Capability) Decision()
    {
        var export = (string)JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!["export"]!;
        var payload = JsonNode.Parse(export.Split('\n')[0])!["payload"]!.AsObject().DeepClone().AsObject();
        var decision = payload["decision"]!.AsObject();
        decision.Remove("signature");
        var body = OfflineVerifier.ReceivedCanonical("BoundaryDecision", JsonSerializer.SerializeToElement(decision));
        decision["signature"] = new JsonObject { ["key_id"] = "na", ["sig"] = Signature(body, NA0) };
        return (payload, (string)decision["decision_id"]!, (string)payload["context"]!["requested_capability"]!);
    }

    /// <summary>Execution evidence resting on <paramref name="decisionId"/>, signed by the executor key.</summary>
    private static JsonObject Execution(string decisionId, string capability = "kv.rotate", string executedAt = "2026-10-01T10:00:01Z")
    {
        var record = new JsonObject
        {
            ["agreement_id"] = "6ce38f75-23a8-4da8-901b-762132db5951",
            ["context_id"] = "2b8591fd-8584-4d0e-a899-42fc79436acc",
            ["decision_id"] = decisionId,
            ["evidence_id"] = "7ecd4840-b991-4790-862e-c5bbe0da4503",
            ["executed_at"] = executedAt,
            ["executed_capability"] = capability,
            ["execution_parameters"] = new JsonObject { ["secret_version"] = "v1" },
            ["executor_sovereign_id"] = "sovereign-a",
            ["outcome"] = "success",
            ["outcome_detail"] = null,
            ["prev_evidence_digest"] = null,
            ["sequence_no"] = 1,
        };
        var body = OfflineVerifier.ReceivedCanonical("ExecutionEvidence", JsonSerializer.SerializeToElement(record));
        record["signature"] = new JsonObject { ["key_id"] = "ex", ["sig"] = Signature(body, Executor) };
        return record;
    }

    private static JsonObject Registry(string id, Key na, Action<JsonObject>? before = null)
    {
        var record = new JsonObject
        {
            ["registry_record_id"] = id,
            ["event"] = "policy_history_started",
            ["effective_at"] = "2026-10-01T09:00:00Z",
            ["issuer_sovereign_id"] = "sovereign-c",
            ["issued_by"] = "na",
        };
        before?.Invoke(record);
        return Signed(record, na, "na");
    }

    /// <summary>A retention checkpoint the NA signed, removing entries 1 to <paramref name="through"/>.</summary>
    private static JsonObject Checkpoint(long through, JsonObject? observationHeads, Key? key = null)
    {
        var checkpoint = new JsonObject
        {
            ["checkpoint_id"] = "cp",
            ["created_at"] = "2026-10-01T09:30:00Z",
            ["cutoff"] = "2026-10-01T09:29:00Z",
            ["removed_through_sequence"] = through,
            ["last_removed_entry_digest"] = new string('a', 64),
            ["removed_count"] = through,
            ["resource_heads"] = new JsonObject(),
            ["previous_checkpoint_id"] = null,
            ["issued_by"] = "na",
        };
        if (observationHeads is not null) checkpoint["observation_heads"] = observationHeads;
        var body = OfflineVerifier.ReceivedCanonical("RetentionCheckpoint", JsonSerializer.SerializeToElement(checkpoint));
        checkpoint["signature"] = new JsonObject { ["key_id"] = "na", ["sig"] = Signature(body, key ?? NA0) };
        return checkpoint;
    }

    /// <summary>An entry of <paramref name="kind"/>, with the envelope fields the NA derives from its record.</summary>
    private static EvidenceEvent Entry(string kind, JsonObject payload, string recordedAt = "2026-10-01T10:00:02Z", long? position = null)
    {
        var entry = new JsonObject { ["entry_kind"] = kind, ["recorded_at"] = recordedAt, ["store_sequence"] = 1, ["payload_digest"] = "" };
        if (Derived.TryGetValue(kind, out var fields))
        {
            foreach (var (envelope, field) in fields)
                entry[envelope] = payload[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        if (kind == "observation") entry["observation_sequence"] = position;
        return new EvidenceEvent
        {
            Schema = "gm.evidence.event",
            SchemaVersion = 1,
            Entry = entry.Deserialize<EvidenceStoreEntry>()!,
            Payload = JsonSerializer.SerializeToElement(payload),
        };
    }

    /// <summary>Positions from <paramref name="start"/>, each envelope's digests, and its link to the one before (the first to <paramref name="first"/>).</summary>
    private static List<EvidenceEvent> Relink(long start, string? first, params EvidenceEvent[] events)
    {
        var previous = first;
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            e.Entry.StoreSequence = start + i;
            e.Entry.PrevEntryDigest = previous;
            e.Entry.PayloadDigest = OfflineVerifier.PayloadDigest(e.Payload);
            e.EntryDigest = OfflineVerifier.EntryDigest(e.Entry);
            previous = e.EntryDigest;
        }
        return [.. events];
    }

    private static EvidenceVerifyOptions Options(string? checkpoint = null, NAKeyHistory? history = null, bool contiguous = true) => new()
    {
        NAPublicKeys = history is null ? [Pub(NA0)] : [],
        KeyHistory = history,
        ExecutorKeys = [ObserverKey, OtherObserverKey, ExecutorKey, OtherExecutorKey],
        Checkpoint = checkpoint,
        Contiguous = contiguous,
    };

    private static string DigestOf(JsonObject record) => OfflineVerifier.PayloadDigest(JsonSerializer.SerializeToElement(record));

    private static List<(long?, string, string)> Failures(EvidenceStoreVerification v) =>
        v.Failures.Select(f => (f.StoreSequence, f.Reason, f.Detail)).ToList();

    [Fact]
    public void AnObservationVerifies() =>
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Relink(1, null, Entry("observation", Observation("o1"), position: 1)), Options()).Verified);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AnObservationContinuesThePositionsOfTheCheckpointBeforeIt(bool passedIn, bool withHead)
    {
        // Positions 1 and 2 of the resource were removed by retention: the next observation is position 3.
        var heads = withHead ? new JsonObject { [Resource] = 2 } : null;
        var observation = Entry("observation", Observation("o3"), position: 3);
        var r = passedIn
            ? OfflineVerifier.VerifyEvidenceEvents(Relink(6, new string('a', 64), observation),
                Options(checkpoint: Checkpoint(5, heads).ToJsonString()))
            : OfflineVerifier.VerifyEvidenceEvents(
                Relink(1, null, Entry("retention_checkpoint", Checkpoint(0, heads), "2026-10-01T09:30:00Z"), observation), Options());
        List<(long?, string, string)> expected = withHead ? [] : [(passedIn ? 6L : 2L, "observation_chain_break", Resource)];
        Assert.Equal(expected, Failures(r));
    }

    [Fact]
    public void ACheckpointTheNADidNotSign_SeedsNoObservationPosition()
    {
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("retention_checkpoint", Checkpoint(0, new JsonObject { [Resource] = 2 }, NA1), "2026-10-01T09:30:00Z"),
            Entry("observation", Observation("o3"), position: 3)), Options());
        Assert.Equal([(1L, "invalid_signature", "retention_checkpoint"), (2L, "observation_chain_break", Resource)], Failures(r));
    }

    [Fact]
    public void ACheckpointMovesNoObservationPositionBack()
    {
        // Position 4 held, then a checkpoint naming 2 for the resource: the next is still 5.
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("observation", Observation("o4"), position: 4),
            Entry("retention_checkpoint", Checkpoint(0, new JsonObject { [Resource] = 2 }), "2026-10-01T10:00:02Z"),
            Entry("observation", Observation("o5"), position: 5)), Options());
        // Entry 1 is position 4 of a run from entry 1, where the resource starts at 1.
        Assert.Equal([(1L, "observation_chain_break", Resource)], Failures(r));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void APositionOf0PassedIn_IsAHeadToo(bool withHead, bool verified)
    {
        // A run that starts after the entry following the checkpoint does not hold its chains from their start: only a
        // head the checkpoint names is checked, 0 included.
        var heads = withHead ? new JsonObject { [Resource] = 0 } : null;
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(9, new string('b', 64), Entry("observation", Observation("o2"), position: 2)),
            Options(checkpoint: Checkpoint(5, heads).ToJsonString()));
        Assert.Equal(verified, r.Verified);
        if (!verified) Assert.Equal([(9L, "observation_chain_break", Resource)], Failures(r));
    }

    [Theory]
    [InlineData("registry", true)]
    [InlineData("registry", false)]
    [InlineData("observation", true)]
    [InlineData("observation", false)]
    public void AFieldThisSdkDoesNotKnow_IsRefusedWhenSigned_AndMakesTheRecordInvalidWhenNot(string kind, bool covered)
    {
        JsonObject record;
        if (kind == "registry")
        {
            record = Registry("r1", NA0, covered ? r => r["later_field"] = "x" : null);
        }
        else
        {
            record = Observation("o1");
            if (covered)
            {
                record["later_field"] = "x";
                Signed(record, Observer, "obs");
            }
        }
        if (!covered) record["later_field"] = "x";
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null, Entry(kind, record, position: 1)), Options());
        Assert.Equal([(1L, covered ? "unknown_field" : "payload_invalid", "later_field")], Failures(r));
        List<(long?, string, string)> warnings = covered ? [] : [(1L, "unsigned_field", "later_field")];
        Assert.Equal(warnings, r.Warnings?.Select(w => (w.StoreSequence, w.Reason, w.Detail)) ?? []);
    }

    [Fact]
    public void ARecordTheNASigns_IsSignedByTheKeyValidWhenItWasStored()
    {
        // NA0 signed until 10:00, NA1 from then: a registry record stored at 10:10, after the hand-over, and signed by NA0
        // is not the NA's.
        var handover = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        const string Stored = "2026-10-01T10:10:00Z";
        var history = new NAKeyHistory("na", null,
            [new NAKey(1, "na0", Pub(NA0), null, handover, null, false), new NAKey(2, "na1", Pub(NA1), handover, null, null, false)]);
        Assert.Equal([(1L, "na_key_not_valid_at_time", "registry")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(1, null, Entry("registry", Registry("r1", NA0), Stored)), Options(history: history))));
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Relink(1, null, Entry("registry", Registry("r1", NA1), Stored)), Options(history: history)).Verified);
    }

    [Theory]
    [InlineData("ex")]
    [InlineData("obs-x")]
    public void AnObservationIsSignedOnlyByAnObserverKeyOfItsSovereign(string keyId)
    {
        // The executor key of the observer's own sovereign, or the observer key of another sovereign.
        var record = keyId == "ex" ? Observation("o1", Executor, "ex") : Observation("o1", OtherObserver, "obs-x");
        Assert.Equal([(1L, "invalid_signature", "observation")],
            Failures(OfflineVerifier.VerifyEvidenceEvents(Relink(1, null, Entry("observation", record, position: 1)), Options())));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void AJudgementNamesItsSubjectsStorePosition(long subjectSequence, bool verified)
    {
        var observation = Observation("o1");
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("observation", observation, position: 1),
            Entry("judgement", Judgement("j1", "o1", DigestOf(observation), subjectSequence))), Options());
        Assert.Equal(verified, r.Verified);
        if (!verified) Assert.Equal([(2L, "judgement_subject_mismatch", "o1")], Failures(r));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AJudgementOfARecordAtAPositionTheRunHolds_MustFindIt_InARunOfTheStore(bool contiguous)
    {
        // The subject would be entry 1, which is a registry record: missing in a run of the store, not looked for in a
        // history, which holds only some of the store's records.
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("registry", Registry("r1", NA0)),
            Entry("judgement", Judgement("j1", "o-missing", new string('c', 64), 1))), Options(contiguous: contiguous));
        List<(long?, string, string)> expected = contiguous ? [(2L, "judgement_subject_missing", "o-missing")] : [];
        Assert.Equal(expected, Failures(r));
    }

    [Fact]
    public void NoExecutionRecordRestsOnAChangeOutsideTheGovernedPath()
    {
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("observation", Observation("o1"), position: 1), Entry("execution", Execution("o1"))), Options());
        Assert.Equal([(2L, "evidence_cites_judgement", "o1")], Failures(r));
    }

    [Fact]
    public void AnObservationWithoutAPosition_LeavesItsResourcesPositionWhereItWas()
    {
        var r = OfflineVerifier.VerifyEvidenceEvents(Relink(1, null,
            Entry("observation", Observation("o1"), position: 1),
            Entry("observation", Observation("o2"), position: null),
            Entry("observation", Observation("o3"), position: 2)), Options());
        Assert.Equal([(2L, "observation_chain_break", Resource)], Failures(r));
    }

    [Fact]
    public void AJudgementNamesItsSubjectByTheDigestItsEntryNames()
    {
        // The entry names another digest than its payload's: that is the entry's failure, and the judgement that
        // names the entry's digest matches it, as in the TypeScript SDK.
        var events = Relink(1, null,
            Entry("observation", Observation("o1"), position: 1),
            Entry("judgement", Judgement("j1", "o1", new string('f', 64), 1)));
        events[0].Entry.PayloadDigest = new string('f', 64);
        events[0].EntryDigest = OfflineVerifier.EntryDigest(events[0].Entry);
        events[1].Entry.PrevEntryDigest = events[0].EntryDigest;
        events[1].EntryDigest = OfflineVerifier.EntryDigest(events[1].Entry);
        Assert.Equal([(1L, "payload_digest_mismatch", "")], Failures(OfflineVerifier.VerifyEvidenceEvents(events, Options())));
    }

    // ── The review's cases: each a rule a wrong implementation would otherwise pass ────────────────

    private static readonly string[] Kinds = ["observation", "break_glass", "judgement", "quarantine", "registry"];

    /// <summary>One valid record of each kind, in <see cref="Kinds"/> order; <paramref name="change"/> may replace an entry.</summary>
    private static List<EvidenceEvent> EveryKind(Func<int, EvidenceEvent, EvidenceEvent>? change = null, string? attestation = "att-1")
    {
        var observation = Observation("o1");
        EvidenceEvent[] events =
        [
            Entry("observation", observation, position: 1),
            Entry("break_glass", BreakGlass("b1", attestation)),
            Entry("judgement", Judgement("j1", "o1", DigestOf(observation), 1)),
            Entry("quarantine", Quarantine("q1", Observation("o9"))),
            Entry("registry", Registry("r1", NA0)),
        ];
        return Relink(1, null, [.. events.Select((e, i) => change?.Invoke(i, e) ?? e)]);
    }

    /// <summary>The event with one envelope field set to <paramref name="value"/>.</summary>
    private static EvidenceEvent WithEnvelope(EvidenceEvent e, string field, string? value)
    {
        var entry = JsonSerializer.SerializeToNode(e.Entry)!.AsObject();
        entry[field] = value;
        e.Entry = entry.Deserialize<EvidenceStoreEntry>()!;
        return e;
    }

    private static EvidenceStoreVerification Verify(List<EvidenceEvent> events, bool contiguous = true, string? checkpoint = null) =>
        OfflineVerifier.VerifyEvidenceEvents(events, Options(checkpoint: checkpoint, contiguous: contiguous));

    public static IEnumerable<object[]> EnvelopePairs() => Derived.SelectMany(k => k.Value.Select(f => new object[] { k.Key, f.Envelope }));

    [Fact]
    public void OneRecordOfEachKindVerifies() => Assert.Empty(Failures(Verify(EveryKind())));

    [Theory]
    [MemberData(nameof(EnvelopePairs))]
    public void EachEnvelopeFieldNamesWhatItsRecordSays(string kind, string field)
    {
        var index = Array.IndexOf(Kinds, kind);
        var r = Verify(EveryKind((i, e) => i == index ? WithEnvelope(e, field, "other") : e));
        Assert.Equal([(index + 1L, "envelope_mismatch", $"{kind}: {field}")], Failures(r));
    }

    [Theory]
    [InlineData("break_glass", "attestation_id")]
    [InlineData("judgement", "matched_evidence_id")]
    public void AnEmptyEnvelopeField_IsNotTheNullItsRecordHolds(string kind, string field)
    {
        var index = Array.IndexOf(Kinds, kind);
        var r = Verify(EveryKind((i, e) => i == index ? WithEnvelope(e, field, "") : e, attestation: null));
        Assert.Equal([(index + 1L, "envelope_mismatch", $"{kind}: {field}")], Failures(r));
    }

    [Fact]
    public void AnEnvelopeMismatchIsNamedBeforeTheSignature()
    {
        // The record changed after it was signed, its envelope not: both fail, the envelope first.
        var r = Verify(EveryKind((i, e) =>
        {
            if (i != 4) return e;
            var record = JsonNode.Parse(e.Payload.GetRawText())!.AsObject();
            record["event"] = "policy_activated";
            e.Payload = JsonSerializer.SerializeToElement(record);
            return e;
        }));
        Assert.Equal([(5L, "envelope_mismatch", "registry: outcome"), (5L, "invalid_signature", "registry")], Failures(r));
    }

    [Theory]
    [InlineData("judgement")]
    [InlineData("quarantine")]
    public void EveryRecordTheNASigns_IsSignedByTheKeyValidWhenItWasStored(string kind)
    {
        var handover = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var history = new NAKeyHistory("na", null,
            [new NAKey(1, "na0", Pub(NA0), null, handover, null, false), new NAKey(2, "na1", Pub(NA1), handover, null, null, false)]);
        const string Stored = "2026-10-01T10:10:00Z";
        var observation = Observation("o1");
        var record = kind == "judgement" ? Judgement("j1", "o1", DigestOf(observation), 1, na: NA0) : Quarantine("q1", Observation("o9"), NA0);
        var r = OfflineVerifier.VerifyEvidenceEvents(
            Relink(1, null, Entry("observation", observation, Stored, 1), Entry(kind, record, Stored)), Options(history: history));
        Assert.Equal([(2L, "na_key_not_valid_at_time", kind)], Failures(r));
    }

    [Theory]
    [InlineData("obs")]
    [InlineData("ex-x")]
    public void ABreakGlassRecordIsSignedOnlyByAnExecutorKeyOfItsSovereign(string keyId)
    {
        // The observer key of its own sovereign, or the executor key of another sovereign.
        var record = keyId == "obs" ? BreakGlass("b1", key: Observer, keyId: "obs") : BreakGlass("b1", key: OtherExecutor, keyId: "ex-x");
        Assert.Equal([(1L, "invalid_signature", "break_glass")], Failures(Verify(Relink(1, null, Entry("break_glass", record)))));
    }

    [Fact]
    public void AFieldThisSdkDoesNotKnow_SignedByTheKeyItNames_IsRefusedByName_WhateverItsRole()
    {
        // An observation the executor key of its sovereign signed, with a later field: an authentic record from a newer
        // signer, refused by name first, as the reference refuses it.
        var record = Observation("o1");
        record["later_field"] = "x";
        Signed(record, Executor, "ex");
        Assert.Equal([(1L, "unknown_field", "later_field")], Failures(Verify(Relink(1, null, Entry("observation", record, position: 1)))));
    }

    [Fact]
    public void ANullFieldThisSdkDoesNotKnow_IsLeftOutOfTheSignedForm_AndRefusedByName()
    {
        // The NA signs its records without their nulls, an unknown field's too: signed, it is refused by name.
        var record = Registry("r1", NA0, r => r["later_field"] = null);
        Assert.Equal([(1L, "unknown_field", "later_field")], Failures(Verify(Relink(1, null, Entry("registry", record)))));
    }

    [Fact]
    public void ARecordStoredTwice_IsJudgedAsItsLaterCopy()
    {
        var first = Observation("o1");
        var second = Observation("o1");
        second["observed_at"] = "2026-10-01T10:00:05Z";
        Signed(second, Observer, "obs");
        var r = Verify(Relink(1, null,
            Entry("observation", first, position: 1), Entry("observation", second, position: 2),
            Entry("judgement", Judgement("j1", "o1", DigestOf(second), 2))));
        Assert.Equal([(2L, "duplicate_record", "observation o1")], Failures(r));
    }

    [Fact]
    public void ASubjectIsJudgedOnce()
    {
        var observation = Observation("o1");
        var r = Verify(Relink(1, null, Entry("observation", observation, position: 1),
            Entry("judgement", Judgement("j1", "o1", DigestOf(observation), 1)), Entry("judgement", Judgement("j2", "o1", DigestOf(observation), 1))));
        Assert.Equal([(3L, "duplicate_judgement", "o1")], Failures(r));
    }

    [Theory]
    [InlineData("from-1")]
    [InlineData("history")]
    [InlineData("after-checkpoint")]
    public void AMatchedRecordIsLookedForOnlyInARunOfTheStoreFromEntry1(string run)
    {
        // The judged observation matched a record the run does not hold: missing only where the run would hold it.
        var observation = Observation("o1");
        var start = run == "after-checkpoint" ? 6 : 1;
        var events = Relink(start, run == "after-checkpoint" ? new string('a', 64) : null, Entry("observation", observation, position: 1),
            Entry("judgement", Judgement("j1", "o1", DigestOf(observation), start, matched: "e-x")));
        var r = Verify(events, contiguous: run != "history", checkpoint: run == "after-checkpoint" ? Checkpoint(5, null).ToJsonString() : null);
        List<(long?, string, string)> expected = run == "from-1" ? [(2L, "judgement_match_missing", "e-x")] : [];
        Assert.Equal(expected, Failures(r));
    }

    [Fact]
    public void ARecordIsMatchedOnce_HeldOrNot()
    {
        // A run that starts mid-store: the subjects and the matched record come before it.
        var r = Verify(Relink(5, new string('b', 64),
            Entry("judgement", Judgement("j1", "o1", new string('c', 64), 2, matched: "e-x")),
            Entry("judgement", Judgement("j2", "o2", new string('d', 64), 3, matched: "e-x"))));
        Assert.Equal([(6L, "match_reused", "e-x")], Failures(r));
    }

    [Fact]
    public void ASubjectAtAPositionAMidStoreRunHolds_IsMissing()
    {
        var r = Verify(Relink(3, new string('b', 64), Entry("registry", Registry("r1", NA0)),
            Entry("judgement", Judgement("j1", "o-missing", new string('c', 64), 3))));
        Assert.Equal([(4L, "judgement_subject_missing", "o-missing")], Failures(r));
    }

    [Theory]
    [InlineData("o1")]
    [InlineData("j1")]
    public void AJudgementMatchesOnlyExecutionEvidenceOrABreakGlassRecord(string target)
    {
        var first = Observation("o1");
        var second = Observation("o2");
        var r = Verify(Relink(1, null,
            Entry("observation", first, position: 1), Entry("judgement", Judgement("j1", "o1", DigestOf(first), 1)),
            Entry("observation", second, position: 2), Entry("judgement", Judgement("j2", "o2", DigestOf(second), 3, matched: target))));
        Assert.Equal([(4L, "judgement_match_missing", target)], Failures(r));
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("quarantine")]
    public void AnExecutionRecordMayRestOnTheIdOfAnNARecordThatIsNoJudgement(string kind)
    {
        var record = kind == "registry" ? Registry("r1", NA0) : Quarantine("r1", Observation("o9"));
        Assert.Empty(Failures(Verify(Relink(1, null, Entry(kind, record), Entry("execution", Execution("r1"))))));
    }

    [Fact]
    public void AnExecutionRecordOnADecisionWithTheIdOfAChangeRecord_FailsBoth_InTheTypeScriptOrder()
    {
        // The decision checks first, then the rule that no execution record rests on a change outside the governed path.
        var (decision, decisionId, capability) = Decision();
        var r = Verify(Relink(1, null,
            Entry("observation", Observation(decisionId), position: 1),
            Entry("decision", decision),
            Entry("execution", Execution(decisionId, capability, "2026-10-01T11:00:00Z"))));
        Assert.Equal([(3L, "evidence_outside_decision_window", ""), (3L, "evidence_cites_judgement", decisionId)], Failures(r));
    }

    [Fact]
    public void InAHistory_AResourcesObservationsStillFollowEachOther()
    {
        var r = Verify(Relink(1, null, Entry("observation", Observation("o1"), position: 1), Entry("observation", Observation("o2"), position: 3)),
            contiguous: false);
        Assert.Equal([(2L, "observation_chain_break", Resource)], Failures(r));
    }

    [Fact]
    public void AResourcesPositionIsItsLatestObservations()
    {
        // 1, 3, 2, 3: the 3 after 1 and the 2 after 3 break the chain; the last 3 follows the 2.
        var r = Verify(Relink(1, null,
            Entry("observation", Observation("o1"), position: 1), Entry("observation", Observation("o2"), position: 3),
            Entry("observation", Observation("o3"), position: 2), Entry("observation", Observation("o4"), position: 3)));
        Assert.Equal([(2L, "observation_chain_break", Resource), (3L, "observation_chain_break", Resource)], Failures(r));
    }
}
