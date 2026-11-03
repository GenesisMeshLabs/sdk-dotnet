using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>Why a grant cannot be used, for which a holder falls back to break-glass (the public contract's <c>grant_fallback</c> rule).</summary>
public static class GrantFallbackCodes
{
    /// <summary>The grant has ended, or the NA key that signed it no longer vouches (1.8.0).</summary>
    public const string Expired = "grant_expired";
    /// <summary>Its budget is spent.</summary>
    public const string BudgetExhausted = "grant_budget_exhausted";
    /// <summary>It is revoked: the revocation feed the holder holds lists it, or the NA said so.</summary>
    public const string Revoked = "grant_revoked";
    /// <summary>The uses its time token allows are spent, and the NA could not be asked for another.</summary>
    public const string TimeTokenExhausted = "grant_time_token_exhausted";

    /// <summary>Every code for which the holder falls back.</summary>
    public static IReadOnlySet<string> All { get; } = new[] { Expired, BudgetExhausted, Revoked, TimeTokenExhausted }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>What an action runs under (<see cref="GrantAuthority.Path"/>).</summary>
public static class GrantPaths
{
    /// <summary>The grant: the action is a use of it.</summary>
    public const string Grant = "grant";
    /// <summary>A decision of the NA's, when the grant could not be used and the NA answered.</summary>
    public const string Decision = "decision";
    /// <summary>Break-glass, when the grant could not be used and the NA could not be reached.</summary>
    public const string BreakGlass = "break_glass";
}

/// <summary>
/// What a <see cref="GrantHolder"/> keeps between runs (the TypeScript SDK's <c>GrantHolderState</c>, the same JSON, nulls
/// written):
/// persist it (<see cref="GrantHolderOptions.Persist"/>) with the record outbox, and resume from it
/// (<see cref="GrantHolderOptions.State"/>).
/// </summary>
public sealed class GrantHolderState
{
    /// <summary>The grant, as the NA signed it.</summary>
    [JsonPropertyName("grant")]
    public CapabilityGrant Grant { get; set; } = new();

    /// <summary>The latest time token, exactly as the NA signed it.</summary>
    [JsonPropertyName("time_token")]
    public JsonElement TimeToken { get; set; }

    /// <summary>The uses made so far: the head of the grant's use chain.</summary>
    [JsonPropertyName("uses")]
    public int Uses { get; set; }

    /// <summary>The digest of the last use; null before the first.</summary>
    [JsonPropertyName("head_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? HeadDigest { get; set; }

    /// <summary>When the last use ran; null before the first.</summary>
    [JsonPropertyName("head_executed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? HeadExecutedAt { get; set; }

    /// <summary>The latest revocation feed the holder verified; null for none.</summary>
    [JsonPropertyName("revocation_feed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public SovereignRevocationFeed? RevocationFeed { get; set; }

    /// <summary>What the NA last answered when it refused a time token (<c>grant_expired</c>, <c>grant_revoked</c>, ...); null for none.</summary>
    [JsonPropertyName("unusable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Unusable { get; set; }
}

/// <summary>How a <see cref="GrantHolder"/> holds a grant.</summary>
public sealed class GrantHolderOptions
{
    /// <summary>
    /// The client: with a record outbox (<see cref="ClientOptions.RecordOutbox"/>) for the uses, and an operator key
    /// that evaluates when the holder falls back to a governed action.
    /// </summary>
    public required GenesisMeshClient Client { get; init; }

    /// <summary>The grant's bearer key: its <see cref="ISigner.KeyId"/> is the grant's <c>bearer_key_id</c>.</summary>
    public required ISigner Signer { get; init; }

    /// <summary>
    /// The NA's verified key history (<see cref="NetworkAuthorityClient"/>), or <see cref="NAKeyHistory.ValidAlways"/>
    /// of its keys: the grant, its time tokens and the revocation feeds must verify under it.
    /// </summary>
    public required NAKeyHistory NAKeys { get; init; }

    /// <summary>
    /// The bearer key's resource prefix, as it is registered: a use outside it, or without a resource, is refused
    /// offline (<c>grant_use_out_of_scope</c>), as the NA would quarantine it. Null for none.
    /// </summary>
    public string? ResourcePrefix { get; init; }

    /// <summary>A grant just issued (<see cref="IssuedGrant.Grant"/>), with <see cref="TimeToken"/>; or give <see cref="State"/> to resume.</summary>
    public CapabilityGrant? Grant { get; init; }

    /// <summary>The grant's first time token (<see cref="IssuedGrant.TimeToken"/>), exactly as the NA signed it.</summary>
    public JsonElement? TimeToken { get; init; }

    /// <summary>The state a holder of the grant persisted, to resume from.</summary>
    public GrantHolderState? State { get; init; }

    /// <summary>Called with the new state after every change, before the call that made it returns; null to keep nothing.</summary>
    public Func<GrantHolderState, CancellationToken, Task>? Persist { get; init; }

    /// <summary>The clock; null for the system clock.</summary>
    public Func<DateTimeOffset>? Now { get; init; }
}

/// <summary>Break-glass through a governed action, when the grant cannot be used (<see cref="GrantActRequest.Fallback"/>).</summary>
public sealed class GrantFallback
{
    /// <summary>Why the change cannot wait: 1 to 1024 characters, no secret values.</summary>
    public required string Justification { get; init; }

    /// <summary>How the NA's decision is verified, should it answer (as <see cref="GovernedActionRequest.Verify"/>).</summary>
    public required GovernedVerification Verify { get; init; }

    /// <summary>The attestation the evaluation and the break-glass record name; null for the grant's basis attestation.</summary>
    public string? AttestationId { get; init; }

    /// <summary>Attributes for the evaluation and the break-glass record; null for none.</summary>
    public IDictionary<string, object?>? Attributes { get; init; }
}

/// <summary>A change to make within a grant (<see cref="GrantHolder.Act"/>).</summary>
public sealed class GrantActRequest
{
    /// <summary>One of the grant's capabilities.</summary>
    public required string Capability { get; init; }

    /// <summary>The request's parameters: inside the grant's envelope. Null for none.</summary>
    public IDictionary<string, object?>? RequestParameters { get; init; }

    /// <summary>The resource: an identifier, never a value. Set with <see cref="ResourceAction"/>.</summary>
    public string? ResourceId { get; init; }

    /// <summary>A value of <see cref="ResourceActions"/>. Set with <see cref="ResourceId"/>.</summary>
    public string? ResourceAction { get; init; }

    /// <summary>What to do when the grant cannot be used; null to throw <see cref="GrantUnusableException"/> instead.</summary>
    public GrantFallback? Fallback { get; init; }
}

/// <summary>What an action runs under.</summary>
public sealed class GrantAuthority
{
    /// <summary>A value of <see cref="GrantPaths"/>.</summary>
    public string Path { get; init; } = "";

    /// <summary>Under the grant: the grant.</summary>
    public CapabilityGrant? Grant { get; init; }

    /// <summary>Under the grant: the use's place in its chain.</summary>
    public int UseSequence { get; init; }

    /// <summary>Under a decision: the NA's verified decision.</summary>
    public BoundaryDecision? Decision { get; init; }
}

/// <summary>The action ran under the grant: its use, signed and kept in the record outbox.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class GrantUseResult<T>
{
    /// <summary>The action's value.</summary>
    public T? Value { get; init; }

    /// <summary>The signed use.</summary>
    public GrantExecutionEvidence Use { get; init; } = new();

    /// <summary>The NA's acknowledgement, when it was reached and admitted the use.</summary>
    public RecordSubmission? Submission { get; init; }

    /// <summary>The record outbox entry holding the use otherwise: pending, or a dead letter when the NA refused it.</summary>
    public RecordOutboxEntry? Queued { get; init; }

    /// <summary>The reported metadata the secret guard refused, left out of the use; empty when nothing was.</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
}

/// <summary>The grant could not be used (<see cref="Unusable"/>): a governed action with break-glass decided what ran.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class GrantFallbackResult<T>
{
    /// <summary>Why the grant could not be used: a value of <see cref="GrantFallbackCodes"/>.</summary>
    public string Unusable { get; init; } = "";

    /// <summary>What the governed action did: under the NA's decision, or under break-glass.</summary>
    public required GovernedOutcome<T> Outcome { get; init; }

    /// <summary><see cref="GrantPaths.BreakGlass"/> or <see cref="GrantPaths.Decision"/>.</summary>
    public string Path => Outcome.BrokeGlass ? GrantPaths.BreakGlass : GrantPaths.Decision;
}

/// <summary>What <see cref="GrantHolder.Act"/> did: a use of the grant, or a fallback.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class GrantActResult<T>
{
    /// <summary>A value of <see cref="GrantPaths"/>.</summary>
    /// <exception cref="InvalidOperationException">The result holds neither.</exception>
    public string Path => UnderGrant is not null ? GrantPaths.Grant
        : Fallback?.Path ?? throw new InvalidOperationException("genesismesh: the result holds neither a use nor a fallback");

    /// <summary>The use, when the action ran under the grant; null otherwise.</summary>
    public GrantUseResult<T>? UnderGrant { get; init; }

    /// <summary>The fallback, when the grant could not be used; null otherwise.</summary>
    public GrantFallbackResult<T>? Fallback { get; init; }
}

/// <summary>What one <see cref="GrantHolder.Sync"/> did; a step the NA did not answer is left out and <see cref="Reachable"/> false.</summary>
public sealed class GrantSyncResult
{
    /// <summary>True when every step got an answer from the NA.</summary>
    public bool Reachable { get; init; }

    /// <summary>The time token the NA issued, exactly as it signed it; null when it issued none.</summary>
    public JsonElement? TimeToken { get; init; }

    /// <summary>The NA's refusal of a time token, when it refused (<c>grant_expired</c>, <c>grant_revoked</c>, ...); null otherwise.</summary>
    public string? Unusable { get; init; }

    /// <summary>
    /// The NA's refusal of a time token for another reason, such as <c>time_token_request_stale</c> (this clock is more
    /// than 300 seconds off the NA's); the upload still ran. Null otherwise.
    /// </summary>
    public GenesisMeshException? TimeTokenRefusal { get; init; }

    /// <summary>The record outbox flush, when it ran.</summary>
    public RecordFlushResult? Flushed { get; init; }
}

/// <summary>
/// The request is outside the grant, or the grant cannot be held (<c>grant_invalid</c>, <c>grant_bearer_mismatch</c>,
/// <c>grant_time_token_invalid</c>, <c>feed_invalid_signature</c>, ...): nothing ran. <see cref="GenesisMeshException.Code"/> names why.
/// </summary>
public sealed class GrantRefusedException : GenesisMeshException
{
    /// <summary>A refusal with <paramref name="code"/> for <paramref name="detail"/>.</summary>
    /// <param name="code">Why.</param>
    /// <param name="detail">What is refused.</param>
    public GrantRefusedException(string code, string detail)
        : base(0, code, detail, "genesismesh: " + detail)
    {
    }
}

/// <summary>
/// The grant cannot be used (<see cref="GenesisMeshException.Code"/> a value of <see cref="GrantFallbackCodes"/>), and no
/// fallback was given or it cannot run: nothing ran.
/// </summary>
public sealed class GrantUnusableException : GenesisMeshException
{
    /// <summary>The grant cannot be used, for <paramref name="code"/>.</summary>
    /// <param name="code">A value of <see cref="GrantFallbackCodes"/>.</param>
    /// <param name="detail">What is missing.</param>
    public GrantUnusableException(string code, string detail)
        : base(0, code, detail, "genesismesh: " + detail)
    {
    }
}

/// <summary>
/// The action ran and its use is kept, but the holder's state could not be persisted
/// (<c>grant_state_not_kept</c>): <see cref="Exception.InnerException"/> is why. The holder itself moved on; persist
/// <see cref="GrantHolder.State"/> once the cause is fixed.
/// </summary>
public sealed class GrantStateNotKeptException : GenesisMeshException
{
    internal GrantStateNotKeptException(Exception cause, object? value, GrantExecutionEvidence use, RecordSubmission? submission, RecordOutboxEntry? queued)
        : base(0, "grant_state_not_kept", "the use is kept in the record outbox; the holder state was not persisted: " + cause.Message,
            "genesismesh: the use is kept in the record outbox; the holder state was not persisted: " + cause.Message, cause)
    {
        Value = value;
        Use = use;
        Submission = submission;
        Queued = queued;
    }

    /// <summary>The action's value.</summary>
    public object? Value { get; }

    /// <summary>The signed use.</summary>
    public GrantExecutionEvidence Use { get; }

    /// <summary>The NA's acknowledgement, when it admitted the use.</summary>
    public RecordSubmission? Submission { get; }

    /// <summary>The record outbox entry holding the use otherwise.</summary>
    public RecordOutboxEntry? Queued { get; }

    /// <summary>When the action itself failed: its error, and <see cref="Use"/> is its failure use. Null when it ran.</summary>
    public Exception? ActionError { get; internal init; }
}

/// <summary>
/// The holder of a pre-issued grant (Genesis Mesh 1.6.0; 1.9.0 in this SDK), as the TypeScript SDK's
/// <c>GrantHolder</c>: it acts within the grant while the NA cannot be reached, keeps each use in the record outbox,
/// and uploads the uses when the NA is back. One instance per grant and process; its calls run one at a time.
/// <para>
/// Before each change it checks the grant offline, in this order (the NA's where the two share a rule): secret material
/// in the request parameters (<c>grant_use_secret_material</c>), the bearer key's resource prefix
/// (<c>grant_use_out_of_scope</c>), the capability (<c>grant_capability_mismatch</c>), the closed envelope
/// (<c>grant_envelope_mismatch</c>), the budget, counting its own uses but those the NA has quarantined
/// (<c>grant_budget_exhausted</c>), the validity (<c>grant_expired</c> past its end, or once the NA key that signed it
/// has passed its vouching horizon; <c>grant_use_outside_validity</c> before its start), the revocation feed it holds
/// and what the NA last answered (<c>grant_revoked</c>, or the NA's code), and the uses left on its time token: at most
/// <c>max_uses_per_time_token</c> after the use the token names (<c>grant_time_token_exhausted</c>, after asking for a
/// fresh token when the NA answers).
/// </para>
/// <para>
/// Every use it signs takes the next place in its chain: one the NA refuses for good is quarantined and keeps its place,
/// so the uses after it are judged on their own. Offline it counts every use it signed against the budget; once the NA
/// has quarantined one that continued the chain (a dead letter in the record outbox naming its quarantine entry), that
/// use no longer counts, as the NA's budget counts admitted uses only. It never signs a use the NA would refuse without
/// storing it: secret material is checked first.
/// </para>
/// <para>
/// The public contract's <c>grant_fallback</c> rule: when the grant has ended, its budget or time token is spent, or
/// it is revoked, the holder does not block the change. It asks the NA through a governed action with break-glass
/// (<see cref="Governance.GovernedAction{T}(GenesisMeshClient, ExecutionRecorder, GovernedActionRequest, BreakGlassOptions, Func{BoundaryDecision?, CancellationToken, Task{ActionReport{T}?}}, CancellationToken)"/>),
/// naming the capability: an NA that answers decides (a DENY is never broken through), and one that cannot be reached
/// (no answer, a timeout, a 5xx or a 429) leaves the change to run under break-glass, recorded and judged after the
/// fact. Any other error of the governed action (a refused evaluation, a decision that fails verification) is thrown
/// as it is, and nothing runs.
/// </para>
/// <para>
/// Its calls run one at a time, and its state changes whole: <see cref="State"/> never shows a use counted without its
/// digest. Do not call the holder from inside an action: that call would wait for the action. On its first change it
/// takes up the latest use of its grant the record outbox holds beyond its state (kept before the process stopped,
/// unpersisted), so no place in the chain is signed twice.
/// </para>
/// </summary>
public sealed class GrantHolder
{
    /// <summary>The NA's answers to a time token request that end the grant for its holder.</summary>
    private static readonly FrozenSet<string> Ended =
        new[] { "grant_expired", "grant_revoked", "grant_budget_exhausted", "grant_bearer_key_retired" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The refusals of a quarantined use that still counts against the budget: a conflict or a chain mismatch did not
    /// continue the chain, and a use refused as over budget means the holder's count is short.
    /// </summary>
    private static readonly FrozenSet<string> StillCounted =
        new[] { "grant_use_conflict", "grant_use_chain_mismatch", "grant_budget_exhausted" }.ToFrozenSet(StringComparer.Ordinal);

    private const string Probe = "genesis-mesh grant holder: bearer key check for grant ";

    private readonly GrantHolderState _state;
    private readonly GenesisMeshClient _client;
    private readonly ISigner _signer;
    private readonly NAKeyHistory _naKeys;
    private readonly string? _resourcePrefix;
    private readonly Func<GrantHolderState, CancellationToken, Task>? _persist;
    private readonly Func<DateTimeOffset> _now;
    private readonly ExecutionRecorder _recorder;
    private readonly object _turns = new();
    private Task _last = Task.CompletedTask;
    private readonly object _stateLock = new();
    private bool _bearerChecked;
    private bool _adopted;

    /// <summary>A holder of the grant in <paramref name="options"/>, checked before anything else.</summary>
    /// <param name="options">The grant and its first time token, or a state to resume from, and how to hold it.</param>
    /// <exception cref="ArgumentException">Neither a grant with its time token nor a state is given.</exception>
    /// <exception cref="GrantRefusedException">
    /// <c>grant_invalid</c>: the grant is not signed by the NA, or not in its form, or its NA key no longer vouches.
    /// <c>grant_bearer_mismatch</c>: the signer is not the grant's bearer key. <c>grant_time_token_invalid</c>: the time
    /// token is not the NA's for this grant and holder.
    /// </exception>
    /// <exception cref="OutboxException">The client has no record outbox (<c>record_outbox_required</c>).</exception>
    public GrantHolder(GrantHolderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Client);
        ArgumentNullException.ThrowIfNull(options.Signer);
        ArgumentNullException.ThrowIfNull(options.NAKeys);
        _state = options.State is { } state ? Copy(state)
            : options.Grant is { } grant && options.TimeToken is { } token
                ? new GrantHolderState { Grant = Copy(grant), TimeToken = token.Clone() }
                : throw new ArgumentException("genesismesh: a grant holder needs a grant and its time token, or a state", nameof(options));
        _client = options.Client;
        _signer = options.Signer;
        _naKeys = options.NAKeys;
        _resourcePrefix = options.ResourcePrefix;
        _persist = options.Persist;
        _now = options.Now ?? (() => DateTimeOffset.UtcNow);
        var held = _state.Grant;
        // 1.8.0: judged now, so a grant of an NA key past its vouching horizon is refused.
        if (!GrantVouches(held, _now()))
            throw new GrantRefusedException("grant_invalid", "the grant is not signed by the NA, or not in its form, or its NA key no longer vouches");
        if (_signer.KeyId != held.BearerKeyId)
            throw new GrantRefusedException("grant_bearer_mismatch", $"the signer {_signer.KeyId} is not the grant's bearer key {held.BearerKeyId}");
        CheckToken(_state.TimeToken, held, _state.Uses);
        _client.EvidenceStore.RequireRecordOutbox(); // a holder keeps its uses there
        _recorder = new ExecutionRecorder(held.BearerSovereignId, _signer);
    }

    /// <summary>A copy of the holder's state, to persist and resume from.</summary>
    public GrantHolderState State
    {
        get
        {
            lock (_stateLock) return Copy(_state);
        }
    }

    /// <summary>The grant held.</summary>
    public CapabilityGrant Grant
    {
        get
        {
            lock (_stateLock) return Copy(_state.Grant);
        }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Auth.SerializerOptions), Auth.SerializerOptions)!;

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Auth.SerializerOptions);

    /// <summary>Runs <paramref name="run"/> after the calls before it: two uses never take the same place in the chain.</summary>
    private Task<TResult> Serial<TResult>(Func<Task<TResult>> run, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_turns)
        {
            previous = _last;
            _last = done.Task;
        }
        return Turn(previous, done, run, ct);
    }

    private static async Task<TResult> Turn<TResult>(Task previous, TaskCompletionSource done, Func<Task<TResult>> run, CancellationToken ct)
    {
        try
        {
            await previous.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not run: the calls after it still wait for the ones before it.
            _ = previous.ContinueWith(_ => done.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>
    /// Once, before the first change: the latest use of this grant the record outbox holds beyond the state's head (kept,
    /// then the process stopped before its state was persisted) becomes the head, so no place is signed twice.
    /// </summary>
    private async Task AdoptKeptUses(CancellationToken ct)
    {
        if (_adopted) return;
        var grantId = _state.Grant.GrantId;
        RecordOutboxEntry? latest = null;
        var place = _state.Uses;
        foreach (var entry in await _client.EvidenceStore.RequireRecordOutbox().List(ct).ConfigureAwait(false))
        {
            if (entry.Kind == RecordOutboxKinds.GrantExecution && Text(entry.Record, "grant_id") == grantId
                && Sequence(entry.Record) is { } sequence && sequence > place)
                (latest, place) = (entry, sequence);
        }
        if (latest is not null)
        {
            var digest = GrantRules.RecordDigest("GrantExecutionEvidence", latest.Record.GetRawText());
            lock (_stateLock) (_state.Uses, _state.HeadDigest, _state.HeadExecutedAt) = (place, digest, Text(latest.Record, "executed_at"));
            await Persist(ct).ConfigureAwait(false);
        }
        _adopted = true;
    }

    private async Task Persist(CancellationToken ct)
    {
        if (_persist is not null) await _persist(State, ct).ConfigureAwait(false);
    }

    private bool GrantVouches(CapabilityGrant grant, DateTimeOffset now) =>
        OfflineVerifier.VerifyCapabilityGrant(Json(grant), _naKeys, now).Valid;

    /// <summary>A time token is the NA's, for this grant, and no newer than the holder's chain.</summary>
    private void CheckToken(JsonElement token, CapabilityGrant grant, int uses)
    {
        if (token.ValueKind != JsonValueKind.Object || !OfflineVerifier.VerifyTimeToken(token.GetRawText(), _naKeys)
            || Text(token, "grant_id") != grant.GrantId || Sequence(token) is not { } sequence || sequence > uses)
            throw new GrantRefusedException("grant_time_token_invalid", "the time token is not the NA's for this grant and holder");
    }

    private static string? Text(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Sequence(JsonElement record) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty("use_sequence", out var v) && v.TryGetInt32(out var n) ? n : null;

    private static long Micros(string? timestamp) => timestamp is null ? long.MinValue : OfflineVerifier.TimestampMicros(timestamp) ?? long.MinValue;

    /// <summary>An instant in microseconds since 0001-01-01, as <see cref="OfflineVerifier.TimestampMicros"/> counts them.</summary>
    private static long Micros(DateTimeOffset at) => at.UtcTicks / 10;

    /// <summary>Once: the signer holds the grant's bearer key (it signs, and the grant's public key verifies it).</summary>
    private async Task CheckBearer(CancellationToken ct)
    {
        if (_bearerChecked) return;
        var message = Probe + _state.Grant.GrantId;
        var signature = await _signer.SignAsync(Encoding.UTF8.GetBytes(message), ct).ConfigureAwait(false);
        if (!OfflineVerifier.VerifyEd25519(message, Convert.ToBase64String(signature), [_state.Grant.BearerPublicKey]))
            throw new GrantRefusedException("grant_bearer_mismatch", "the signer does not hold the grant's bearer key");
        _bearerChecked = true;
    }

    // ── The revocation feed ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies a revocation feed (signed by the NA's key valid at its time and still vouching now, issued by the grant's
    /// issuer) and keeps it unless the one held is newer. A holder refuses a grant the feed it holds lists as revoked.
    /// Feeds are ordered by the key sequence of their signer in the NA's verified history (keys all valid at every time
    /// count as one), then by their <c>sequence</c> (1.8.0): a feed signed by a later key always replaces one signed by an earlier key, and a held
    /// feed whose key has passed its vouching horizon no longer blocks a newer one; otherwise a feed with a forged high
    /// <c>sequence</c>, signed with a succeeded key, would stop the holder from taking the NA's feeds for good.
    /// </summary>
    /// <param name="feed">The feed, as the NA serves it (<see cref="AttestationClient.RevocationFeed"/>).</param>
    /// <param name="ct">Cancels the waiting and the persisting.</param>
    /// <returns>True when the feed is kept; false when the one held is newer.</returns>
    /// <exception cref="GrantRefusedException"><c>feed_invalid_signature</c>: the feed is not the grant issuer's.</exception>
    public Task<bool> UpdateRevocationFeed(SovereignRevocationFeed feed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return Serial(async () =>
        {
            var now = _now();
            if (!OfflineVerifier.VerifyRevocationFeedSignature(Json(feed), _naKeys, now) || feed.IssuerSovereignId != _state.Grant.IssuerSovereignId)
                throw new GrantRefusedException("feed_invalid_signature", "the revocation feed is not the grant issuer's");
            if (_state.RevocationFeed is { } held && !FeedReplaces(feed, held, now)) return false;
            lock (_stateLock) _state.RevocationFeed = Copy(feed);
            await Persist(ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// The key sequence of a feed's signer in the NA's verified history; 0 for keys that are all valid at every time
    /// (<see cref="NAKeyHistory.ValidAlways"/>, a list of keys), or no signer.
    /// </summary>
    private int FeedKeySequence(SovereignRevocationFeed feed)
    {
        if (_naKeys.Keys.All(k => k.ValidFrom is null && k.SucceededAt is null && k.ValidTo is null)
            || OfflineVerifier.RevocationFeedSigner(Json(feed), _naKeys) is not { } signer) return 0;
        return _naKeys.Keys.FirstOrDefault(k => k.PublicKey == signer)?.KeySequence ?? 0;
    }

    /// <summary>True when <paramref name="feed"/> replaces the <paramref name="held"/> one: a later signing key, else a sequence no lower (1.8.0).</summary>
    private bool FeedReplaces(SovereignRevocationFeed feed, SovereignRevocationFeed held, DateTimeOffset now)
    {
        if (OfflineVerifier.RevocationFeedSigner(Json(held), _naKeys) is not { } heldSigner || _naKeys.RecordOutlivesKey(heldSigner, now)) return true;
        var (incoming, kept) = (FeedKeySequence(feed), FeedKeySequence(held));
        if (incoming != kept) return incoming > kept;
        return feed.Sequence >= held.Sequence;
    }

    /// <summary>Reads the NA's revocation feed and keeps it (<see cref="UpdateRevocationFeed"/>).</summary>
    /// <param name="ct">Cancels the request and the persisting.</param>
    /// <returns>True when the feed is kept; false when the one held is newer.</returns>
    /// <exception cref="GrantRefusedException"><c>feed_invalid_signature</c>: the feed is not the grant issuer's.</exception>
    public async Task<bool> RefreshRevocationFeed(CancellationToken ct = default)
    {
        var feed = await _client.Attestation.RevocationFeed(_state.Grant.IssuerSovereignId, ct).ConfigureAwait(false);
        return await UpdateRevocationFeed(feed, ct).ConfigureAwait(false);
    }

    // ── Time tokens ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the NA for a fresh time token naming the head of the holder's chain, verifies it and keeps it. When the NA
    /// answers that the grant has ended (<c>grant_expired</c>, <c>grant_revoked</c>, <c>grant_budget_exhausted</c>,
    /// <c>grant_bearer_key_retired</c>), that answer is kept (<see cref="GrantHolderState.Unusable"/>) and the error
    /// thrown.
    /// </summary>
    /// <param name="ct">Cancels the request and the persisting.</param>
    /// <returns>The token, exactly as the NA signed it.</returns>
    /// <exception cref="GrantRefusedException">
    /// The token is not the NA's for this grant (<c>grant_time_token_invalid</c>), or names another head than the holder
    /// asked for (<c>grant_time_token_mismatch</c>).
    /// </exception>
    public Task<JsonElement> RefreshTimeToken(CancellationToken ct = default) => Serial(() => Refresh(ct), ct);

    private async Task<JsonElement> Refresh(CancellationToken ct)
    {
        await AdoptKeptUses(ct).ConfigureAwait(false);
        var grant = _state.Grant;
        var (uses, head) = (_state.Uses, _state.HeadDigest);
        JsonElement token;
        try
        {
            var request = await GrantRecords.SignTimeTokenRequest(new TimeTokenRequestInput
            {
                GrantId = grant.GrantId,
                UseSequence = uses,
                UseHeadDigest = head,
                RequestedAt = _now(),
            }, _signer, ct).ConfigureAwait(false);
            token = await _client.Grants.RequestTimeToken(request, ct).ConfigureAwait(false);
        }
        catch (GenesisMeshException e) when (e.Status == 409 && Ended.Contains(e.Code))
        {
            lock (_stateLock) _state.Unusable = e.Code;
            await Persist(ct).ConfigureAwait(false);
            throw;
        }
        CheckToken(token, grant, uses);
        if (Sequence(token) != uses || Text(token, "use_head_digest") != head)
            throw new GrantRefusedException("grant_time_token_mismatch", "the time token names another chain head than the holder asked for");
        lock (_stateLock) _state.TimeToken = token.Clone();
        await Persist(ct).ConfigureAwait(false);
        return token;
    }

    /// <summary>
    /// On reconnect: reads the revocation feed, asks for a fresh time token, and uploads the record outbox (the grant's
    /// uses in their chain's order, with the other records). A step the NA does not answer is skipped, not thrown
    /// (<see cref="GrantSyncResult.Reachable"/> false), and the steps after it with it. A time token the NA refuses is
    /// reported (<see cref="GrantSyncResult.Unusable"/> when the grant has ended, <see cref="GrantSyncResult.TimeTokenRefusal"/>
    /// otherwise, such as a clock more than 300 seconds off the NA's) and the upload still runs; other errors are thrown.
    /// </summary>
    /// <param name="ignoreBackoff">Upload records still in their backoff too (the default: the NA is back).</param>
    /// <param name="ct">Cancels the steps.</param>
    /// <exception cref="GrantRefusedException">The NA served a feed or a time token that is not its own for this grant.</exception>
    public async Task<GrantSyncResult> Sync(bool ignoreBackoff = true, CancellationToken ct = default)
    {
        try
        {
            await RefreshRevocationFeed(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Governance.EvaluationFailure(e) is not null)
        {
            return new GrantSyncResult { Reachable = false };
        }
        JsonElement? token = null;
        string? unusable = null;
        GenesisMeshException? refused = null;
        try
        {
            token = await RefreshTimeToken(ct).ConfigureAwait(false);
        }
        catch (GenesisMeshException e) when (e.Status == 409 && Ended.Contains(e.Code))
        {
            unusable = e.Code;
        }
        catch (Exception e) when (Governance.EvaluationFailure(e) is not null)
        {
            return new GrantSyncResult { Reachable = false };
        }
        catch (GenesisMeshException e) when (e.Status is >= 400 and < 500)
        {
            refused = e;
        }
        var flushed = await _client.EvidenceStore.FlushRecords(new FlushOptions { IgnoreBackoff = ignoreBackoff }, ct).ConfigureAwait(false);
        return new GrantSyncResult { Reachable = true, TimeToken = token, Unusable = unusable, TimeTokenRefusal = refused, Flushed = flushed };
    }

    // ── Acting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Acts within the grant, offline: checks it (see the class), runs <paramref name="action"/>, signs the use with the
    /// bearer key and keeps it in the record outbox (submitted at once when the NA is reachable). An action that throws
    /// is recorded as a <c>failure</c> use and the error rethrown. When the grant cannot be used,
    /// <see cref="GrantActRequest.Fallback"/> decides (see the class); so it does when the uses of the time token are
    /// spent and the NA will not issue another for a reason other than the grant's end (such as a clock more than 300
    /// seconds off its own): the NA then decides. <paramref name="ct"/> cancels the steps before and including the
    /// action; once it has run, its use is kept whatever the token says.
    /// </summary>
    /// <typeparam name="T">The action's value.</typeparam>
    /// <param name="request">The change.</param>
    /// <param name="action">The change itself, given what it runs under; it returns what to record, or null for nothing more.</param>
    /// <param name="ct">Cancels the steps before and including the action.</param>
    /// <exception cref="ArgumentException">A resource ID without its action, or the reverse.</exception>
    /// <exception cref="GrantRefusedException">
    /// The request is outside the grant (<c>grant_use_secret_material</c>, <c>grant_use_out_of_scope</c>,
    /// <c>grant_capability_mismatch</c>, <c>grant_envelope_mismatch</c>, <c>grant_use_outside_validity</c> before its
    /// start), the signer does not hold the bearer key (<c>grant_bearer_mismatch</c>), or the NA answered the grant ended
    /// for a reason no fallback covers (its code): nothing ran.
    /// </exception>
    /// <exception cref="GrantUnusableException">The grant cannot be used and there is no fallback, or no attestation to break the glass under: nothing ran.</exception>
    /// <exception cref="GovernedActionException">The action failed, and its failure use could not be kept.</exception>
    /// <exception cref="EvidenceNotKeptException">
    /// The action ran, and its use could not be kept (<see cref="EvidenceNotKeptException.GrantUse"/>): do not run it
    /// again. The chain did not move: the next use takes that place.
    /// </exception>
    /// <exception cref="GrantStateNotKeptException">
    /// The use is kept (for a failed action, its failure use: <see cref="GrantStateNotKeptException.ActionError"/>); the
    /// holder's state could not be persisted.
    /// </exception>
    /// <exception cref="GenesisMeshException">Under a fallback: the governed action's own errors, such as a refused evaluation.</exception>
    public Task<GrantActResult<T>> Act<T>(GrantActRequest request, Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        return Serial(() => ActNow(request, action, ct), ct);
    }

    private async Task<GrantActResult<T>> ActNow<T>(GrantActRequest request, Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct)
    {
        await AdoptKeptUses(ct).ConfigureAwait(false);
        var grant = _state.Grant;
        if ((request.ResourceId is null) != (request.ResourceAction is null))
            throw new ArgumentException("genesismesh: resource_id and resource_action go together", nameof(request));
        var parameters = request.RequestParameters ?? new Dictionary<string, object?>();
        var parametersJson = ExecutionRecorder.Element(parameters);
        // Refused before anything runs: the NA would refuse such a use and never store it.
        if (EvidenceMetadata.MetadataProblem(ExecutionRecorder.Element(new Dictionary<string, object?> { ["request_parameters"] = parametersJson })) is { } secret)
            throw new GrantRefusedException("grant_use_secret_material", secret);
        if (_resourcePrefix is not null && !(request.ResourceId ?? "").StartsWith(_resourcePrefix, StringComparison.Ordinal))
            throw new GrantRefusedException("grant_use_out_of_scope", $"the bearer key covers only resources starting with {_resourcePrefix}");
        if (!grant.Capabilities.Contains(request.Capability, StringComparer.Ordinal))
            throw new GrantRefusedException("grant_capability_mismatch", $"{request.Capability} is not one of the grant's capabilities");
        if (GrantRules.EnvelopeProblem(grant.ParameterEnvelope, parametersJson) is { } outside)
            throw new GrantRefusedException("grant_envelope_mismatch", outside);
        await CheckBearer(ct).ConfigureAwait(false);

        var sequence = _state.Uses + 1;
        var now = _now();
        // Never dated before the time token, nor before the previous use: the token proves the time is no earlier (the
        // NA allows 300 seconds before the token; the holder needs none of it).
        var at = Math.Max(Micros(now), Math.Max(Micros(Text(_state.TimeToken, "issued_at")), Micros(_state.HeadExecutedAt)));
        string? unusable = null;
        if (sequence > grant.MaxUses && sequence - await QuarantinedUses(ct).ConfigureAwait(false) > grant.MaxUses) unusable = GrantFallbackCodes.BudgetExhausted;
        else if (at > Micros(grant.ValidUntil)) unusable = GrantFallbackCodes.Expired;
        // 1.8.0: a grant of an NA key past its vouching horizon no longer vouches; it ends as if expired.
        else if (!GrantVouches(grant, now)) unusable = GrantFallbackCodes.Expired;
        else if (at < Micros(grant.ValidFrom))
            throw new GrantRefusedException("grant_use_outside_validity", $"the grant is valid from {grant.ValidFrom}");
        else if (_state.RevocationFeed?.RevokedGrantIds?.Contains(grant.GrantId) == true) unusable = GrantFallbackCodes.Revoked;
        else if (_state.Unusable is { } answered)
        {
            if (!GrantFallbackCodes.All.Contains(answered)) throw new GrantRefusedException(answered, $"the NA answered {answered} for this grant");
            unusable = answered;
        }
        else if (sequence - (Sequence(_state.TimeToken) ?? 0) > grant.MaxUsesPerTimeToken) unusable = await FreshToken(ct).ConfigureAwait(false);
        if (unusable is not null) return await FallBack(request, action, unusable, ct).ConfigureAwait(false);

        var token = _state.TimeToken;
        var executedAt = new DateTimeOffset(Math.Max(at, Micros(Text(token, "issued_at"))) * 10, TimeSpan.Zero);
        var previousDigest = _state.HeadDigest;
        Task<GrantExecutionEvidence> Sign(ActionReport<T> report, string outcome) => GrantRecords.SignUse(new GrantUseInput
        {
            Grant = grant,
            UseSequence = sequence,
            PreviousDigest = previousDigest,
            Capability = request.Capability,
            RequestParameters = new Dictionary<string, object?>(parameters),
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction,
            ExecutedAt = executedAt,
            TimeToken = token,
            Outcome = outcome,
            OutcomeDetail = report.OutcomeDetail,
            ExecutionParameters = report.ExecutionParameters is { } p ? new Dictionary<string, object?>(p) : null,
        }, _signer, CancellationToken.None);

        ActionReport<T> ran;
        try
        {
            ran = await action(new GrantAuthority { Path = GrantPaths.Grant, Grant = Copy(grant), UseSequence = sequence }, ct).ConfigureAwait(false)
                ?? new ActionReport<T>();
        }
        catch (Exception actionError)
        {
            GrantExecutionEvidence failure;
            RecordDelivery kept;
            try
            {
                failure = await Sign(new ActionReport<T> { OutcomeDetail = "action failed" }, GrantUseOutcomes.Failure).ConfigureAwait(false);
                kept = await Keep(failure).ConfigureAwait(false);
            }
            catch (Exception recordError)
            {
                throw new GovernedActionException(actionError, recordError, null);
            }
            try
            {
                await Persist(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception persistError)
            {
                throw new GrantStateNotKeptException(persistError, null, failure, kept.Submission, kept.Queued) { ActionError = actionError };
            }
            throw;
        }

        // The action ran: from here on its outcome is always recorded.
        GrantExecutionEvidence? use = null;
        IReadOnlyList<string> dropped = [];
        RecordDelivery delivery;
        try
        {
            var outcome = ran.Outcome ?? GrantUseOutcomes.Success;
            try
            {
                use = await Sign(ran, outcome).ConfigureAwait(false);
            }
            catch (Exception e) when (e is StrictJsonException or GrantRecordException { Code: "grant_use_secret_material" })
            {
                var cleaned = Governance.WithoutRefusedMetadata(ran.ExecutionParameters ?? new Dictionary<string, object?>(), ran.OutcomeDetail);
                use = await Sign(new ActionReport<T> { ExecutionParameters = cleaned.ExecutionParameters, OutcomeDetail = cleaned.OutcomeDetail }, outcome)
                    .ConfigureAwait(false);
                dropped = cleaned.Dropped;
            }
            delivery = await Keep(use).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EvidenceNotKeptException(e, ran.Value, null) { GrantUse = use };
        }
        var result = new GrantUseResult<T> { Value = ran.Value, Use = use, Submission = delivery.Submission, Queued = delivery.Queued, Dropped = dropped };
        try
        {
            await Persist(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new GrantStateNotKeptException(e, ran.Value, use, delivery.Submission, delivery.Queued);
        }
        return new GrantActResult<T> { UnderGrant = result };
    }

    /// <summary>Advances the chain to a signed use and keeps it in the record outbox; the chain stays put when it cannot be kept.</summary>
    private async Task<RecordDelivery> Keep(GrantExecutionEvidence use)
    {
        var (uses, head, headAt) = (_state.Uses, _state.HeadDigest, _state.HeadExecutedAt);
        var digest = GrantRules.UseDigest(use);
        lock (_stateLock) (_state.Uses, _state.HeadDigest, _state.HeadExecutedAt) = (use.UseSequence, digest, use.ExecutedAt);
        try
        {
            return await _client.EvidenceStore.EnqueueRecord(use, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateLock) (_state.Uses, _state.HeadDigest, _state.HeadExecutedAt) = (uses, head, headAt);
            throw;
        }
    }

    /// <summary>
    /// How many of the holder's uses the NA has quarantined after they continued the chain: the grant's dead letters in
    /// the record outbox the NA answered with a quarantine entry, other than a conflict or a chain mismatch (which do not
    /// continue it) or an exhausted budget. They keep their place in the chain but are never admitted, so the budget,
    /// which counts admitted uses, does not count them.
    /// </summary>
    private async Task<int> QuarantinedUses(CancellationToken ct)
    {
        var (grantId, uses) = (_state.Grant.GrantId, _state.Uses);
        var quarantined = new HashSet<int>();
        foreach (var entry in await _client.EvidenceStore.RequireRecordOutbox().List(ct).ConfigureAwait(false))
        {
            if (entry.Kind == RecordOutboxKinds.GrantExecution && entry.State == OutboxStates.DeadLetter && Text(entry.Record, "grant_id") == grantId
                && Sequence(entry.Record) is { } sequence && sequence <= uses && entry.LastError?.QuarantineId is not null
                && !StillCounted.Contains(entry.LastError.Code))
                quarantined.Add(sequence);
        }
        return quarantined.Count;
    }

    /// <summary>The token's uses are spent: a fresh one when the NA answers (null), else why the grant cannot be used.</summary>
    private async Task<string?> FreshToken(CancellationToken ct)
    {
        try
        {
            await Refresh(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (Governance.EvaluationFailure(e) is not null)
        {
            return GrantFallbackCodes.TimeTokenExhausted;
        }
        catch (GenesisMeshException e) when (GrantFallbackCodes.All.Contains(e.Code))
        {
            return e.Code;
        }
        catch (GenesisMeshException e) when (Ended.Contains(e.Code))
        {
            throw new GrantRefusedException(e.Code, $"the NA answered {e.Code} for this grant");
        }
        catch (GenesisMeshException e) when (e.Status is >= 400 and < 500)
        {
            // The NA answered but will not issue a token now (such as a clock more than 300 s off its own,
            // time_token_request_stale): the change goes to it, which decides.
            return GrantFallbackCodes.TimeTokenExhausted;
        }
    }

    /// <summary>The grant cannot be used: a governed action with break-glass, naming the capability.</summary>
    private async Task<GrantActResult<T>> FallBack<T>(GrantActRequest request, Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>> action,
        string unusable, CancellationToken ct)
    {
        if (request.Fallback is not { } fallback)
            throw new GrantUnusableException(unusable, $"the grant cannot be used ({unusable}) and no fallback was given");
        var grant = _state.Grant;
        var attestationId = fallback.AttestationId ?? (grant.BasisKind == "attestation" ? grant.BasisId : null)
            ?? throw new GrantUnusableException(unusable, $"the grant cannot be used ({unusable}); break-glass needs an attestation (GrantFallback.AttestationId)");
        var outcome = await Governance.GovernedAction<T>(_client, _recorder, new GovernedActionRequest
        {
            AttestationId = attestationId,
            RequestedCapability = request.Capability,
            Context = new EvaluationContext
            {
                RequestParameters = request.RequestParameters ?? new Dictionary<string, object?>(),
                Attributes = fallback.Attributes,
            },
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction,
            Verify = fallback.Verify,
        }, new BreakGlassOptions { Justification = fallback.Justification },
            (decision, c) => action(decision is null
                ? new GrantAuthority { Path = GrantPaths.BreakGlass }
                : new GrantAuthority { Path = GrantPaths.Decision, Decision = decision }, c), ct).ConfigureAwait(false);
        return new GrantActResult<T> { Fallback = new GrantFallbackResult<T> { Unusable = unusable, Outcome = outcome } };
    }
}
