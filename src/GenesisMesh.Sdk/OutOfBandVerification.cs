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
        // v1.4.0
        ("remediation_id", "RemediationRecord", Shapes.Remediation),
        ("review_id", "ReviewRecord", Shapes.Review),
    ];

    /// <summary>
    /// The signed form of a record of a change outside the governed path (v1.3.0; 1.9.0 in this SDK): an observation, a
    /// break-glass record, a judgement, a quarantine record or a registry record; v1.4.0 a remediation or review record.
    /// Every field but the signature, absent optional fields (null included) left out, as canonical JSON. The forms are
    /// frozen from 1.3.0 on and match the Python reference byte for byte.
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
    /// <c>break_glass_id</c>, <c>judgement_id</c>, <c>quarantine_id</c>, <c>registry_record_id</c>, and (v1.4.0)
    /// <c>remediation_id</c> or <c>review_id</c>; any other record is false.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="publicKeys">
    /// The base64 Ed25519 public keys that may have signed it: the observer's for an observation, the executor's for a
    /// break-glass or remediation record, the NA's for the others.
    /// </param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyOutOfBandRecord(string recordJson, IReadOnlyList<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        return OutOfBandSigned(recordJson, (_, _) => publicKeys);
    }

    /// <summary>
    /// As <see cref="VerifyOutOfBandRecord(string, IReadOnlyList{string})"/>, under the NA's verified key history: a
    /// judgement, quarantine record, registry record or review by the key valid at its signed time (<c>judged_at</c>,
    /// <c>quarantined_at</c>, <c>effective_at</c>, <c>decided_at</c>); an observation, break-glass record or remediation
    /// record, which the NA does not sign, by any key of the history.
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

    /// <summary>
    /// True when a remediation record (v1.4.0; 1.9.0 in this SDK) is well formed, its steps consistent with its outcome
    /// and versions, signed over its signed form by one of <paramref name="executorKeys"/>, and in the reference's form
    /// with no field this SDK does not know. Whether the NA would admit it from a registered key is
    /// <see cref="RemediationRules.RecordProblem"/>.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="executorKeys">The base64 Ed25519 public keys that may have signed it: its executor's.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyRemediationRecord(string recordJson, IReadOnlyList<string> executorKeys)
    {
        ArgumentNullException.ThrowIfNull(executorKeys);
        return OutOfBandSigned(recordJson, (_, _) => executorKeys, "remediation_id");
    }

    /// <summary>
    /// True when a review record (v1.4.0; 1.9.0 in this SDK) is well formed, its fields consistent (a waiver's bounds, a
    /// requested review's requester), signed by one of the NA's keys, and in the reference's form with no field this SDK
    /// does not know. Whether its embedded requests prove it is <see cref="RemediationRules.ReviewApprovalProblem"/>; whether
    /// a second holder decided it is <see cref="RemediationRules.ReviewHolderProblem"/>.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="naPublicKeys">The NA's base64 Ed25519 public keys.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyReviewRecord(string recordJson, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        return OutOfBandSigned(recordJson, (_, _) => naPublicKeys, "review_id");
    }

    /// <summary>
    /// As <see cref="VerifyReviewRecord(string, IReadOnlyList{string})"/>, under the NA's verified key history: by the
    /// key valid at its <c>decided_at</c>.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="keyHistory">The NA's verified key history (<see cref="VerifyKeyHistory(string, string?, string)"/>).</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static bool VerifyReviewRecord(string recordJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        return OutOfBandSigned(recordJson,
            (_, record) => keyHistory.KeysAt(Instant(Micros(record.GetProperty("decided_at").GetString()!))), "review_id");
    }

    /// <summary>Why a remediation record's steps do not fit its outcome and versions, or null (the reference model's rule).</summary>
    internal static string? RemediationStepsProblem(JsonElement record) => Shapes.StepsProblem(record);

    /// <summary>Why a review record's fields do not fit together, or null (the reference model's rule).</summary>
    internal static string? ReviewConsistencyProblem(JsonElement record) => Shapes.ReviewProblem(record);

    /// <summary>A timestamp in the shape the records' times take, in microseconds; null for anything else.</summary>
    internal static long? TimestampMicros(string value) => TryReadTimestamp(value, out var micros) ? micros : null;

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

    /// <summary>
    /// The record read as the first kind whose ID field it has, as TypeScript reads it; with <paramref name="only"/>,
    /// false unless that kind's ID field is <paramref name="only"/>.
    /// </summary>
    private static bool OutOfBandSigned(string json, Func<string, JsonElement, IReadOnlyList<string>> keys, string? only = null)
    {
        using var doc = StrictJsonReader.Parse(json);
        var record = doc.RootElement;
        if (record.ValueKind != JsonValueKind.Object) return false;
        foreach (var (idField, model, shape) in OutOfBandKinds)
        {
            if (!record.TryGetProperty(idField, out _)) continue;
            if (only is not null && idField != only) return false;
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

        // v1.5.0: a key succession and an archive record, as the reference reads them in a store.
        private static readonly Check Signatures = ListOf(Shape([("key_id", Text), ("sig", Text)]));

        private static readonly Check KeySuccessionFields = Shape(
        [
            ("succession_id", Filled(Bounded(128))), ("sovereign_id", Bounded(256)),
            ("key_sequence", v => v is { } e && PayloadInteger(e) >= 2), ("previous_key_id", Bounded(128)),
            ("previous_public_key", Bounded(128)), ("previous_succession_digest", Absent(Sha256)), ("new_key_id", Bounded(128)),
            ("new_public_key", Bounded(128)), ("effective_at", Utc), ("new_valid_to", Utc), ("issued_at", Utc),
            ("signatures", Filled(Signatures)),
        ], exact: true);

        private static readonly Check ArchiveFields = Shape(
        [
            ("archive_id", Filled(Bounded(128))), ("pack_id", Bounded(128)), ("from_sequence", Positive), ("through_sequence", Positive),
            ("through_entry_digest", Sha256), ("end_anchor_sequence", Positive), ("manifest_sha256", Sha256), ("location", Bounded(1024)),
            ("archived_at", Utc), ("recorded_by", Absent(Text)), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ], exact: true);

        // v1.2.0: a store anchor, as the reference reads one (no field it does not have).
        private static readonly Check AnchorFields = Shape(
        [
            ("anchor_sequence", Positive), ("sovereign_id", Text), ("store_sequence", Positive), ("entry_digest", Text),
            ("anchored_at", Utc), ("previous_anchor_digest", Absent(Text)), ("issued_by", Text), ("signature", Optional(Signature)),
        ], exact: true);

        public static bool Anchor(JsonElement value) => AnchorFields(value);
        public static bool KeySuccession(JsonElement value) => KeySuccessionFields(value);
        public static bool Archive(JsonElement value) => ArchiveFields(value);

        public static bool BreakGlass(JsonElement value) => BreakGlassFields(value);
        public static bool Judgement(JsonElement value) => JudgementFields(value);
        public static bool Quarantine(JsonElement value) => QuarantineFields(value);
        public static bool Registry(JsonElement value) => RegistryFields(value);

        // v1.4.0: remediations and reviews of changes governed after the fact.

        /// <summary>An operator's signed admin request, kept whole: its fields and no other.</summary>
        private static readonly Check AdminApprovalFields = Shape(
        [
            ("key_id", Bounded(256)), ("public_key", Bounded(256)), ("method", Bounded(16)), ("path", Bounded(2048)),
            ("query", Filled(MapOf(ListOf(Text)))), ("audience", Bounded(256)), ("body", Filled(AnyObject)),
            ("timestamp", Bounded(256)), ("nonce", Bounded(256)), ("admin_signature", Bounded(256)),
        ], exact: true);

        private static readonly Check RemediationFields = Shape(
        [
            ("remediation_id", Bounded(128)), ("executor_sovereign_id", Bounded(256)), ("subject_kind", OneOf("judgement", "decision")),
            ("subject_id", Bounded(128)), ("change_kind", OneOf("observation", "break_glass", "execution")), ("change_id", Bounded(128)),
            ("resource_id", Bounded(256)), ("outcome", OneOf("cancelled", "alerted")),
            ("steps", OneOf("rotate_forward_then_disable", "disable", "delete", "none")), ("rotation_evidence_id", Absent(Bounded(128))),
            ("version_id", Absent(Bounded(256))), ("disabled_version_id", Absent(Bounded(256))), ("remediated_at", Utc),
            ("detail", Absent(Bounded(1024))), ("signature", Optional(Signature)),
        ]);

        private static readonly Check ReviewFields = Shape(
        [
            ("review_id", Bounded(128)), ("subject_kind", OneOf("judgement", "decision")), ("subject_id", Bounded(128)),
            ("subject_digest", Sha256), ("resource_id", Absent(Bounded(256))), ("capability", Absent(Bounded(256))),
            ("opened_for", OneOf("requested", "flagged_for_review", "review_required")), ("requested_by", Absent(Bounded(256))),
            ("requester_holder", Absent(Bounded(128))), ("justification", Absent(Bounded(1024))),
            ("request_approval", Absent(AdminApprovalFields)), ("requested_at", Utc), ("actor", Absent(Bounded(256))),
            ("actor_holder", Absent(Bounded(128))), ("verdict", OneOf("uphold", "deny", "waive_until")), ("waive_until", Absent(Utc)),
            ("note", Absent(Bounded(1024))), ("decided_by", Bounded(256)), ("decider_holder", Bounded(128)),
            ("approval", AdminApprovalFields), ("decided_at", Utc), ("issuer_sovereign_id", Text), ("issued_by", Text),
            ("signature", Optional(Signature)),
        ]);

        /// <summary>What a requested review names, and one the NA opened does not.</summary>
        private static readonly string[] RequesterFields = ["requested_by", "requester_holder", "justification", "request_approval"];

        /// <summary>The longest a waiver may run, whatever the NA's configuration: 90 days, in microseconds.</summary>
        private const long MaxWaiverMicros = 90L * 24 * 3600 * 1_000_000;

        /// <summary>A remediation record in the shape the reference reads, its steps consistent.</summary>
        public static bool Remediation(JsonElement value) => RemediationFields(value) && StepsProblem(value) is null;

        /// <summary>A review record in the shape the reference reads, its fields consistent.</summary>
        public static bool Review(JsonElement value) => ReviewFields(value) && ReviewProblem(value) is null;

        private static bool Given(JsonElement record, string key) =>
            record.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;

        private static bool Is(JsonElement record, string key, string value) =>
            record.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == value;

        /// <summary>Two values equal as TypeScript's <c>===</c> compares JSON values: scalars by value, never two objects or arrays.</summary>
        private static bool SameValue(JsonElement a, JsonElement b) => (a.ValueKind, b.ValueKind) switch
        {
            (JsonValueKind.String, JsonValueKind.String) => a.GetString() == b.GetString(),
            (JsonValueKind.Number, JsonValueKind.Number) => a.GetDouble() == b.GetDouble(),
            (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) => true,
            _ => false,
        };

        /// <summary>
        /// Why a remediation record's steps do not match its outcome and versions, or null (the reference model's rule):
        /// it alerts exactly when nothing changed (<c>none</c>); a forward rotation names its evidence, the version it
        /// issued and another one it disabled; nothing else names rotation evidence or a new version; an alert disables
        /// nothing.
        /// </summary>
        public static string? StepsProblem(JsonElement record)
        {
            if (Is(record, "outcome", "alerted") != Is(record, "steps", "none"))
                return "an alerted remediation changes nothing (steps none); a cancelled one does";
            if (Is(record, "steps", "rotate_forward_then_disable"))
            {
                if (!Given(record, "rotation_evidence_id") || !Given(record, "version_id") || !Given(record, "disabled_version_id"))
                    return "a forward rotation names its evidence, the version it issued and the one disabled";
                if (SameValue(record.GetProperty("version_id"), record.GetProperty("disabled_version_id")))
                    return "the forward rotation issues another version than the one disabled";
            }
            else if (Given(record, "rotation_evidence_id") || Given(record, "version_id"))
            {
                return "only a forward rotation names rotation evidence or a new version";
            }
            if (Is(record, "steps", "none") && Given(record, "disabled_version_id")) return "an alerted remediation disables nothing";
            return null;
        }

        /// <summary>
        /// Why a review's fields do not fit together, or null (the reference model's rule): <c>waive_until</c> is given
        /// exactly for a waiver, after the decision and within 90 days of it; a requested review names its requester,
        /// holder, justification and request, and one the NA opened none of them.
        /// </summary>
        public static string? ReviewProblem(JsonElement record)
        {
            if (Is(record, "verdict", "waive_until") != Given(record, "waive_until")) return "waive_until is given exactly for a waiver";
            if (Given(record, "waive_until"))
            {
                // A time that is not one fails the bounds, as a NaN comparison does in TypeScript.
                long? At(string key) => record.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? TimestampMicros(v.GetString()!) : null;
                if (!(At("decided_at") is { } decided && At("waive_until") is { } until && decided < until && until <= decided + MaxWaiverMicros))
                    return "a waiver ends after it is decided and within the longest waiver";
            }
            var requested = Is(record, "opened_for", "requested");
            var named = RequesterFields.Select(k => Given(record, k)).ToList();
            if (requested && named.Contains(false)) return "a requested review names its requester, holder, justification and request";
            if (!requested && named.Contains(true)) return "a review the NA opened has no requester";
            return null;
        }
    }
}
