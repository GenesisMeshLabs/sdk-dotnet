namespace GenesisMesh;

// The record outbox (1.9.0): observations and break-glass records kept until the NA admits them, as the TypeScript
// SDK keeps them.
public sealed partial class EvidenceStoreClient
{
    /// <summary>
    /// The kinds a flush sends first, in this order. Break-glass records share the NA's submission rate with
    /// observations, and an observation of the same change then finds its break-glass record; grant uses go next, in
    /// the order they were made, so an observation of the change finds its use. Remediation records go last, once the
    /// changes they remediate are recorded and judged.
    /// </summary>
    private static readonly string[] FirstKinds = [RecordOutboxKinds.BreakGlass, RecordOutboxKinds.GrantExecution, RecordOutboxKinds.Observation];

    /// <summary>
    /// Guards <see cref="_recordFlush"/>, <see cref="_recordsInFlight"/>, <see cref="_recordsStored"/>,
    /// <see cref="_observationBatch"/> and <see cref="_recordsPausedUntil"/>.
    /// </summary>
    private readonly object _recordGate = new();
    /// <summary>The record flush in progress.</summary>
    private RecordFlushRun? _recordFlush;
    /// <summary>
    /// Records a call is submitting right now (<see cref="ClaimRecord"/>), each with what completes when that call lets
    /// it go: every other call leaves them to it.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource> _recordsInFlight = new(StringComparer.Ordinal);
    /// <summary>
    /// The state a call stored for a record while <see cref="_recordFlush"/> runs, null once removed: the run may have
    /// listed the record before, and leaves it to that call rather than sending it again or storing its older state
    /// over the newer one.
    /// </summary>
    private readonly Dictionary<string, RecordOutboxEntry?> _recordsStored = new(StringComparer.Ordinal);
    /// <summary>The most observations a batch carries: halved each time the NA throttles one (429).</summary>
    private int _observationBatch = MaxObservationBatch;
    /// <summary>Until when the NA asked for no more records (<c>Retry-After</c>): a flush waits unless it ignores the backoff.</summary>
    private DateTimeOffset _recordsPausedUntil;

    private sealed class RecordFlushRun(bool ignoreBackoff)
    {
        public bool IgnoreBackoff { get; } = ignoreBackoff;
        public TaskCompletionSource<RecordFlushResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>What one record flush run has done so far.</summary>
    private sealed class RecordRun
    {
        private readonly List<RecordOutboxEntry> _admitted = [];
        private readonly List<RecordOutboxEntry> _quarantined = [];
        private readonly List<RecordOutboxEntry> _deadLettered = [];

        public List<RecordOutboxEntry> Pending { get; } = [];

        /// <summary>Files what became of <paramref name="entry"/>; true when it is settled: admitted or dead-lettered.</summary>
        public bool Settle(RecordOutboxEntry entry, RecordDelivery delivery)
        {
            if (delivery.Submission is { } submission) return Admit(entry, submission.Status);
            var queued = delivery.Queued!;
            if (queued.State == OutboxStates.DeadLetter)
            {
                _deadLettered.Add(queued);
                return true;
            }
            Pending.Add(queued);
            return false;
        }

        /// <summary>Files <paramref name="entry"/> as admitted, and as quarantined when the NA kept it as a quarantine entry.</summary>
        public bool Admit(RecordOutboxEntry entry, string status)
        {
            _admitted.Add(entry);
            if (status == "quarantined") _quarantined.Add(entry);
            return true;
        }

        public RecordFlushResult Result() =>
            new() { Admitted = _admitted, Quarantined = _quarantined, Pending = Pending, DeadLettered = _deadLettered };
    }

