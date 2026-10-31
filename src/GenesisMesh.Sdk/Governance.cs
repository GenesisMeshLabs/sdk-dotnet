using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>A gate that failed (<see cref="DecisionSummary"/>).</summary>
/// <param name="Gate">The gate's name, such as <c>capability_check</c> or <c>&lt;policy_id&gt;/&lt;gate&gt;</c>.</param>
/// <param name="Detail">Why it failed.</param>
public sealed record GateFailure(string Gate, string Detail);

/// <summary>What a decision means for a controller and a reviewer, in one object (<see cref="Governance.SummarizeDecision"/>).</summary>
public sealed class DecisionSummary
{
    /// <summary>The decision's ID.</summary>
    public string DecisionId { get; init; } = "";

    /// <summary>True when the request is authorized.</summary>
    public bool Authorized { get; init; }

    /// <summary>Why it was denied; null when it is authorized.</summary>
    public string? DenialReason { get; init; }

    /// <summary>Failed gates that deny the request: built-in, attestation, policy resolution and enforce-mode policy gates.</summary>
    public IReadOnlyList<GateFailure> EnforcedFailures { get; init; } = [];

    /// <summary>Observe-mode policy gates that failed: recorded, not enforced. Review them before switching a policy to enforce.</summary>
    public IReadOnlyList<GateFailure> ObservedFailures { get; init; } = [];

    /// <summary><c>&lt;policy_id&gt;@&lt;version&gt;</c> of each policy applied, in resolution order.</summary>
    public IReadOnlyList<string> AppliedPolicies { get; init; } = [];

    /// <summary>The attestation the decision was made on; null for an agreement basis.</summary>
    public string? AttestationId { get; init; }
}

/// <summary>What a governed action reports for its evidence record: identifiers and versions only.</summary>
/// <typeparam name="T">The value returned to the caller.</typeparam>
public sealed class ActionReport<T>
{
    /// <summary>Returned to the caller (<see cref="GovernedActionResult{T}.Value"/>); never recorded.</summary>
    public T? Value { get; init; }

    /// <summary>Recorded in the evidence, such as the secret's version, the vault and the expiry; never a secret value.</summary>
    public IDictionary<string, object?>? ExecutionParameters { get; init; }

    /// <summary>A value of <see cref="ExecutionOutcomes"/>; default <see cref="ExecutionOutcomes.Success"/>.</summary>
    public string? Outcome { get; init; }

    /// <summary>A short description of the outcome; counts toward the metadata limit.</summary>
    public string? OutcomeDetail { get; init; }
}

/// <summary>
/// How a governed action verifies the decision before it acts (<see cref="OfflineVerifier.VerifyBoundaryDecision"/>):
/// the NA's keys, and the policies and attestation the decision must be bound to.
/// </summary>
public sealed class GovernedVerification
{
    /// <summary>NA keys that may sign decisions (base64 Ed25519). Set these or <see cref="KeyHistory"/>.</summary>
    public IReadOnlyList<string> OperatorPublicKeys { get; init; } = [];

    /// <summary>The NA's verified key history, instead of <see cref="OperatorPublicKeys"/>.</summary>
    public NAKeyHistory? KeyHistory { get; init; }

    /// <summary>When set, an embedded freshness proof must verify under these keys.</summary>
    public IReadOnlyList<string>? FreshnessProofIssuerKeys { get; init; }

    /// <summary>
    /// The policies the decision must be bound to, as the NA signed them (<see cref="PolicyClient.Publish"/>). Required:
    /// pass an empty list for a capability no policy selects.
    /// </summary>
    public IReadOnlyList<BoundaryPolicy>? ExpectedPolicies { get; init; }

    /// <summary>The attestation an authorized decision on <see cref="GovernedActionRequest.AttestationId"/> must be bound to. Required with it.</summary>
    public MembershipAttestation? ExpectedAttestation { get; init; }
}

/// <summary>What <see cref="Governance.GovernedAction"/> evaluates, verifies and records.</summary>
public sealed class GovernedActionRequest
{
    /// <summary>The capability requested, such as <c>partner-secret.rotate</c>. Required.</summary>
    public string RequestedCapability { get; init; } = "";

