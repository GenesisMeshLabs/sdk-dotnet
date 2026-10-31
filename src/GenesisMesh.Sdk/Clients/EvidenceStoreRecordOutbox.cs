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

    /// <summary>Guards <see cref="_recordFlush"/>, <see cref="_observationBatch"/> and <see cref="_recordsPausedUntil"/>.</summary>
    private readonly object _recordGate = new();
    /// <summary>The record flush in progress.</summary>
    private RecordFlushRun? _recordFlush;
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
    /// Passing a record already in the outbox submits it again; a dead letter is returned as it is.
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
    /// Submits the record outbox's pending records: break-glass records first, then grant uses, then observations up
    /// to 100 at a time, then remediation records, each in the order they were added. A batch the NA refuses whole (a
    /// record it cannot read) or that is larger than it takes is split in halves until the record it refuses is tried
    /// alone; a batch it throttles (429) halves the batches that follow, and the records wait as long as its
    /// <c>Retry-After</c> asks. Records in backoff are skipped unless <see cref="FlushOptions.IgnoreBackoff"/>; a
    /// transient failure ends the run, leaving the rest for the next one. Run it at startup and on a timer. A call joins
    /// the run in progress when that run tries everything it asks for, and otherwise starts one once it ends.
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

    private IOutbox<RecordOutboxEntry> RequireRecordOutbox() =>
        RecordOutbox ?? throw new OutboxException("record_outbox_required", "no record outbox is configured (ClientOptions.RecordOutbox)");

    private async Task<RecordDelivery> Enqueued(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry fresh, CancellationToken ct)
    {
        var entry = (await outbox.List(ct).ConfigureAwait(false)).FirstOrDefault(e => e.Id == fresh.Id);
        if (entry is not null && RecordDigest(entry) != RecordDigest(fresh))
            throw new InvalidOperationException($"genesismesh: a different record with ID {fresh.Id} is in the record outbox");
        if (entry is null)
        {
            entry = fresh;
            await outbox.Add(entry, ct).ConfigureAwait(false);
        }
        if (entry.State == OutboxStates.DeadLetter) return new RecordDelivery { Queued = entry };
        return await AttemptRecord(outbox, entry, ct).ConfigureAwait(false);
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

    private void FreeRecords()
    {
        lock (_recordGate) _recordFlush = null;
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
            var entry = entries[i];
            if (stopped || !Due(entry, ignoreBackoff))
            {
                run.Pending.Add(entry);
                i++;
                continue;
            }
            if (entry.Kind != RecordOutboxKinds.Observation)
            {
                stopped = !run.Settle(entry, await AttemptRecord(outbox, entry, ct).ConfigureAwait(false));
                i++;
                continue;
            }
            int size;
            lock (_recordGate) size = _observationBatch;
            var batch = new List<RecordOutboxEntry>();
            while (i < entries.Count && batch.Count < size && entries[i].Kind == RecordOutboxKinds.Observation && Due(entries[i], ignoreBackoff))
                batch.Add(entries[i++]);
            stopped = await SubmitRecordBatch(outbox, batch, run, ct).ConfigureAwait(false);
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
    /// halves until the record it refuses is tried alone; a batch it throttles (429) halves the batches that follow.
    /// </summary>
    private async Task<bool> SubmitRecordBatch(IOutbox<RecordOutboxEntry> outbox, List<RecordOutboxEntry> batch, RecordRun run, CancellationToken ct)
    {
        IReadOnlyList<ObservationBatchResult> results;
        try
        {
            results = (await _t.PublicPostAsync<ObservationBatch>("/evidence/observations/batch",
                new Dictionary<string, object?> { ["observations"] = batch.Select(e => e.Record).ToList() }, idempotent: true, ct)
                .ConfigureAwait(false)).Results;
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
            var answer = results.FirstOrDefault(r => r.Index == index);
            if (answer is null)
            {
                run.Pending.Add(entry);
            }
            else if (answer.Status == "refused")
            {
                run.Settle(entry, new RecordDelivery { Queued = await RecordFailed(outbox, entry, Refusal(answer.Error)).ConfigureAwait(false) });
            }
            else
            {
                await RemoveRecord(outbox, entry.Id).ConfigureAwait(false);
                run.Admit(entry, answer.Status);
            }
        }
        return false;

        // One observation alone goes by itself, so the NA's answer is about that record.
        async Task<bool> Part(List<RecordOutboxEntry> records) => records.Count == 1
            ? !run.Settle(records[0], await AttemptRecord(outbox, records[0], ct).ConfigureAwait(false))
            : await SubmitRecordBatch(outbox, records, run, ct).ConfigureAwait(false);
    }

    /// <summary>A record a batch's answer refused, as the exception submitting it alone would have thrown.</summary>
    private static GenesisMeshException Refusal(RecordRefusal? error)
    {
        var code = error?.Code ?? "unknown";
        var refusal = ErrorMapping.ForStatus(code.EndsWith("_conflict", StringComparison.Ordinal) ? 409 : 422, code, error?.Message ?? code);
        refusal.Details = error?.Details ?? default;
        return refusal;
    }

    /// <summary>Submits one entry and stores what became of it. Throws only when the caller cancels.</summary>
    private async Task<RecordDelivery> AttemptRecord(IOutbox<RecordOutboxEntry> outbox, RecordOutboxEntry entry, CancellationToken ct)
    {
        RecordSubmission submission;
        try
        {
            var (path, field) = RecordRoute(entry.Kind);
            submission = await _t.PublicPostAsync<RecordSubmission>(path,
                new Dictionary<string, object?> { [field] = entry.Record }, idempotent: true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new RecordDelivery { Queued = await RecordFailed(outbox, entry, e).ConfigureAwait(false) };
        }
        await RemoveRecord(outbox, entry.Id).ConfigureAwait(false);
        return new RecordDelivery { Submission = submission };
    }

    /// <summary>The route that admits a record of <paramref name="kind"/>, and the field of the body that carries it.</summary>
    private static (string Path, string Field) RecordRoute(string kind) => kind switch
    {
        RecordOutboxKinds.Observation => ("/evidence/observations", "observation"),
        RecordOutboxKinds.BreakGlass => ("/evidence/break-glass", "record"),
        RecordOutboxKinds.Remediation => ("/evidence/remediations", "record"),
        RecordOutboxKinds.GrantExecution => ("/evidence/grant-executions", "evidence"),
        _ => throw new InvalidOperationException($"genesismesh: the record outbox holds a record of an unknown kind, {kind}"),
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
        var next = transient
            ? entry with { Attempts = attempts, LastError = failure, NextAttemptAt = EvidenceOutbox.Time(EvidenceOutbox.NextAttemptAt(attempts, error, now)) }
            : entry with { Attempts = attempts, State = OutboxStates.DeadLetter, NextAttemptAt = null, LastError = failure };
        try
        {
            await outbox.Update(next, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The entry keeps its previous state; the next flush settles it.
        }
        return next;
    }

    private static async Task RemoveRecord(IOutbox<RecordOutboxEntry> outbox, string id)
    {
        try
        {
            await outbox.Remove(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The NA holds the record: the next flush resubmits it, gets a duplicate and removes it.
        }
    }
}
