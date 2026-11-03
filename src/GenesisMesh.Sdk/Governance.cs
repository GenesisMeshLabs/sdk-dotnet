using System.Diagnostics.CodeAnalysis;
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

/// <summary>What a governed action (<see cref="Governance"/>) evaluates, verifies and records.</summary>
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

/// <summary>What a governed action did when the NA answered (<see cref="Governance"/>).</summary>
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

/// <summary>What a governed action needs to run when the NA cannot be reached (1.9.0).</summary>
public sealed class BreakGlassOptions
{
    /// <summary>Why the change cannot wait for the NA: 1 to 1024 characters, no secret values. Required.</summary>
    public string Justification { get; init; } = "";
}

/// <summary>
/// The evaluation failed transiently, and the action ran under break-glass (1.9.0): the NA judges the record after the
/// fact, as the failed evaluation would have gone.
/// </summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class BreakGlassResult<T>
{
    /// <summary>How the evaluation failed: a value of <see cref="EvaluationFailures"/>.</summary>
    public string Failure { get; init; } = "";

    /// <summary>The evaluation's error.</summary>
    public required Exception EvaluationError { get; init; }

    /// <summary>The action's value.</summary>
    public T? Value { get; init; }

    /// <summary>The signed break-glass record of the action.</summary>
    public BreakGlassRecord Record { get; init; } = new();

    /// <summary>The NA's acknowledgement, when it was reached again by then and admitted the record.</summary>
    public RecordSubmission? Submission { get; init; }

    /// <summary>
    /// The record outbox entry holding the record otherwise: <see cref="OutboxStates.Pending"/> for
    /// <see cref="EvidenceStoreClient.FlushRecords"/> to submit, or <see cref="OutboxStates.DeadLetter"/> when it was refused.
    /// </summary>
    public RecordOutboxEntry? Queued { get; init; }

    /// <summary>The reported metadata the secret guard refused, left out of the record; empty when nothing was.</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
}

/// <summary>What a governed action with break-glass did: under the NA's decision, or under break-glass.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class GovernedOutcome<T>
{
    /// <summary>
    /// True when the evaluation failed transiently and the action ran under break-glass (<see cref="BreakGlass"/>); false
    /// when the NA answered (<see cref="Governed"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The outcome holds neither.</exception>
    [MemberNotNullWhen(true, nameof(BreakGlass))]
    [MemberNotNullWhen(false, nameof(Governed))]
    public bool BrokeGlass
    {
        get
        {
            if (BreakGlass is not null) return true;
            if (Governed is null) throw new InvalidOperationException("genesismesh: the outcome holds neither a governed result nor a break-glass result");
            return false;
        }
    }

    /// <summary>What the governed action did when the NA answered, authorized or not; null under break-glass.</summary>
    public GovernedActionResult<T>? Governed { get; init; }

    /// <summary>What the action did under break-glass; null when the NA answered.</summary>
    public BreakGlassResult<T>? BreakGlass { get; init; }
}

/// <summary>Metadata without what the secret guard refused (<see cref="Governance.WithoutRefusedMetadata"/>).</summary>
/// <param name="ExecutionParameters">The parameters kept.</param>
/// <param name="OutcomeDetail">The outcome detail, with a note naming what was dropped.</param>
/// <param name="Dropped">The parameters dropped, and <c>outcome_detail</c> when the detail was, in ordinal order.</param>
public sealed record CleanedMetadata(IDictionary<string, object?> ExecutionParameters, string OutcomeDetail, IReadOnlyList<string> Dropped);

/// <summary>The version a forward rotation issued (<see cref="Governance.CancelByRotatingForward"/>).</summary>
/// <typeparam name="T">The value returned to the caller.</typeparam>
public sealed class RotatedVersion<T>
{
    /// <summary>The new version's ID: recorded as the rotation evidence's <c>execution_parameters.version_id</c>. Required.</summary>
    public string VersionId { get; init; } = "";

    /// <summary>Returned to the caller; never recorded.</summary>
    public T? Value { get; init; }

    /// <summary>Other metadata for the rotation's evidence: identifiers and versions only.</summary>
    public IDictionary<string, object?>? ExecutionParameters { get; init; }
}

/// <summary>What <see cref="Governance.CancelByRotatingForward"/> cancels, and how.</summary>
/// <typeparam name="T">The rotation's value.</typeparam>
public sealed class RotateForwardOptions<T>
{
    /// <summary>
    /// The change's remediation state from the due queue (<c>remediation_due</c> or <c>overdue</c>): its subject, change
    /// and resource name the record. Its state, mode and steps are checked before anything runs. Required.
    /// </summary>
    public RemediationStatus? Remediation { get; init; }

    /// <summary>
    /// The denied version: disabled once the readers have the new one. Default the state's
    /// <see cref="RemediationStatus.ChangeVersionId"/>; when the state names one, it must be that one (the NA refuses a
    /// record that disables another, <c>remediation_subject_mismatch</c>).
    /// </summary>
    public string? DeniedVersionId { get; init; }

    /// <summary>
    /// The governed rotation's evaluation and verification, as for <see cref="Governance.GovernedAction{T}(GenesisMeshClient, ExecutionRecorder, GovernedActionRequest, Func{BoundaryDecision, CancellationToken, Task{ActionReport{T}?}}, CancellationToken)"/>.
    /// Its resource is the remediation's (leave <see cref="GovernedActionRequest.ResourceId"/> unset, or give the same);
    /// its action <c>rotate</c> (the default) or <c>update</c>. Required.
    /// </summary>
    public GovernedActionRequest? Rotation { get; init; }

    /// <summary>Issues a new version and moves the resource's readers to it, given the rotation's decision. Required.</summary>
    public Func<BoundaryDecision, CancellationToken, Task<RotatedVersion<T>>>? Rotate { get; init; }

    /// <summary>Disables the denied version, once the readers have the new one. Required.</summary>
    public Func<string, CancellationToken, Task>? Disable { get; init; }

    /// <summary>A note for the remediation record: up to 1024 characters, no secret material.</summary>
    public string? Detail { get; init; }
}