    /// <summary>The basis: an NA-issued membership attestation. Set this or <see cref="Agreement"/>.</summary>
    public string? AttestationId { get; init; }

    /// <summary>The basis: a signed agreement as the NA returned it. Set this or <see cref="AttestationId"/>.</summary>
    public AgreementRecord? Agreement { get; init; }

    /// <summary>
    /// The request's parameters and attributes, which the policy gates read. Never put secret values in it. Its
    /// <see cref="EvaluationContext.ContextId"/> defaults to a new UUID, which the decision must carry.
    /// </summary>
    public EvaluationContext? Context { get; init; }

    /// <summary>The resource acted on, such as <c>kv:vault/secret</c>. Set with <see cref="ResourceAction"/>.</summary>
    public string? ResourceId { get; init; }

    /// <summary>A value of <see cref="ResourceActions"/>. Set with <see cref="ResourceId"/>.</summary>
    public string? ResourceAction { get; init; }

    /// <summary>
    /// Where the resource's chain ends, when the caller knows it. Unset, it is read: the newest pending record in the
    /// outbox, otherwise the NA's head (<see cref="EvidenceStoreClient.ResourceHead"/>).
    /// </summary>
    public ResourceHead? PriorResource { get; init; }

    /// <summary>The resource has no history: its record is its first, and no head is read. Not with <see cref="PriorResource"/>.</summary>
    public bool NewResource { get; init; }

    /// <summary>How the decision is verified before the action runs. Required.</summary>
    public GovernedVerification? Verify { get; init; }
}

/// <summary>What <see cref="Governance.GovernedAction"/> did.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class GovernedActionResult<T>
{
    /// <summary>The NA's evaluation: the verified decision and its justification proof.</summary>
    public BoundaryEvaluation Evaluation { get; init; } = new();

    /// <summary>True when the decision authorized the request and the action ran.</summary>
    public bool Authorized { get; init; }

    /// <summary>The decision, summarized (<see cref="Governance.SummarizeDecision"/>).</summary>
    public DecisionSummary Summary { get; init; } = new();

    /// <summary>The action's value; default when it did not run.</summary>
    public T? Value { get; init; }

    /// <summary>The signed record of the action; null when it did not run.</summary>
    public ExecutionEvidence? Evidence { get; init; }

    /// <summary>The NA's acknowledgement of the record, when it admitted it.</summary>
    public EvidenceSubmission? Submission { get; init; }

    /// <summary>
    /// With an outbox: the entry holding the record when the NA has not admitted it, <see cref="OutboxStates.Pending"/>
    /// for <see cref="EvidenceStoreClient.FlushPending"/> to submit, or <see cref="OutboxStates.DeadLetter"/> when it was refused.
    /// </summary>
    public OutboxEntry? Queued { get; init; }
}

/// <summary>Metadata without what the secret guard refused (<see cref="Governance.WithoutRefusedMetadata"/>).</summary>
/// <param name="ExecutionParameters">The parameters kept.</param>
/// <param name="OutcomeDetail">The outcome detail, with a note naming what was dropped.</param>
/// <param name="Dropped">The parameters dropped, and <c>outcome_detail</c> when the detail was, in ordinal order.</param>
public sealed record CleanedMetadata(IDictionary<string, object?> ExecutionParameters, string OutcomeDetail, IReadOnlyList<string> Dropped);

/// <summary>
/// The decision did not pass offline verification, or could not be verified as asked: the action was not run.
/// <see cref="GenesisMeshException.Code"/> is the reason, such as <c>invalid_signature</c>, <c>decision_expired</c>,
/// <c>policy_binding_mismatch</c>, <c>context_binding_mismatch</c> or <c>verification_keys_required</c>.
/// </summary>
public sealed class DecisionVerificationException : GenesisMeshException
{
    /// <summary>A decision refused for <paramref name="reason"/>.</summary>
    /// <param name="reason">The verifier's reason code.</param>
    public DecisionVerificationException(string reason)
        : base(0, reason, "decision failed verification: " + reason, "genesismesh: decision failed verification: " + reason)
    {
    }
}

