using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// The NA's evidence store (v0.59; 1.9.0 in this SDK): a controller submits the execution evidence it signed and reads
/// where each resource's chain ends; an operator searches what is stored. Reads need an operator key of the read tier
/// or above; registering keys needs a privileged one.
/// </summary>
public sealed class EvidenceStoreClient
{
    private readonly Transport _t;
    internal EvidenceStoreClient(Transport t) => _t = t;

    /// <summary>
    /// Submits one signed record (<see cref="ExecutionRecorder"/>). The executor's signature authenticates it, not
    /// operator headers. An identical resubmission returns <c>duplicate</c>, so it is safe to retry.
    /// POST /evidence/execution
    /// </summary>
    /// <exception cref="SecretMaterialException">The record carries secret material: nothing is sent.</exception>
    /// <exception cref="ValidationException">
    /// The NA refused the record, with its code: for example <c>evidence_out_of_scope</c> for a key limited to other
    /// resources, <c>evidence_decision_denied</c>, or <c>resource_chain_mismatch</c> when the resource's chain moved on.
    /// </exception>
    public Task<EvidenceSubmission> Submit(ExecutionEvidence evidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (EvidenceMetadata.CheckMetadataOnly(evidence.ExecutionParameters, evidence.OutcomeDetail) is { } reason)
            throw new SecretMaterialException(reason);
        return _t.PublicPostAsync<EvidenceSubmission>("/evidence/execution",
            new Dictionary<string, object?> { ["evidence"] = evidence }, idempotent: true, ct);
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
    /// <exception cref="ArgumentException">A segment of <paramref name="resourceId"/> is not a path segment.</exception>
    public Task<StoredResourceHead> StoredResourceHead(string resourceId, CancellationToken ct = default) =>
        _t.AdminGetAsync<StoredResourceHead>(
            "/admin/evidence/resource-heads/" + Transport.ResourcePath(resourceId, nameof(resourceId)), null, ct);

    /// <summary>
    /// Where the next record of a resource must link (<see cref="RecordExecutionRequest.PriorResource"/>), or null for
    /// a resource with no history. Submission still checks the chain: a head read before another controller's record
    /// is refused as <c>resource_chain_mismatch</c>.
    /// </summary>
    /// <exception cref="ArgumentException">A segment of <paramref name="resourceId"/> is not a path segment.</exception>
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
                var success = outcome == ExecutionOutcome.Success;
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
    public Task<ExecutorKeyState> RegisterExecutorKey(RegisterExecutorKeyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _t.AdminPostAsync<ExecutorKeyState>("/admin/evidence/executor-keys", request, ct);
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