/// <summary>What <see cref="Governance.CancelByRotatingForward"/> did.</summary>
/// <typeparam name="T">The rotation's value.</typeparam>
public sealed class RotateForwardResult<T>
{
    /// <summary>The governed rotation. When it was denied, nothing was rotated, disabled or recorded.</summary>
    public GovernedActionResult<T> Rotation { get; init; } = new();

    /// <summary>The signed remediation record, once the denied version was disabled.</summary>
    public RemediationRecord? Remediation { get; init; }

    /// <summary>The NA's acknowledgement, when it admitted the record.</summary>
    public RecordSubmission? Submission { get; init; }

    /// <summary>With a record outbox: the entry holding the record otherwise (pending or a dead letter).</summary>
    public RecordOutboxEntry? Queued { get; init; }
}

/// <summary>What <see cref="Governance.UrgentAction"/> requests when the ordinary capability is denied, or cannot be evaluated.</summary>
public sealed class EmergencyOptions
{
    /// <summary>The emergency capability: the one capability a policy with an <c>emergency</c> section selects. Required.</summary>
    public string Capability { get; init; } = "";

    /// <summary>
    /// Why the change cannot wait: 1 to 1024 characters, no secret values. It is the emergency policy's justification
    /// parameter, and a break-glass record's justification. Required.
    /// </summary>
    public string Justification { get; init; } = "";

    /// <summary>The emergency policy's <c>justification_parameter</c>; default <c>justification</c>.</summary>
    public string JustificationParameter { get; init; } = "justification";

    /// <summary>The policies the emergency capability's decision must be bound to. Required.</summary>
    public IReadOnlyList<BoundaryPolicy>? ExpectedPolicies { get; init; }
}

/// <summary>How an urgent change went (<see cref="UrgentActionResult{T}.Path"/>).</summary>
public static class UrgentPaths
{
    /// <summary>Under the ordinary capability's ALLOW.</summary>
    public const string Ordinary = "ordinary";
    /// <summary>Under the emergency capability's decision (a DENY too, when it was denied).</summary>
    public const string Emergency = "emergency";
    /// <summary>Under break-glass naming the emergency capability: the NA did not answer.</summary>
    public const string BreakGlass = "break_glass";
}

/// <summary>What <see cref="Governance.UrgentAction"/> did.</summary>
/// <typeparam name="T">The action's value.</typeparam>
public sealed class UrgentActionResult<T>
{
    /// <summary>A value of <see cref="UrgentPaths"/>.</summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// The change: under the ordinary or the emergency capability's decision (<see cref="GovernedOutcome{T}.Governed"/>,
    /// <c>Authorized</c> false when the emergency capability was denied too), or under break-glass
    /// (<see cref="GovernedOutcome{T}.BreakGlass"/>).
    /// </summary>
    public GovernedOutcome<T> Outcome { get; init; } = new();

    /// <summary>The ordinary capability's DENY, when the emergency capability was requested after it.</summary>
    public GovernedActionResult<T>? OrdinaryDenial { get; init; }
}

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
    internal GovernedActionException(Exception actionError, Exception evidenceError, ExecutionEvidence? evidence, BreakGlassRecord? breakGlassRecord = null)
        : base(0, "governed_action_unrecorded", "action failed and its failure could not be recorded: " + evidenceError.Message,
            "genesismesh: action failed and its failure could not be recorded: " + evidenceError.Message, actionError)
    {
        EvidenceError = evidenceError;
        Evidence = evidence;
        BreakGlassRecord = breakGlassRecord;
    }

    /// <summary>Why the failure could not be recorded.</summary>
    public Exception EvidenceError { get; }

    /// <summary>
    /// The signed failure record, when it was signed: submit it once the NA can be reached. Null under break-glass,
    /// whose record is not execution evidence.
    /// </summary>
    public ExecutionEvidence? Evidence { get; }

    /// <summary>
    /// Under break-glass: the signed break-glass record of the failure, when it was signed. Keep it in the record
    /// outbox, or submit it, once either works.
    /// </summary>
    public BreakGlassRecord? BreakGlassRecord { get; }
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
/// With an outbox, or under break-glass: the action ran, but its record could not be signed or kept in the outbox
/// (<c>governed_action_evidence_unkept</c>). <see cref="Evidence"/> is the signed execution record when signing
/// succeeded: submit it once the outbox works (a resubmission is idempotent). Under break-glass it is null, and
/// <see cref="BreakGlassRecord"/> is the signed break-glass record. <see cref="Exception.InnerException"/> is the
/// error. Do not run the action again.
/// </summary>
public sealed class EvidenceNotKeptException : GenesisMeshException
{
    internal EvidenceNotKeptException(Exception cause, object? value, ExecutionEvidence? evidence, BreakGlassRecord? breakGlassRecord = null)
        : base(0, "governed_action_evidence_unkept", "the action ran; its evidence was not kept: " + cause.Message,
            "genesismesh: the action ran; its evidence was not kept: " + cause.Message, cause)
    {
        Value = value;
        Evidence = evidence;
        BreakGlassRecord = breakGlassRecord;
    }

    /// <summary>The action's value.</summary>
    public object? Value { get; }

    /// <summary>The signed execution record, when signing succeeded; null under break-glass.</summary>
    public ExecutionEvidence? Evidence { get; }

    /// <summary>
    /// Under break-glass: the signed break-glass record, when signing succeeded. Keep it in the record outbox, or submit
    /// it, once either works; a resubmission is a duplicate.
    /// </summary>
    public BreakGlassRecord? BreakGlassRecord { get; }

    /// <summary>
    /// For a use of a grant (<see cref="GrantHolder.Act"/>, 1.9.0): the signed use, when signing succeeded. Keep it in the
    /// record outbox, or submit it, once either works; the holder's chain did not move to it.
    /// </summary>
    public GrantExecutionEvidence? GrantUse { get; internal init; }
}