    /// <summary>
    /// Keeps a signed observation (<see cref="ObservationRecorder.Record"/>) in the record outbox
    /// (<see cref="ClientOptions.RecordOutbox"/>) and submits it. A failed submission does not throw: a transient
    /// failure leaves the record pending, with a backoff, for <see cref="FlushRecords"/>, and a refusal no retry
    /// overcomes (<see cref="EvidenceOutbox.RecordPermanentRefusals"/>) keeps it as a dead letter with the NA's code.
    /// A success that does not say the NA admitted the record (<c>recorded</c>, <c>duplicate</c> or
    /// <c>quarantined</c>), such as an empty answer, leaves it pending too (<c>response_body_unreadable</c>). Passing a
    /// record already in the outbox submits it again; a dead letter is returned as it is. While another call is
    /// submitting the same record, this one returns it as stored (<see cref="RecordDelivery.Queued"/>) without sending
    /// it, or, when it is not stored yet, waits for that call and starts over.
    /// </summary>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.
    /// </exception>
    /// <exception cref="InvalidOperationException">The record outbox holds another record with the same ID.</exception>
    public async Task<RecordDelivery> EnqueueRecord(ObservationRecord observation, CancellationToken ct = default)
    {
        var outbox = RequireRecordOutbox();
        return await Enqueued(outbox, EvidenceOutbox.RecordEntry(observation), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a signed break-glass record (<see cref="ExecutionRecorder.SignBreakGlass"/>) in the record outbox and
    /// submits it, as <see cref="EnqueueRecord(ObservationRecord, CancellationToken)"/> does an observation.
    /// </summary>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.
    /// </exception>
    /// <exception cref="InvalidOperationException">The record outbox holds another record with the same ID.</exception>
    public async Task<RecordDelivery> EnqueueRecord(BreakGlassRecord record, CancellationToken ct = default)
    {
        var outbox = RequireRecordOutbox();
        return await Enqueued(outbox, EvidenceOutbox.RecordEntry(record), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a signed remediation record (<see cref="ExecutionRecorder.SignRemediation"/>) in the record outbox and
    /// submits it, as <see cref="EnqueueRecord(ObservationRecord, CancellationToken)"/> does an observation. The NA admits
    /// it once it holds the forward rotation's execution evidence it names: flush the evidence outbox first.
    /// </summary>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.
    /// </exception>
    /// <exception cref="InvalidOperationException">The record outbox holds another record with the same ID.</exception>
    public async Task<RecordDelivery> EnqueueRecord(RemediationRecord record, CancellationToken ct = default)
    {
        var outbox = RequireRecordOutbox();
        return await Enqueued(outbox, EvidenceOutbox.RecordEntry(record), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a grant use signed with its bearer key (<see cref="GrantRecords.SignUse"/>) in the record outbox and
    /// submits it, as <see cref="EnqueueRecord(ObservationRecord, CancellationToken)"/> does an observation. A grant's
    /// uses are admitted in their chain's order: one the NA cannot place yet (<c>grant_use_chain_gap</c>) or that is
    /// dated past its clock (<c>grant_use_in_future</c>) stays pending; one it refuses for good is quarantined and
    /// kept as a dead letter naming its quarantine entry.
    /// </summary>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.
    /// </exception>
    /// <exception cref="InvalidOperationException">The record outbox holds another record with the same ID.</exception>
    public async Task<RecordDelivery> EnqueueRecord(GrantExecutionEvidence use, CancellationToken ct = default)
    {
        var outbox = RequireRecordOutbox();
        return await Enqueued(outbox, EvidenceOutbox.RecordEntry(use), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits the record outbox's pending records: break-glass records first, then grant uses, then observations up
    /// to 100 at a time, then remediation records, each in the order they were added. A batch the NA refuses whole (a
    /// record it cannot read) or that is larger than it takes is split in halves until the record it refuses is tried
    /// alone. A batch it throttles (429) halves the batches of this client from then on. A transient answer that names a
    /// wait (<c>Retry-After</c>, such as a 429 or a 503) holds every record back that long, at least 5 seconds and at
    /// most 15 minutes; <see cref="EnqueueRecord(ObservationRecord, CancellationToken)"/> still submits its own record.
    /// Records in backoff are skipped unless <see cref="FlushOptions.IgnoreBackoff"/>; a transient failure ends the run,
    /// leaving the rest for the next one, and so does an answer to a batch this SDK cannot read (one that does not give
    /// each observation of the batch one result, by its index, with a status the NA sends), which leaves the whole batch
    /// pending (<c>response_body_unreadable</c>). A record another call is submitting is left to it, pending as far as
    /// this run knows, and so is one another call stored a new state for since this run read the outbox: pending as that
    /// call stored it, or left out once admitted or dead-lettered. Run it at startup and on a timer. A call joins the run
    /// in progress when that run tries everything it asks for, and otherwise starts one once it ends.
    /// </summary>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.
    /// </exception>
    public async Task<RecordFlushResult> FlushRecords(FlushOptions? options = null, CancellationToken ct = default)
    {
        var outbox = RequireRecordOutbox();
        var ignoreBackoff = options?.IgnoreBackoff ?? false;
        while (true)
        {
            RecordFlushRun? running;
            RecordFlushRun? mine = null;
            lock (_recordGate)
            {
                running = _recordFlush;
                if (running is null) _recordFlush = mine = new RecordFlushRun(ignoreBackoff);
            }
            if (mine is not null) return await TrackedRecords(mine, () => FlushRecordRun(outbox, ignoreBackoff, ct)).ConfigureAwait(false);
            var joins = running!.IgnoreBackoff || !ignoreBackoff;
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

    internal IOutbox<RecordOutboxEntry> RequireRecordOutbox() =>
        RecordOutbox ?? throw new OutboxException("record_outbox_required", "no record outbox is configured (ClientOptions.RecordOutbox)");

    private async Task<RecordDelivery> Enqueued(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry fresh, CancellationToken ct)
    {
        while (true)
        {
            // Claimed before the outbox is read: a flush that lists the record meanwhile leaves it to this call, and so
            // does another enqueue of it, so what this call reads is still the record's state when it stores the next.
            var holder = ClaimRecord(fresh.Id);
            try
            {
                var entry = (await outbox.List(ct).ConfigureAwait(false)).FirstOrDefault(e => e.Id == fresh.Id);
                if (entry is not null && RecordDigest(entry) != RecordDigest(fresh))
                    throw new InvalidOperationException($"genesismesh: a different record with ID {fresh.Id} is in the record outbox");
                if (holder is null)
                {
                    if (entry is null)
                    {
                        entry = fresh;
                        await outbox.Add(entry, ct).ConfigureAwait(false);
                    }
                    if (entry.State == OutboxStates.DeadLetter) return new RecordDelivery { Queued = entry };
                    return await AttemptRecord(outbox, entry, ct).ConfigureAwait(false);
                }
                // Stored, and another call is submitting it: pending as far as this call knows.
                if (entry is not null) return new RecordDelivery { Queued = entry };
            }
            finally
            {
                if (holder is null) ReleaseRecord(fresh.Id);
            }
            // Not stored yet: the call holding it may fail to store it, or hold another record with its ID.
            await holder.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Marks a record as being submitted by this call: null when it now is, otherwise what completes when the call
    /// submitting it lets it go.
    /// </summary>
    private Task? ClaimRecord(string id)
    {
        lock (_recordGate)
        {
            if (_recordsInFlight.TryGetValue(id, out var holder)) return holder.Task;
            _recordsInFlight[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return null;
        }
    }

    private void ReleaseRecord(string id)
    {
        TaskCompletionSource? holder;
        lock (_recordGate) _recordsInFlight.Remove(id, out holder);
        holder?.TrySetResult();
    }

    /// <summary>
    /// Claims <paramref name="entry"/> for the flush <paramref name="run"/> when it is to be <paramref name="tried"/>
    /// now; true when claimed. One another call stored a new state for since the run read the outbox is that call's:
    /// pending as that call stored it, or left out once admitted or dead-lettered. One not tried, or that another call
    /// is submitting, is pending as far as the run knows.
    /// </summary>
    private bool TakeRecord(RecordOutboxEntry entry, bool tried, RecordRun run)
    {
        bool stored, claimed = false;
        RecordOutboxEntry? newer;
        lock (_recordGate)
        {
            stored = _recordsStored.TryGetValue(entry.Id, out newer);
            if (tried && !stored)
                claimed = _recordsInFlight.TryAdd(entry.Id, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        if (stored)
        {
            if (newer is { State: OutboxStates.Pending }) run.Pending.Add(newer);
        }
        else if (!claimed)
        {
            run.Pending.Add(entry);
        }
        return claimed;
    }

    /// <summary>
    /// Notes, for the flush in progress, the state stored for <paramref name="id"/>, null once removed. Called while the
    /// record is claimed and after the store's write, so a run that lists the record before the write sees this note
    /// before it can claim the record.
    /// </summary>
    private void RecordStored(string id, RecordOutboxEntry? state)
    {
        lock (_recordGate)
        {
            if (_recordFlush is not null) _recordsStored[id] = state;
        }
    }

    /// <summary>The digest of an entry's record without its signature: a record never changes once signed.</summary>
    private static string RecordDigest(RecordOutboxEntry entry) => OfflineVerifier.OutOfBandDigest(entry.Record.GetRawText());

    /// <summary>
    /// Runs <paramref name="flush"/> as the record flush in progress, <paramref name="run"/>, which this caller claimed;
    /// the slot is free again before anyone waiting on the run sees its end.
    /// </summary>
    private async Task<RecordFlushResult> TrackedRecords(RecordFlushRun run, Func<Task<RecordFlushResult>> flush)
    {
        RecordFlushResult done;
        try
        {
            done = await flush().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            FreeRecords();
            if (e is OperationCanceledException canceled) run.Result.TrySetCanceled(canceled.CancellationToken);
            else run.Result.TrySetException(e);
            // Observed here: a run nobody joined does not report its failure as unobserved.
            _ = run.Result.Task.Exception;
            throw;
        }
        FreeRecords();
        run.Result.TrySetResult(done);
        return done;
    }

    /// <summary>Frees the record flush slot, with the states stored during the run.</summary>
    private void FreeRecords()
    {
        lock (_recordGate)
        {
            _recordFlush = null;
            _recordsStored.Clear();
        }
    }

    private async Task<RecordFlushResult> FlushRecordRun(IOutbox<RecordOutboxEntry> outbox, bool ignoreBackoff, CancellationToken ct)
    {
        var run = new RecordRun();
        var pending = (await outbox.List(ct).ConfigureAwait(false)).Where(e => e.State == OutboxStates.Pending).ToList();
        var entries = FirstKinds.SelectMany(kind => pending.Where(e => e.Kind == kind))
            .Concat(pending.Where(e => !FirstKinds.Contains(e.Kind))).ToList();
        var stopped = false;
        for (var i = 0; i < entries.Count;)
        {
            var entry = entries[i++];
            if (!TakeRecord(entry, tried: !stopped && Due(entry, ignoreBackoff), run)) continue;
            if (entry.Kind != RecordOutboxKinds.Observation)
            {
                try
                {
                    stopped = !run.Settle(entry, await AttemptRecord(outbox, entry, ct).ConfigureAwait(false));
                }
                finally
                {
                    ReleaseRecord(entry.Id);
                }
                continue;
            }
            int size;
            lock (_recordGate) size = _observationBatch;
            var batch = new List<RecordOutboxEntry> { entry };
            try
            {
                while (i < entries.Count && batch.Count < size && entries[i].Kind == RecordOutboxKinds.Observation && Due(entries[i], ignoreBackoff))
                {
                    var next = entries[i++];
                    if (TakeRecord(next, tried: true, run)) batch.Add(next);
                }
                stopped = await SubmitRecordBatch(outbox, batch, run, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var claimed in batch) ReleaseRecord(claimed.Id);
            }
        }
        return run.Result();
    }

    /// <summary>The entry is to be tried now: its backoff, and the NA's <c>Retry-After</c>, have run out, or are ignored.</summary>
    private bool Due(RecordOutboxEntry entry, bool ignoreBackoff)
    {
        if (ignoreBackoff) return true;
        var now = DateTimeOffset.UtcNow;
        lock (_recordGate)
        {
            if (_recordsPausedUntil > now) return false;
        }
        return !(EvidenceOutbox.ParseTime(entry.NextAttemptAt) is { } next && next > now);
    }

    /// <summary>
    /// Submits a batch of observations and settles each; true when a transient failure ends the run. A batch the NA
    /// refuses whole (a record it cannot read, <c>invalid_json</c>) or that is larger than it takes (413) is split in
    /// halves until the record it refuses is tried alone; a batch it throttles (429) halves the batches that follow. An
    /// answer this SDK cannot read (<see cref="BatchAnswers"/>) leaves the whole batch pending.
    /// </summary>
    private async Task<bool> SubmitRecordBatch(IOutbox<RecordOutboxEntry> outbox, List<RecordOutboxEntry> batch, RecordRun run, CancellationToken ct)
    {
        RecordBatchResult?[] results;
        try
        {
            var answer = await _t.PublicPostAsync<RecordBatchAnswer?>("/evidence/observations/batch",
                new Dictionary<string, object?> { ["observations"] = batch.Select(e => e.Record).ToList() }, idempotent: true, ct)
                .ConfigureAwait(false) ?? throw EvidenceOutbox.EmptyAnswer();
            results = BatchAnswers(answer, batch.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var status = (e as GenesisMeshException)?.Status;
            if (status != 413 && EvidenceOutbox.ClassifyRecord(e).Transient)
            {
                if (status == 429)
                {
                    lock (_recordGate) _observationBatch = Math.Min(_observationBatch, Math.Max(1, batch.Count / 2));
                }
                foreach (var entry in batch) run.Settle(entry, new RecordDelivery { Queued = await RecordFailed(outbox, entry, e).ConfigureAwait(false) });
                return true;
            }
            if (batch.Count == 1) return await Part(batch).ConfigureAwait(false);
            var half = (batch.Count + 1) / 2;
            if (await Part(batch.GetRange(0, half)).ConfigureAwait(false))
            {
                run.Pending.AddRange(batch.GetRange(half, batch.Count - half));
                return true;
            }
            return await Part(batch.GetRange(half, batch.Count - half)).ConfigureAwait(false);
        }
        for (var index = 0; index < batch.Count; index++)
        {
            var entry = batch[index];
            var answer = results[index]!;
            if (answer.Status == "refused")
            {
                run.Settle(entry, new RecordDelivery { Queued = await RecordFailed(outbox, entry, Refusal(answer.Error)).ConfigureAwait(false) });
            }
            else
            {
                await RemoveRecord(outbox, entry.Id).ConfigureAwait(false);
                run.Admit(entry, answer.Status!);
            }
        }
        return false;

        // One observation alone goes by itself, so the NA's answer is about that record.
        async Task<bool> Part(List<RecordOutboxEntry> records) => records.Count == 1
            ? !run.Settle(records[0], await AttemptRecord(outbox, records[0], ct).ConfigureAwait(false))
            : await SubmitRecordBatch(outbox, records, run, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The result for each observation of a batch of <paramref name="count"/>, by index, every one of them set. Throws
    /// <c>response_body_unreadable</c> for an answer that does not give each observation one result, as the NA does, so
    /// that no record is taken for another's: a result without the index of an observation in the batch or with the
    /// index of another result, an observation without a result, or a status the NA does not send.
    /// </summary>
    private static RecordBatchResult?[] BatchAnswers(RecordBatchAnswer answer, int count)
    {
        var byIndex = new RecordBatchResult?[count];
        foreach (var result in answer.Results ?? [])
        {
            if (result is null || result.Index is not { } index || index < 0 || index >= count || byIndex[index] is not null)
                throw EvidenceOutbox.UnreadAnswer("a result of the NA's answer to the batch names no observation of it, or one another result names");
            if (result.Status != "refused" && !Admits(result.Status))
                throw EvidenceOutbox.UnreadAnswer($"a result of the NA's answer to the batch has the status {Quoted(result.Status)}");
            byIndex[index] = result;
        }
        var missing = Array.IndexOf(byIndex, null);
        if (missing >= 0) throw EvidenceOutbox.UnreadAnswer($"the NA's answer to the batch has no result for observation {missing}");
        return byIndex;
    }

    /// <summary>True for the status of a record the NA admitted: recorded, a duplicate of one it holds, or quarantined.</summary>
    private static bool Admits(string? status) => status is "recorded" or "duplicate" or "quarantined";

    private static string Quoted(string? status) => status is null ? "null" : $"\"{status}\"";

    /// <summary>A record a batch's answer refused, as the exception submitting it alone would have thrown.</summary>
    private static GenesisMeshException Refusal(RecordRefusal? error)
    {
        var code = error?.Code is { Length: > 0 } named ? named : "unknown";
        var message = error?.Message is { Length: > 0 } said ? said : code;
        var refusal = ErrorMapping.ForStatus(code.EndsWith("_conflict", StringComparison.Ordinal) ? 409 : 422, code, message);
        refusal.Details = error?.Details ?? default;
        return refusal;
    }

    /// <summary>Submits one entry and stores what became of it. Throws only when the caller cancels.</summary>
    private async Task<RecordDelivery> AttemptRecord(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry entry, CancellationToken ct)
    {
        if (RecordRoute(entry.Kind) is not { } route)
        {
            // No route takes it: no retry can send it, and it must not hold back the records after it.
            return new RecordDelivery
            {
                Queued = await KeepRecord(outbox, entry with
                {
                    Attempts = entry.Attempts + 1,
                    State = OutboxStates.DeadLetter,
                    NextAttemptAt = null,
                    LastError = new SubmissionFailure
                    {
                        Status = 0,
                        Code = EvidenceOutbox.UnknownRecordKind,
                        Message = $"the record outbox holds a record of an unknown kind, {entry.Kind}",
                    },
                }).ConfigureAwait(false),
            };
        }
        RecordSubmission submission;
        try
        {
            submission = await _t.PublicPostAsync<RecordSubmission?>(route.Path,
                new Dictionary<string, object?> { [route.Field] = entry.Record }, idempotent: true, ct).ConfigureAwait(false) ?? throw EvidenceOutbox.EmptyAnswer();
            // A success that does not say the NA admitted the record is no answer of the NA's: it may or may not hold it.
            if (!Admits(submission.Status))
                throw EvidenceOutbox.UnreadAnswer($"the NA's answer to the record has the status {Quoted(submission.Status)}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new RecordDelivery { Queued = await RecordFailed(outbox, entry, e).ConfigureAwait(false) };
        }
        await RemoveRecord(outbox, entry.Id).ConfigureAwait(false);
        return new RecordDelivery { Submission = submission };
    }

    /// <summary>
    /// The route that admits a record of <paramref name="kind"/>, and the field of the body that carries it; null for a
    /// kind no route takes.
    /// </summary>
    private static (string Path, string Field)? RecordRoute(string kind) => kind switch
    {
        RecordOutboxKinds.Observation => ("/evidence/observations", "observation"),
        RecordOutboxKinds.BreakGlass => ("/evidence/break-glass", "record"),
        RecordOutboxKinds.Remediation => ("/evidence/remediations", "record"),
        RecordOutboxKinds.GrantExecution => ("/evidence/grant-executions", "evidence"),
        _ => null,
    };

    /// <summary>
    /// The entry after a failed submission, stored: pending with a backoff, or a dead letter. A wait the NA asked for
    /// (<c>Retry-After</c>) holds every record back until it has passed.
    /// </summary>
    private async Task<RecordOutboxEntry> RecordFailed(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry entry, Exception error)
    {
        var (failure, transient) = EvidenceOutbox.ClassifyRecord(error);
        var attempts = entry.Attempts + 1;
        var now = DateTimeOffset.UtcNow;
        if (transient && error is GenesisMeshException { RetryAfter: not null })
        {
            var until = EvidenceOutbox.NextAttemptAt(0, error, now);
            lock (_recordGate)
            {
                if (until > _recordsPausedUntil) _recordsPausedUntil = until;
            }
        }
        return await KeepRecord(outbox, transient
            ? entry with { Attempts = attempts, LastError = failure, NextAttemptAt = EvidenceOutbox.Time(EvidenceOutbox.NextAttemptAt(attempts, error, now)) }
            : entry with { Attempts = attempts, State = OutboxStates.DeadLetter, NextAttemptAt = null, LastError = failure }).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores an entry's new state and returns it. Whatever the outbox throws, even a cancellation of its own, the
    /// answer stands: the entry keeps its previous state, and the next flush settles it.
    /// </summary>
    private async Task<RecordOutboxEntry> KeepRecord(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry next)
    {
        try
        {
            await outbox.Update(next, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every failure of the outbox is the same here: the entry is settled later.
        }
        RecordStored(next.Id, next);
        return next;
    }

    private async Task RemoveRecord(IOutbox<RecordOutboxEntry> outbox, string id)
    {
        try
        {
            await outbox.Remove(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The NA holds the record: the next flush resubmits it, gets a duplicate and removes it.
        }
        RecordStored(id, null);
    }
}
