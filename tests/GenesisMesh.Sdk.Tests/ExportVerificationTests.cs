using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Verifying an evidence export offline (1.9.0): the Python reference's export and the verification it gave, then the
/// TypeScript SDK's negative paths, with records signed again by the test key where a check needs authentic ones.
/// </summary>
public class ExportVerificationTests
{
    /// <summary>The public key of <see cref="TestHelpers.ZeroSeedB64"/>.</summary>
    private const string TestKey = "O2onvM62pC1io6jQKm8Nc2UyFXcd4kOmOsBIoYtZ2ik=";
    private static readonly SeedSigner Signer = new(TestHelpers.ZeroSeedB64, "test");

    private static readonly Lazy<JsonObject> Reference = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!.AsObject());

    private static string NAKey => (string)Reference.Value["na_public_key"]!;

    private static List<ExecutorKeyRecord> ReferenceKeys() =>
        JsonSerializer.Deserialize<List<ExecutorKeyRecord>>(Reference.Value["executor_keys"]!.ToJsonString())!;

    private static List<EvidenceEvent> Events() => OfflineVerifier.ParseExportLines((string)Reference.Value["export"]!).ToList();

    private static EvidenceVerifyOptions Options(bool contiguous = true, string? checkpoint = null) => new()
    {
        NAPublicKeys = [NAKey],
        ExecutorKeys = ReferenceKeys(),
        Contiguous = contiguous,
        Checkpoint = checkpoint,
    };

    /// <summary>The reference's keys and the test key, for records signed again.</summary>
    private static EvidenceVerifyOptions Resigned(string? checkpoint = null) => new()
    {
        NAPublicKeys = [NAKey, TestKey],
        ExecutorKeys = [.. ReferenceKeys(), new ExecutorKeyRecord { KeyId = "test", ExecutorSovereignId = "secrets-controller", PublicKey = TestKey }],
        Checkpoint = checkpoint,
    };

    private static List<string> Reasons(EvidenceStoreVerification v) => v.Failures.Select(f => f.Reason).ToList();

    /// <summary>The event's payload, changed by <paramref name="change"/>.</summary>
    private static void Edit(EvidenceEvent e, Action<JsonObject> change)
    {
        var payload = JsonNode.Parse(e.Payload.GetRawText())!.AsObject();
        change(payload);
        e.Payload = JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>A record's <c>signature</c>, by the test key over its signed form.</summary>
    private static void Sign(JsonObject record, string model)
    {
        record.Remove("signature");
        var body = OfflineVerifier.ReceivedCanonical(model, JsonSerializer.SerializeToElement(record));
        var sig = Signer.SignAsync(Encoding.UTF8.GetBytes(body)).AsTask().GetAwaiter().GetResult();
        record["signature"] = new JsonObject { ["key_id"] = "test", ["sig"] = Convert.ToBase64String(sig) };
    }

    /// <summary>Each envelope's digests and links made right again, as the NA would have stored the events.</summary>
    private static List<EvidenceEvent> Relink(List<EvidenceEvent> events)
    {
        string? previous = null;
        foreach (var e in events)
        {
            e.Entry.PrevEntryDigest = previous;
            e.Entry.PayloadDigest = OfflineVerifier.PayloadDigest(e.Payload);
            e.EntryDigest = OfflineVerifier.EntryDigest(e.Entry);
            previous = e.EntryDigest;
        }
        return events;
    }

    // ── The reference ───────────────────────────────────────────────────────────

    [Fact]
    public void VerifiesTheReferenceExport_AsTheReferenceDid()
    {
        var result = OfflineVerifier.VerifyEvidenceEvents(Events(), Options());
        Assert.True(JsonNode.DeepEquals(Reference.Value["server_verification"],
            JsonNode.Parse(JsonSerializer.Serialize(result, Auth.SerializerOptions))), JsonSerializer.Serialize(result, Auth.SerializerOptions));
        Assert.Null(result.Warnings);
    }

    [Fact]
    public void VerifiesTheReferenceCheckpoint()
    {
        var checkpoint = Reference.Value["checkpoint"]!.ToJsonString();
        Assert.True(OfflineVerifier.VerifyRetentionCheckpoint(checkpoint, [NAKey]));
        Assert.True(OfflineVerifier.VerifyRetentionCheckpoint(checkpoint, NAKeyHistory.ValidAlways([NAKey])));
        Assert.False(OfflineVerifier.VerifyRetentionCheckpoint(checkpoint, [TestKey]));
        var changed = Reference.Value["checkpoint"]!.DeepClone().AsObject();
        changed["removed_count"] = 2;
        Assert.False(OfflineVerifier.VerifyRetentionCheckpoint(changed.ToJsonString(), [NAKey]));
        // v1.5.0: signed by the key valid when it was created.
        var created = OfflineVerifier.ParseTimestamp((string)changed["created_at"]!);
        var later = new NAKeyHistory("na", null, [new NAKey(1, "na", NAKey, created.AddDays(1), null, null, false)]);
        Assert.False(OfflineVerifier.VerifyRetentionCheckpoint(checkpoint, later));
    }

    // ── The TypeScript SDK's protocol tests ─────────────────────────────────────

    [Fact]
    public void ADecisionEntryWithAWrapperFieldAndNoContext_IsAFinding_NotAnException()
    {
        var events = Events();
        Edit(events[0], payload =>
        {
            payload.Remove("context");
            payload["extra"] = 1;
        });
        var result = OfflineVerifier.VerifyEvidenceEvents(events, Options(contiguous: false));
        Assert.Contains("payload_digest_mismatch", Reasons(result));
        Assert.Contains(result.Failures, f => (f.Reason, f.Detail) == ("payload_invalid", "context"));
        Assert.Contains(result.Warnings!, w => (w.Reason, w.Detail) == ("unsigned_field", "extra"));
    }

    [Fact]
    public void AMalformedEvent_IsAFailure()
    {
        var result = OfflineVerifier.VerifyEvidenceEvents([new EvidenceEvent()], Options());
        Assert.False(result.Verified);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(((long?)null, "payload_invalid", "event envelope"), (failure.StoreSequence, failure.Reason, failure.Detail));
        Assert.Equal(1, result.CheckedEntries);
    }

    [Fact]
    public void ARecordOfAnUnknownExecutorKey_IsNotAuthentic()
    {
        var result = OfflineVerifier.VerifyEvidenceEvents(Events(), new EvidenceVerifyOptions { NAPublicKeys = [NAKey] });
        Assert.Contains(result.Failures, f => (f.Reason, f.Detail) == ("invalid_signature", "execution"));
    }

    [Fact]
    public void ARemovedOrReorderedEntry_BreaksTheStoreChain()
    {
        var events = Events();
        Assert.Contains("store_sequence_gap", Reasons(OfflineVerifier.VerifyEvidenceEvents(events.Where((_, i) => i != 1), Options())));
        Assert.Contains("store_sequence_gap", Reasons(OfflineVerifier.VerifyEvidenceEvents(Enumerable.Reverse(events), Options())));
    }

    [Fact]
    public void AFilteredHistory_MayHaveGaps()
    {
        var decisions = Events().Where(e => e.Entry.EntryKind == "decision");
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(decisions, Options(contiguous: false)).Verified);
        Assert.Contains("store_sequence_gap", Reasons(OfflineVerifier.VerifyEvidenceEvents(decisions, Options())));
    }

    [Fact]
    public void AChangedEnvelopePayloadAndResourceChain_AreEachFound()
    {
        var events = Events();
        var execution = events.First(e => e.Entry.EntryKind == "execution");
        Edit(execution, payload => payload["resource_sequence"] = 7);
        execution.Entry.VendorId = "changed";
        var reasons = Reasons(OfflineVerifier.VerifyEvidenceEvents(events, Options()));
        Assert.Contains("entry_digest_mismatch", reasons);
        Assert.Contains("payload_digest_mismatch", reasons);
        Assert.Contains("invalid_signature", reasons);
        Assert.Contains("resource_chain_break", reasons);
    }

    [Theory]
    [InlineData("executed_at", "42")]
    [InlineData("sequence_no", "-1")]
    [InlineData("signature", "42")]
    [InlineData("resource_action", "\"rename\"")]
    [InlineData("resource_id", "\"\"")]
    public void AnInvalidExecutionField_IsPayloadInvalid(string field, string json)
    {
        var events = Events();
        Edit(events.First(e => e.Entry.EntryKind == "execution"), payload => payload[field] = JsonNode.Parse(json));
        Assert.Contains("payload_invalid", Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
    }

    [Fact]
    public void ACheckpointTheCallerSupplies_MustBeTheNAs()
    {
        var unsigned = Reference.Value["checkpoint"]!.DeepClone().AsObject();
        unsigned["signature"] = null;
        var result = OfflineVerifier.VerifyEvidenceEvents([], Options(checkpoint: unsigned.ToJsonString()));
        Assert.Equal(((long?)null, "invalid_signature", "retention_checkpoint"),
            (Assert.Single(result.Failures).StoreSequence, result.Failures[0].Reason, result.Failures[0].Detail));
        var newer = Reference.Value["checkpoint"]!.DeepClone().AsObject();
        newer["x"] = 1;
        Sign(newer, "RetentionCheckpoint");
        var refused = OfflineVerifier.VerifyEvidenceEvents([], Resigned(checkpoint: newer.ToJsonString()));
        Assert.Equal(("unknown_field", "checkpoint.x"), (Assert.Single(refused.Failures).Reason, refused.Failures[0].Detail));
    }

    // ── The TypeScript SDK's negative paths, with authentic records ─────────────

    [Theory]
    [InlineData("denied", "evidence_decision_denied")]
    [InlineData("window", "evidence_outside_decision_window")]
    [InlineData("capability", "evidence_capability_mismatch")]
    [InlineData("decision chain", "evidence_chain_break")]
    [InlineData("resource chain", "resource_chain_break")]
    public void AnAuthenticRecordThatBreaksARule_IsFound(string mode, string reason)
    {
        var events = Events();
        var execution = events.First(e => e.Entry.EntryKind == "execution");
        var decisionId = execution.Payload.GetProperty("decision_id").GetString();
        if (mode == "denied")
        {
            Edit(events.First(e => e.Entry.EntryKind == "decision" && e.Entry.DecisionId == decisionId), payload =>
            {
                var decision = payload["decision"]!.AsObject();
                decision["authorized"] = false;
                Sign(decision, "BoundaryDecision");
            });
        }
        Edit(execution, record =>
        {
            switch (mode)
            {
                case "window": record["executed_at"] = "2000-01-01T00:00:00Z"; break;
                case "capability": record["executed_capability"] = "different"; break;
                case "decision chain": record["sequence_no"] = 3; break;
                case "resource chain": record["prev_resource_digest"] = "different"; break;
            }
            Sign(record, "ExecutionEvidence");
        });
        var reasons = Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Resigned()));
        Assert.Contains(reason, reasons);
        Assert.DoesNotContain("invalid_signature", reasons);
    }

    [Fact]
    public void AMalformedContextAndJustification_AreEachPayloadInvalid()
    {
        var events = Events();
        Edit(events[0], payload => payload["context"]!["requested_at"] = "invalid");
        Edit(events[1], payload => payload["trace"] = null);
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(events), Resigned());
        Assert.Equal(2, result.Failures.Count(f => f.Reason == "payload_invalid"));
    }

    [Fact]
    public void ACheckpointEntry_AndARunThatContinuesFromOne()
    {
        // Nothing removed yet: a checkpoint removes as many entries as positions (retention_count_mismatch).
        var checkpoint = Reference.Value["checkpoint"]!.DeepClone().AsObject();
        checkpoint["resource_heads"] = new JsonObject();
        checkpoint["removed_through_sequence"] = 0;
        checkpoint["removed_count"] = 0;
        checkpoint["last_removed_entry_digest"] = "anchor";
        Sign(checkpoint, "RetentionCheckpoint");
        var first = Events()[0];
        first.Entry.PrevEntryDigest = "anchor";
        first.EntryDigest = OfflineVerifier.EntryDigest(first.Entry);
        Assert.True(OfflineVerifier.VerifyEvidenceEvents([first], Resigned(checkpoint.ToJsonString())).Verified);
        first.Entry.PrevEntryDigest = "wrong";
        first.EntryDigest = OfflineVerifier.EntryDigest(first.Entry);
        Assert.Contains("store_chain_break", Reasons(OfflineVerifier.VerifyEvidenceEvents([first], Resigned(checkpoint.ToJsonString()))));

        var entry = JsonSerializer.Deserialize<EvidenceStoreEntry>(JsonSerializer.Serialize(first.Entry))!;
        entry.EntryKind = "retention_checkpoint";
        EvidenceEvent CheckpointEvent(JsonObject payload) => new()
        {
            Schema = "gm.evidence.event",
            SchemaVersion = 1,
            Entry = entry,
            Payload = JsonSerializer.SerializeToElement(payload),
        };
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(Relink([CheckpointEvent(checkpoint)]), Resigned()).Verified);
        var unsigned = checkpoint.DeepClone().AsObject();
        unsigned["signature"] = null;
        Assert.Contains("invalid_signature", Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink([CheckpointEvent(unsigned)]), Resigned())));
        unsigned["removed_count"] = -1;
        Assert.Contains("payload_invalid", Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink([CheckpointEvent(unsigned)]), Resigned())));
        var miscounted = checkpoint.DeepClone().AsObject();
        miscounted["removed_count"] = 1;
        Sign(miscounted, "RetentionCheckpoint");
        Assert.Contains("retention_count_mismatch", Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink([CheckpointEvent(miscounted)]), Resigned())));
        // A field outside a checkpoint's signature is no part of one.
        var carried = checkpoint.DeepClone().AsObject();
        carried["x"] = 1;
        var outside = OfflineVerifier.VerifyEvidenceEvents(Relink([CheckpointEvent(carried)]), Resigned());
        Assert.Contains(outside.Failures, f => (f.Reason, f.Detail) == ("payload_invalid", "x"));
    }

    [Fact]
    public void AResourceChainContinuesFromTheCheckpointsHead()
    {
        // The reference checkpoint removed the resource's first record: the export after it starts at its second.
        var checkpoint = Reference.Value["checkpoint"]!.ToJsonString();
        var executions = Events().Where(e => e.Entry.EntryKind == "execution").ToList();
        var second = executions.Single(e => e.Payload.GetProperty("resource_sequence").GetInt64() == 2);
        var heads = JsonNode.Parse(checkpoint)!["resource_heads"]!.AsObject();
        var resource = second.Payload.GetProperty("resource_id").GetString()!;
        Assert.True(heads.ContainsKey(resource));
        // The head names another digest than the first record's, so the second record does not continue from it.
        var result = OfflineVerifier.VerifyEvidenceEvents([second], Options(contiguous: false, checkpoint: checkpoint));
        Assert.Contains(result.Failures, f => (f.Reason, f.Detail) == ("resource_chain_break", resource));
    }

    // ── Unknown fields, replays and record times ────────────────────────────────

    [Fact]
    public void ASignedUnknownField_IsRefused_AndAnUnsignedOne_IsSetAside()
    {
        var signed = Events();
        Edit(signed.First(e => e.Entry.EntryKind == "execution"), record =>
        {
            record["x"] = 1;
            Sign(record, "ExecutionEvidence");
        });
        var refused = OfflineVerifier.VerifyEvidenceEvents(Relink(signed), Resigned());
        Assert.Contains(refused.Failures, f => (f.Reason, f.Detail) == ("unknown_field", "x"));

        var unsigned = Events();
        Edit(unsigned.First(e => e.Entry.EntryKind == "execution"), record => record["x"] = 1);
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(unsigned), Options());
        Assert.True(result.Verified, string.Join("; ", Reasons(result)));
        Assert.Equal(("unsigned_field", "x"), (Assert.Single(result.Warnings!).Reason, result.Warnings![0].Detail));
    }

    [Fact]
    public void ARecordStoredTwice_IsAReplay()
    {
        var events = Events();
        var copy = JsonSerializer.Deserialize<EvidenceEvent>(JsonSerializer.Serialize(events.First(e => e.Entry.EntryKind == "execution")))!;
        copy.Entry.StoreSequence = events.Count + 1;
        events.Add(copy);
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options());
        Assert.Contains(result.Failures, f => f.Reason == "duplicate_record" && f.StoreSequence == events.Count);
    }

    [Fact]
    public void AnEntryRecordedWellBeforeAnEarlierOne_IsOutOfOrder()
    {
        var events = Events();
        var last = events[^1];
        var at = events.SkipLast(1).Max(e => OfflineVerifier.ParseTimestamp(e.Entry.RecordedAt));
        last.Entry.RecordedAt = Canonical.Timestamp(at.AddMinutes(-4));
        Assert.DoesNotContain("recorded_at_out_of_order", Reasons(OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options())));
        last.Entry.RecordedAt = Canonical.Timestamp(at.AddMinutes(-6));
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options());
        Assert.Contains(result.Failures, f => f.Reason == "recorded_at_out_of_order" && f.StoreSequence == last.Entry.StoreSequence);
        // A history is no run of the store: its times are not compared.
        Assert.DoesNotContain("recorded_at_out_of_order", Reasons(OfflineVerifier.VerifyEvidenceEvents(events, Options(contiguous: false))));
    }

    [Fact]
    public void AKindThisSdkDoesNotVerify_StillChains()
    {
        var events = Events();
        events[2].Entry.EntryKind = "observation";
        var result = OfflineVerifier.VerifyEvidenceEvents(Relink(events), Options());
        var unknown = Assert.Single(result.Failures, f => f.Reason == "unknown_entry_kind");
        Assert.Equal(((long?)3, "observation"), (unknown.StoreSequence, unknown.Detail));
        Assert.DoesNotContain("store_chain_break", Reasons(result));
    }

    // ── The NA's key history (v1.5.0) ───────────────────────────────────────────

    [Fact]
    public void WithAKeyHistory_EachRecordIsSignedByTheKeyValidThen()
    {
        var events = Events();
        Assert.True(OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
        {
            KeyHistory = NAKeyHistory.ValidAlways([NAKey]),
            ExecutorKeys = ReferenceKeys(),
        }).Verified);
        // The reference's key took over only after the entries were recorded: the test key was the NA's then.
        var start = OfflineVerifier.ParseTimestamp(events[^1].Entry.RecordedAt).AddDays(1);
        var history = new NAKeyHistory("na", null,
        [
            new NAKey(1, "test", TestKey, null, start, null, false),
            new NAKey(2, "na", NAKey, start, null, null, false),
        ]);
        var result = OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions { KeyHistory = history, ExecutorKeys = ReferenceKeys() });
        Assert.Contains(result.Failures, f => (f.Reason, f.Detail) == ("na_key_not_valid_at_time", "decision"));
        Assert.Contains(result.Failures, f => (f.Reason, f.Detail) == ("na_key_not_valid_at_time", "justification"));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
        {
            NAPublicKeys = [NAKey],
            KeyHistory = history,
        }));
    }
}
