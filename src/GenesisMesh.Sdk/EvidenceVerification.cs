using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenesisMesh;

/// <summary>Options for <see cref="OfflineVerifier.VerifyEvidenceEvents"/>.</summary>
public sealed class EvidenceVerifyOptions
{
    /// <summary>
    /// The NA's keys (base64 Ed25519), valid at every time: they sign decisions, justification proofs, retention
    /// checkpoints, and judgement, quarantine, registry and review records; a review's embedded requests name one of
    /// them as their audience. Or give <see cref="KeyHistory"/>.
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
    /// The executor and observer keys records are signed with, retired ones included
    /// (<see cref="EvidenceStoreClient.ListExecutorKeys"/>): a retired key still verifies the records it signed.
    /// </summary>
    public IReadOnlyList<ExecutorKeyRecord> ExecutorKeys { get; init; } = [];

    /// <summary>
    /// True (the default) for an unbroken run of the store: an export, whole or from a position
    /// (<see cref="EvidenceStoreClient.ExportAll"/> with a cursor). False for a history (<see cref="ResourceHistory"/>,
    /// <see cref="VendorHistory"/>), whose store links are checked only between adjacent positions and whose record times
    /// are not compared. A run that starts at neither entry 1 nor right after the <see cref="Checkpoint"/> takes the first
    /// record it holds of a resource as that chain's start.
    /// </summary>
    public bool Contiguous { get; init; } = true;

    /// <summary>
    /// The retention checkpoint JSON as received, for a run that starts after the entries it removed: the run's first
    /// entry continues from its last removed entry, and each resource and observation chain from its head. It must be a checkpoint signed
    /// by the NA (with <see cref="KeyHistory"/>, by the key valid at its <c>created_at</c>; <c>invalid_signature</c>), with
    /// no field this SDK does not know (<c>unknown_field</c>); otherwise nothing of the run is checked.
    /// </summary>
    public string? Checkpoint { get; init; }
}

public static partial class OfflineVerifier
{
    /// <summary>
    /// The entry kinds this SDK verifies (1.9.0): those a governed store holds, the records of changes outside the
    /// governed path, remediation and review records, and pre-issued grants, their uses and revocations. The store's
    /// other kinds (key successions and archives) fail as <c>unknown_entry_kind</c>, as a kind from a later release does;
    /// their envelopes still chain.
    /// </summary>
    internal static readonly string[] VerifiedEntryKinds =
    [
        "decision", "justification", "execution", "retention_checkpoint",
        "observation", "break_glass", "judgement", "quarantine", "registry",
        "remediation", "review",
        "grant", "grant_execution", "grant_revocation",
    ];

    /// <summary>
    /// The kinds the NA signs among the records it stores exactly as signed (v1.6.0: also grants and their revocations);
    /// an observer or executor key signs the others (a grant use: its grant's bearer key).
    /// </summary>
    private static readonly HashSet<string> NASignedOutOfBandKinds = new(StringComparer.Ordinal)
    {
        "judgement", "quarantine", "registry", "review", "grant", "grant_revocation",
    };

    /// <summary>v1.3.0: a kind whose record the store holds exactly as signed: a record of a change outside the governed path.</summary>
    private static bool IsOutOfBand(string kind) => EnvelopeFields.ContainsKey(kind);

    /// <summary>How far before an earlier entry of a run an entry may be recorded, in microseconds (v1.5.0, the reference's tolerance).</summary>
    private const long RecordTimeTolerance = 300_000_000;

    /// <summary>v1.5.0: the kinds retention appends again after its checkpoint, each with the time it was first recorded.</summary>
    private static readonly HashSet<string> CarriedKinds = new(StringComparer.Ordinal) { "registry", "key_succession", "archive" };

    /// <summary>v1.3.1: the field holding each kind's record ID, which appears once per kind in a run (<c>duplicate_record</c>).</summary>
    private static readonly Dictionary<string, string> RecordIdFields = new(StringComparer.Ordinal)
    {
        ["decision"] = "decision_id",
        ["justification"] = "proof_id",
        ["execution"] = "evidence_id",
        ["retention_checkpoint"] = "checkpoint_id",
        ["observation"] = "observation_id",
        ["break_glass"] = "break_glass_id",
        ["judgement"] = "judgement_id",
        ["quarantine"] = "quarantine_id",
        ["registry"] = "registry_record_id",
        ["remediation"] = "remediation_id",
        ["review"] = "review_id",
        ["grant"] = "grant_id",
        ["grant_execution"] = "use_id",
        ["grant_revocation"] = "revocation_id",
    };

    /// <summary>The model each kind's payload holds; a decision payload wraps two, its decision and its context.</summary>
    private static readonly Dictionary<string, string> PayloadModels = new(StringComparer.Ordinal)
    {
        ["justification"] = "JustificationProof",
        ["execution"] = "ExecutionEvidence",
        ["retention_checkpoint"] = "RetentionCheckpoint",
        ["observation"] = "ObservationRecord",
        ["break_glass"] = "BreakGlassRecord",
        ["judgement"] = "JudgementRecord",
        ["quarantine"] = "QuarantineRecord",
        ["registry"] = "RegistryRecord",
        ["remediation"] = "RemediationRecord",
        ["review"] = "ReviewRecord",
        ["grant"] = "CapabilityGrant",
        ["grant_execution"] = "GrantExecutionEvidence",
        ["grant_revocation"] = "GrantRevocationRecord",
    };