/// <summary>
/// The forward rotation ran, but the remediation was not recorded (<c>remediation_not_recorded</c>,
/// <see cref="Governance.CancelByRotatingForward"/>). <see cref="Exception.InnerException"/> is the cause:
/// <list type="bullet">
/// <item>Before the disable (<see cref="Disabled"/> false): the rotation's evidence was refused for good
/// (<see cref="RotationQueued"/> a dead letter), or the record could not be built, for example the rotation issued the
/// denied version (<c>remediation_malformed</c>). That rotation can never be cited: rotate again.</item>
/// <item>The disable failed (<see cref="Disabled"/> false): disable the denied version, then record it.</item>
/// <item>After the disable (<see cref="Disabled"/> true): the record could not be signed, submitted or kept. Once the
/// cause is fixed, submit <see cref="Record"/>, or sign it again when there is none (a resubmission is a duplicate); do
/// not rotate again.</item>
/// </list>
/// </summary>
public sealed class RemediationNotRecordedException : GenesisMeshException
{
    internal RemediationNotRecordedException(Exception cause, ExecutionEvidence? rotationEvidence, object? value, RemediationRecord? record,
        bool disabled = false, OutboxEntry? rotationQueued = null)
        : base(0, "remediation_not_recorded", "the forward rotation ran; the remediation was not recorded: " + cause.Message,
            "genesismesh: the forward rotation ran; the remediation was not recorded: " + cause.Message, cause)
    {
        RotationEvidence = rotationEvidence;
        Value = value;
        Record = record;
        Disabled = disabled;
        RotationQueued = rotationQueued;
    }

    /// <summary>True when the denied version was disabled.</summary>
    public bool Disabled { get; }

    /// <summary>With an evidence outbox: the rotation's evidence entry, when the NA has not admitted it.</summary>
    public OutboxEntry? RotationQueued { get; }

    /// <summary>The forward rotation's execution evidence.</summary>
    public ExecutionEvidence? RotationEvidence { get; }

    /// <summary>The rotation's value.</summary>
    public object? Value { get; }

    /// <summary>The signed remediation record, when signing succeeded.</summary>
    public RemediationRecord? Record { get; }
}

/// <summary>
/// A controller's governed changes (1.9.0), as the TypeScript SDK makes them: evaluate the request, verify the
/// decision offline, act only on an authorized decision, and record the outcome on the resource's chain; with
/// break-glass, act when the NA cannot be reached and keep a break-glass record for it to judge. Then compare the cloud
/// with the governed history (<see cref="ReconcileResources"/>).
/// </summary>
public static partial class Governance
{
    private const string ObservePrefix = "[observe] ";
    private const string GuardNote = "secret guard dropped";

    /// <summary>Room kept for the action's report within the metadata limit of a break-glass record.</summary>
    private const int RecordReserve = 2048;

    /// <summary>The longest outcome detail a break-glass record carries (the reference's limit).</summary>
    private const int MaxOutcomeDetail = 1024;