/// <summary>
/// The action failed, and recording the failure failed too (<c>governed_action_unrecorded</c>).
/// <see cref="Exception.InnerException"/> is the action's error; <see cref="EvidenceError"/> the recording's.
/// </summary>
public sealed class GovernedActionException : GenesisMeshException
{
    internal GovernedActionException(Exception actionError, Exception evidenceError, ExecutionEvidence? evidence)
        : base(0, "governed_action_unrecorded", "action failed and its failure could not be recorded: " + evidenceError.Message,
            "genesismesh: action failed and its failure could not be recorded: " + evidenceError.Message, actionError)
    {
        EvidenceError = evidenceError;
        Evidence = evidence;
    }

    /// <summary>Why the failure could not be recorded.</summary>
    public Exception EvidenceError { get; }

    /// <summary>The signed failure record, when it was signed: submit it once the NA can be reached.</summary>
    public ExecutionEvidence? Evidence { get; }
}

/// <summary>
/// With an outbox: the action ran, but the secret guard refused metadata it reported
/// (<c>governed_action_metadata_refused</c>). The outcome was recorded without the refused fields
/// (<see cref="Dropped"/>), as <see cref="Evidence"/>; <see cref="Submission"/> or <see cref="Queued"/> say what became
/// of it. <see cref="Exception.InnerException"/> is the guard's <see cref="SecretMaterialException"/>, or a
/// <see cref="StrictJsonException"/> for a value the NA would not read alike. Do not run the action again.
/// </summary>
public sealed class MetadataRefusedException : GenesisMeshException
{
    internal MetadataRefusedException(Exception cause, object? value, ExecutionEvidence evidence, Delivery delivery, IReadOnlyList<string> dropped)
        : base(0, "governed_action_metadata_refused", Describe(cause, dropped), "genesismesh: " + Describe(cause, dropped), cause)
    {
        Value = value;
        Evidence = evidence;
        Submission = delivery.Submission;
        Queued = delivery.Queued;
        Dropped = dropped;
    }

    private static string Describe(Exception cause, IReadOnlyList<string> dropped) =>
        $"the action ran; its metadata was refused and recorded without {string.Join(", ", dropped)}: {cause.Message}";

    /// <summary>The action's value.</summary>
    public object? Value { get; }

    /// <summary>The signed record of the outcome, without the refused fields.</summary>
    public ExecutionEvidence Evidence { get; }

    /// <summary>The NA's acknowledgement, when it admitted the record.</summary>
    public EvidenceSubmission? Submission { get; }

    /// <summary>The outbox entry holding the record otherwise.</summary>
    public OutboxEntry? Queued { get; }

    /// <summary>The fields left out.</summary>
    public IReadOnlyList<string> Dropped { get; }
}

/// <summary>
/// With an outbox: the action ran, but its evidence could not be signed or kept in the outbox
/// (<c>governed_action_evidence_unkept</c>). <see cref="Evidence"/> is the signed record when signing succeeded: submit
/// it once the outbox works (a resubmission is idempotent). <see cref="Exception.InnerException"/> is the error. Do not
/// run the action again.
/// </summary>
public sealed class EvidenceNotKeptException : GenesisMeshException
{
    internal EvidenceNotKeptException(Exception cause, object? value, ExecutionEvidence? evidence)
        : base(0, "governed_action_evidence_unkept", "the action ran; its evidence was not kept: " + cause.Message,
            "genesismesh: the action ran; its evidence was not kept: " + cause.Message, cause)
    {
        Value = value;
        Evidence = evidence;
    }

    /// <summary>The action's value.</summary>
    public object? Value { get; }

    /// <summary>The signed record, when signing succeeded.</summary>
    public ExecutionEvidence? Evidence { get; }
}

/// <summary>
/// A controller's governed changes (1.9.0), as the TypeScript SDK makes them: evaluate the request, verify the
/// decision offline, act only on an authorized decision, and record the outcome on the resource's chain. Then compare
/// the cloud with the governed history (<see cref="ReconcileResources"/>).
/// </summary>
public static partial class Governance
{
    private const string ObservePrefix = "[observe] ";
    private const string GuardNote = "secret guard dropped";
    private static readonly string[] TerminalActions = [ResourceActions.Revoke, ResourceActions.Delete];

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex Nameable();

