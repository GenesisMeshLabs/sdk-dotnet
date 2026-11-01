using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Export verification of the records of changes outside the governed path (1.9.0), for the rules the shared
/// <c>out_of_band</c> vectors do not reach: observation positions continued from a retention checkpoint, a field this
/// SDK does not know in a record the NA signs, and the NA key valid when a record was stored. Records are signed by
/// test keys; each case gives what the TypeScript SDK gives.
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

    private const string Resource = "kv:app-1/secret";

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

    /// <summary>The NA's judgement of an observation, naming it by digest and store position.</summary>
    private static JsonObject Judgement(string id, string subjectId, string subjectDigest, long subjectSequence) => Signed(new JsonObject
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
        ["issuer_sovereign_id"] = "sovereign-c",
        ["issued_by"] = "na",
    }, NA0, "na");

    /// <summary>Execution evidence resting on <paramref name="decisionId"/>, signed by the executor key.</summary>
    private static JsonObject Execution(string decisionId)
    {
        var record = new JsonObject
        {
            ["agreement_id"] = "6ce38f75-23a8-4da8-901b-762132db5951",
            ["context_id"] = "2b8591fd-8584-4d0e-a899-42fc79436acc",
            ["decision_id"] = decisionId,
            ["evidence_id"] = "7ecd4840-b991-4790-862e-c5bbe0da4503",
            ["executed_at"] = "2026-10-01T10:00:01Z",
            ["executed_capability"] = "kv.rotate",
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
        switch (kind)
        {
            case "observation":
                entry["record_id"] = (string?)payload["observation_id"];
                entry["resource_id"] = Resource;
                entry["resource_action"] = "rotate";
                entry["capability"] = "kv.rotate";
                entry["executor_sovereign_id"] = "sovereign-a";
                entry["observation_sequence"] = position;
                break;
            case "registry":
                entry["record_id"] = (string?)payload["registry_record_id"];
                entry["outcome"] = (string?)payload["event"];
                break;
            case "judgement":
                entry["record_id"] = (string?)payload["judgement_id"];
                entry["subject_id"] = (string?)payload["subject_id"];
                entry["resource_id"] = Resource;
                entry["resource_action"] = "rotate";
                entry["capability"] = "kv.rotate";
                entry["outcome"] = (string?)payload["verdict"];
                break;
        }
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
        ExecutorKeys = [ObserverKey, OtherObserverKey, ExecutorKey],
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
}
