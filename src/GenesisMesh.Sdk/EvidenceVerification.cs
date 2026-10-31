using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenesisMesh;

/// <summary>Options for <see cref="OfflineVerifier.VerifyEvidenceEvents"/>.</summary>
public sealed class EvidenceVerifyOptions
{
    /// <summary>
    /// The NA's keys (base64 Ed25519), valid at every time: they sign decisions, justification proofs and retention
    /// checkpoints. Or give <see cref="KeyHistory"/>.
    /// </summary>
    public IReadOnlyList<string> NAPublicKeys { get; init; } = [];

    /// <summary>
    /// v1.5.0: instead of <see cref="NAPublicKeys"/>, the NA's verified key history
    /// (<see cref="OfflineVerifier.VerifyKeyHistoryResponse"/>). Each record must then be signed by the key valid at its
    /// entry's <c>recorded_at</c>, and at the latest earlier time of the run (<c>na_key_not_valid_at_time</c> for another
    /// key of the history), and a decision may stay valid no longer than its key vouches for
    /// (<c>decision_outlives_key</c>).
    /// </summary>
    public NAKeyHistory? KeyHistory { get; init; }

    /// <summary>
    /// The executor keys records are signed with, retired ones included (<see cref="EvidenceStoreClient.ListExecutorKeys"/>):
    /// a retired key still verifies the records it signed.
    /// </summary>
    public IReadOnlyList<ExecutorKeyRecord> ExecutorKeys { get; init; } = [];

    /// <summary>
    /// True (the default) for an unbroken run of the store: an export from entry 1, or from right after a retention
    /// checkpoint. False for a history (<see cref="ResourceHistory"/>, <see cref="VendorHistory"/>), whose store links
    /// are checked only between adjacent positions.
    /// </summary>
    public bool Contiguous { get; init; } = true;

    /// <summary>
    /// The retention checkpoint JSON as received, for a run that starts after the entries it removed: the run's first
    /// entry continues from its last removed entry, and each resource chain from its head. It must be signed by the NA
    /// (<c>invalid_signature</c>, and nothing else is checked).
    /// </summary>
    public string? Checkpoint { get; init; }
}

public static partial class OfflineVerifier
{
    /// <summary>
    /// The entry kinds this SDK verifies (1.9.0): those a governed store holds. The store's other kinds (changes outside
    /// the governed path, remediation, key successions, archives and grants) fail as <c>unknown_entry_kind</c>, as a kind
    /// from a later release does; their envelopes still chain.
    /// </summary>
    internal static readonly string[] VerifiedEntryKinds = ["decision", "justification", "execution", "retention_checkpoint"];

    /// <summary>How far before an earlier entry of a run an entry may be recorded (v1.5.0, the reference's tolerance).</summary>
    private static readonly TimeSpan RecordTimeTolerance = TimeSpan.FromSeconds(300);

    /// <summary>v1.5.0: the kinds retention appends again after its checkpoint, each with the time it was first recorded.</summary>
    private static readonly HashSet<string> CarriedKinds = new(StringComparer.Ordinal) { "registry", "key_succession", "archive" };

    /// <summary>v1.3.1: the field holding each kind's record ID, which appears once per kind in a run (<c>duplicate_record</c>).</summary>
    private static readonly Dictionary<string, string> RecordIdFields = new(StringComparer.Ordinal)
    {
        ["decision"] = "decision_id",
        ["justification"] = "proof_id",
        ["execution"] = "evidence_id",
        ["retention_checkpoint"] = "checkpoint_id",
    };

    /// <summary>The model each kind's payload holds; a decision payload wraps two, its decision and its context.</summary>
    private static readonly Dictionary<string, string> PayloadModels = new(StringComparer.Ordinal)
    {
        ["justification"] = "JustificationProof",
        ["execution"] = "ExecutionEvidence",
        ["retention_checkpoint"] = "RetentionCheckpoint",
    };