    /// <summary>What a decision means for a controller and a reviewer, in one object.</summary>
    public static DecisionSummary SummarizeDecision(BoundaryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var enforced = new List<GateFailure>();
        var observed = new List<GateFailure>();
        if (decision.GateResults.ValueKind == JsonValueKind.Array)
        {
            foreach (var gate in decision.GateResults.EnumerateArray())
            {
                if (gate.ValueKind != JsonValueKind.Object || (gate.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True))
                    continue;
                var name = Text(gate, "gate_name") ?? "";
                var detail = Text(gate, "detail") ?? "";
                if (detail.StartsWith(ObservePrefix, StringComparison.Ordinal)) observed.Add(new GateFailure(name, detail[ObservePrefix.Length..]));
                else enforced.Add(new GateFailure(name, detail));
            }
        }
        var applied = new List<string>();
        if (decision.PolicyBinding.ValueKind == JsonValueKind.Object
            && decision.PolicyBinding.TryGetProperty("policies", out var policies) && policies.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in policies.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var version = p.TryGetProperty("version", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : "";
                applied.Add($"{Text(p, "policy_id")}@{version}");
            }
        }
        return new DecisionSummary
        {
            DecisionId = decision.DecisionId,
            Authorized = decision.Authorized,
            DenialReason = decision.DenialReason,
            EnforcedFailures = enforced,
            ObservedFailures = observed,
            AppliedPolicies = applied,
            AttestationId = AttestationIdOf(decision),
        };
    }

    /// <summary>
    /// Evaluates the request, verifies the decision offline, runs <paramref name="action"/> only on an authorized
    /// decision, then signs and submits the execution evidence. A denial returns <see cref="GovernedActionResult{T}.Authorized"/>
    /// false without running the action. When the action throws, a <see cref="ExecutionOutcomes.Failure"/> record is
    /// submitted and the error rethrown.
    /// <para>
    /// The decision is verified before the action runs and again after the resource head is read, since reading it may
    /// outlast the decision's validity: its signature and expiry, its policy binding against
    /// <see cref="GovernedVerification.ExpectedPolicies"/>, its attestation binding, its context, and the basis asked
    /// for. Verification needs the NA's keys and the expected policies; nothing is evaluated without them.
    /// </para>
    /// <para>
    /// With an outbox (<see cref="ClientOptions.Outbox"/>) every record is kept until the NA admits it, and a failed
    /// submission does not throw: <see cref="GovernedActionResult{T}.Queued"/> is the outbox entry, which
    /// <see cref="EvidenceStoreClient.FlushPending"/> submits later. A resource with pending records chains from the
    /// newest of them, not from the NA's head. A guard refusal after the action records the outcome without the refused
    /// fields and throws <see cref="MetadataRefusedException"/>; a failure to keep the record throws
    /// <see cref="EvidenceNotKeptException"/>. Both carry the action's value.
    /// </para>
    /// <para>
    /// <paramref name="ct"/> cancels the evaluation, the head lookup and the action. Once the action has run, its
    /// outcome is recorded whatever the token says.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The action's value.</typeparam>
    /// <param name="client">The client of the NA, with the operator key that evaluates.</param>
    /// <param name="recorder">The executor key's recorder.</param>
    /// <param name="request">What to evaluate, verify and record.</param>
    /// <param name="action">The change, given the verified decision; it returns what to record, or null for nothing more.</param>
    /// <param name="ct">Cancels the steps before and including the action.</param>
    /// <exception cref="ArgumentException">The request is incomplete, such as a resource ID without an action.</exception>
    /// <exception cref="DecisionVerificationException">The decision failed verification, or cannot be verified as asked.</exception>
    /// <exception cref="SecretMaterialException">
    /// Without an outbox: the action ran, and its report holds secret material; nothing was signed or sent.
    /// </exception>
    public static async Task<GovernedActionResult<T>> GovernedAction<T>(
        GenesisMeshClient client,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        Func<BoundaryDecision, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await Run(client.Boundary.EvaluateAsReceived, client.EvidenceStore, recorder, request, action, ct).ConfigureAwait(false);
    }