    /// <summary>
    /// v1.3.1: envelope field and record field, for the envelope fields the NA derives from a record of a change outside
    /// the governed path. The NA searches and matches on them, so each must name what the signed record says
    /// (<c>envelope_mismatch</c>); an absent field is null. The NA derives none from the kinds that came before.
    /// </summary>
    private static readonly Dictionary<string, (string Envelope, string Record)[]> EnvelopeFields = new(StringComparer.Ordinal)
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
        // v1.4.0
        ["remediation"] =
        [
            ("record_id", "remediation_id"), ("subject_id", "subject_id"), ("resource_id", "resource_id"),
            ("executor_sovereign_id", "executor_sovereign_id"), ("outcome", "outcome"),
        ],
        ["review"] =
        [
            ("record_id", "review_id"), ("subject_id", "subject_id"), ("resource_id", "resource_id"), ("capability", "capability"),
            ("outcome", "verdict"),
        ],
        // v1.6.0 (no new envelope field): a use's grant is its subject_id, its place in its chain its exec_sequence_no; its
        // resource_sequence is the NA's, outside the record.
        ["grant"] = [("record_id", "grant_id"), ("executor_sovereign_id", "bearer_sovereign_id")],
        ["grant_execution"] =
        [
            ("record_id", "use_id"), ("subject_id", "grant_id"), ("resource_id", "resource_id"), ("resource_action", "resource_action"),
            ("capability", "capability"), ("executor_sovereign_id", "executor_sovereign_id"), ("outcome", "outcome"),
            ("exec_sequence_no", "use_sequence"),
        ],
        ["grant_revocation"] = [("record_id", "revocation_id"), ("subject_id", "grant_id"), ("outcome", "reason")],
    };

    /// <summary>
    /// Verifies stored entries offline, as the reference's <c>verify_evidence_events</c> does: each envelope, its digest
    /// and its payload's, the store chain, every signature (the NA's on decisions, justification proofs, retention
    /// checkpoints, judgements, quarantine and registry records; the executor's on execution evidence and break-glass
    /// records; the observer's on observations), each execution record against its decision, and the decision, resource
    /// and observation chains. A judgement names its subject as stored, once, and a record it matches at most once; a
    /// quarantine entry holds the record it names. A remediation record (signed by an executor key of its sovereign) is
    /// the one of its judgement or decision, of the change, resource and version that judgement names, and cites a
    /// forward rotation of that resource by its own executor, made after the change, once; a review (signed by the NA)
    /// is proved by the requests it embeds, signed with the keys and holders the holder registry records, decided by a
    /// second holder, of its subject as stored, and no waiver follows a remediation (the holder registry is checked for an
    /// operator key when the run holds a holder record for it). A grant (v1.6.0, signed by the NA) is held for its uses
    /// and revocation; a use (signed by its grant's bearer key) continues its grant's use chain, names the grant the run
    /// holds and its digest, keeps the use rules (<see cref="GrantRules.UseProblem"/>) against what the run holds, and
    /// takes the next place of its resource's chain; a revocation names a grant held, once. Pass an export (<see cref="ParseExportLines(string)"/>,
    /// <see cref="EvidenceStoreClient.ExportAll"/>) or, with <see cref="EvidenceVerifyOptions.Contiguous"/> false, a
    /// history. Nothing is thrown for a bad entry, or an event built by hand that is not one: each finding is a failure,
    /// with the reason codes every implementation uses.
    /// </summary>
    /// <remarks>
    /// This SDK verifies the kinds a governed store holds (<c>decision</c>, <c>justification</c>, <c>execution</c>,
    /// <c>retention_checkpoint</c>) and, from 1.9.0, the records of changes outside the governed path
    /// (<c>observation</c>, <c>break_glass</c>, <c>judgement</c>, <c>quarantine</c>, <c>registry</c>), remediation
    /// and review records (<c>remediation</c>, <c>review</c>), and pre-issued grants (<c>grant</c>,
    /// <c>grant_execution</c>, <c>grant_revocation</c>). Any other kind fails as <c>unknown_entry_kind</c>; verify
    /// such a store with the TypeScript or Rust SDK, or the core's <c>genesis-mesh audit verify</c>. Anchors are not
    /// checked here, nor where the store placed an executor key (an audit pack's registry records).
    /// </remarks>
    /// <param name="events">The entries, in store order.</param>
    /// <param name="options">The keys, and what the run is.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="events"/>, <paramref name="options"/>, its <see cref="EvidenceVerifyOptions.NAPublicKeys"/> or its
    /// <see cref="EvidenceVerifyOptions.ExecutorKeys"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Both <see cref="EvidenceVerifyOptions.NAPublicKeys"/> and <see cref="EvidenceVerifyOptions.KeyHistory"/> are given, or
    /// <see cref="EvidenceVerifyOptions.ExecutorKeys"/> holds a null.
    /// </exception>
    /// <exception cref="StrictJsonException"><see cref="EvidenceVerifyOptions.Checkpoint"/> is not strict JSON.</exception>
    public static EvidenceStoreVerification VerifyEvidenceEvents(IEnumerable<EvidenceEvent> events, EvidenceVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.NAPublicKeys, nameof(options));
        ArgumentNullException.ThrowIfNull(options.ExecutorKeys, nameof(options));
        if (options.KeyHistory is not null && options.NAPublicKeys.Count > 0)
            throw new ArgumentException("pass NAPublicKeys or KeyHistory, not both", nameof(options));
        if (options.ExecutorKeys.Any(k => k is null))
            throw new ArgumentException("ExecutorKeys holds a null", nameof(options));
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
            Str(c, "created_at") is { } created && TryReadTimestamp(created, out var at) ? keyHistory.KeysAt(Instant(at)) : []);
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
        /// <summary>v1.3.0: the last observation position held per resource.</summary>
        private readonly Dictionary<string, long> _observationHeads = new(StringComparer.Ordinal);
        /// <summary>v1.3.1: the judgement subjects held, by kind and ID (<see cref="SubjectKey"/>): payload digest and position.</summary>
        private readonly Dictionary<string, (string Digest, long Sequence)> _subjects = new(StringComparer.Ordinal);
        /// <summary>The subjects judged, and the records a judgement matched.</summary>
        private readonly HashSet<string> _judged = new(StringComparer.Ordinal);
        private readonly HashSet<string> _matched = new(StringComparer.Ordinal);
        /// <summary>v1.3.0: the IDs of judgements and of changes outside the governed path, which no execution record rests on.</summary>
        private readonly HashSet<string> _notDecisions = new(StringComparer.Ordinal);
        /// <summary>v1.3.1: the records a judgement can match: execution evidence and break-glass records held so far.</summary>
        private readonly HashSet<string> _matchable = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: judgements and decisions by ID, as a remediation or review checks its subject.</summary>
        private readonly Dictionary<string, JudgedSubject> _judgedRecords = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: execution evidence by ID, as a remediation checks its change or rotation.</summary>
        private readonly Dictionary<string, HeldExecution> _executed = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: the subjects a remediation record was held for.</summary>
        private readonly HashSet<string> _remediated = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: the version each observation or break-glass record names (<see cref="SubjectKey"/>); null for none.</summary>
        private readonly Dictionary<string, string?> _versions = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: the forward rotations remediation records cite.</summary>
        private readonly HashSet<string> _cited = new(StringComparer.Ordinal);
        /// <summary>v1.4.0: each operator key's holder records the NA signed (<c>operator_key_holder</c>), in store order.</summary>
        private readonly Dictionary<string, List<HolderRecord>> _operatorKeys = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: the authentic grants held, by ID, with their digest as received.</summary>
        private readonly Dictionary<string, (CapabilityGrant Grant, string Digest)> _grantsHeld = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: when each grant was revoked: its first authentic revocation.</summary>
        private readonly Dictionary<string, string> _revoked = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: the head of each grant's use chain: an admitted use, or a quarantined one that continued it.</summary>
        private readonly Dictionary<string, UseChainHead> _chainHeads = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: each grant's last admitted use, as the order and time rules compare a use with it.</summary>
        private readonly Dictionary<string, PreviousUse> _lastAdmitted = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: how many uses of each grant were admitted, when the run holds its chain from its first use.</summary>
        private readonly Dictionary<string, int> _admittedUses = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: each chain use's digest and time, by grant and place (<see cref="UseKey"/>).</summary>
        private readonly Dictionary<string, TokenHead> _useHeads = new(StringComparer.Ordinal);
        /// <summary>v1.6.0: the IDs of uses, and of the other records a judgement matches by ID (execution evidence, break-glass records).</summary>
        private readonly HashSet<string> _useIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _otherMatchable = new(StringComparer.Ordinal);
        private long? _firstSequence;
        private bool _fromStart;
        private JsonElement? _previous;
        private long _previousSequence;
        private string _previousKind = "";
        /// <summary>v1.5.0: the latest <c>recorded_at</c> of the run so far (<see cref="TryReadTimestamp"/>).</summary>
        private long? _clock;
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
        }

        private IReadOnlyList<string> KeysAt(long at) => _history?.KeysAt(Instant(at)) ?? _naKeys;

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
            // The chains it removed records from continue from its heads, a position of 0 included.
            foreach (var head in cp.GetProperty("resource_heads").EnumerateObject())
                _resourceHeads[head.Name] = (PayloadInteger(head.Value, "resource_sequence")!.Value, Str(head.Value, "record_digest")!);
            if (IsObject(cp, "observation_heads", out var positions))
                foreach (var head in positions.EnumerateObject())
                    _observationHeads[head.Name] = PayloadInteger(head.Value) ?? 0;
            return true;
        }

        /// <summary>
        /// v1.5.0: the resource and observation chains a checkpoint of the run removed records from continue from its
        /// heads, where they are ahead of the heads held.
        /// </summary>
        private void SeedHeads(JsonElement cp)
        {
            if (IsObject(cp, "resource_heads", out var heads))
            {
                foreach (var head in heads.EnumerateObject())
                {
                    var sequence = PayloadInteger(head.Value, "resource_sequence") ?? 0;
                    if (!_resourceHeads.TryGetValue(head.Name, out var held) || held.Sequence < sequence)
                        _resourceHeads[head.Name] = (sequence, Str(head.Value, "record_digest")!);
                }
            }
            if (IsObject(cp, "observation_heads", out var positions))
            {
                foreach (var head in positions.EnumerateObject())
                {
                    var position = PayloadInteger(head.Value) ?? 0;
                    if (_observationHeads.GetValueOrDefault(head.Name) < position) _observationHeads[head.Name] = position;
                }
            }
        }

        /// <summary>v1.3.1: judgement subjects are keyed by kind and ID: a break-glass record may share an observation's ID.</summary>
        private static string SubjectKey(string kind, string id) => kind + "\0" + id;

        /// <summary>v1.6.0: a use of a grant, by grant and place in its chain.</summary>
        private static string UseKey(string grantId, int place) => grantId + "\0" + place.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// v1.6.0: a quarantined use (signed by its grant's bearer key, when the run holds the grant) that continues its
        /// grant's use chain is the chain's head: the next use may link to it.
        /// </summary>
        private void QuarantinedUse(JsonElement raw)
        {
            if (!Shapes.GrantUse(raw)) return;
            var use = ReadPayload<GrantExecutionEvidence>(raw)!;
            if (_grantsHeld.TryGetValue(use.GrantId, out var held)
                && (!IsObject(raw, "signature", out var sig) || Str(sig, "key_id") != held.Grant.BearerKeyId
                    || !OutOfBandSignedBy(raw, [held.Grant.BearerPublicKey])))
                return;
            var hasHead = _chainHeads.TryGetValue(use.GrantId, out var head);
            if (!hasHead && !_fromStart && use.UseSequence != 1) return;
            if (!GrantRules.ContinuesChain(use, hasHead ? head! : new UseChainHead(0, null))) return;
            var digest = GrantRecordDigest("GrantExecutionEvidence", raw);
            _chainHeads[use.GrantId] = new UseChainHead(use.UseSequence, digest);
            _useHeads[UseKey(use.GrantId, use.UseSequence)] = new TokenHead(digest, use.ExecutedAt);
            if (use.UseSequence == 1) _admittedUses.TryAdd(use.GrantId, 0);
        }

        /// <summary>
        /// The registered key that may sign an observation (an observer key), or execution evidence or a break-glass record
        /// (an executor key): of the record's sovereign, with the role its kind needs.
        /// </summary>
        private ExecutorKeyRecord? FittingKey(string kind, JsonElement record)
        {
            var observation = kind == "observation";
            return KeyNamed(record) is { } key
                   && key.ExecutorSovereignId == Str(record, observation ? "observer_sovereign_id" : "executor_sovereign_id")
                   && (key.Role ?? "executor") == (observation ? "observer" : "executor")
                ? key
                : null;
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
            long? notBefore = null;
            if (_contiguous)
            {
                var adjacent = _previous is not null && seq == _previousSequence + 1;
                _carrying = CarriedKinds.Contains(kind) && adjacent && (_previousKind == "retention_checkpoint" || _carrying);
                if (!_carrying)
                {
                    if (_clock is { } clock && recordedAt < clock - RecordTimeTolerance)
                        Fail(seq, "recorded_at_out_of_order",
                            $"recorded at {Canonical.Timestamp(Instant(recordedAt))}, after an entry recorded at {Canonical.Timestamp(Instant(clock))}");
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
                // v1.3.0: the reference stores the records of changes outside the governed path exactly as signed, and
                // (v1.5.0) a retention checkpoint: a field outside the signature is no part of one.
                if (kind == "retention_checkpoint" || IsOutOfBand(kind))
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
            new RecordCheck(this, seq, kind, recordedAt, notBefore, entry, payload, record).Check();
        }

        /// <summary>The event's envelope as received, or null when it is not one (TypeScript <c>validEvent</c>).</summary>
        private static JsonElement? Envelope(EvidenceEvent e)
        {
            if (e?.Entry is null || e.Schema != "gm.evidence.event" || e.SchemaVersion != 1 || e.EntryDigest is null
                || e.Payload.ValueKind != JsonValueKind.Object)
                return null;
            // A payload built by hand from JSON that is not strict (a duplicate key, 1e400, a lone surrogate) is no record.
            try
            {
                StrictJsonReader.Check(e.Payload.GetRawText());
            }
            catch (StrictJsonException)
            {
                return null;
            }
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
                // An observation or break-glass record: by the key the signature names, whatever its role, so that an
                // authentic record with a field this SDK does not know is refused by name first, as the reference refuses it.
                _ when IsOutOfBand(kind) => NASignedOutOfBandKinds.Contains(kind)
                    ? OutOfBandSignedBy(payload, _allNaKeys)
                    : KeyNamed(payload) is { } named && OutOfBandSignedBy(payload, [named.PublicKey]),
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
            "retention_checkpoint" => Shapes.Checkpoint(payload),
            "observation" => Shapes.Observation(payload),
            "break_glass" => Shapes.BreakGlass(payload),
            "judgement" => Shapes.Judgement(payload),
            "quarantine" => Shapes.Quarantine(payload),
            "remediation" => Shapes.Remediation(payload),
            "review" => Shapes.Review(payload),
            "grant" => Shapes.Grant(payload),
            "grant_execution" => Shapes.GrantUse(payload),
            "grant_revocation" => Shapes.GrantRevocation(payload),
            _ => Shapes.Registry(payload),
        };

        /// <summary>One entry's record, checked once its envelope and payload are.</summary>
        private sealed class RecordCheck(EventRun run, long seq, string kind, long recordedAt, long? notBefore,
            JsonElement entry, JsonElement payload, JsonElement record)
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
                // v1.3.1: the envelope of a record outside the governed path names what its record says.
                if (EnvelopeFields.TryGetValue(kind, out var fields))
                {
                    var wrong = fields.Where(f => EnvelopeValue(entry, f.Envelope) != EnvelopeValue(payload, f.Record)).Select(f => f.Envelope).ToList();
                    if (wrong.Count > 0) Fail("envelope_mismatch", $"{kind}: {string.Join(", ", wrong)}");
                }
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
                    case "retention_checkpoint":
                        RetentionCheckpoint();
                        break;
                    case "observation":
                    case "break_glass":
                        Change(recordId);
                        break;
                    case "judgement":
                        Judgement(recordId);
                        break;
                    case "quarantine":
                        Quarantine();
                        break;
                    case "remediation":
                        Remediation(recordId);
                        break;
                    case "review":
                        Review(recordId);
                        break;
                    case "grant":
                        Grant(recordId);
                        break;
                    case "grant_execution":
                        GrantUse(recordId);
                        break;
                    case "grant_revocation":
                        GrantRevocation();
                        break;
                    default:
                        Registry();
                        break;
                }
            }

            /// <summary>
            /// A registry record: the NA's, as its key history is. One the NA signed that names an operator key's holder
            /// (v1.4.0) is what a review's embedded requests are checked against.
            /// </summary>
            private void Registry()
            {
                var failures = Result.Failures.Count;
                NASignature(keys => OutOfBandSignedBy(record, keys), "RegistryRecord");
                if (Result.Failures.Count != failures || Str(payload, "event") != "operator_key_holder" || Str(payload, "key_id") is not { } keyId)
                    return;
                if (!run._operatorKeys.TryGetValue(keyId, out var records)) run._operatorKeys[keyId] = records = [];
                records.Add(new(Str(payload, "effective_at") is { } effective && TryReadTimestamp(effective, out var at) ? at : null,
                    Str(payload, "public_key"), Str(payload, "holder")));
            }

            /// <summary>
            /// A remediation record (v1.4.0): signed by an executor key of its sovereign, as execution evidence is; one per
            /// judgement or decision (<c>duplicate_remediation</c>), of its subject's change, resource and the version that
            /// change made (<c>remediation_subject_mismatch</c>), after a forward rotation of the same resource
            /// (<c>remediation_rotation_mismatch</c>) that <see cref="RemediationRules.RotationProblem"/> accepts. In a run
            /// from its start (entry 1, or right after the checkpoint) its subject and rotation are held before it
            /// (<c>remediation_subject_missing</c>, <c>remediation_rotation_missing</c>).
            /// </summary>
            private void Remediation(string remediationId)
            {
                var key = run.FittingKey(kind, payload);
                Signature(key is not null && OutOfBandSignedBy(payload, [key.PublicKey]), "RemediationRecord");
                Result.Remediations++;
                run._notDecisions.Add(remediationId);
                var heldFromStart = run._contiguous && run._fromStart;
                var subjectId = Str(payload, "subject_id")!;
                var resource = Str(payload, "resource_id");
                var changeKind = Str(payload, "change_kind")!;
                var changeId = Str(payload, "change_id")!;
                if (!run._remediated.Add(subjectId)) Fail("duplicate_remediation", subjectId);
                long? changeSequence = null;
                long? changedAt = null;
                if (!run._judgedRecords.TryGetValue(subjectId, out var subject))
                {
                    if (heldFromStart) Fail("remediation_subject_missing", subjectId);
                }
                else if (Str(payload, "subject_kind") == "judgement")
                {
                    var version = run._versions.GetValueOrDefault(SubjectKey(changeKind, changeId));
                    var disabled = Str(payload, "disabled_version_id");
                    if (subject.Resource != resource || changeKind != subject.ChangeKind || changeId != subject.ChangeId
                        || (disabled is not null && version is not null && disabled != version))
                        Fail("remediation_subject_mismatch", subjectId);
                    changeSequence = subject.ChangeSequence;
                    changedAt = subject.ChangedAt;
                }
                else if (changeId != subjectId)
                {
                    // A decision's change is its execution evidence (or, never executed, the decision itself).
                    var executed = run._executed.TryGetValue(changeId, out var change);
                    if (changeKind != "execution" || (executed && (change!.DecisionId != subjectId || change.Resource != resource)))
                    {
                        Fail("remediation_subject_mismatch", subjectId);
                    }
                    else if (executed)
                    {
                        changeSequence = change!.Sequence;
                        changedAt = change.ExecutedAt;
                    }
                }
                if (Str(payload, "rotation_evidence_id") is not { } rotation) return;
                if (run._executed.TryGetValue(rotation, out var held))
                {
                    if (held.Resource != resource) Fail("remediation_rotation_mismatch", rotation);
                    // In microseconds as read: a time near the year 1 or 9999 is not clamped before it is compared.
                    if (RemediationRules.RotationProblemAt(held.Executor, held.Sequence, held.ExecutedAt,
                            Str(payload, "executor_sovereign_id")!, changeSequence, changedAt, run._cited.Contains(rotation)) is { } problem)
                        Fail(problem.Code, rotation);
                }
                else if (heldFromStart)
                {
                    Fail("remediation_rotation_missing", rotation);
                }
                else if (run._cited.Contains(rotation))
                {
                    Fail("remediation_rotation_reused", rotation);
                }
                run._cited.Add(rotation);
            }

            /// <summary>
            /// A review (v1.4.0), signed by the NA: its embedded requests prove it offline (<c>review_approval_invalid</c>,
            /// <see cref="RemediationRules.ReviewApprovalProblem"/>), each signed with the public key and holder the holder
            /// registry last recorded for its operator key by then, when the run holds a record for that key
            /// (<c>review_approval_key_mismatch</c>); a second holder decided it (<c>review_same_holder</c>); it names its
            /// subject's digest (<c>review_subject_mismatch</c>; <c>review_subject_missing</c> in a run from its start); and
            /// no waiver follows a remediation record (<c>review_waiver_after_remediation</c>).
            /// </summary>
            private void Review(string reviewId)
            {
                NASignature(keys => OutOfBandSignedBy(record, keys), "ReviewRecord");
                Result.Reviews++;
                run._notDecisions.Add(reviewId);
                var review = payload.Deserialize<ReviewRecord>()!;
                if (RemediationRules.ReviewApprovalProblem(review, run._allNaKeys) is { } problem) Fail("review_approval_invalid", problem);
                foreach (var (keyId, approval, holder, at) in new[]
                         {
                             (review.DecidedBy, review.Approval, review.DeciderHolder, review.DecidedAt),
                             (review.RequestedBy, review.RequestApproval, review.RequesterHolder, review.RequestedAt),
                         })
                {
                    if (keyId is null || approval is null || !run._operatorKeys.TryGetValue(keyId, out var records)) continue;
                    // The latest record at or before the time (the first of equal times, as the reference picks it).
                    HolderRecord? latest = null;
                    if (TryReadTimestamp(at, out var when))
                        foreach (var r in records)
                            if (r.At is { } effective && effective <= when && (latest is null || effective > latest.At))
                                latest = r;
                    if (latest is null || latest.PublicKey != approval.PublicKey || latest.Holder != holder)
                        Fail("review_approval_key_mismatch", keyId);
                }
                if (RemediationRules.ReviewHolderProblem(review) is not null) Fail("review_same_holder", review.DecidedBy);
                if (!run._judgedRecords.TryGetValue(review.SubjectId, out var subject))
                {
                    if (run._contiguous && run._fromStart) Fail("review_subject_missing", review.SubjectId);
                }
                else if (subject.Digest != review.SubjectDigest)
                {
                    Fail("review_subject_mismatch", review.SubjectId);
                }
                if (review.Verdict == ReviewVerdicts.WaiveUntil && run._remediated.Contains(review.SubjectId))
                    Fail("review_waiver_after_remediation", review.SubjectId);
            }

            /// <summary>
            /// An observation or break-glass record (v1.3.0): signed by a registered key of its sovereign with the role its
            /// kind needs, a subject a judgement may name; an observation continues its resource's observation positions,
            /// and a break-glass record is a record a judgement may match.
            /// </summary>
            private void Change(string recordId)
            {
                var model = PayloadModels[kind];
                var key = run.FittingKey(kind, payload);
                Signature(key is not null && OutOfBandSignedBy(payload, [key.PublicKey]), model);
                run._subjects[SubjectKey(kind, recordId)] = (Str(entry, "payload_digest")!, seq);
                // v1.4.0: the version the change made, which a remediation of it disables.
                var version = kind == "observation" ? Str(payload, "version_id")
                    : IsObject(payload, "execution_parameters", out var parameters) ? Str(parameters, "version_id") : null;
                run._versions[SubjectKey(kind, recordId)] = version is { Length: > 0 } ? version : null;
                run._notDecisions.Add(recordId);
                if (kind == "break_glass")
                {
                    Result.BreakGlass++;
                    run._matchable.Add(recordId);
                    // v1.6.0: a judgement matches a grant use, execution evidence and a break-glass record by ID.
                    if (run._useIds.Contains(recordId)) Fail("duplicate_record", $"break_glass {recordId} is a grant use's ID");
                    run._otherMatchable.Add(recordId);
                    return;
                }
                Result.Observations++;
                var resource = Str(payload, "resource_id")!;
                var position = PayloadInteger(entry, "observation_sequence");
                var hasHead = run._observationHeads.TryGetValue(resource, out var head);
                if ((hasHead || run._fromStart) && position != (hasHead ? head : 0) + 1) Fail("observation_chain_break", resource);
                run._observationHeads[resource] = position is { } p && p != 0 ? p : head;
            }

            /// <summary>
            /// A judgement (v1.3.0): signed by the NA, one per subject, of a subject as stored (or, in a run that holds its
            /// position, held), and matching a record at most once, one the store held before (a run from entry 1 holds it).
            /// </summary>
            private void Judgement(string judgementId)
            {
                NASignature(keys => OutOfBandSignedBy(record, keys), "JudgementRecord");
                Result.Judgements++;
                run._notDecisions.Add(judgementId);
                var subjectId = Str(payload, "subject_id")!;
                TryReadTimestamp(Str(payload, "evaluated_as_of")!, out var evaluatedAsOf);
                run._judgedRecords[judgementId] = new(Str(entry, "payload_digest")!, Str(payload, "resource_id"), Str(payload, "subject_kind"),
                    subjectId, PayloadInteger(payload, "subject_store_sequence"), evaluatedAsOf);
                var key = SubjectKey(Str(payload, "subject_kind")!, subjectId);
                if (!run._judged.Add(key)) Fail("duplicate_judgement", subjectId);
                var subjectSequence = PayloadInteger(payload, "subject_store_sequence");
                if (run._subjects.TryGetValue(key, out var subject))
                {
                    if (subject.Digest != Str(payload, "subject_digest") || subject.Sequence != subjectSequence)
                        Fail("judgement_subject_mismatch", subjectId);
                }
                else if (run._contiguous && run._firstSequence is { } first && subjectSequence >= first)
                {
                    Fail("judgement_subject_missing", subjectId);
                }
                if (Str(payload, "matched_evidence_id") is { } match)
                {
                    if (!run._matched.Add(match)) Fail("match_reused", match);
                    // v1.3.1: the NA matches a record it already holds, so a run from entry 1 holds it.
                    if (run._contiguous && run._firstSequence == 1 && !run._matchable.Contains(match)) Fail("judgement_match_missing", match);
                }
            }

            /// <summary>
            /// A quarantine entry (v1.3.0): signed by the NA, holding the record whose digest it names. An authentic one of a
            /// grant use (v1.6.0) that continued its chain is the chain's head.
            /// </summary>
            private void Quarantine()
            {
                var failures = Result.Failures.Count;
                NASignature(keys => OutOfBandSignedBy(record, keys), "QuarantineRecord");
                var authentic = Result.Failures.Count == failures;
                Result.Quarantined++;
                if (PayloadDigest(payload.GetProperty("record")) != Str(payload, "record_digest")) Fail("quarantine_digest_mismatch");
                else if (authentic && Str(payload, "record_kind") == "grant_execution") run.QuarantinedUse(payload.GetProperty("record"));
            }

            /// <summary>A grant (v1.6.0): signed by the NA; held for its uses and revocation once its signature verifies.</summary>
            private void Grant(string grantId)
            {
                var failures = Result.Failures.Count;
                NASignature(keys => OutOfBandSignedBy(record, keys), "CapabilityGrant");
                Result.Grants++;
                // A grant whose signature fails is not held: its uses and revocation are checked as for a grant the run does not hold.
                if (Result.Failures.Count == failures)
                    run._grantsHeld[grantId] = (ReadPayload<CapabilityGrant>(payload)!, GrantRecordDigest("CapabilityGrant", payload));
            }

            /// <summary>
            /// A grant's revocation (v1.6.0): signed by the NA, of a grant the run holds when it holds the store from its start
            /// (<c>grant_missing</c>), naming its digest (<c>grant_mismatch</c>), once (<c>duplicate_grant_revocation</c>).
            /// </summary>
            private void GrantRevocation()
            {
                var failures = Result.Failures.Count;
                NASignature(keys => OutOfBandSignedBy(record, keys), "GrantRevocationRecord");
                var authentic = Result.Failures.Count == failures;
                Result.GrantRevocations++;
                var grantId = Str(payload, "grant_id")!;
                if (!run._grantsHeld.TryGetValue(grantId, out var held))
                {
                    if (run._contiguous && run._fromStart) Fail("grant_missing", grantId);
                }
                else if (Str(payload, "grant_digest") != held.Digest)
                {
                    Fail("grant_mismatch", grantId);
                }
                if (run._revoked.ContainsKey(grantId)) Fail("duplicate_grant_revocation", grantId);
                else if (authentic) run._revoked[grantId] = Str(payload, "revoked_at")!;
            }

            /// <summary>
            /// A grant use (v1.6.0), signed by its grant's bearer key (a registered executor key of its sovereign): it
            /// continues its grant's use chain, whose head may be a quarantined use that continued it
            /// (<c>grant_use_chain_break</c>); names a grant the run holds when it holds the store from its start
            /// (<c>grant_missing</c>), signed by that grant's bearer key (<c>grant_bearer_mismatch</c>), within the key's
            /// resource prefix (<c>grant_use_out_of_scope</c>), naming the grant's digest (<c>grant_mismatch</c>); then the
            /// use rules (<see cref="GrantRules.UseProblem"/>, with the admission codes) with what the run holds: the budget
            /// counts admitted uses when the run holds the chain from its first use, and the order and time rules compare a
            /// use with the last admitted one. A use takes the next place of its resource's chain, which the NA assigns in
            /// the envelope (<c>resource_chain_break</c>); one without a resource has none (<c>envelope_mismatch</c>).
            /// </summary>
            private void GrantUse(string useId)
            {
                var key = run.FittingKey(kind, payload);
                Signature(key is not null && OutOfBandSignedBy(payload, [key.PublicKey]), "GrantExecutionEvidence");
                Result.GrantExecutions++;
                var use = ReadPayload<GrantExecutionEvidence>(payload)!;
                var grantId = use.GrantId;
                var received = GrantRecordDigest("GrantExecutionEvidence", payload);
                var isHeld = run._grantsHeld.TryGetValue(grantId, out var held);
                var signer = run.KeyNamed(payload);
                var hasHead = run._chainHeads.TryGetValue(grantId, out var head);
                if ((hasHead || run._fromStart) && !GrantRules.ContinuesChain(use, hasHead ? head! : new UseChainHead(0, null)))
                    Fail("grant_use_chain_break", grantId);
                if (!isHeld)
                {
                    if (run._contiguous && run._fromStart) Fail("grant_missing", grantId);
                }
                else if (!IsObject(payload, "signature", out var sig) || Str(sig, "key_id") != held.Grant.BearerKeyId
                         || use.ExecutorSovereignId != held.Grant.BearerSovereignId
                         || (signer is not null && signer.PublicKey != held.Grant.BearerPublicKey))
                {
                    Fail("grant_bearer_mismatch", useId);
                }
                // A use without a resource is outside any prefix, the empty one included (unlike execution evidence).
                if (signer?.ResourcePrefix is { } prefix && (use.ResourceId is null || !use.ResourceId.StartsWith(prefix, StringComparison.Ordinal)))
                    Fail("grant_use_out_of_scope", $"signing key covers only resources starting with {prefix}");
                if (isHeld && use.GrantDigest != held.Digest) Fail("grant_mismatch", grantId);
                if (use.UseSequence == 1) run._admittedUses.TryAdd(grantId, 0);
                var token = payload.GetProperty("time_token");
                var tokenKeys = Str(token, "issued_at") is { } issued && TryReadTimestamp(issued, out var at) ? run.KeysAt(at)
                    : run._history is null ? run._naKeys : [];
                var tokenPlace = PayloadInteger(token, "use_sequence");
                var named = tokenPlace is > 0 && run._useHeads.TryGetValue(UseKey(grantId, (int)tokenPlace), out var n) ? n : null;
                try
                {
                    if (GrantRules.UseProblem(use, new UseCheck
                    {
                        Grant = isHeld ? held.Grant : null,
                        Previous = run._lastAdmitted.GetValueOrDefault(grantId),
                        Admitted = run._admittedUses.TryGetValue(grantId, out var admitted) ? admitted : null,
                        TokenHead = named,
                        TokenKeys = tokenKeys,
                        RawToken = token,
                        RevokedAt = run._revoked.GetValueOrDefault(grantId),
                        RecordedAt = Str(entry, "recorded_at"),
                    }) is { } problem)
                        Fail(problem.Code, problem.Detail);
                }
                catch (ArgumentException e)
                {
                    // A time the use's checks cannot read: the entry's own, as the shapes accepted the records'.
                    Fail("payload_invalid", e.Message);
                }
                run._chainHeads[grantId] = new UseChainHead(use.UseSequence, received);
                run._lastAdmitted[grantId] = GrantRules.NextPreviousUse(use);
                if (run._admittedUses.TryGetValue(grantId, out var count)) run._admittedUses[grantId] = count + 1;
                run._useHeads[UseKey(grantId, use.UseSequence)] = new TokenHead(received, use.ExecutedAt);
                var position = entry.TryGetProperty("resource_sequence", out var p) && p.ValueKind != JsonValueKind.Null ? PayloadInteger(p) : null;
                if (use.ResourceId is { } resourceId)
                {
                    var hasResource = run._resourceHeads.TryGetValue(resourceId, out var resourceHead);
                    if ((hasResource || run._fromStart) && position != (hasResource ? resourceHead.Sequence + 1 : 1))
                        Fail("resource_chain_break", resourceId);
                    run._resourceHeads[resourceId] = (position ?? 0, received);
                }
                else if (entry.TryGetProperty("resource_sequence", out var given) && given.ValueKind != JsonValueKind.Null)
                {
                    Fail("envelope_mismatch", "grant_execution: resource_sequence");
                }
                if (run._otherMatchable.Contains(useId)) Fail("duplicate_record", $"grant_execution {useId} is another record's ID");
                run._useIds.Add(useId);
                run._matchable.Add(useId);
            }

            private void Decision(string decisionId)
            {
                var valid = NASignature(keys => SignedAsReceived("BoundaryDecision", record, keys), "BoundaryDecision");
                // v1.5.0: valid longer than the key that signed it may vouch for.
                var signer = valid?.FirstOrDefault(k => SignedAsReceived("BoundaryDecision", record, [k]));
                if (signer is not null && run._history is { } history
                    && TryReadTimestamp(Str(record, "decision_valid_until")!, out var validUntil)
                    && history.DecisionOutlivesKey(signer, Instant(validUntil)))
                    Fail("decision_outlives_key", decisionId);
                Result.Decisions++;
                run._decisions[decisionId] = record.Clone();
                run._judgedRecords[decisionId] = new(Str(entry, "payload_digest")!, null, null, null, null, null);
                if (payload.TryGetProperty("context", out var context) && Shapes.Context(context))
                    run._contexts[decisionId] = context.Clone();
                else
                    Fail("payload_invalid", "context");
            }

            private void Execution()
            {
                // v1.3.0: only an executor key signs execution evidence.
                var key = run.FittingKey(kind, payload);
                Signature(key is not null && SignedAsReceived("ExecutionEvidence", payload, [key.PublicKey]), "ExecutionEvidence");
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
                // v1.3.0: execution evidence never rests on a judgement or a record of a change outside the governed path.
                if (run._notDecisions.Contains(decisionId)) Fail("evidence_cites_judgement", decisionId);
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
                var evidenceId = Str(payload, "evidence_id")!;
                run._matchable.Add(evidenceId);
                if (run._useIds.Contains(evidenceId)) Fail("duplicate_record", $"execution {evidenceId} is a grant use's ID");
                run._otherMatchable.Add(evidenceId);
                TryReadTimestamp(Str(payload, "executed_at")!, out var executed);
                run._executed[Str(payload, "evidence_id")!] = new(Str(payload, "resource_id"), seq, executed,
                    Str(payload, "executor_sovereign_id")!, decisionId);
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
                // An empty ID names no checkpoint, as TypeScript reads it.
                var previous = Str(payload, "previous_checkpoint_id");
                var hasBefore = previous is { Length: > 0 } && run._checkpoints.ContainsKey(previous);
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
                if (authentic) run.SeedHeads(payload);
            }
        }
    }

    /// <summary>
    /// v1.4.0: what a remediation or review checks of its judgement or decision: the payload's digest, and for a judgement
    /// its resource, change (kind, ID, store position) and when it was evaluated (microseconds).
    /// </summary>
    private sealed record JudgedSubject(string Digest, string? Resource, string? ChangeKind, string? ChangeId, long? ChangeSequence, long? ChangedAt);

    /// <summary>v1.4.0: what a remediation checks of execution evidence (<c>executed_at</c> in microseconds).</summary>
    private sealed record HeldExecution(string? Resource, long Sequence, long ExecutedAt, string Executor, string DecisionId);

    /// <summary>v1.4.0: an <c>operator_key_holder</c> registry record: when it took effect (null when unreadable), the key and its holder.</summary>
    private sealed record HolderRecord(long? At, string? PublicKey, string? Holder);

    /// <summary>The digest a record links to: <see cref="ExecutionDigest"/> of the record as received.</summary>
    private static string ExecutionDigestOf(JsonElement record) => Sha256Hex(ReceivedCanonical("ExecutionEvidence", record));

    /// <summary>Whether a record of a change outside the governed path is signed, as received, by one of <paramref name="keys"/>.</summary>
    private static bool OutOfBandSignedBy(JsonElement record, IReadOnlyList<string> keys) =>
        IsObject(record, "signature", out var signature) && Str(signature, "sig") is { } sig
        && VerifyEd25519(OutOfBandCanonical(record), sig, keys);

    /// <summary>A field as an envelope comparison reads it: its text, or null when absent or null (the record shapes allow nothing else).</summary>
    private static string? TextOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// An envelope field or the record field it names, as JavaScript's strict equality compares them: a string by its
    /// text, a number by its value (<c>7</c> is <c>7.0</c>); null for null, an absent field or anything else.
    /// </summary>
    private static string? EnvelopeValue(JsonElement obj, string name) =>
        !obj.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.String ? "s" + value.GetString()
        : value.ValueKind == JsonValueKind.Number
            ? "n" + (PayloadInteger(value)?.ToString(CultureInfo.InvariantCulture) ?? value.GetRawText())
            : null;

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
    /// A payload read into its model as JavaScript reads it: an integer written with a fraction or an exponent
    /// (<c>1.0</c>, <c>1e0</c>), which the shapes accept, is read as the integer. Digests and signatures stay over the
    /// payload as received.
    /// </summary>
    internal static T? ReadPayload<T>(JsonElement payload) => Integral(payload).Deserialize<T>();

    private static JsonNode? Integral(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var member in value.EnumerateObject()) obj[member.Name] = Integral(member.Value);
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray()) array.Add(Integral(item));
                return array;
            case JsonValueKind.Number when PayloadInteger(value) is { } integer:
                return JsonValue.Create(integer);
            default:
                return JsonValue.Create(value);
        }
    }

    /// <summary>
    /// The record shapes the reference reads, as TypeScript checks them (<c>validation.ts</c>): a field is absent,
    /// null, or of its type; a decision's or record's unknown fields are the field registry's to judge.
    /// </summary>
    private static partial class Shapes
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
