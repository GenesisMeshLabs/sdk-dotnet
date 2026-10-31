using System.Text.Json;

namespace GenesisMesh;

public static partial class OfflineVerifier
{
    // ── changes outside the governed path (1.9.0) ──────────────────────────

    /// <summary>
    /// The records of changes outside the governed path, by the field that names each kind, in the order a record's
    /// kind is read: the first of these fields the record has.
    /// </summary>
    private static readonly (string IdField, string Model, Func<JsonElement, bool> Shape)[] OutOfBandKinds =
    [
        ("observation_id", "ObservationRecord", Shapes.Observation),
        ("break_glass_id", "BreakGlassRecord", Shapes.BreakGlass),
        ("judgement_id", "JudgementRecord", Shapes.Judgement),
        ("quarantine_id", "QuarantineRecord", Shapes.Quarantine),
        ("registry_record_id", "RegistryRecord", Shapes.Registry),
    ];

    /// <summary>
    /// The signed form of a record of a change outside the governed path (v1.3.0; 1.9.0 in this SDK): an observation, a
    /// break-glass record, a judgement, a quarantine record or a registry record. Every field but the signature, absent
    /// optional fields (null included) left out, as canonical JSON. The forms are frozen from 1.3.0 on and match the
    /// Python reference byte for byte.
    /// </summary>
    /// <param name="recordJson">The record JSON.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="recordJson"/> is not an object.</exception>
    public static string OutOfBandCanonical(string recordJson)
    {
        using var doc = StrictJsonReader.Parse(recordJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("genesismesh: a record must be a JSON object", nameof(recordJson));
        return OutOfBandCanonical(doc.RootElement);
    }

    /// <summary>SHA-256 hex of the signed form (<see cref="OutOfBandCanonical(string)"/>).</summary>
    /// <param name="recordJson">The record JSON.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    /// <exception cref="ArgumentException"><paramref name="recordJson"/> is not an object.</exception>
    public static string OutOfBandDigest(string recordJson) => Sha256Hex(OutOfBandCanonical(recordJson));

    /// <summary>
    /// True when a record of a change outside the governed path is well formed, signed over its signed form
    /// (<see cref="OutOfBandCanonical(string)"/>) by one of <paramref name="publicKeys"/>, and in the reference's form
    /// with no field this SDK does not know. Its kind is read from its ID field: <c>observation_id</c>,
    /// <c>break_glass_id</c>, <c>judgement_id</c>, <c>quarantine_id</c> or <c>registry_record_id</c>; any other record
    /// is false (remediation and review records from a later release of this SDK).
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="publicKeys">
    /// The base64 Ed25519 public keys that may have signed it: the observer's for an observation, the executor's for a
    /// break-glass record, the NA's for the others.
    /// </param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyOutOfBandRecord(string recordJson, IReadOnlyList<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        return OutOfBandSigned(recordJson, (_, _) => publicKeys);
    }