    private static async Task<GovernedActionResult<T>> Run<T>(
        Func<EvaluateRequest, CancellationToken, Task<(BoundaryEvaluation Evaluation, string DecisionJson)>> evaluate,
        EvidenceStoreClient store,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        Func<BoundaryDecision, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        if ((request.ResourceId is null) != (request.ResourceAction is null))
            throw new ArgumentException("genesismesh: ResourceId and ResourceAction go together", nameof(request));
        if (request.NewResource && request.PriorResource is not null)
            throw new ArgumentException("genesismesh: set PriorResource or NewResource, not both", nameof(request));
        if ((request.NewResource || request.PriorResource is not null) && request.ResourceId is null)
            throw new ArgumentException("genesismesh: PriorResource and NewResource need a ResourceId", nameof(request));
        var verify = request.Verify;
        if (verify is null || (verify.OperatorPublicKeys.Count == 0 && verify.KeyHistory is null))
            throw new DecisionVerificationException("verification_keys_required");
        if (verify.OperatorPublicKeys.Count > 0 && verify.KeyHistory is not null)
            throw new ArgumentException("genesismesh: pass OperatorPublicKeys or KeyHistory, not both", nameof(request));
        if (verify.ExpectedPolicies is null) throw new DecisionVerificationException("policy_expectations_required");
        var policies = verify.ExpectedPolicies.Select(p => JsonSerializer.Serialize(p, Auth.SerializerOptions)).ToList();
        var attestation = verify.ExpectedAttestation is { } a ? JsonSerializer.Serialize(a, Auth.SerializerOptions) : null;

        ct.ThrowIfCancellationRequested();
        var outbox = store.Outbox;
        // An outbox that cannot be read fails here, before anything is evaluated or run.
        if (outbox is not null) await outbox.List(ct).ConfigureAwait(false);
        var contextId = request.Context?.ContextId is { Length: > 0 } given ? given : Guid.NewGuid().ToString();
        var (evaluation, decisionJson) = await evaluate(new EvaluateRequest
        {
            RequestedCapability = request.RequestedCapability,
            AttestationId = request.AttestationId,
            Agreement = request.Agreement,
            Context = WithContextId(request.Context, contextId),
        }, ct).ConfigureAwait(false);
        var decision = evaluation.Decision;

        void CheckDecision()
        {
            if (decision.Authorized && request.AttestationId is not null && attestation is null)
                throw new DecisionVerificationException("attestation_expectation_required");
            var check = OfflineVerifier.VerifyBoundaryDecision(decisionJson, new DecisionVerifyOptions
            {
                OperatorPublicKeys = verify.OperatorPublicKeys,
                KeyHistory = verify.KeyHistory,
                FreshnessProofIssuerKeys = verify.FreshnessProofIssuerKeys,
                ExpectedPolicies = policies,
                ExpectedAttestation = attestation,
                Now = DateTimeOffset.UtcNow,
            });
            if (!check.Accepted) throw new DecisionVerificationException(check.Reason);
            if (decision.ContextId != contextId) throw new DecisionVerificationException("context_binding_mismatch");
            if (request.AttestationId is not null && AttestationIdOf(decision) != request.AttestationId)
                throw new DecisionVerificationException("attestation_binding_mismatch");
            if (request.Agreement is not null && decision.AgreementId != request.Agreement.AgreementId)
                throw new DecisionVerificationException("agreement_binding_mismatch");
        }

        CheckDecision();
        var summary = SummarizeDecision(decision);
        if (!decision.Authorized) return new GovernedActionResult<T> { Evaluation = evaluation, Authorized = false, Summary = summary };

        ResourceHead? prior = null;
        if (request.ResourceId is { } resourceId && !request.NewResource)
        {
            prior = request.PriorResource;
            if (prior is null && outbox is not null && await store.PendingHead(resourceId, ct).ConfigureAwait(false) is { } pending)
                prior = ExecutionRecorder.HeadOf(pending);
            prior ??= await store.ResourceHead(resourceId, ct).ConfigureAwait(false);
        }
        // Reading history may outlast the decision's validity window.
        CheckDecision();

        // From the action on, the outcome is recorded whatever the caller's token says.
        Task<ExecutionEvidence> Record(string? outcome, IDictionary<string, object?>? parameters, string? detail) =>
            recorder.Record(new RecordExecutionRequest
            {
                Decision = decision,
                ExecutedCapability = request.RequestedCapability,
                Outcome = outcome ?? ExecutionOutcomes.Success,
                ExecutionParameters = parameters,
                OutcomeDetail = detail,
                ResourceId = request.ResourceId,
                ResourceAction = request.ResourceAction,
                PriorResource = prior,
            }, CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        ActionReport<T> report;
        try
        {
            report = await action(decision, ct).ConfigureAwait(false) ?? new ActionReport<T>();
        }
        catch (Exception actionError)
        {
            ExecutionEvidence? failure = null;
            try
            {
                failure = await Record(ExecutionOutcomes.Failure, null, "action failed").ConfigureAwait(false);
                if (outbox is not null) await store.Enqueue(failure, CancellationToken.None).ConfigureAwait(false);
                else await store.Submit(failure, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception evidenceError)
            {
                throw new GovernedActionException(actionError, evidenceError, failure);
            }
            throw;
        }

        if (outbox is null)
        {
            var evidence = await Record(report.Outcome, report.ExecutionParameters, report.OutcomeDetail).ConfigureAwait(false);
            var submission = await store.Submit(evidence, CancellationToken.None).ConfigureAwait(false);
            return new GovernedActionResult<T>
            {
                Evaluation = evaluation,
                Authorized = true,
                Summary = summary,
                Value = report.Value,
                Evidence = evidence,
                Submission = submission,
            };
        }

        ExecutionEvidence? kept = null;
        Exception? refused = null;
        IReadOnlyList<string> dropped = [];
        Delivery delivery;
        try
        {
            try
            {
                kept = await Record(report.Outcome, report.ExecutionParameters, report.OutcomeDetail).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SecretMaterialException or StrictJsonException)
            {
                var cleaned = WithoutRefusedMetadata(report.ExecutionParameters ?? new Dictionary<string, object?>(), report.OutcomeDetail);
                kept = await Record(report.Outcome, cleaned.ExecutionParameters, cleaned.OutcomeDetail).ConfigureAwait(false);
                refused = e;
                dropped = cleaned.Dropped;
            }
            delivery = await store.Enqueue(kept, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EvidenceNotKeptException(e, report.Value, kept);
        }
        if (refused is not null) throw new MetadataRefusedException(refused, report.Value, kept, delivery, dropped);
        return new GovernedActionResult<T>
        {
            Evaluation = evaluation,
            Authorized = true,
            Summary = summary,
            Value = report.Value,
            Evidence = kept,
            Submission = delivery.Submission,
            Queued = delivery.Queued,
        };
    }

    /// <summary>
    /// The reported metadata without what the secret guard refuses, or what the NA would not read alike: each
    /// top-level parameter is checked alone, and the outcome detail names what was dropped. Everything is dropped when
    /// the rest is still refused (its size).
    /// </summary>
    public static CleanedMetadata WithoutRefusedMetadata(IDictionary<string, object?> executionParameters, string? outcomeDetail)
    {
        ArgumentNullException.ThrowIfNull(executionParameters);
        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        var dropped = new List<string>();
        foreach (var (key, value) in executionParameters)
        {
            if (!ExecutionRecorder.Unrecordable(new Dictionary<string, object?> { [key] = value }, null)) kept[key] = value;
            else dropped.Add(key);
        }
        var empty = new Dictionary<string, object?>();
        var detail = outcomeDetail is not null && !ExecutionRecorder.Unrecordable(empty, outcomeDetail) ? outcomeDetail : null;
        if (outcomeDetail is not null && detail is null) dropped.Add("outcome_detail");
        List<string> All()
        {
            var all = executionParameters.Keys.ToList();
            if (outcomeDetail is not null) all.Add("outcome_detail");
            all.Sort(StringComparer.Ordinal);
            return all;
        }
        if (ExecutionRecorder.Unrecordable(kept, detail))
        {
            dropped = All();
            kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        dropped.Sort(StringComparer.Ordinal);
        var note = Note(dropped);
        var outcome = detail is not null && !dropped.Contains("outcome_detail") ? detail + " " + note : note;
        if (ExecutionRecorder.Unrecordable(kept, outcome))
            return new CleanedMetadata(new Dictionary<string, object?>(StringComparer.Ordinal), $"[{GuardNote}]", All());
        return new CleanedMetadata(kept, outcome, dropped);
    }

    /// <summary>The note naming what the guard dropped: plain field names only, the others counted.</summary>
    private static string Note(List<string> dropped)
    {
        var parts = dropped.Where(d => Nameable().IsMatch(d)).ToList();
        var others = dropped.Count - parts.Count;
        if (others > 0) parts.Add($"{others} other field{(others == 1 ? "" : "s")}");
        return $"[{GuardNote}: {string.Join(", ", parts)}]";
    }

    /// <summary>
    /// Compares a cloud inventory with the NA's governed history. Pure: the caller supplies both sides
    /// (<see cref="EvidenceStoreClient.ResourceStates"/>). Correct each finding through <see cref="GovernedAction"/>.
    /// </summary>
    /// <param name="observed">The resources as seen in the cloud.</param>
    /// <param name="recorded">The governed state of each resource, by ID.</param>
    /// <param name="options">The version parameter, and whether the inventory is complete.</param>
    public static IReadOnlyList<ReconciliationFinding> ReconcileResources(
        IEnumerable<ObservedResource> observed, IReadOnlyDictionary<string, ResourceState> recorded, ReconcileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(recorded);
        options ??= new ReconcileOptions();
        var findings = new List<ReconciliationFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in observed)
        {
            seen.Add(item.ResourceId);
            var state = recorded.TryGetValue(item.ResourceId, out var s) ? s : null;
            void Finding(string status, string detail) => findings.Add(new ReconciliationFinding
            {
                ResourceId = item.ResourceId,
                Status = status,
                Observed = item,
                Recorded = state,
                Detail = detail,
            });
            var live = state?.LastSuccessAction is { } done && !TerminalActions.Contains(done);
            if (!item.Exists)
            {
                if (live) Finding(ReconciliationStatuses.Missing, $"governed as {state!.LastSuccessAction}, not found in the cloud");
                else Finding(ReconciliationStatuses.InSync, state is not null ? "absent, as governed" : "absent and never governed");
                continue;
            }
            if (state?.LastSuccessAction is null)
            {
                Finding(ReconciliationStatuses.Unmanaged, "exists with no successful governed change");
                continue;
            }
            if (!live)
            {
                Finding(ReconciliationStatuses.PresentAfterRevoke, $"still present after governed {state.LastSuccessAction}");
                continue;
            }
            if (item.Version is not null && RecordedVersion(state, options.VersionKey) is { } version && version != item.Version)
            {
                Finding(ReconciliationStatuses.Drifted, $"cloud version {item.Version}, governed version {version}");
                continue;
            }
            Finding(ReconciliationStatuses.InSync, $"matches governed {state.LastSuccessAction}");
        }

        if (options.CompleteInventory)
        {
            foreach (var state in recorded.Values)
            {
                if (seen.Contains(state.ResourceId)) continue;
                if (options.ScopePrefix is { Length: > 0 } prefix && !state.ResourceId.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (state.LastSuccessAction is { } done && !TerminalActions.Contains(done))
                {
                    findings.Add(new ReconciliationFinding
                    {
                        ResourceId = state.ResourceId,
                        Status = ReconciliationStatuses.Missing,
                        Recorded = state,
                        Detail = $"governed as {done}, absent from the inventory",
                    });
                }
            }
        }
        return findings;
    }

    /// <summary>The version the last successful change recorded, as text; null when it recorded none.</summary>
    private static string? RecordedVersion(ResourceState state, string versionKey) =>
        state.LastSuccessParameters.ValueKind == JsonValueKind.Object
        && state.LastSuccessParameters.TryGetProperty(versionKey, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;

    private static EvaluationContext WithContextId(EvaluationContext? context, string contextId) => new()
    {
        RequestParameters = context?.RequestParameters,
        Attributes = context?.Attributes,
        RequesterSovereignId = context?.RequesterSovereignId,
        ProviderSovereignId = context?.ProviderSovereignId,
        ContextId = contextId,
        ContextFreshnessSeq = context?.ContextFreshnessSeq,
        ParentKind = context?.ParentKind,
    };

    private static string? AttestationIdOf(BoundaryDecision decision) =>
        decision.AttestationBinding.ValueKind == JsonValueKind.Object ? Text(decision.AttestationBinding, "attestation_id") : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