    /// <summary>
    /// Verifies stored entries offline, as the reference's <c>verify_evidence_events</c> does: each envelope, its digest
    /// and its payload's, the store chain, every signature (the NA's on decisions, justification proofs and retention
    /// checkpoints, the executor's on execution evidence), each execution record against its decision, and the decision
    /// and resource chains. Pass an export (<see cref="ParseExportLines(string)"/>, <see cref="EvidenceStoreClient.ExportAll"/>)
    /// or, with <see cref="EvidenceVerifyOptions.Contiguous"/> false, a history. Nothing is thrown for a bad entry: each
    /// finding is a failure, with the reason codes every implementation uses.
    /// </summary>
    /// <remarks>
    /// This SDK verifies the kinds a governed store holds: <c>decision</c>, <c>justification</c>, <c>execution</c> and
    /// <c>retention_checkpoint</c>. Any other kind fails as <c>unknown_entry_kind</c>; verify such a store with the
    /// TypeScript or Rust SDK, or the core's <c>genesis-mesh audit verify</c>. Anchors are not checked here.
    /// </remarks>
    /// <param name="events">The entries, in store order.</param>
    /// <param name="options">The keys, and what the run is.</param>
    /// <exception cref="ArgumentException">Both <see cref="EvidenceVerifyOptions.NAPublicKeys"/> and <see cref="EvidenceVerifyOptions.KeyHistory"/> are given.</exception>
    /// <exception cref="StrictJsonException"><see cref="EvidenceVerifyOptions.Checkpoint"/> is not strict JSON.</exception>
    public static EvidenceStoreVerification VerifyEvidenceEvents(IEnumerable<EvidenceEvent> events, EvidenceVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeyHistory is not null && options.NAPublicKeys.Count > 0)
            throw new ArgumentException("pass NAPublicKeys or KeyHistory, not both", nameof(options));
        using var checkpoint = options.Checkpoint is { } json ? StrictJsonReader.Parse(json) : null;
        var run = new EventRun(options, checkpoint?.RootElement);
        if (run.CheckpointAccepted())
            foreach (var e in events) run.Visit(e);
        return run.Result;
    }

    /// <summary>
    /// True when the NA signed the retention checkpoint with one of <paramref name="naPublicKeys"/>, and it has no field
    /// this SDK does not know and is in canonical form.
    /// </summary>
    /// <param name="checkpointJson">The RetentionCheckpoint JSON, as received.</param>
    /// <param name="naPublicKeys">The NA's base64 Ed25519 public keys.</param>
    /// <exception cref="StrictJsonException"><paramref name="checkpointJson"/> is not strict JSON.</exception>
    public static bool VerifyRetentionCheckpoint(string checkpointJson, IReadOnlyList<string> naPublicKeys) =>
        SignedRecord("RetentionCheckpoint", checkpointJson, _ => naPublicKeys);

    /// <summary>
    /// As <see cref="VerifyRetentionCheckpoint(string, IReadOnlyList{string})"/>, but signed by the key of the NA's
    /// verified history that was valid at the checkpoint's <c>created_at</c>.
    /// </summary>
    /// <param name="checkpointJson">The RetentionCheckpoint JSON, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <exception cref="StrictJsonException"><paramref name="checkpointJson"/> is not strict JSON.</exception>
    public static bool VerifyRetentionCheckpoint(string checkpointJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        return SignedRecord("RetentionCheckpoint", checkpointJson, c =>
            Str(c, "created_at") is { } created && TryReadTimestamp(created, out var at) ? keyHistory.KeysAt(at) : []);
    }

    /// <summary><c>EvidenceStoreEntry.digest()</c>: SHA-256 hex of the envelope's canonical form.</summary>
    internal static string EntryDigest(JsonElement entry) => Sha256Hex(ReceivedCanonical("EvidenceStoreEntry", entry));

    /// <inheritdoc cref="EntryDigest(JsonElement)"/>
    internal static string EntryDigest(EvidenceStoreEntry entry) => EntryDigest(JsonSerializer.SerializeToElement(entry));

    /// <summary>SHA-256 hex of a stored payload's canonical form.</summary>
    internal static string PayloadDigest(JsonElement payload) => Sha256Hex(Canonical.Of(payload));

    /// <summary>One run of entries being verified, in order.</summary>
    private sealed class EventRun
    {
        private readonly NAKeyHistory? _history;
        private readonly IReadOnlyList<string> _naKeys;
        private readonly IReadOnlyList<string> _allNaKeys;
        private readonly Dictionary<string, ExecutorKeyRecord> _executorKeys = new(StringComparer.Ordinal);
        private readonly bool _contiguous;
        private readonly JsonElement? _checkpoint;
        private readonly Dictionary<string, JsonElement> _decisions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _contexts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _lastExecution = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _checkpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Sequence, string Digest)> _resourceHeads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _recordIds = new(StringComparer.Ordinal);
        private long? _firstSequence;
        private bool _fromStart;
        private JsonElement? _previous;
        private long _previousSequence;
        private string _previousKind = "";
        /// <summary>v1.5.0: the latest <c>recorded_at</c> of the run so far.</summary>
        private DateTimeOffset? _clock;
        /// <summary>v1.5.0: whether the entries just visited are a block retention carried forward.</summary>
        private bool _carrying;

        public EvidenceStoreVerification Result { get; } = new() { Verified = true };

        public EventRun(EvidenceVerifyOptions options, JsonElement? checkpoint)
        {
            _history = options.KeyHistory;
            _naKeys = options.NAPublicKeys;
            _allNaKeys = _history?.AllKeys() ?? _naKeys;
            foreach (var key in options.ExecutorKeys) _executorKeys[key.KeyId] = key;
            _contiguous = options.Contiguous;
            _checkpoint = checkpoint?.Clone();
            if (_checkpoint is { } cp && IsObject(cp, "resource_heads", out var heads))
                foreach (var head in heads.EnumerateObject())
                    if (PayloadInteger(head.Value, "resource_sequence") is { } sequence && Str(head.Value, "record_digest") is { } digest)
                        _resourceHeads[head.Name] = (sequence, digest);
        }

        private IReadOnlyList<string> KeysAt(DateTimeOffset at) => _history?.KeysAt(at) ?? _naKeys;

        private void Fail(long? sequence, string reason, string detail = "")
        {
            Result.Verified = false;
            Result.Failures.Add(new EvidenceVerificationFailure { StoreSequence = sequence, Reason = reason, Detail = detail });
        }

        private void Warn(long? sequence, string reason, string detail) =>
            (Result.Warnings ??= []).Add(new EvidenceVerificationFailure { StoreSequence = sequence, Reason = reason, Detail = detail });

        /// <summary>A checkpoint the caller supplied is the NA's, with no field this SDK does not know.</summary>
        public bool CheckpointAccepted()
        {
            if (_checkpoint is not { } cp) return true;
            if (!Shapes.Checkpoint(cp) || !SignedAsReceived("RetentionCheckpoint", cp,
                    TryReadTimestamp(Str(cp, "created_at")!, out var created) ? KeysAt(created) : []))
            {
                Fail(null, "invalid_signature", "retention_checkpoint");
                return false;
            }
            var unknown = StrictFields.UnknownFields("RetentionCheckpoint", cp, "checkpoint.");
            if (unknown.Count > 0)
            {
                Fail(null, "unknown_field", string.Join(", ", unknown));
                return false;
            }
            return true;
        }

        public void Visit(EvidenceEvent e)
        {
            if (Envelope(e) is not { } entry)
            {
                Result.CheckedEntries++;
                Fail(null, "payload_invalid", "event envelope");
                return;
            }
            var seq = e.Entry.StoreSequence;
            var kind = e.Entry.EntryKind;
            Result.CheckedEntries++;
            // v1.5.0: in a contiguous run an entry is not recorded more than the tolerance before an earlier one
            // (carried entries keep their first time), and an NA key must also be valid at the latest earlier time.
            TryReadTimestamp(e.Entry.RecordedAt, out var recordedAt);
            DateTimeOffset? notBefore = null;
            if (_contiguous)
            {
                var adjacent = _previous is not null && seq == _previousSequence + 1;
                _carrying = CarriedKinds.Contains(kind) && adjacent && (_previousKind == "retention_checkpoint" || _carrying);
                if (!_carrying)
                {
                    if (_clock is { } clock && recordedAt < clock - RecordTimeTolerance)
                        Fail(seq, "recorded_at_out_of_order",
                            $"recorded at {Canonical.Timestamp(recordedAt)}, after an entry recorded at {Canonical.Timestamp(clock)}");
                    notBefore = _clock;
                    if (_clock is null || recordedAt > _clock) _clock = recordedAt;
                }
            }
            if (EntryDigest(entry) != e.EntryDigest) Fail(seq, "entry_digest_mismatch");
            if (PayloadDigest(e.Payload) != e.Entry.PayloadDigest) Fail(seq, "payload_digest_mismatch");
            if (_previous is { } previous && (_contiguous || seq == _previousSequence + 1))
            {
                if (seq != _previousSequence + 1) Fail(seq, "store_sequence_gap");
                else if (e.Entry.PrevEntryDigest != EntryDigest(previous)) Fail(seq, "store_chain_break");
            }
            else if (_previous is null && _checkpoint is { } cp && seq == PayloadInteger(cp, "removed_through_sequence") + 1
                     && e.Entry.PrevEntryDigest != Str(cp, "last_removed_entry_digest"))
            {
                Fail(seq, "store_chain_break", "does not continue from the checkpoint");
            }
            if (_firstSequence is null)
            {
                _firstSequence = seq;
                _fromStart = seq == 1 || (_checkpoint is { } start && seq == PayloadInteger(start, "removed_through_sequence") + 1);
            }
            _previous = entry;
            _previousSequence = seq;
            _previousKind = kind;

            if (!VerifiedEntryKinds.Contains(kind))
            {
                // A kind this SDK does not verify, or (v1.2.0) one from a later release: its envelope still chains.
                Fail(seq, "unknown_entry_kind", kind);
                return;
            }
            var (signedUnknown, unsignedUnknown, payload) = CheckPayloadFields(kind, e.Payload);
            if (signedUnknown.Count > 0)
            {
                Fail(seq, "unknown_field", string.Join(", ", signedUnknown));
                return;
            }
            if (unsignedUnknown.Count > 0)
            {
                Warn(seq, "unsigned_field", string.Join(", ", unsignedUnknown));
                // v1.5.0: a retention checkpoint is stored exactly as signed; a field outside its signature is no part of one.
                if (kind == "retention_checkpoint")
                {
                    Fail(seq, "payload_invalid", string.Join(", ", unsignedUnknown));
                    return;
                }
            }
            if (!ValidPayload(kind, payload))
            {
                Fail(seq, "payload_invalid");
                return;
            }
            var record = kind == "decision" ? payload.GetProperty("decision") : payload;
            new RecordCheck(this, seq, kind, recordedAt, notBefore, payload, record).Check();
        }

        /// <summary>The event's envelope as received, or null when it is not one (TypeScript <c>validEvent</c>).</summary>
        private static JsonElement? Envelope(EvidenceEvent e)
        {
            if (e?.Entry is null || e.Schema != "gm.evidence.event" || e.SchemaVersion != 1 || e.EntryDigest is null
                || e.Payload.ValueKind != JsonValueKind.Object)
                return null;
            var entry = JsonSerializer.SerializeToElement(e.Entry);
            return IsEntry(entry) ? entry : null;
        }

        /// <summary>
        /// The payload's unknown fields, signed (refused) and unsigned (reported, then set aside), and the payload to
        /// verify further, without its unsigned unknown fields.
        /// </summary>
        private (List<string> Signed, List<string> Unsigned, JsonElement Payload) CheckPayloadFields(string kind, JsonElement payload)
        {
            if (kind == "decision")
            {
                var unsigned = payload.EnumerateObject().Select(p => p.Name).Where(k => k is not "decision" and not "context").ToList();
                var hasContext = payload.TryGetProperty("context", out var context);
                if (hasContext) unsigned.AddRange(StrictFields.UnknownFields("ContextRecord", context, "context."));
                var hasDecision = payload.TryGetProperty("decision", out var decision);
                var found = hasDecision ? StrictFields.UnknownFields("BoundaryDecision", decision, "decision.") : [];
                if (found.Count > 0 && decision.ValueKind == JsonValueKind.Object
                    && SignedAsReceived("BoundaryDecision", decision, _allNaKeys))
                    return (found, unsigned, payload);
                var cleaned = new JsonObject();
                if (hasDecision) cleaned["decision"] = JsonNode.Parse(StrictFields.WithoutUnknownFields("BoundaryDecision", decision).GetRawText());
                if (hasContext) cleaned["context"] = JsonNode.Parse(StrictFields.WithoutUnknownFields("ContextRecord", context).GetRawText());
                return ([], [.. unsigned, .. found], JsonSerializer.SerializeToElement(cleaned));
            }
            var model = PayloadModels[kind];
            var fields = StrictFields.UnknownFields(model, payload, "");
            if (fields.Count == 0) return ([], [], payload);
            var signedAsReceived = kind switch
            {
                "execution" => KeyNamed(payload) is { } key && SignedAsReceived(model, payload, [key.PublicKey]),
                _ => SignedAsReceived(model, payload, _allNaKeys),
            };
            return signedAsReceived ? (fields, [], payload) : ([], fields, StrictFields.WithoutUnknownFields(model, payload));
        }

        /// <summary>The registered key a record's signature names, or null.</summary>
        private ExecutorKeyRecord? KeyNamed(JsonElement record) =>
            IsObject(record, "signature", out var sig) && Str(sig, "key_id") is { } id && _executorKeys.TryGetValue(id, out var key)
                ? key
                : null;

        private static bool ValidPayload(string kind, JsonElement payload) => kind switch
        {
            "decision" => payload.TryGetProperty("decision", out var d) && Shapes.Decision(d),
            "justification" => Shapes.Justification(payload),
            "execution" => Shapes.Execution(payload),
            _ => Shapes.Checkpoint(payload),
        };

        /// <summary>One entry's record, checked once its envelope and payload are.</summary>
        private sealed class RecordCheck(EventRun run, long seq, string kind, DateTimeOffset recordedAt, DateTimeOffset? notBefore,
            JsonElement payload, JsonElement record)
        {
            private EvidenceStoreVerification Result => run.Result;

            private void Fail(string reason, string detail = "") => run.Fail(seq, reason, detail);

            /// <summary>
            /// v1.3.0: a record whose signature covers it as received, in a form the reference does not write, is refused
            /// by name, as the reference refuses it.
            /// </summary>
            private void Signature(bool signed, string model)
            {
                if (!signed) Fail("invalid_signature", kind);
                else if (StrictFields.NonCanonicalFields(model, record).Count > 0) Fail("non_canonical_form", kind);
            }

            /// <summary>
            /// v1.5.0: an NA signature by a key valid at the entry's <c>recorded_at</c>, and at the latest earlier time of the
            /// run; by another key of the history, <c>na_key_not_valid_at_time</c> (a form the reference does not write is
            /// named first). Returns the keys valid then when the signature is accepted.
            /// </summary>
            private IReadOnlyList<string>? NASignature(Func<IReadOnlyList<string>, bool> signedBy, string model)
            {
                var valid = run.KeysAt(recordedAt);
                if (notBefore is { } before && before > recordedAt)
                {
                    var later = run.KeysAt(before).ToHashSet(StringComparer.Ordinal);
                    valid = valid.Where(later.Contains).ToList();
                }
                var loose = StrictFields.NonCanonicalFields(model, record).Count > 0;
                if (signedBy(valid))
                {
                    Signature(true, model);
                    return loose ? null : valid;
                }
                var others = run._allNaKeys.Where(k => !valid.Contains(k)).ToList();
                if (others.Count > 0 && signedBy(others)) Fail(loose ? "non_canonical_form" : "na_key_not_valid_at_time", kind);
                else Fail("invalid_signature", kind);
                return null;
            }

            public void Check()
            {
                // v1.3.1: a record's ID appears once per kind: a copy is a replay, still checked like any record.
                var recordId = Str(record, RecordIdFields[kind])!;
                if (!run._recordIds.TryGetValue(kind, out var seen)) run._recordIds[kind] = seen = new(StringComparer.Ordinal);
                if (!seen.Add(recordId)) Fail("duplicate_record", $"{kind} {recordId}");
                switch (kind)
                {
                    case "decision":
                        Decision(recordId);
                        break;
                    case "justification":
                        NASignature(keys => SignedAsReceived("JustificationProof", payload, keys), "JustificationProof");
                        break;
                    case "execution":
                        Execution();
                        break;
                    default:
                        RetentionCheckpoint();
                        break;
                }
            }

            private void Decision(string decisionId)
            {
                var valid = NASignature(keys => SignedAsReceived("BoundaryDecision", record, keys), "BoundaryDecision");
                // v1.5.0: valid longer than the key that signed it may vouch for.
                var signer = valid?.FirstOrDefault(k => SignedAsReceived("BoundaryDecision", record, [k]));
                if (signer is not null && run._history is { } history
                    && TryReadTimestamp(Str(record, "decision_valid_until")!, out var validUntil)
                    && history.DecisionOutlivesKey(signer, validUntil))
                    Fail("decision_outlives_key", decisionId);
                Result.Decisions++;
                run._decisions[decisionId] = record.Clone();
                if (payload.TryGetProperty("context", out var context) && Shapes.Context(context))
                    run._contexts[decisionId] = context.Clone();
                else
                    Fail("payload_invalid", "context");
            }

            private void Execution()
            {
                var key = run.KeyNamed(payload);
                // v1.3.0: only an executor key signs execution evidence.
                var fits = key is not null && key.ExecutorSovereignId == Str(payload, "executor_sovereign_id")
                    && (key.Role ?? "executor") == "executor";
                Signature(fits && SignedAsReceived("ExecutionEvidence", payload, [key!.PublicKey]), "ExecutionEvidence");
                Result.Executions++;
                var decisionId = Str(payload, "decision_id")!;
                var hasDecision = run._decisions.TryGetValue(decisionId, out var decision);
                if (hasDecision)
                {
                    TryReadTimestamp(Str(payload, "executed_at")!, out var executedAt);
                    TryReadTimestamp(Str(decision, "decision_made_at")!, out var madeAt);
                    TryReadTimestamp(Str(decision, "decision_valid_until")!, out var validUntil);
                    if (!Bool(decision, "authorized")) Fail("evidence_decision_denied");
                    else if (executedAt < madeAt || executedAt > validUntil) Fail("evidence_outside_decision_window");
                    else if (run._contexts.TryGetValue(decisionId, out var context)
                             && Str(payload, "executed_capability") != Str(context, "requested_capability"))
                        Fail("evidence_capability_mismatch");
                }
                var sequenceNo = PayloadInteger(payload, "sequence_no");
                var prevEvidence = Str(payload, "prev_evidence_digest");
                if (run._lastExecution.TryGetValue(decisionId, out var prior))
                {
                    if (sequenceNo != PayloadInteger(prior, "sequence_no") + 1 || prevEvidence != ExecutionDigestOf(prior))
                        Fail("evidence_chain_break");
                }
                else if (hasDecision && (sequenceNo != 1 || prevEvidence is not null))
                {
                    Fail("evidence_chain_break");
                }
                run._lastExecution[decisionId] = payload.Clone();
                if (Str(payload, "resource_id") is { } resourceId)
                {
                    var hasHead = run._resourceHeads.TryGetValue(resourceId, out var head);
                    var resourceSequence = PayloadInteger(payload, "resource_sequence");
                    // v1.3.1: a run that starts neither at entry 1 nor right after the checkpoint takes the first record
                    // it holds of a resource as that chain's start.
                    if ((hasHead || run._fromStart)
                        && (resourceSequence != (hasHead ? head.Sequence + 1 : 1)
                            || Str(payload, "prev_resource_digest") != (hasHead ? head.Digest : null)))
                        Fail("resource_chain_break", resourceId);
                    run._resourceHeads[resourceId] = (resourceSequence ?? 0, ExecutionDigestOf(payload));
                }
            }

            private void RetentionCheckpoint()
            {
                var failures = Result.Failures.Count;
                NASignature(keys => SignedAsReceived("RetentionCheckpoint", payload, keys), "RetentionCheckpoint");
                var authentic = Result.Failures.Count == failures;
                // v1.3.1: a checkpoint removed as many entries as positions, counted from the checkpoint before it when
                // the run holds that one, or from entry 1 when there is none.
                var previous = Str(payload, "previous_checkpoint_id");
                var hasBefore = previous is not null && run._checkpoints.ContainsKey(previous);
                if (previous is null || hasBefore)
                {
                    var start = hasBefore ? PayloadInteger(run._checkpoints[previous!], "removed_through_sequence") ?? 0 : 0;
                    var through = PayloadInteger(payload, "removed_through_sequence") ?? 0;
                    var removed = PayloadInteger(payload, "removed_count") ?? 0;
                    if (removed != through - start)
                        Fail("retention_count_mismatch",
                            $"removed {removed} of {through - start} entries after #{start} through #{through}");
                }
                run._checkpoints[Str(payload, "checkpoint_id")!] = payload.Clone();
                // v1.5.0: the chains a checkpoint removed continue from its heads.
                if (authentic && IsObject(payload, "resource_heads", out var heads))
                {
                    foreach (var head in heads.EnumerateObject())
                    {
                        var sequence = PayloadInteger(head.Value, "resource_sequence") ?? 0;
                        if (!run._resourceHeads.TryGetValue(head.Name, out var held) || held.Sequence < sequence)
                            run._resourceHeads[head.Name] = (sequence, Str(head.Value, "record_digest")!);
                    }
                }
            }
        }
    }

    /// <summary>The digest a record links to: <see cref="ExecutionDigest"/> of the record as received.</summary>
    private static string ExecutionDigestOf(JsonElement record) => Sha256Hex(ReceivedCanonical("ExecutionEvidence", record));

    /// <summary>
    /// An integer in a payload, as JavaScript reads one (TypeScript <c>Number.isSafeInteger</c>): a number with no
    /// fraction within 2^53 - 1, also when written <c>7.0</c>; null otherwise.
    /// </summary>
    private static long? PayloadInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsInteger(d) && Math.Abs(d) <= MaxSafeInteger
            ? (long)d
            : null;

    private static long? PayloadInteger(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? PayloadInteger(v) : null;

    /// <summary>
    /// The record shapes the reference reads, as TypeScript checks them (<c>validation.ts</c>): a field is absent,
    /// null, or of its type; a decision's or record's unknown fields are the field registry's to judge.
    /// </summary>
    private static class Shapes
    {
        private delegate bool Check(JsonElement? value);

        private static readonly Check Text = v => v?.ValueKind == JsonValueKind.String;
        private static readonly Check Flag = v => v?.ValueKind is JsonValueKind.True or JsonValueKind.False;
        private static readonly Check Positive = v => v is { } e && PayloadInteger(e) > 0;
        private static readonly Check NonNegative = v => v is { } e && PayloadInteger(e) >= 0;
        private static readonly Check AnyObject = v => v?.ValueKind == JsonValueKind.Object;
        private static readonly Check Time = v => v?.ValueKind == JsonValueKind.String && IsTimestamp(v.Value.GetString()!);

        private static Check Nullable(Check check) => v => v?.ValueKind == JsonValueKind.Null || check(v);
        private static Check Optional(Check check) => v => v is null || check(v);

        /// <summary>A field the reference always writes, as null without a value: left out, it still reads as null.</summary>
        private static Check Written(Check check) => Optional(Nullable(check));

        private static Check ListOf(Check check) =>
            v => v?.ValueKind == JsonValueKind.Array && v.Value.EnumerateArray().All(item => check(item));

        private static Check OneOf(params string[] values) =>
            v => v?.ValueKind == JsonValueKind.String && values.Contains(v.Value.GetString(), StringComparer.Ordinal);

        private static Check MapOf(Check check) =>
            v => v?.ValueKind == JsonValueKind.Object && v.Value.EnumerateObject().All(p => check(p.Value));

        private static Check Shape((string Name, Check Check)[] fields, bool exact = false) => v =>
            v is { ValueKind: JsonValueKind.Object } o
            && fields.All(f => f.Check(o.TryGetProperty(f.Name, out var value) ? value : null))
            && (!exact || o.EnumerateObject().All(p => fields.Any(f => f.Name == p.Name)));

        private static readonly Check Signature = Nullable(Shape([("key_id", Text), ("sig", Text)]));
        private static readonly Check GateResult = Shape([("gate_name", Text), ("passed", Flag), ("detail", Text)]);
        private static readonly Check AppliedPolicy = Shape([("policy_id", Text), ("version", Positive), ("policy_digest", Text), ("signed_by", Text)]);
        private static readonly Check PolicyGate = Shape(
        [
            ("policy_id", Text), ("policy_version", Positive), ("gate_id", Text), ("gate_type", Text), ("order", NonNegative),
            ("mode", OneOf("observe", "enforce")), ("passed", Flag),
            ("outcome", OneOf("pass", "fail", "missing_context", "invalid_context", "gate_error")),
        ]);
        private static readonly Check PolicyBinding = Shape(
        [
            ("policies", ListOf(AppliedPolicy)), ("policy_set_digest", Text), ("gate_evaluations", ListOf(PolicyGate)),
            ("context_digest", Text), ("registry_gate_types", ListOf(Text)), ("resolution_status", OneOf("resolved", "failed")),
            ("resolution_failure", Written(Text)),
        ]);
        private static readonly Check AttestationBinding = Shape(
        [
            ("attestation_id", Text), ("subject_id", Written(Text)), ("issuer_sovereign_id", Written(Text)),
            ("attestation_digest", Written(Text)), ("revocation_seq_checked", NonNegative),
        ]);
        private static readonly Check FreshnessProof = Shape(
        [
            ("proof_id", Text), ("feed_sovereign_id", Text), ("feed_sequence", NonNegative), ("feed_digest", Text),
            ("attested_at", Time), ("proof_valid_until", Time), ("issuer_sovereign_id", Text), ("signature", Optional(Signature)),
        ]);
        private static readonly Check DecisionShape = Shape(
        [
            ("decision_id", Text), ("context_id", Text), ("agreement_id", Text), ("authorized", Flag),
            // Absent is read as absent, so a decision whose signature covers a null fails at the signature.
            ("denial_reason", Optional(Nullable(Text))), ("gate_results", ListOf(GateResult)), ("decision_made_at", Time),
            ("decision_valid_until", Time), ("operator_sovereign_id", Text), ("freshness_proof", Optional(Nullable(FreshnessProof))),
            ("policy_binding", Optional(Nullable(PolicyBinding))), ("attestation_binding", Optional(Nullable(AttestationBinding))),
            ("signature", Optional(Signature)),
        ]);
        private static readonly Check ContextShape = Shape(
        [
            ("context_id", Text), ("agreement_id", Text), ("parent_kind", Text), ("requester_sovereign_id", Text),
            ("provider_sovereign_id", Text), ("requested_capability", Text), ("request_parameters", AnyObject),
            ("requested_at", Time), ("context_freshness_seq", NonNegative), ("attributes", AnyObject),
            ("attestation_id", Optional(Nullable(Text))),
        ]);
        private static readonly Check ResourceIdShape =
            v => v?.ValueKind == JsonValueKind.String && v.Value.GetString() is { Length: > 0 } s && s.EnumerateRunes().Count() <= 256;
        private static readonly Check ExecutionShape = Shape(
        [
            ("evidence_id", Text), ("sequence_no", Positive), ("decision_id", Text), ("context_id", Text), ("agreement_id", Text),
            ("executor_sovereign_id", Text), ("executed_capability", Text), ("execution_parameters", AnyObject),
            ("executed_at", Time), ("outcome", Text), ("outcome_detail", Written(Text)), ("prev_evidence_digest", Written(Text)),
            ("resource_id", Optional(Nullable(ResourceIdShape))),
            ("resource_action", Optional(Nullable(OneOf("create", "rotate", "revoke", "update", "delete")))),
            ("resource_sequence", Optional(Nullable(Positive))), ("prev_resource_digest", Optional(Nullable(Text))),
            ("signature", Optional(Signature)),
        ]);
        private static readonly Check GateTraceEntry = Shape(
        [
            ("gate_name", Text), ("gate_type", Text), ("evaluated_at", Time), ("inputs", AnyObject), ("result", Flag),
            ("reason", Text), ("metadata", AnyObject),
        ]);
        private static readonly Check JustificationShape = Shape(
        [
            ("proof_id", Text), ("decision_id", Text), ("proof_issued_at", Time), ("issuer_sovereign_id", Text),
            ("signature", Optional(Signature)),
            ("trace", Shape(
            [
                ("trace_id", Text), ("decision_id", Text), ("agreement_id", Text), ("operator_sovereign_id", Text),
                ("traced_at", Time), ("entries", ListOf(GateTraceEntry)), ("short_circuited_at", Written(Text)),
                ("final_authorized", Flag),
            ])),
        ]);
        private static readonly Check CheckpointShape = Shape(
        [
            ("checkpoint_id", Text), ("created_at", Time), ("cutoff", Time), ("removed_through_sequence", NonNegative),
            ("last_removed_entry_digest", Text), ("removed_count", NonNegative), ("previous_checkpoint_id", Written(Text)),
            ("issued_by", Text), ("signature", Optional(Signature)),
            ("resource_heads", MapOf(Shape([("resource_sequence", Positive), ("record_digest", Text)], exact: true))),
            ("observation_heads", Optional(Nullable(MapOf(NonNegative)))),
        ]);

        public static bool Decision(JsonElement value) => DecisionShape(value);
        public static bool Context(JsonElement value) => ContextShape(value);
        public static bool Execution(JsonElement value) => ExecutionShape(value);
        public static bool Justification(JsonElement value) => JustificationShape(value);
        public static bool Checkpoint(JsonElement value) => CheckpointShape(value);
    }
}
