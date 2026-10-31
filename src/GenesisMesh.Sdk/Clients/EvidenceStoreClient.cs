using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// The NA's evidence store (v0.59; 1.9.0 in this SDK): a controller submits the execution evidence it signed and reads
/// where each resource's chain ends; an operator searches, verifies and exports what is stored. Reads need an operator
/// key of the read tier or above; registering and retiring keys and applying retention need a privileged one.
/// </summary>
public sealed class EvidenceStoreClient
{
    /// <summary>Most pending records an enqueued record submits before its own; older ones wait for a flush.</summary>
    private const int MaxInlineDrain = 100;

    /// <summary>The most entries the NA returns in one page.</summary>
    private const int MaxPage = 1000;

    /// <summary>Refusals of a record whose predecessor is not stored: final once that predecessor is a dead letter.</summary>
    private static readonly HashSet<string> ChainGaps = new(StringComparer.Ordinal) { "evidence_chain_gap", "resource_chain_gap" };

    private readonly Transport _t;
    /// <summary>Digests of outbox records by ID: a record never changes once signed.</summary>
    private readonly ConcurrentDictionary<string, string> _digests = new(StringComparer.Ordinal);
    /// <summary>Guards <see cref="_inFlight"/> and <see cref="_flush"/>.</summary>
    private readonly object _gate = new();
    /// <summary>Records a call is submitting right now (<see cref="Claim"/>): every other call leaves them to it.</summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    /// <summary>The flush in progress: every due entry (full) or an enqueued record's own drain.</summary>
    private FlushRun? _flush;
    /// <summary>
    /// Records admitted while <see cref="_flush"/> runs: it may have listed them before, and skips them rather than
    /// sending them again.
    /// </summary>
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);

    private sealed class FlushRun(bool full, bool ignoreBackoff)
    {
        public bool Full { get; } = full;
        public bool IgnoreBackoff { get; } = ignoreBackoff;
        public TaskCompletionSource<FlushResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal EvidenceStoreClient(Transport t, IOutbox<OutboxEntry>? outbox = null)
    {
        _t = t;
        Outbox = outbox;
    }

    /// <summary>
    /// The outbox signed records wait in until the NA admits them (<see cref="ClientOptions.Outbox"/>); null when none
    /// is configured.
    /// </summary>
    public IOutbox<OutboxEntry>? Outbox { get; }

    /// <summary>
    /// Submits one signed record (<see cref="ExecutionRecorder"/>). The executor's signature authenticates it, not
    /// operator headers. An identical resubmission returns <c>duplicate</c>, so it is safe to retry.
    /// POST /evidence/execution
    /// </summary>
    /// <remarks>
    /// The NA keeps an authentic record it refuses as a quarantine entry: the action it describes happened. Build a new
    /// record rather than resubmitting a refused one.
    /// </remarks>
    /// <exception cref="SecretMaterialException">The record carries secret material: nothing is sent.</exception>
    /// <exception cref="ConflictException">
    /// <c>evidence_conflict</c>: the record's position is taken. Most often another record was stored on the resource
    /// after its head was read; read the head again (<see cref="ResourceHead"/>) and record anew.
    /// </exception>
    /// <exception cref="ValidationException">
    /// The NA refused the record, with its code: for example <c>evidence_out_of_scope</c> for a key limited to other
    /// resources, <c>evidence_decision_denied</c>, <c>resource_chain_mismatch</c> for a link to another record than the
    /// resource's head, or <c>resource_chain_gap</c> for a position past it.
    /// </exception>
    public async Task<EvidenceSubmission> Submit(ExecutionEvidence evidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (EvidenceMetadata.CheckMetadataOnly(evidence.ExecutionParameters, evidence.OutcomeDetail) is { } reason)
            throw new SecretMaterialException(reason);
        return await _t.PublicPostAsync<EvidenceSubmission>("/evidence/execution",
            new Dictionary<string, object?> { ["evidence"] = evidence }, idempotent: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a signed record in the outbox (<see cref="ClientOptions.Outbox"/>) and submits it. Pending records it
    /// chains from go first, oldest first (up to 100; older ones wait for <see cref="FlushPending"/>). A failed
    /// submission does not throw: a transient failure leaves the record pending, with a backoff, and a refusal no
    /// retry overcomes (<see cref="EvidenceOutbox.PermanentRefusals"/>) keeps it as a dead letter with the NA's code.
    /// A record whose predecessor was refused is still sent, so the NA can quarantine it when it refuses it for good
    /// too; refused only for the gap its predecessor left, it is a dead letter
    /// (<see cref="EvidenceOutbox.PredecessorDeadLettered"/>). Passing a record already in the outbox submits it again;
    /// while another call is submitting the same record, this one returns it as <see cref="Delivery.Queued"/> without
    /// sending it.
    /// </summary>
    /// <exception cref="OutboxException">No outbox is configured (<c>outbox_required</c>), or it cannot be read.</exception>
    /// <exception cref="InvalidOperationException">The outbox holds another record with the same evidence ID.</exception>
    public async Task<Delivery> Enqueue(ExecutionEvidence evidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var outbox = RequireOutbox();
        var entries = (await outbox.List(ct).ConfigureAwait(false)).ToList();
        var entry = entries.Find(e => e.Id == evidence.EvidenceId);
        if (entry is not null && DigestOf(entry) != Digest(evidence))
            throw new InvalidOperationException($"genesismesh: a different record with evidence_id {evidence.EvidenceId} is in the outbox");
        // Claimed before it is stored: a flush that lists it meanwhile leaves it to this call, and so does another
        // enqueue of the same record, which returns it as queued.
        if (!Claim(evidence.EvidenceId)) return new Delivery { Queued = entry ?? EvidenceOutbox.Entry(evidence) };
        var claimed = true;
        try
        {
            if (entry is null)
            {
                entry = EvidenceOutbox.Entry(evidence);
                await outbox.Add(entry, ct).ConfigureAwait(false);
                entries.Add(entry);
            }
            if (entry.State == OutboxStates.DeadLetter) return new Delivery { Queued = entry };

            var pending = PendingBefore(entries, entry);
            if (pending.Count == 0) return await Attempt(outbox, entries, entry, ct).ConfigureAwait(false);
            FlushRun? run = null;
            if (pending.Count <= MaxInlineDrain)
            {
                lock (_gate)
                {
                    if (_flush is null) _flush = run = new FlushRun(full: false, ignoreBackoff: true);
                }
            }
            if (run is null) return new Delivery { Queued = entry };
            // The drain submits the record in its turn, and claims it then.
            Release(entry.Id);
            claimed = false;
            var only = pending.Select(e => e.Id).Append(entry.Id).ToHashSet(StringComparer.Ordinal);
            var (_, outcomes) = await Tracked(run, () => Flush(outbox, ignoreBackoff: true, only, ct)).ConfigureAwait(false);
            return outcomes.TryGetValue(entry.Id, out var delivery) ? delivery : new Delivery { Queued = entry };
        }
        finally
        {
            if (claimed) Release(evidence.EvidenceId);
        }
    }

    /// <summary>
    /// Submits the outbox's pending records in the order they were added. A record waits while one it chains from is
    /// pending; a record whose predecessor was refused is sent too, and is a dead letter
    /// (<see cref="EvidenceOutbox.PredecessorDeadLettered"/>) when the NA refuses it for that gap. Records in backoff
    /// are skipped unless <see cref="FlushOptions.IgnoreBackoff"/>; a transient failure ends the run, leaving the rest
    /// for the next one. Run it at startup and on a timer. A call joins the run in progress when that run tries
    /// everything it asks for, and otherwise starts one once it ends.
    /// </summary>
    /// <exception cref="OutboxException">No outbox is configured (<c>outbox_required</c>), or it cannot be read.</exception>
    public async Task<FlushResult> FlushPending(FlushOptions? options = null, CancellationToken ct = default)
    {
        var outbox = RequireOutbox();
        var ignoreBackoff = options?.IgnoreBackoff ?? false;
        while (true)
        {
            FlushRun? running;
            FlushRun? mine = null;
            lock (_gate)
            {
                running = _flush;
                if (running is null) _flush = mine = new FlushRun(full: true, ignoreBackoff);
            }
            if (mine is not null)
                return (await Tracked(mine, () => Flush(outbox, ignoreBackoff, null, ct)).ConfigureAwait(false)).Result;
            var joins = running!.Full && (running.IgnoreBackoff || !ignoreBackoff);
            try
            {
                var result = await running.Result.Task.WaitAsync(ct).ConfigureAwait(false);
                if (joins) return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The run was its caller's to cancel, not this one's: start another.
            }
            catch (Exception) when (!joins && !ct.IsCancellationRequested)
            {
                // A run this call does not join: its failure is its own caller's.
            }
        }
    }

    /// <summary>
    /// The newest pending record in the outbox for <paramref name="resourceId"/> that can still be admitted, or null.
    /// <see cref="Governance.GovernedAction"/> chains from it, not from the NA's head, while it waits.
    /// </summary>
    /// <exception cref="OutboxException">No outbox is configured (<c>outbox_required</c>), or it cannot be read.</exception>
    public async Task<ExecutionEvidence?> PendingHead(string resourceId, CancellationToken ct = default)
    {
        var entries = await RequireOutbox().List(ct).ConfigureAwait(false);
        var dead = DeadDigests(entries);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.State == OutboxStates.Pending && e.Evidence.ResourceId == resourceId && !dead.Contains(DigestOf(e)))
                return e.Evidence;
        }
        return null;
    }

    /// <summary>
    /// One page of stored entries matching <paramref name="search"/>; pass its <see cref="EvidenceSearchResult.NextAfterSequence"/>
    /// as the next <see cref="EvidenceSearchParams.AfterSequence"/>, or use <see cref="Iterate"/>.
    /// GET /admin/evidence
    /// </summary>
    public Task<EvidenceSearchResult> Search(EvidenceSearchParams? search = null, CancellationToken ct = default) =>
        _t.AdminGetAsync<EvidenceSearchResult>("/admin/evidence", Query(search, search?.AfterSequence), ct);

    /// <summary>Every entry matching <paramref name="search"/>, following the pages from its <see cref="EvidenceSearchParams.AfterSequence"/> (default the start).</summary>
    /// <exception cref="InvalidOperationException">The NA's cursor did not advance.</exception>
    public async IAsyncEnumerable<EvidenceEvent> Iterate(
        EvidenceSearchParams? search = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long? after = search?.AfterSequence ?? 0;
        while (after is { } cursor)
        {
            var page = await _t.AdminGetAsync<EvidenceSearchResult>("/admin/evidence", Query(search, cursor), ct)
                .ConfigureAwait(false);
            if (page.NextAfterSequence is { } next && next <= cursor)
                throw new InvalidOperationException("genesismesh: the evidence search cursor did not advance");
            foreach (var entry in page.Entries) yield return entry;
            after = page.NextAfterSequence;
        }
    }

    /// <summary>
    /// The NA's head of a resource chain: its position and digest, and (v1.6.0) what the head is. One indexed lookup
    /// (NA 0.63.1 and later), which also covers chains whose stored records retention removed.
    /// GET /admin/evidence/resource-heads/&lt;resource_id&gt;
    /// </summary>
    /// <exception cref="NotFoundException">The resource has no history (<c>resource_not_found</c>).</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceId"/> cannot be sent as a path (see <see cref="ResourceHead"/>): nothing is sent.
    /// </exception>
    public async Task<StoredResourceHead> StoredResourceHead(string resourceId, CancellationToken ct = default) =>
        await _t.AdminGetAsync<StoredResourceHead>(
            "/admin/evidence/resource-heads/" + Transport.ResourcePath(resourceId, nameof(resourceId)), null, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Where the next record of a resource must link (<see cref="RecordExecutionRequest.PriorResource"/>), or null for
    /// a resource with no history. Submission still checks the chain: a record built on a head read before another
    /// record was stored on the resource is refused as <c>evidence_conflict</c> (<see cref="ConflictException"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceId"/> cannot be sent as a path: it is empty, has an empty or dot segment, holds '%', '?',
    /// '#', '\' or a control character, or starts or ends with white space. Nothing is sent.
    /// </exception>
    public async Task<ResourceHead?> ResourceHead(string resourceId, CancellationToken ct = default)
    {
        try
        {
            return (await StoredResourceHead(resourceId, ct).ConfigureAwait(false)).ToHead();
        }
        catch (NotFoundException e) when (e.Code == "resource_not_found")
        {
            return null;
        }
    }

    /// <summary>
    /// The latest state of every resource with stored execution evidence or (1.3.1) break-glass records, for
    /// reconciliation against a cloud inventory. A break-glass change counts as the resource's change, in the order
    /// the changes were made (<c>executed_at</c>), so reconciling after one does not report it again as drift. The
    /// chain head (<see cref="ResourceState.ResourceSequence"/>, <see cref="ResourceState.RecordDigest"/>) and
    /// <see cref="ResourceState.LastDecisionId"/> stay the newest execution record's (0 and empty without one).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ResourceState>> ResourceStates(CancellationToken ct = default)
    {
        var changes = new Dictionary<string, List<(long Store, JsonElement Record, bool Execution)>>(StringComparer.Ordinal);
        void Add(string resourceId, long store, JsonElement record, bool execution)
        {
            if (!changes.TryGetValue(resourceId, out var list)) changes[resourceId] = list = [];
            list.Add((store, record, execution));
        }
        await foreach (var e in Iterate(new EvidenceSearchParams { EntryKind = "execution" }, ct).ConfigureAwait(false))
        {
            if (Text(e.Payload, "resource_id") is { } id && Number(e.Payload, "resource_sequence") is not null)
                Add(id, e.Entry.StoreSequence, e.Payload, execution: true);
        }
        await foreach (var e in Iterate(new EvidenceSearchParams { EntryKind = "break_glass" }, ct).ConfigureAwait(false))
        {
            if (Text(e.Payload, "resource_id") is { } id) Add(id, e.Entry.StoreSequence, e.Payload, execution: false);
        }

        var states = new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        foreach (var (resourceId, list) in changes)
        {
            var ordered = list
                .OrderBy(c => OfflineVerifier.ParseTimestamp(Text(c.Record, "executed_at") ?? ""))
                .ThenBy(c => c.Store);
            ResourceState? state = null;
            foreach (var (_, record, execution) in ordered)
            {
                var sequence = Number(record, "resource_sequence");
                if (execution && state is not null && state.ResourceSequence >= sequence) continue;
                var action = Text(record, "resource_action");
                var outcome = Text(record, "outcome") ?? "";
                var success = outcome == ExecutionOutcomes.Success;
                state = new ResourceState
                {
                    ResourceId = resourceId,
                    ResourceSequence = execution ? sequence!.Value : state?.ResourceSequence ?? 0,
                    RecordDigest = execution ? OfflineVerifier.ExecutionDigest(record.GetRawText()) : state?.RecordDigest ?? "",
                    LastAction = action,
                    LastOutcome = outcome,
                    LastSuccessAction = success ? action : state?.LastSuccessAction,
                    LastSuccessParameters = success && record.TryGetProperty("execution_parameters", out var p)
                        ? p.Clone()
                        : state?.LastSuccessParameters ?? default,
                    LastExecutedAt = Text(record, "executed_at") ?? "",
                    LastDecisionId = execution ? Text(record, "decision_id") ?? "" : state?.LastDecisionId ?? "",
                    LastBreakGlassId = execution ? null : Text(record, "break_glass_id"),
                };
            }
            states[resourceId] = state!;
        }
        return states;
    }

    /// <summary>
    /// Registers an executor or (v1.3.0) observer key (privileged). The controller generates the key pair and keeps
    /// the private key; the platform team registers the public key, optionally limited to a resource prefix.
    /// POST /admin/evidence/executor-keys
    /// </summary>
    /// <exception cref="ConflictException">The key ID is already registered (<c>executor_key_exists</c>).</exception>
    public async Task<ExecutorKeyState> RegisterExecutorKey(RegisterExecutorKeyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _t.AdminPostAsync<ExecutorKeyState>("/admin/evidence/executor-keys", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The store's mode, size, last position and latest retention checkpoint; (v1.2.0) its anchoring; and the counts
    /// of the records it keeps. Answered with the store off too.
    /// GET /admin/evidence/status
    /// </summary>
    public Task<EvidenceStoreStatus> Status(CancellationToken ct = default) =>
        _t.AdminGetAsync<EvidenceStoreStatus>("/admin/evidence/status", null, ct);

    /// <summary>
    /// The NA's verification of every stored entry from the latest retention checkpoint on: the envelopes, the store
    /// chain, every signature, the decision and resource chains, and (v1.2.0) the store against the NA's own anchors.
    /// GET /admin/evidence/verify
    /// </summary>
    public Task<EvidenceStoreVerification> Verify(CancellationToken ct = default) =>
        _t.AdminGetAsync<EvidenceStoreVerification>("/admin/evidence/verify", null, ct);

    /// <summary>
    /// One resource's records, the decisions they rest on and (v1.3.0) its changes outside the governed path, as the NA
    /// verified them: at most the oldest 10,000 records (<see cref="GenesisMesh.ResourceHistory.Truncated"/>).
    /// GET /admin/evidence/resources/&lt;resource_id&gt;
    /// </summary>
    /// <exception cref="NotFoundException">
    /// The store holds no record of the resource (<c>resource_not_found</c>), also when retention removed them all;
    /// <see cref="ResourceHead"/> still reads where its chain ends.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceId"/> cannot be sent as a path (see <see cref="ResourceHead"/>): nothing is sent.
    /// </exception>
    public async Task<ResourceHistory> ResourceHistory(string resourceId, CancellationToken ct = default) =>
        await _t.AdminGetAsync<ResourceHistory>(
            "/admin/evidence/resources/" + Transport.ResourcePath(resourceId, nameof(resourceId)), null, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// A vendor's decisions and the evidence under them, as the NA verified them: at most the oldest 10,000 decisions
    /// (<see cref="GenesisMesh.VendorHistory.Truncated"/>).
    /// GET /admin/evidence/vendors/&lt;vendor_id&gt;
    /// </summary>
    /// <exception cref="NotFoundException">The store holds no decision for the vendor (<c>vendor_not_found</c>).</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="vendorId"/> is not one path segment: it is empty, a dot segment, or holds '/', '\', '?', '#', '%'
    /// or a control character. Nothing is sent.
    /// </exception>
    public async Task<VendorHistory> VendorHistory(string vendorId, CancellationToken ct = default) =>
        await _t.AdminGetAsync<VendorHistory>(
            "/admin/evidence/vendors/" + Transport.PathSegment(vendorId, nameof(vendorId)), null, ct).ConfigureAwait(false);

    /// <summary>
    /// One page of the store as <c>gm.evidence.event</c> JSON Lines, unparsed: what a SIEM pipeline keeps. Read it with
    /// <see cref="OfflineVerifier.ParseExportLines(string)"/>, or use <see cref="Export"/> or <see cref="ExportAll"/>.
    /// GET /admin/evidence/export
    /// </summary>
    public Task<string> ExportText(EvidenceExportParams? export = null, CancellationToken ct = default) =>
        _t.AdminGetTextAsync("/admin/evidence/export", ExportQuery(export?.SinceSequence, export?.Limit), ct);

    /// <summary>One page of the store's export, parsed (<see cref="ExportText"/>).</summary>
    /// <exception cref="System.Text.Json.JsonException">A line is not a <c>gm.evidence.event</c> of schema version 1.</exception>
    public async Task<IReadOnlyList<EvidenceEvent>> Export(EvidenceExportParams? export = null, CancellationToken ct = default) =>
        OfflineVerifier.ParseExportLines(await ExportText(export, ct).ConfigureAwait(false));

    /// <summary>
    /// Every entry after <paramref name="sinceSequence"/>, following the export's pages: an incremental pull, from the
    /// last position a previous pull kept.
    /// </summary>
    /// <param name="sinceSequence">The last position already held; 0 for the whole store.</param>
    /// <param name="pageSize">Entries a page, 1 to 1000.</param>
    /// <param name="ct">Cancels the pull.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sinceSequence"/> is negative, or <paramref name="pageSize"/> is not 1 to 1000.
    /// </exception>
    /// <exception cref="InvalidOperationException">An entry of the export is not after the one before it.</exception>
    public IAsyncEnumerable<EvidenceEvent> ExportAll(long sinceSequence = 0, int pageSize = MaxPage, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sinceSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPage);
        return ExportFrom(sinceSequence, pageSize, ct);
    }

    private async IAsyncEnumerable<EvidenceEvent> ExportFrom(
        long since, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var text = await _t.AdminGetTextAsync("/admin/evidence/export", ExportQuery(since, pageSize), ct)
                .ConfigureAwait(false);
            var events = OfflineVerifier.ParseExportLines(text);
            foreach (var e in events)
            {
                if (e.Entry.StoreSequence <= since)
                    throw new InvalidOperationException("genesismesh: the evidence export cursor did not advance");
                since = e.Entry.StoreSequence;
                yield return e;
            }
            if (events.Count < pageSize) yield break;
        }
    }

    /// <summary>
    /// The registered executor and (v1.3.0) observer keys, retired ones included: a retired key still verifies the
    /// records it signed.
    /// GET /admin/evidence/executor-keys
    /// </summary>
    public async Task<IReadOnlyList<ExecutorKeyRecord>> ListExecutorKeys(CancellationToken ct = default) =>
        (await _t.AdminGetAsync<ExecutorKeyList>("/admin/evidence/executor-keys", null, ct).ConfigureAwait(false))
            .ExecutorKeys;

    /// <summary>
    /// Retires an executor or observer key (privileged): the NA refuses new records signed by it
    /// (<c>evidence_executor_key_retired</c>), and it still verifies the records it signed. (v1.6.0) The grants it
    /// bears are revoked with it. A key cannot be registered again: register a new one.
    /// POST /admin/evidence/executor-keys/&lt;key_id&gt;/retire
    /// </summary>
    /// <returns>The key, no longer active. <see cref="ExecutorKeyState.ExecutorSovereignId"/> is not sent.</returns>
    /// <exception cref="NotFoundException">No such key is registered (<c>executor_key_not_found</c>).</exception>
    /// <exception cref="ConflictException">
    /// The key is retired already (<c>executor_key_retired</c>), or (v1.7.0) it is an edge node's key, retired by
    /// revoking the node (<c>executor_key_is_edge_node</c>).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is not one path segment (see <see cref="VendorHistory"/>): nothing is sent.</exception>
    public async Task<ExecutorKeyState> RetireExecutorKey(string keyId, CancellationToken ct = default) =>
        await _t.AdminPostAsync<ExecutorKeyState>(
            $"/admin/evidence/executor-keys/{Transport.PathSegment(keyId, nameof(keyId))}/retire",
            new Dictionary<string, object?>(), ct).ConfigureAwait(false);

    /// <summary>
    /// Removes a prefix of the store behind a checkpoint the NA signs (privileged), so the rest still verifies. The
    /// prefix ends before the first entry recorded within <paramref name="olderThanDays"/> days, under a decision still
    /// valid, or not yet judged, and never splits a decision from its evidence. (v1.5.0) It never goes past
    /// <paramref name="throughSequence"/>, and while the NA requires archives (its default) not past what archive
    /// records cover. Whatever stopped it short is in <see cref="RetentionResult.HeldBack"/>.
    /// POST /admin/evidence/retention/apply
    /// </summary>
    /// <param name="olderThanDays">Keep entries recorded within this many days (1 or more); null to give only <paramref name="throughSequence"/>.</param>
    /// <param name="throughSequence">v1.5.0: remove nothing past this position (1 or more); null for no limit.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ArgumentException">Neither <paramref name="olderThanDays"/> nor <paramref name="throughSequence"/> is given: nothing is sent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value given is less than 1: nothing is sent.</exception>
    /// <exception cref="ValidationException">
    /// The NA refused the request (<c>invalid_retention</c>): for example, with out-of-band records, an
    /// <paramref name="olderThanDays"/> within the observation backlog.
    /// </exception>
    /// <exception cref="ConflictException">Retention is running on another instance (<c>retention_in_progress</c>).</exception>
    public async Task<RetentionResult> ApplyRetention(int? olderThanDays, long? throughSequence = null, CancellationToken ct = default)
    {
        if (olderThanDays is null && throughSequence is null)
            throw new ArgumentException("genesismesh: give olderThanDays, throughSequence or both", nameof(olderThanDays));
        if (olderThanDays is { } days) ArgumentOutOfRangeException.ThrowIfLessThan(days, 1, nameof(olderThanDays));
        if (throughSequence is { } through) ArgumentOutOfRangeException.ThrowIfLessThan(through, 1L, nameof(throughSequence));
        var body = new Dictionary<string, object?>();
        if (olderThanDays is not null) body["older_than_days"] = olderThanDays;
        if (throughSequence is not null) body["through_sequence"] = throughSequence;
        return await _t.AdminPostAsync<RetentionResult>("/admin/evidence/retention/apply", body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The store's most recent retention checkpoint, or null before the first retention: the heads of the chains it
    /// removed records from. Read from the store's <c>retention_checkpoint</c> entries.
    /// </summary>
    public async Task<RetentionCheckpoint?> LatestCheckpoint(CancellationToken ct = default)
    {
        JsonElement? latest = null;
        await foreach (var e in Iterate(new EvidenceSearchParams { EntryKind = "retention_checkpoint" }, ct).ConfigureAwait(false))
            latest = e.Payload;
        return latest?.Deserialize<RetentionCheckpoint>();
    }

    // ── The outbox ──────────────────────────────────────────────────────────

    private IOutbox<OutboxEntry> RequireOutbox() =>
        Outbox ?? throw new OutboxException("outbox_required", "no evidence outbox is configured (ClientOptions.Outbox)");

    /// <summary>Marks a record as being submitted by this call; false when another call is submitting it.</summary>
    private bool Claim(string id)
    {
        lock (_gate) return _inFlight.Add(id);
    }

    private void Release(string id)
    {
        lock (_gate) _inFlight.Remove(id);
    }

    /// <summary>
    /// Runs <paramref name="flush"/> as the flush in progress, <paramref name="run"/>, which this caller claimed; the
    /// slot is free again before anyone waiting on the run sees its end.
    /// </summary>
    private async Task<(FlushResult Result, Dictionary<string, Delivery> Outcomes)> Tracked(
        FlushRun run, Func<Task<(FlushResult, Dictionary<string, Delivery>)>> flush)
    {
        (FlushResult, Dictionary<string, Delivery>) done;
        try
        {
            done = await flush().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Free();
            if (e is OperationCanceledException canceled) run.Result.TrySetCanceled(canceled.CancellationToken);
            else run.Result.TrySetException(e);
            // Observed here: a run nobody joined does not report its failure as unobserved.
            _ = run.Result.Task.Exception;
            throw;
        }
        Free();
        run.Result.TrySetResult(done.Item1);
        return done;
    }

    /// <summary>Frees the flush slot, with what was admitted during the run.</summary>
    private void Free()
    {
        lock (_gate)
        {
            _flush = null;
            _settled.Clear();
        }
    }

    private async Task<(FlushResult, Dictionary<string, Delivery>)> Flush(
        IOutbox<OutboxEntry> outbox, bool ignoreBackoff, HashSet<string>? only, CancellationToken ct)
    {
        var admitted = new List<OutboxEntry>();
        var pending = new List<OutboxEntry>();
        var deadLettered = new List<OutboxEntry>();
        var outcomes = new Dictionary<string, Delivery>(StringComparer.Ordinal);
        var entries = (await outbox.List(ct).ConfigureAwait(false)).ToList();
        var waiting = new HashSet<string>(StringComparer.Ordinal);
        var stopped = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var digest = DigestOf(entry);
            if (entry.State == OutboxStates.DeadLetter) continue;
            if (only is not null && !only.Contains(entry.Id))
            {
                waiting.Add(digest);
                continue;
            }
            var due = ignoreBackoff || !(EvidenceOutbox.ParseTime(entry.NextAttemptAt) is { } next && next > DateTimeOffset.UtcNow);
            var tried = !stopped && due && !Predecessors(entry.Evidence).Any(waiting.Contains);
            // Claimed only when it is tried, and in one step with the check that another call has not admitted it since
            // the list was read (nothing left to send) or claimed it (it submits the record; pending as far as this run
            // knows).
            bool settled, claimed = false;
            lock (_gate)
            {
                settled = _settled.Contains(entry.Id);
                if (tried && !settled) claimed = _inFlight.Add(entry.Id);
            }
            if (settled) continue;
            if (!claimed)
            {
                waiting.Add(digest);
                pending.Add(entry);
                outcomes[entry.Id] = new Delivery { Queued = entry };
                continue;
            }
            Delivery delivery;
            try
            {
                delivery = await Attempt(outbox, entries, entry, ct).ConfigureAwait(false);
            }
            finally
            {
                Release(entry.Id);
            }
            outcomes[entry.Id] = delivery;
            if (delivery.Submission is not null)
            {
                admitted.Add(entry);
            }
            else if (delivery.Queued!.State == OutboxStates.DeadLetter)
            {
                deadLettered.Add(delivery.Queued);
            }
            else
            {
                waiting.Add(digest);
                pending.Add(delivery.Queued);
                stopped = true;
            }
        }
        return (new FlushResult { Admitted = admitted, Pending = pending, DeadLettered = deadLettered }, outcomes);
    }

    /// <summary>Submits one entry and stores what became of it. Throws only when the caller cancels.</summary>
    private async Task<Delivery> Attempt(IOutbox<OutboxEntry> outbox, List<OutboxEntry> entries, OutboxEntry entry, CancellationToken ct)
    {
        EvidenceSubmission submission;
        try
        {
            submission = await Submit(entry.Evidence, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (failure, transient) = EvidenceOutbox.Classify(e);
            var attempts = entry.Attempts + 1;
            // A gap behind a record not admitted yet is retried; behind a dead letter it never closes.
            if (ChainGaps.Contains(failure.Code) && Predecessors(entry.Evidence).Any(DeadDigests(entries).Contains))
            {
                return new Delivery
                {
                    Queued = await DeadLetter(outbox, entries, entry with { Attempts = attempts }, new SubmissionFailure
                    {
                        Status = failure.Status,
                        Code = EvidenceOutbox.PredecessorDeadLettered,
                        Message = $"a record this one chains from was refused; the NA answered {failure.Code}: {failure.Message}",
                    }).ConfigureAwait(false),
                };
            }
            if (!transient)
                return new Delivery { Queued = await DeadLetter(outbox, entries, entry with { Attempts = attempts }, failure).ConfigureAwait(false) };
            var failed = entry with
            {
                Attempts = attempts,
                LastError = failure,
                NextAttemptAt = EvidenceOutbox.Time(EvidenceOutbox.NextAttemptAt(attempts, e, DateTimeOffset.UtcNow)),
            };
            await Keep(outbox, failed).ConfigureAwait(false);
            Replace(entries, failed);
            return new Delivery { Queued = failed };
        }
        lock (_gate)
        {
            if (_flush is not null) _settled.Add(entry.Id);
        }
        try
        {
            await outbox.Remove(entry.Id, CancellationToken.None).ConfigureAwait(false);
            _digests.TryRemove(entry.Id, out _);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The NA holds the record: the next flush resubmits it, gets a duplicate and removes it.
        }
        return new Delivery { Submission = submission };
    }

    /// <summary>
    /// Dead-letters <paramref name="entry"/>. Records that chain from it stay pending: each is sent in turn, so the NA
    /// quarantines one it refuses for good, and one it refuses only for the gap is dead-lettered then.
    /// </summary>
    private static async Task<OutboxEntry> DeadLetter(IOutbox<OutboxEntry> outbox, List<OutboxEntry> entries, OutboxEntry entry, SubmissionFailure failure)
    {
        var refused = entry with { State = OutboxStates.DeadLetter, NextAttemptAt = null, LastError = failure };
        await Keep(outbox, refused).ConfigureAwait(false);
        Replace(entries, refused);
        return refused;
    }

    /// <summary>Stores an entry's new state. Once the NA has answered, its answer stands even if this fails.</summary>
    private static async Task Keep(IOutbox<OutboxEntry> outbox, OutboxEntry entry)
    {
        try
        {
            await outbox.Update(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The entry keeps its previous state; the next flush settles it.
        }
    }

    private static void Replace(List<OutboxEntry> entries, OutboxEntry entry)
    {
        var i = entries.FindIndex(e => e.Id == entry.Id);
        if (i >= 0) entries[i] = entry;
    }

    private static string Digest(ExecutionEvidence evidence) =>
        OfflineVerifier.ExecutionDigest(Transport.ToElement(evidence).GetRawText());

    private string DigestOf(OutboxEntry entry) => _digests.GetOrAdd(entry.Id, _ => Digest(entry.Evidence));

    private static IEnumerable<string> Predecessors(ExecutionEvidence evidence)
    {
        if (!string.IsNullOrEmpty(evidence.PrevEvidenceDigest)) yield return evidence.PrevEvidenceDigest;
        if (!string.IsNullOrEmpty(evidence.PrevResourceDigest)) yield return evidence.PrevResourceDigest;
    }

    /// <summary>Digests of the dead letters and of every record that chains from one.</summary>
    private HashSet<string> DeadDigests(IReadOnlyList<OutboxEntry> entries)
    {
        var dead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.State == OutboxStates.DeadLetter || Predecessors(e.Evidence).Any(dead.Contains)) dead.Add(DigestOf(e));
        }
        return dead;
    }

    /// <summary>The pending records <paramref name="entry"/> chains from, oldest first.</summary>
    private List<OutboxEntry> PendingBefore(List<OutboxEntry> entries, OutboxEntry entry)
    {
        var byDigest = new Dictionary<string, OutboxEntry>(StringComparer.Ordinal);
        foreach (var e in entries) byDigest.TryAdd(DigestOf(e), e);
        var pending = new List<OutboxEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visit = new Stack<OutboxEntry>();
        visit.Push(entry);
        while (visit.TryPop(out var next))
        {
            foreach (var digest in Predecessors(next.Evidence))
            {
                if (!byDigest.TryGetValue(digest, out var before) || !seen.Add(digest)) continue;
                visit.Push(before);
                if (before.State == OutboxStates.Pending) pending.Add(before);
            }
        }
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++) order.TryAdd(entries[i].Id, i);
        return pending.OrderBy(e => order[e.Id]).ToList();
    }

    private static Dictionary<string, string> Query(EvidenceSearchParams? search, long? afterSequence)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string name, string? value)
        {
            if (value is not null) query[name] = value;
        }
        Put("vendor_id", search?.VendorId);
        Put("attestation_id", search?.AttestationId);
        Put("capability", search?.Capability);
        Put("resource_id", search?.ResourceId);
        Put("outcome", search?.Outcome);
        Put("entry_kind", search?.EntryKind);
        Put("decision_id", search?.DecisionId);
        Put("grant_id", search?.GrantId);
        Put("since", search?.Since);
        Put("until", search?.Until);
        Put("after_sequence", afterSequence?.ToString(CultureInfo.InvariantCulture));
        Put("limit", search?.Limit?.ToString(CultureInfo.InvariantCulture));
        return query;
    }

    private static Dictionary<string, string> ExportQuery(long? sinceSequence, int? limit)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sinceSequence is { } since) query["since_sequence"] = since.ToString(CultureInfo.InvariantCulture);
        if (limit is { } n) query["limit"] = n.ToString(CultureInfo.InvariantCulture);
        return query;
    }

    private static string? Text(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? Number(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;
}