    /// <summary>
    /// As <see cref="VerifyOutOfBandRecord(string, IReadOnlyList{string})"/>, under the NA's verified key history: a
    /// judgement, quarantine record or registry record by the key valid at its signed time (<c>judged_at</c>,
    /// <c>quarantined_at</c>, <c>effective_at</c>); an observation or break-glass record, which the NA does not sign, by
    /// any key of the history.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="keyHistory">The NA's verified key history (<see cref="VerifyKeyHistory(string, string?, string)"/>).</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyOutOfBandRecord(string recordJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        // The keys are read once the record matches its shape, so its signed time is a timestamp.
        return OutOfBandSigned(recordJson, (model, record) =>
            NAKeyHistory.RecordTimeFields.TryGetValue(model, out var field)
                ? keyHistory.KeysAt(Instant(Micros(record.GetProperty(field).GetString()!)))
                : keyHistory.AllKeys());
    }

    /// <summary>The signed form of a record outside the governed path: every field but the signature, nulls left out.</summary>
    internal static string OutOfBandCanonical(JsonElement record) =>
        Canonical.Of(record, ["signature"], record.EnumerateObject().Select(p => p.Name).ToList());

    /// <summary>True when the record matches its model's shape, as the reference reads it (TypeScript <c>validation.ts</c>).</summary>
    internal static bool ValidOutOfBand(string model, JsonElement record) => OutOfBandKinds.Single(k => k.Model == model).Shape(record);

    /// <summary>A timestamp the shapes accepted, in microseconds.</summary>
    private static long Micros(string timestamp)
    {
        TryReadTimestamp(timestamp, out var micros);
        return micros;
    }

    private static bool OutOfBandSigned(string json, Func<string, JsonElement, IReadOnlyList<string>> keys)
    {
        using var doc = StrictJsonReader.Parse(json);
        var record = doc.RootElement;
        if (record.ValueKind != JsonValueKind.Object) return false;
        foreach (var (idField, model, shape) in OutOfBandKinds)
        {
            if (!record.TryGetProperty(idField, out _)) continue;
            return shape(record)
                && record.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object
                && Str(signature, "sig") is { } sig
                && VerifyEd25519(OutOfBandCanonical(record), sig, keys(model, record))
                && StrictFields.UnknownFields(model, record).Count == 0
                && StrictFields.NonCanonicalFields(model, record).Count == 0;
        }
        return false;
    }

    private static partial class Shapes
    {
        /// <summary>A string of <paramref name="min"/> to <paramref name="max"/> characters, counted as the reference counts them (code points).</summary>
        private static Check Bounded(int max, int min = 1) =>
            v => v?.ValueKind == JsonValueKind.String && v.Value.GetString()!.EnumerateRunes().Count() is var n && n >= min && n <= max;

        /// <summary>An optional field: left out of the signed form when absent; a null reads as absent.</summary>
        private static Check Absent(Check check) => Optional(Nullable(check));

        /// <summary>A field the reference fills when absent: left out, the record is not in its form (<c>non_canonical_form</c>).</summary>
        private static Check Filled(Check check) => Optional(check);

        /// <summary>A timestamp in UTC, as the reference keeps the 1.3.0 records' times.</summary>
        private static readonly Check Utc = v => Time(v) && v!.Value.GetString()! is var s
            && (s.EndsWith('Z') || s.EndsWith("+00:00", StringComparison.Ordinal) || s.EndsWith("-00:00", StringComparison.Ordinal));

        private static readonly Check Sha256 = Bounded(64, 64);
        private static readonly Check ActionValue = OneOf("create", "rotate", "revoke", "update", "delete");
        private static readonly Check VerdictValue = OneOf("allow", "deny", "indeterminate");

        private static readonly Check ObservationFields = Shape(
        [
            ("observation_id", Bounded(128)), ("observer_sovereign_id", Bounded(256)), ("resource_id", Bounded(256)),
            ("action", ActionValue), ("capability", Bounded(256)), ("changed_at", Absent(Utc)), ("changed_not_before", Absent(Utc)),
            ("changed_not_after", Absent(Utc)), ("observed_at", Utc), ("actor", Absent(Bounded(256))), ("source", Bounded(128)),
            ("source_event_id", Bounded(256)), ("version_id", Absent(Bounded(256))), ("metadata", Filled(AnyObject)),
            ("signature", Optional(Signature)),
        ]);

        private static readonly Check BreakGlassFields = Shape(
        [
            ("break_glass_id", Bounded(128)), ("executor_sovereign_id", Bounded(256)), ("resource_id", Bounded(256)),
            ("resource_action", ActionValue), ("capability", Bounded(256)), ("attestation_id", Absent(Bounded(128))),
            ("request_parameters", Filled(AnyObject)), ("attributes", Filled(AnyObject)), ("justification", Bounded(1024)),
            ("evaluation_request_digest", Sha256),
            ("evaluation_failure", OneOf("network_error", "timeout", "server_error", "rate_limited")), ("executed_at", Utc),
            ("outcome", Text), ("outcome_detail", Absent(Bounded(1024, 0))), ("execution_parameters", Filled(AnyObject)),
            ("signature", Optional(Signature)),
        ]);

        private static readonly Check JudgementFields = Shape(
        [
            ("judgement_id", Bounded(128)), ("subject_kind", OneOf("observation", "break_glass")), ("subject_id", Bounded(128)),
            ("subject_digest", Sha256), ("subject_store_sequence", Positive), ("resource_id", Bounded(256)), ("action", ActionValue),
            ("capability", Bounded(256)),
            // v1.6.0: grant, a change matched to a grant use.
            ("governed_by", OneOf("prior_decision", "after_the_fact", "grant")), ("verdict", VerdictValue),
            ("reason", Absent(Bounded(1024, 0))), ("evaluated_as_of", Utc), ("evaluated_from", Absent(Utc)),
            ("policy_binding", Absent(PolicyBinding)), ("gate_results", Filled(ListOf(GateResult))),
            ("current_verdict", Absent(VerdictValue)), ("current_policy_set_digest", Absent(Text)), ("flagged_for_review", Absent(Flag)),
            ("matched_evidence_id", Absent(Text)), ("matched_decision_id", Absent(Text)), ("possible_match_evidence_id", Absent(Text)),
            ("judged_at", Utc), ("issuer_sovereign_id", Text), ("issued_by", Text), ("signature", Optional(Signature)),
        ]);

        private static readonly Check QuarantineFields = Shape(
        [
            // v1.4.0: a refused remediation record is kept too; v1.6.0: a refused grant use.
            ("quarantine_id", Bounded(128)),
            ("record_kind", OneOf("execution", "observation", "break_glass", "remediation", "grant_execution")),
            ("record", AnyObject), ("record_digest", Sha256), ("rejection_code", Bounded(128)), ("detail", Filled(Bounded(1024, 0))),
            ("resource_id", Absent(Bounded(256))), ("quarantined_at", Utc), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ]);

        private static readonly Check RegistryFields = Shape(
        [
            ("registry_record_id", Bounded(128)),
            ("event", OneOf("policy_history_started", "policy_activated", "policy_deactivated", "executor_key_registered",
                "executor_key_retired", "operator_key_holder")),
            ("effective_at", Utc), ("reconstructed", Absent(Flag)), ("policy_id", Absent(Text)), ("policy_version", Absent(Positive)),
            ("policy_digest", Absent(Text)), ("key_id", Absent(Text)), ("public_key", Absent(Text)),
            ("executor_sovereign_id", Absent(Text)), ("key_role", Absent(OneOf("executor", "observer"))),
            ("resource_prefix", Absent(Text)), ("operator_tier", Absent(Text)), ("holder", Absent(Text)),
            ("approved_by", Absent(Text)), ("recorded_by", Absent(Text)), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ]);

        /// <summary>An observation names its change time, or both ends of a window in order, never both.</summary>
        public static bool Observation(JsonElement value)
        {
            if (!ObservationFields(value)) return false;
            bool Given(string key) => value.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;
            if (Given("changed_at")) return !Given("changed_not_before") && !Given("changed_not_after");
            if (!Given("changed_not_before") || !Given("changed_not_after")) return false;
            // To the microsecond, as the reference compares them.
            return Micros(value.GetProperty("changed_not_before").GetString()!) <= Micros(value.GetProperty("changed_not_after").GetString()!);
        }

        public static bool BreakGlass(JsonElement value) => BreakGlassFields(value);
        public static bool Judgement(JsonElement value) => JudgementFields(value);
        public static bool Quarantine(JsonElement value) => QuarantineFields(value);
        public static bool Registry(JsonElement value) => RegistryFields(value);
    }
}