    /// <summary>
    /// Failures that look transient but are not: the NA throttling a caller whose operator signatures keep failing, an
    /// evaluation the NA computed (perhaps a denial) but could not store, and an answer whose body could not be read
    /// (the NA may have decided). None breaks the glass.
    /// </summary>
    private static readonly HashSet<string> NotBreakable =
        new(["admin_auth_throttled", "evidence_store_unavailable", NetworkException.ResponseBodyUnreadable], StringComparer.Ordinal);
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
    /// submitted and the error rethrown. An evaluation that fails throws, and nothing runs; the overload that takes
    /// <see cref="BreakGlassOptions"/> runs the action when the NA cannot be reached.
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
        ArgumentNullException.ThrowIfNull(action);
        var outcome = await Run<T>(client.Boundary.EvaluateAsReceived, client.EvidenceStore, recorder, request, null,
            (decision, c) => action(decision!, c), ct).ConfigureAwait(false);
        return outcome.Governed!;
    }

    /// <summary>
    /// A governed action that still runs when the NA cannot be reached (1.9.0), as the TypeScript SDK's
    /// <c>governedAction</c> with <c>breakGlass</c> does. When the evaluation fails transiently (no answer, a timeout, a
    /// 5xx or a 429; <see cref="EvaluationFailure"/>), <paramref name="action"/> runs with a null decision, and a
    /// break-glass record of it, signed by the executor key with the justification, is kept in the record outbox: the
    /// NA judges it after the fact, as the evaluation would have gone. <see cref="GovernedOutcome{T}.BreakGlass"/>
    /// holds it. Otherwise the action is governed as without break-glass (<see cref="GovernedOutcome{T}.Governed"/>):
    /// a denial, a decision that fails verification or any other error never breaks the glass.
    /// <para>
    /// Break-glass needs a record outbox (<see cref="ClientOptions.RecordOutbox"/>), a resource, an attestation basis
    /// (the NA could not judge an agreement it does not see) and a requested capability; all of it is checked before
    /// anything is evaluated, as are the context and justification: they must hold no secret material and leave
    /// 2048 bytes of the metadata limit for the action's report.
    /// </para>
    /// <para>
    /// Under break-glass, a report the secret guard refuses is recorded without the refused fields
    /// (<see cref="BreakGlassResult{T}.Dropped"/>), or with the outcome alone when the rest is still refused; an outcome
    /// detail is cut to 1024 characters. When the action throws, a <see cref="ExecutionOutcomes.Failure"/> record is
    /// kept and the error rethrown; a record that cannot be kept throws <see cref="EvidenceNotKeptException"/>, with the
    /// action's value.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The action's value.</typeparam>
    /// <param name="client">The client of the NA, with the operator key that evaluates and a record outbox.</param>
    /// <param name="recorder">The executor key's recorder; it signs the break-glass record too.</param>
    /// <param name="request">What to evaluate, verify and record.</param>
    /// <param name="breakGlass">The justification of a change made without a decision.</param>
    /// <param name="action">The change, given the verified decision, or null under break-glass.</param>
    /// <param name="ct">
    /// Cancels the steps before and including the action. Once the action has run, its record is kept whatever the
    /// token says.
    /// </param>
    /// <exception cref="ArgumentException">The request is incomplete, such as no resource.</exception>
    /// <exception cref="OutboxException">
    /// No record outbox is configured (<c>record_outbox_required</c>), or it or the outbox cannot be read
    /// (<c>outbox_file_unreadable</c>, <c>outbox_file_other_format</c>); nothing was evaluated.
    /// </exception>
    /// <exception cref="OutOfBandRecordException">
    /// <c>break_glass_malformed</c>: no attestation basis or capability, a justification that is empty or over 1024
    /// characters, or a context and justification that leave no room for the report.
    /// <c>break_glass_secret_material</c>: the context or the justification holds secret material.
    /// </exception>
    /// <exception cref="StrictJsonException">
    /// The context or the justification holds a value the NA would not read alike, such as a lone surrogate, or the NA's
    /// answer is not strict JSON. Under the NA's decision without an outbox (<see cref="ClientOptions.Outbox"/>), it
    /// comes after the action ran too: from its report, or from the NA's answer to its record.
    /// </exception>
    /// <exception cref="NetworkException">
    /// The evaluation's answer could not be read (<c>response_body_unreadable</c>): the NA may have decided, so nothing
    /// runs. Under the NA's decision without an outbox, the action ran, and the submission of its record got no answer
    /// or one that could not be read: the NA may hold the record. Do not run the action again.
    /// </exception>
    /// <exception cref="GenesisMeshException">
    /// The NA refused the evaluation, or failed in a way that is no outage, such as <c>admin_auth_throttled</c>. Under
    /// its decision without an outbox, it refused the action's record, after the action ran.
    /// </exception>
    /// <exception cref="SecretMaterialException">
    /// Under the NA's decision without an outbox: the action ran, and its report holds secret material; nothing was
    /// signed or sent.
    /// </exception>
    /// <exception cref="MetadataRefusedException">
    /// Under the NA's decision with an outbox: the action ran, and the secret guard refused its report; the outcome was
    /// kept without the refused fields.
    /// </exception>
    /// <exception cref="DecisionVerificationException">The decision failed verification, or cannot be verified as asked.</exception>
    /// <exception cref="GovernedActionException">
    /// The action failed, and its failure could not be recorded (<see cref="GovernedActionException.BreakGlassRecord"/>
    /// under break-glass).
    /// </exception>
    /// <exception cref="EvidenceNotKeptException">
    /// The action ran, and its record could not be kept (<see cref="EvidenceNotKeptException.BreakGlassRecord"/> under
    /// break-glass). Do not run it again.
    /// </exception>
    public static async Task<GovernedOutcome<T>> GovernedAction<T>(
        GenesisMeshClient client,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        BreakGlassOptions breakGlass,
        Func<BoundaryDecision?, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(breakGlass);
        ArgumentNullException.ThrowIfNull(action);
        return await Run(client.Boundary.EvaluateAsReceived, client.EvidenceStore, recorder, request, breakGlass, action, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The transient failure an evaluation's error is, a value of <see cref="EvaluationFailures"/>, or null for any
    /// other error (a denial is not an error). A request that got no answer is a network error, or a timeout when the
    /// transport's own deadline ended it; an answer is a failure only when it is a 5xx or a 429. That holds for the
    /// lookup of the NA's key before the request too, which counts by its status: a 403 or 404
    /// (<c>na_public_key_unavailable</c>) is no failure, whether its body could be read or not, and a success whose body
    /// cannot be read is no answer, since the request was not sent and no decision made (the TypeScript SDK reads it as
    /// <c>na_public_key_unavailable</c>). An answer to the evaluation whose body could not be read
    /// (<see cref="NetworkException.ResponseBodyUnreadable"/>), whatever its status, or that is not strict JSON
    /// (<see cref="StrictJsonException"/>), is no failure either: the NA answered, perhaps with a denial.
    /// </summary>
    /// <param name="error">The evaluation's error.</param>
    public static string? EvaluationFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            NetworkException network when NotBreakable.Contains(network.Code) => null,
            NetworkException network => TimedOut(network) ? EvaluationFailures.Timeout : EvaluationFailures.NetworkError,
            GenesisMeshException refused when NotBreakable.Contains(refused.Code) => null,
            GenesisMeshException { Status: 429 } => EvaluationFailures.RateLimited,
            GenesisMeshException { Status: >= 500 and < 600 } => EvaluationFailures.ServerError,
            _ => null,
        };
    }

    /// <summary>True when the transport's deadline, a cancellation the caller did not ask for, ended the request.</summary>
    private static bool TimedOut(NetworkException error)
    {
        for (var e = error.InnerException; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TimeoutException) return true;
        }
        return false;
    }

    private static async Task<GovernedOutcome<T>> Run<T>(
        Func<EvaluateRequest, CancellationToken, Task<(BoundaryEvaluation Evaluation, string DecisionJson)>> evaluate,
        EvidenceStoreClient store,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        BreakGlassOptions? breakGlass,
        Func<BoundaryDecision?, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(request);
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
        if (breakGlass is not null) await CheckBreakGlass(store, recorder, request, breakGlass, ct).ConfigureAwait(false);
        var contextId = request.Context?.ContextId is { Length: > 0 } given ? given : Guid.NewGuid().ToString();
        var evaluated = new EvaluateRequest
        {
            RequestedCapability = request.RequestedCapability,
            AttestationId = request.AttestationId,
            Agreement = request.Agreement,
            Context = WithContextId(request.Context, contextId),
        };
        BoundaryEvaluation evaluation;
        string decisionJson;
        try
        {
            (evaluation, decisionJson) = await evaluate(evaluated, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (breakGlass is not null && EvaluationFailure(e) is { } failure)
        {
            return new GovernedOutcome<T>
            {
                BreakGlass = await BreakTheGlass(store, recorder, request, breakGlass, evaluated, failure, e, action, ct).ConfigureAwait(false),
            };
        }
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
        if (!decision.Authorized)
            return new GovernedOutcome<T> { Governed = new GovernedActionResult<T> { Evaluation = evaluation, Authorized = false, Summary = summary } };

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
            return new GovernedOutcome<T>
            {
                Governed = new GovernedActionResult<T>
                {
                    Evaluation = evaluation,
                    Authorized = true,
                    Summary = summary,
                    Value = report.Value,
                    Evidence = evidence,
                    Submission = submission,
                },
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
        return new GovernedOutcome<T>
        {
            Governed = new GovernedActionResult<T>
            {
                Evaluation = evaluation,
                Authorized = true,
                Summary = summary,
                Value = report.Value,
                Evidence = kept,
                Submission = delivery.Submission,
                Queued = delivery.Queued,
            },
        };
    }

    /// <summary>Everything break-glass needs is checked before anything is evaluated or run.</summary>
    private static async Task CheckBreakGlass(
        EvidenceStoreClient store, ExecutionRecorder recorder, GovernedActionRequest request, BreakGlassOptions options, CancellationToken ct)
    {
        var recordOutbox = store.RequireRecordOutbox();
        if (request.ResourceId is null)
            throw new ArgumentException("genesismesh: break-glass needs ResourceId and ResourceAction", nameof(request));
        // An agreement basis rests on the agreement, which a break-glass record does not carry: the NA could not judge
        // the change after the fact.
        if (string.IsNullOrEmpty(request.AttestationId))
            throw new OutOfBandRecordException("break_glass_malformed", "break-glass needs an attestation-based evaluation (AttestationId)");
        if (string.IsNullOrEmpty(request.RequestedCapability))
            throw new OutOfBandRecordException("break_glass_malformed", "break-glass needs RequestedCapability");
        // Everything the record carries besides the action's report is checked now, with room left for the report, so
        // a record can always be kept once the action has run.
        var context = request.Context ?? new EvaluationContext();
        if (EvidenceMetadata.CheckMetadataOnly(ExecutionRecorder.Element(context)) is { } secret)
            throw new OutOfBandRecordException("break_glass_secret_material", secret);
        var record = OutOfBandRecords.BreakGlass(new BreakGlassInput
        {
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction!,
            Capability = request.RequestedCapability,
            AttestationId = request.AttestationId,
            RequestParameters = context.RequestParameters,
            Attributes = context.Attributes,
            Justification = options.Justification,
            EvaluationRequest = new Dictionary<string, object?>(),
            EvaluationFailure = EvaluationFailures.NetworkError,
            Outcome = ExecutionOutcomes.Success,
        }, recorder.ExecutorSovereignId);
        OutOfBandRecords.CheckBreakGlass(record);
        OutOfBandRecords.CheckRoom(record, RecordReserve);
        await recordOutbox.List(ct).ConfigureAwait(false);
    }

    /// <summary>Runs the action without a decision and keeps its break-glass record.</summary>
    private static async Task<BreakGlassResult<T>> BreakTheGlass<T>(
        EvidenceStoreClient store,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        BreakGlassOptions options,
        EvaluateRequest evaluated,
        string failure,
        Exception evaluationError,
        Func<BoundaryDecision?, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct)
    {
        // From the action on, the outcome is recorded whatever the caller's token says.
        Task<BreakGlassRecord> Sign(string? outcome, IDictionary<string, object?>? parameters, string? detail) =>
            recorder.SignBreakGlass(new BreakGlassInput
            {
                ResourceId = request.ResourceId!,
                ResourceAction = request.ResourceAction!,
                Capability = request.RequestedCapability,
                AttestationId = request.AttestationId,
                RequestParameters = request.Context?.RequestParameters,
                Attributes = request.Context?.Attributes,
                Justification = options.Justification,
                EvaluationRequest = evaluated,
                EvaluationFailure = failure,
                Outcome = outcome ?? ExecutionOutcomes.Success,
                OutcomeDetail = Clip(detail, MaxOutcomeDetail),
                ExecutionParameters = parameters,
            }, CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        ActionReport<T> report;
        try
        {
            report = await action(null, ct).ConfigureAwait(false) ?? new ActionReport<T>();
        }
        catch (Exception actionError)
        {
            BreakGlassRecord? failed = null;
            try
            {
                failed = await Sign(ExecutionOutcomes.Failure, null, "action failed").ConfigureAwait(false);
                await store.EnqueueRecord(failed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception recordError)
            {
                throw new GovernedActionException(actionError, recordError, null, failed);
            }
            throw;
        }

        // The action ran: from here on its outcome is always recorded.
        BreakGlassRecord? record = null;
        IReadOnlyList<string> dropped = [];
        RecordDelivery delivery;
        try
        {
            try
            {
                record = await Sign(report.Outcome, report.ExecutionParameters, report.OutcomeDetail).ConfigureAwait(false);
            }
            catch (Exception e) when (RefusedReport(e))
            {
                var parameters = report.ExecutionParameters ?? new Dictionary<string, object?>();
                var cleaned = WithoutRefusedMetadata(parameters, report.OutcomeDetail);
                try
                {
                    record = await Sign(report.Outcome, cleaned.ExecutionParameters, cleaned.OutcomeDetail).ConfigureAwait(false);
                    dropped = cleaned.Dropped;
                }
                catch (Exception again) when (RefusedReport(again))
                {
                    // The report together with the context is still refused (its size): keep the outcome alone.
                    record = await Sign(report.Outcome, new Dictionary<string, object?>(), $"[{GuardNote} the report]").ConfigureAwait(false);
                    dropped = AllFields(parameters, report.OutcomeDetail);
                }
            }
            delivery = await store.EnqueueRecord(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EvidenceNotKeptException(e, report.Value, null, record);
        }
        return new BreakGlassResult<T>
        {
            Failure = failure,
            EvaluationError = evaluationError,
            Value = report.Value,
            Record = record,
            Submission = delivery.Submission,
            Queued = delivery.Queued,
            Dropped = dropped,
        };
    }

    /// <summary>What a report can be recorded without: secret material, or JSON the NA would not read alike.</summary>
    private static bool RefusedReport(Exception e) =>
        e is StrictJsonException or OutOfBandRecordException { Code: "break_glass_secret_material" };

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="max"/> characters (code points, as the reference counts them),
    /// the last one an ellipsis.
    /// </summary>
    private static string? Clip(string? text, int max)
    {
        if (text is null) return null;
        var cut = 0;
        var count = 0;
        for (var i = 0; i < text.Length; i++, count++)
        {
            if (count == max - 1) cut = i;
            if (char.IsSurrogatePair(text, i)) i++;
        }
        return count > max ? text[..cut] + "\u2026" : text;
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
        if (ExecutionRecorder.Unrecordable(kept, detail))
        {
            dropped = AllFields(executionParameters, outcomeDetail);
            kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        dropped.Sort(StringComparer.Ordinal);
        var note = Note(dropped);
        var outcome = detail is not null && !dropped.Contains("outcome_detail") ? detail + " " + note : note;
        if (ExecutionRecorder.Unrecordable(kept, outcome))
            return new CleanedMetadata(new Dictionary<string, object?>(StringComparer.Ordinal), $"[{GuardNote}]", AllFields(executionParameters, outcomeDetail));
        return new CleanedMetadata(kept, outcome, dropped);
    }

    /// <summary>Every reported field, and <c>outcome_detail</c> when there is one, in ordinal order.</summary>
    private static List<string> AllFields(IDictionary<string, object?> executionParameters, string? outcomeDetail)
    {
        var all = executionParameters.Keys.ToList();
        if (outcomeDetail is not null) all.Add("outcome_detail");
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    /// <summary>The note naming what the guard dropped: plain field names only, the others counted.</summary>
    private static string Note(List<string> dropped)
    {
        var parts = dropped.Where(d => Nameable().IsMatch(d)).ToList();
        var others = dropped.Count - parts.Count;
        if (others > 0) parts.Add($"{others} other field{(others == 1 ? "" : "s")}");
        return $"[{GuardNote}: {string.Join(", ", parts)}]";
    }

    // ── Remediation: the cancel pair (1.9.0; protocol 1.4.0) ────────────────

    /// <summary>
    /// Cancels a denied change to a versioned resource others read (1.9.0), as the NA states it
    /// (<c>rotate_forward_then_disable</c>), and as the TypeScript SDK's <c>cancelByRotatingForward</c> does: two linked
    /// records. A credential is never rolled back.
    /// <list type="number">
    /// <item>A governed rotation issues a new version under a decision of its own; its execution evidence names the
    /// version (<c>execution_parameters.version_id</c>), so observers match it instead of judging it again. A DENY
    /// returns without changing anything.</item>
    /// <item><see cref="RotateForwardOptions{T}.Disable"/> then disables the denied version: the readers already have the
    /// new one.</item>
    /// <item>The remediation record names the rotation's evidence, the new version and the disabled one, signed by the
    /// executor key and kept in the record outbox, or submitted without one.</item>
    /// </list>
    /// The state, the plan and the denied version are checked before anything runs, and the record before the denied
    /// version is disabled, so a record the NA would refuse never follows a disable. The NA admits the record once it
    /// holds the rotation's evidence (<c>remediation_rotation_missing</c> is transient): flush the evidence outbox before
    /// the record outbox. A rotation the NA refused for good at once is never followed by the disable. One it refuses
    /// only at a later flush (its evidence a dead letter then) leaves the record pending for good, the denied version
    /// disabled: remove the record from the record outbox and cancel again, with a <see cref="RotateForwardOptions{T}.Disable"/>
    /// that accepts a version already disabled. Read the state right before cancelling: for a decision the NA plans
    /// from its latest execution. <paramref name="ct"/> cancels the steps up to the disable; once the denied version is disabled, the record is
    /// kept whatever the token says.
    /// </summary>
    /// <typeparam name="T">The rotation's value.</typeparam>
    /// <param name="client">The client of the NA, with the operator key that evaluates.</param>
    /// <param name="recorder">The executor key's recorder: it signs the rotation's evidence and the remediation record.</param>
    /// <param name="options">The remediation, the rotation, the disable and the denied version.</param>
    /// <param name="ct">Cancels the steps before the record is kept.</param>
    /// <exception cref="ArgumentException">An option is missing, or the rotation names another resource or action.</exception>
    /// <exception cref="OutOfBandRecordException">
    /// <c>remediation_subject_not_found</c> (the state names no change yet), <c>remediation_steps_mismatch</c> (the
    /// plan is not a forward rotation), <c>remediation_malformed</c> (no denied version) or
    /// <c>remediation_secret_material</c> (the denied version or the detail): nothing ran.
    /// </exception>
    /// <exception cref="TransitionRefusedException">
    /// The state takes no remediation record, with the NA's code: <c>remediation_not_due</c> or
    /// <c>remediation_already_recorded</c>; nothing ran.
    /// </exception>
    /// <exception cref="OutboxException">
    /// A record outbox is configured and cannot be read (<c>outbox_file_unreadable</c>; a custom outbox throws its own
    /// error): nothing ran.
    /// </exception>
    /// <exception cref="RemediationNotRecordedException">
    /// The rotation ran and its evidence is in the NA or the evidence outbox, and the remediation was not recorded:
    /// what to do next is on the exception.
    /// </exception>
    /// <exception cref="GenesisMeshException">
    /// The rotation's own governed action failed after <see cref="RotateForwardOptions{T}.Rotate"/> ran, as
    /// <see cref="GovernedAction{T}(GenesisMeshClient, ExecutionRecorder, GovernedActionRequest, Func{BoundaryDecision, CancellationToken, Task{ActionReport{T}?}}, CancellationToken)"/>
    /// fails: without an evidence outbox, the NA refused its evidence, or did not answer (<see cref="NetworkException"/>),
    /// or its report holds secret material (<see cref="SecretMaterialException"/>); with one,
    /// <see cref="EvidenceNotKeptException"/> or <see cref="MetadataRefusedException"/>. Nothing was disabled.
    /// </exception>
    public static async Task<RotateForwardResult<T>> CancelByRotatingForward<T>(
        GenesisMeshClient client, ExecutionRecorder recorder, RotateForwardOptions<T> options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(options);
        var state = options.Remediation ?? throw new ArgumentException("genesismesh: the remediation state is required", nameof(options));
        var rotation = options.Rotation ?? throw new ArgumentException("genesismesh: the rotation is required", nameof(options));
        var rotate = options.Rotate ?? throw new ArgumentException("genesismesh: Rotate is required", nameof(options));
        var disable = options.Disable ?? throw new ArgumentException("genesismesh: Disable is required", nameof(options));
        var subject = RemediationInput.For(state, RemediationSteps.RotateForwardThenDisable);
        if (!string.IsNullOrEmpty(state.Mode) && !string.IsNullOrEmpty(state.Steps)
            && RemediationRules.StepsProblem(state.Mode, state.Steps,
                new RemediationRecord { Outcome = RemediationOutcomes.Cancelled, Steps = RemediationSteps.RotateForwardThenDisable }) is { } problem)
            throw new OutOfBandRecordException("remediation_steps_mismatch", problem);
        if (!string.IsNullOrEmpty(state.State)
            && RemediationRules.NextState(state.State, RemediationEvents.RecordRemediation, state.ReviewPending) is { Allowed: false } refused)
            throw new TransitionRefusedException(refused.Refusal!, refused.Detail);
        var denied = string.IsNullOrEmpty(options.DeniedVersionId) ? state.ChangeVersionId : options.DeniedVersionId;
        if (string.IsNullOrEmpty(denied))
            throw new OutOfBandRecordException("remediation_malformed", "the denied version is required");
        if (!string.IsNullOrEmpty(state.ChangeVersionId) && denied != state.ChangeVersionId)
            throw new OutOfBandRecordException("remediation_subject_mismatch",
                $"the change made version {state.ChangeVersionId}; the denied version cannot be {denied}");
        if (RemediationRules.SecretProblem(new RemediationRecord { DisabledVersionId = denied, Detail = options.Detail }) is { } secret)
            throw new OutOfBandRecordException("remediation_secret_material", secret);
        var action = rotation.ResourceAction ?? ResourceActions.Rotate;
        if (action is not (ResourceActions.Rotate or ResourceActions.Update))
            throw new ArgumentException("genesismesh: a forward rotation's action is rotate or update", nameof(options));
        if (rotation.ResourceId is { } named && named != subject.ResourceId)
            throw new ArgumentException("genesismesh: the rotation's resource is the remediation's", nameof(options));
        // A record outbox that cannot be read fails here, before anything runs.
        if (client.EvidenceStore.RecordOutbox is { } outbox) await outbox.List(ct).ConfigureAwait(false);

        RotatedVersion<T>? rotated = null;
        var request = new GovernedActionRequest
        {
            RequestedCapability = rotation.RequestedCapability,
            AttestationId = rotation.AttestationId,
            Agreement = rotation.Agreement,
            Context = rotation.Context,
            ResourceId = subject.ResourceId,
            ResourceAction = action,
            PriorResource = rotation.PriorResource,
            NewResource = rotation.NewResource,
            Verify = rotation.Verify,
        };
        var result = await GovernedAction<T>(client, recorder, request, async (decision, c) =>
        {
            rotated = await rotate(decision, c).ConfigureAwait(false)
                ?? throw new InvalidOperationException("genesismesh: Rotate returned no version");
            var parameters = new Dictionary<string, object?>(rotated.ExecutionParameters ?? new Dictionary<string, object?>())
            {
                ["version_id"] = rotated.VersionId,
            };
            return new ActionReport<T> { Value = rotated.Value, ExecutionParameters = parameters };
        }, ct).ConfigureAwait(false);
        if (!result.Authorized) return new RotateForwardResult<T> { Rotation = result };
        // A rotation the NA refused for good can never be cited: nothing is disabled.
        if (result.Queued is { State: OutboxStates.DeadLetter } deadLetter)
            throw new RemediationNotRecordedException(new InvalidOperationException(
                    $"genesismesh: the rotation's evidence was refused for good ({deadLetter.LastError?.Code}): rotate again"),
                result.Evidence, result.Value, null, rotationQueued: deadLetter);

        var input = subject with
        {
            RotationEvidenceId = result.Evidence!.EvidenceId,
            VersionId = rotated!.VersionId,
            DisabledVersionId = denied,
            Detail = options.Detail,
        };
        try
        {
            OutOfBandRecords.CheckRemediation(OutOfBandRecords.RemediationOf(input, recorder.ExecutorSovereignId));
        }
        catch (Exception e) when (e is OutOfBandRecordException or StrictJsonException)
        {
            throw new RemediationNotRecordedException(e, result.Evidence, result.Value, null, rotationQueued: result.Queued);
        }
        RemediationRecord? record = null;
        var disabled = false;
        try
        {
            await disable(denied, ct).ConfigureAwait(false);
            disabled = true;
            // Disabled: the record is kept whatever the token says.
            record = await recorder.SignRemediation(input, CancellationToken.None).ConfigureAwait(false);
            if (client.EvidenceStore.RecordOutbox is not null)
            {
                var delivery = await client.EvidenceStore.EnqueueRecord(record, CancellationToken.None).ConfigureAwait(false);
                return new RotateForwardResult<T> { Rotation = result, Remediation = record, Submission = delivery.Submission, Queued = delivery.Queued };
            }
            var submission = await client.EvidenceStore.SubmitRemediation(record, CancellationToken.None).ConfigureAwait(false);
            return new RotateForwardResult<T> { Rotation = result, Remediation = record, Submission = submission };
        }
        catch (Exception e)
        {
            throw new RemediationNotRecordedException(e, result.Evidence, result.Value, record, disabled, result.Queued);
        }
    }

    // ── Urgent changes (1.9.0; protocol 1.4.0) ───────────────────────────────

    /// <summary>
    /// An urgent change (1.9.0), as the TypeScript SDK's <c>urgentAction</c>: never blocked, always governed and judged.
    /// <list type="bullet">
    /// <item>The ordinary capability is evaluated first (<paramref name="request"/>), as
    /// <see cref="GovernedAction{T}(GenesisMeshClient, ExecutionRecorder, GovernedActionRequest, Func{BoundaryDecision, CancellationToken, Task{ActionReport{T}?}}, CancellationToken)"/>
    /// does; an ALLOW runs the action under it.</item>
    /// <item>On a DENY, the emergency capability is requested, with the justification in its justification parameter:
    /// the NA decides under the emergency policy before the change, and reviews it after.</item>
    /// <item>When the ordinary evaluation gets no answer, times out or is answered with a 5xx or a 429, the emergency
    /// capability is evaluated next. When that fails so too, the action runs under break-glass naming the emergency
    /// capability, with the justification parameter in its request parameters, and the NA judges it under the
    /// emergency policy once it is back.</item>
    /// </list>
    /// Break-glass needs a record outbox, an attestation basis and a resource; all of it is checked before anything is
    /// evaluated. The emergency capability's decision is verified against
    /// <see cref="EmergencyOptions.ExpectedPolicies"/>. Any other error, after the action ran or a decision that fails
    /// verification, is the caller's.
    /// </summary>
    /// <typeparam name="T">The action's value.</typeparam>
    /// <param name="client">The client of the NA, with the operator key that evaluates and a record outbox.</param>
    /// <param name="recorder">The executor key's recorder.</param>
    /// <param name="request">The ordinary capability's evaluation, verification and resource.</param>
    /// <param name="emergency">The emergency capability, the justification and the emergency policies.</param>
    /// <param name="action">The change, given the verified decision, or null under break-glass.</param>
    /// <param name="ct">Cancels the steps before and including the action.</param>
    /// <exception cref="ArgumentException">An emergency option is missing.</exception>
    /// <exception cref="OutboxException">No record outbox is configured (<c>record_outbox_required</c>), or it cannot be read.</exception>
    /// <exception cref="OutOfBandRecordException">
    /// <c>break_glass_malformed</c> or <c>break_glass_secret_material</c>, as for break-glass: nothing was evaluated.
    /// </exception>
    public static async Task<UrgentActionResult<T>> UrgentAction<T>(
        GenesisMeshClient client,
        ExecutionRecorder recorder,
        GovernedActionRequest request,
        EmergencyOptions emergency,
        Func<BoundaryDecision?, CancellationToken, Task<ActionReport<T>?>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emergency);
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrEmpty(emergency.Capability))
            throw new ArgumentException("genesismesh: the emergency capability is required", nameof(emergency));
        if (emergency.ExpectedPolicies is null)
            throw new ArgumentException("genesismesh: the emergency policies are required", nameof(emergency));
        var context = request.Context ?? new EvaluationContext();
        var urgent = new GovernedActionRequest
        {
            RequestedCapability = emergency.Capability,
            AttestationId = request.AttestationId,
            Agreement = request.Agreement,
            Context = new EvaluationContext
            {
                RequestParameters = new Dictionary<string, object?>(context.RequestParameters ?? new Dictionary<string, object?>())
                {
                    [emergency.JustificationParameter] = emergency.Justification,
                },
                Attributes = context.Attributes,
                RequesterSovereignId = context.RequesterSovereignId,
                ProviderSovereignId = context.ProviderSovereignId,
                ContextId = context.ContextId,
                ContextFreshnessSeq = context.ContextFreshnessSeq,
                ParentKind = context.ParentKind,
            },
            ResourceId = request.ResourceId,
            ResourceAction = request.ResourceAction,
            PriorResource = request.PriorResource,
            NewResource = request.NewResource,
            Verify = request.Verify is { } verify
                ? new GovernedVerification
                {
                    OperatorPublicKeys = verify.OperatorPublicKeys,
                    KeyHistory = verify.KeyHistory,
                    FreshnessProofIssuerKeys = verify.FreshnessProofIssuerKeys,
                    ExpectedPolicies = emergency.ExpectedPolicies,
                    ExpectedAttestation = verify.ExpectedAttestation,
                }
                : null,
        };
        var breakGlass = new BreakGlassOptions { Justification = emergency.Justification };
        await CheckBreakGlass(client.EvidenceStore, recorder, urgent, breakGlass, ct).ConfigureAwait(false);

        // Only an evaluation that got no answer, or a 5xx or a 429, goes on to the emergency capability; any other error
        // (after the action ran, or a decision that fails verification) is the caller's.
        Exception? evaluationError = null;
        async Task<(BoundaryEvaluation Evaluation, string DecisionJson)> Evaluate(EvaluateRequest evaluation, CancellationToken c)
        {
            try
            {
                return await client.Boundary.EvaluateAsReceived(evaluation, c).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                evaluationError = e;
                throw;
            }
        }
        GovernedActionResult<T>? ordinaryDenial = null;
        try
        {
            var ordinary = await Run<T>(Evaluate, client.EvidenceStore, recorder, request, null, action, ct).ConfigureAwait(false);
            if (ordinary.Governed!.Authorized) return new UrgentActionResult<T> { Path = UrgentPaths.Ordinary, Outcome = ordinary };
            ordinaryDenial = ordinary.Governed;
        }
        catch (Exception e) when (ReferenceEquals(e, evaluationError) && EvaluationFailure(e) is not null)
        {
        }
        var outcome = await Run(client.Boundary.EvaluateAsReceived, client.EvidenceStore, recorder, urgent, breakGlass, action, ct).ConfigureAwait(false);
        return new UrgentActionResult<T>
        {
            Path = outcome.BrokeGlass ? UrgentPaths.BreakGlass : UrgentPaths.Emergency,
            Outcome = outcome,
            OrdinaryDenial = ordinaryDenial,
        };
    }

    /// <summary>
    /// Compares a cloud inventory with the NA's governed history. Pure: the caller supplies both sides
    /// (<see cref="EvidenceStoreClient.ResourceStates"/>). Correct each finding through a governed action.
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
