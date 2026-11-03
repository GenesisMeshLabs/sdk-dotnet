using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>What applies when no policy gives guidance: the NA's <c>NA_REMEDIATION_*</c> settings.</summary>
/// <param name="GraceSeconds">How long after the judgement the remediation is due; the NA's default is a day.</param>
/// <param name="Mode">A value of <see cref="RemediationModes"/>; the NA's default is <c>alert_only</c>.</param>
public sealed record RemediationDefaults(int GraceSeconds = 86400, string Mode = RemediationModes.AlertOnly);

/// <summary>The guidance for a judged change (<see cref="RemediationRules.Guidance"/>).</summary>
/// <param name="Mode">A value of <see cref="RemediationModes"/>.</param>
/// <param name="GraceSeconds">How long after the judgement the remediation is due.</param>
/// <param name="Source">A value of <see cref="GuidanceSources"/>.</param>
public sealed record RemediationGuidance(string Mode, int GraceSeconds, string Source);

/// <summary>What a judged change leads to, as the NA states it (<see cref="RemediationRules.Plan"/>).</summary>
/// <param name="Mode">A value of <see cref="RemediationModes"/>.</param>
/// <param name="Steps">A value of <see cref="RemediationSteps"/> but <c>delete</c>: what the controller is to do.</param>
/// <param name="Fallback">A value of <see cref="RemediationFallbacks"/> when a <c>cancel</c> fell back to an alert.</param>
/// <param name="GraceSeconds">How long after the judgement the remediation is due.</param>
/// <param name="Source">A value of <see cref="GuidanceSources"/>.</param>
public sealed record RemediationPlan(string Mode, string Steps, string? Fallback, int GraceSeconds, string Source);

/// <summary>What the record of a change says about it (<see cref="RemediationRules.FactsOf"/>).</summary>
/// <param name="Action">The change's action, such as <c>rotate</c>.</param>
/// <param name="VersionId">The version the change made; null when it names none.</param>
/// <param name="Rotatable">False only when the record says the resource cannot be rotated.</param>
public sealed record ChangeFacts(string Action, string? VersionId, bool Rotatable);

/// <summary>The state after an event (<see cref="RemediationRules.NextState"/>), or why the NA's state table refuses it.</summary>
/// <param name="State">A value of <see cref="RemediationStates"/>; null when refused.</param>
/// <param name="Refusal">
/// When refused, the NA's code: <c>review_not_pending</c>, <c>review_already_pending</c>,
/// <c>review_after_remediation</c>, <c>review_waiver_after_remediation</c>, <c>remediation_already_recorded</c>,
/// <c>remediation_not_due</c> or <c>illegal_transition</c>.
/// </param>
/// <param name="Detail">What was refused; empty when allowed.</param>
public sealed record RemediationTransition(string? State, string? Refusal, string Detail)
{
    /// <summary>True when the table allows the event.</summary>
    public bool Allowed => State is not null;
}

/// <summary>Why the NA would refuse a remediation or review: its code and what is wrong.</summary>
/// <param name="Code">The NA's code, such as <c>remediation_invalid_signature</c>.</param>
/// <param name="Detail">What is wrong.</param>
public sealed record RemediationProblem(string Code, string Detail);

/// <summary>
/// The rules the NA applies to remediations and reviews (v1.4.0; 1.9.0 in this SDK), as pure functions with the
/// reference's codes (the <c>remediation</c> conformance suite). Since 1.4.0 a judged DENY leads somewhere: the NA states
/// what is to be done and by when (<see cref="Plan"/>, the due times, the state table), the controller does it and
/// records it (<see cref="ExecutionRecorder.SignRemediation"/>), and a second holder can review the change
/// (<see cref="ReviewRecord"/>). Nothing here calls the NA.
/// </summary>
public static partial class RemediationRules
{
    private const long MicrosPerSecond = 1_000_000;

    // ── The state table ─────────────────────────────────────────────────────

    /// <summary>States a review may be opened, upheld or waived in.</summary>
    private static readonly string[] Reviewable =
        [.. RemediationStates.Standing, RemediationStates.RemediationDue, RemediationStates.Overdue, RemediationStates.Upheld, RemediationStates.Waived];

    private static readonly Dictionary<(string State, string Event), string> Transitions = BuildTransitions();

    private static Dictionary<(string, string), string> BuildTransitions()
    {
        var table = new Dictionary<(string, string), string>
        {
            [(RemediationStates.JudgedDenied, RemediationEvents.Arm)] = RemediationStates.RemediationDue,
            [(RemediationStates.Indeterminate, RemediationEvents.Arm)] = RemediationStates.RemediationDue,
            [(RemediationStates.ReviewDenied, RemediationEvents.Arm)] = RemediationStates.RemediationDue,
            [(RemediationStates.RemediationDue, RemediationEvents.DuePassed)] = RemediationStates.Overdue,
            [(RemediationStates.RemediationDue, RemediationEvents.RecordRemediation)] = RemediationStates.Remediated,
            [(RemediationStates.Overdue, RemediationEvents.RecordRemediation)] = RemediationStates.Remediated,
            [(RemediationStates.Waived, RemediationEvents.WaiverExpired)] = RemediationStates.RemediationDue,
        };
        foreach (var s in RemediationStates.Standing.Append(RemediationStates.Upheld))
            table[(s, RemediationEvents.LifetimeEnded)] = RemediationStates.RemediationDue;
        foreach (var s in Reviewable)
        {
            // Opening a review sets the flag; the state stays.
            table[(s, RemediationEvents.OpenReview)] = s;
            table[(s, RemediationEvents.Uphold)] = RemediationStates.Upheld;
            table[(s, RemediationEvents.Waive)] = RemediationStates.Waived;
        }
        foreach (var s in RemediationStates.Standing.Append(RemediationStates.Upheld).Append(RemediationStates.Waived))
            table[(s, RemediationEvents.Deny)] = RemediationStates.ReviewDenied;
        // A denial of a change already due changes nothing but, perhaps, its due time.
        table[(RemediationStates.RemediationDue, RemediationEvents.Deny)] = RemediationStates.RemediationDue;
        table[(RemediationStates.Overdue, RemediationEvents.Deny)] = RemediationStates.Overdue;
        // A review pending when the remediation was recorded is still decided; it changes nothing.
        table[(RemediationStates.Remediated, RemediationEvents.Uphold)] = RemediationStates.Remediated;
        table[(RemediationStates.Remediated, RemediationEvents.Deny)] = RemediationStates.Remediated;
        return table;
    }

    /// <summary>
    /// The state after <paramref name="event"/>, given whether a review is pending, as the NA's state table has it; or
    /// the NA's refusal. A review is decided only while one is pending (<c>review_not_pending</c>), and only one is
    /// pending at a time (<c>review_already_pending</c>). Every other event applies whether a review is pending or not:
    /// a pending review never stops a clock. A remediation is recorded only from <c>remediation_due</c> or
    /// <c>overdue</c>.
    /// </summary>
    /// <param name="state">A value of <see cref="RemediationStates"/>.</param>
    /// <param name="event">A value of <see cref="RemediationEvents"/>.</param>
    /// <param name="reviewPending">True while a review of the change is pending.</param>
    public static RemediationTransition NextState(string state, string @event, bool reviewPending = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(@event);
        if (@event is RemediationEvents.Uphold or RemediationEvents.Deny or RemediationEvents.Waive && !reviewPending)
            return new(null, "review_not_pending", $"{@event}: no review of this change is pending");
        if (@event == RemediationEvents.OpenReview && reviewPending && state != RemediationStates.Remediated)
            return new(null, "review_already_pending", "a review of this change is already pending");
        if (Transitions.TryGetValue((state, @event), out var next)) return new(next, null, "");
        return new(null, Refusal(state, @event), $"{@event} is not allowed in state {state}");
    }

    private static string Refusal(string state, string @event)
    {
        if (state == RemediationStates.Remediated)
        {
            if (@event == RemediationEvents.Waive) return "review_waiver_after_remediation";
            if (@event == RemediationEvents.OpenReview) return "review_after_remediation";
            if (@event == RemediationEvents.RecordRemediation) return "remediation_already_recorded";
        }
        return @event == RemediationEvents.RecordRemediation ? "remediation_not_due" : "illegal_transition";
    }

    /// <summary>
    /// A judged change's state, where it enters the state table: a change matched to a governed decision's execution or
    /// (v1.6.0) a grant use is <c>matched</c>, unless denied.
    /// </summary>
    /// <param name="verdict">The judgement's verdict: <c>allow</c>, <c>deny</c> or <c>indeterminate</c>.</param>
    /// <param name="governedBy">The judgement's <c>governed_by</c>: <c>prior_decision</c>, <c>after_the_fact</c> or <c>grant</c>.</param>
    public static string JudgementState(string verdict, string governedBy)
    {
        if (governedBy is "prior_decision" or "grant") return verdict == "deny" ? RemediationStates.JudgedDenied : RemediationStates.Matched;
        return verdict switch
        {
            "allow" => RemediationStates.JudgedAllowed,
            "deny" => RemediationStates.JudgedDenied,
            _ => RemediationStates.Indeterminate,
        };
    }

    // ── Remediation plans ───────────────────────────────────────────────────

    /// <summary>
    /// The guidance for a judged change: that of each enforce-mode gate that failed (its own entry in its policy's
    /// <c>remediation</c> section, or the section's <c>"*"</c>); without one, the <c>"*"</c> guidance of the policies
    /// that applied; without that, <paramref name="defaults"/>. Among several, <c>cancel</c> wins over
    /// <c>alert_only</c>, and the shortest grace of the winning mode applies.
    /// </summary>
    /// <param name="policies">The policies the judgement applied, with their <c>remediation</c> sections.</param>
    /// <param name="evaluations">The judgement's gate evaluations.</param>
    /// <param name="defaults">The NA's defaults; null for its standard ones.</param>
    public static RemediationGuidance Guidance(
        IReadOnlyList<BoundaryPolicy> policies, IReadOnlyList<PolicyGateEvaluation> evaluations, RemediationDefaults? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(evaluations);
        defaults ??= new RemediationDefaults();
        var byKey = new Dictionary<(string, int), BoundaryPolicy>();
        foreach (var p in policies) byKey[(p.PolicyId, p.Version)] = p;
        var found = new List<(string? Mode, int? Grace)>();
        foreach (var e in evaluations)
        {
            if (e.Mode != "enforce" || e.Passed) continue;
            if (!byKey.TryGetValue((e.PolicyId, e.PolicyVersion), out var policy) || Guided(policy) is not { } remediation) continue;
            var entry = remediation.TryGetProperty(e.GateId, out var own) && own.ValueKind != JsonValueKind.Null
                ? own
                : remediation.TryGetProperty("*", out var any) && any.ValueKind != JsonValueKind.Null ? any : (JsonElement?)null;
            if (entry is { } g) found.Add(Entry(g));
        }
        var source = GuidanceSources.Gate;
        if (found.Count == 0)
        {
            source = GuidanceSources.Policy;
            found = byKey.Values
                .OrderBy(p => p.PolicyId, Canonical.CodePointComparer.Instance).ThenBy(p => p.Version)
                .Select(p => Guided(p) is { } r && r.TryGetProperty("*", out var g) && g.ValueKind != JsonValueKind.Null ? Entry(g) : ((string?, int?)?)null)
                .OfType<(string?, int?)>()
                .ToList();
        }
        if (found.Count == 0) return new(defaults.Mode, defaults.GraceSeconds, GuidanceSources.Default);
        var mode = found.Any(g => g.Mode == RemediationModes.Cancel) ? RemediationModes.Cancel : RemediationModes.AlertOnly;
        var grace = found.Where(g => g.Mode == mode && g.Grace is not null).Select(g => g.Grace!.Value).DefaultIfEmpty(defaults.GraceSeconds).Min();
        return new(mode, grace, source);
    }

    /// <summary>A policy's <c>remediation</c> section when it names something.</summary>
    private static JsonElement? Guided(BoundaryPolicy policy) =>
        policy.Remediation.ValueKind == JsonValueKind.Object && policy.Remediation.EnumerateObject().Any() ? policy.Remediation : null;

    private static (string? Mode, int? Grace) Entry(JsonElement guidance) =>
        guidance.ValueKind != JsonValueKind.Object
            ? (null, null)
            : (guidance.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
               guidance.TryGetProperty("grace_seconds", out var g) && g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out var s) ? s : null);

    /// <summary>
    /// How a change is cancelled, from what its record says: a created resource nothing reads yet is disabled; a rotated
    /// or updated one with a version is rotated forward, then the denied version disabled, unless the record says it
    /// cannot be rotated. Without a version, or for a revocation or deletion (cancelling it would restore something),
    /// the cancel falls back to <c>alert_only</c>.
    /// </summary>
    private static (string Steps, string? Fallback) CancelSteps(ChangeFacts change)
    {
        if (change.Action == "create") return (RemediationSteps.Disable, null);
        if (change.Action is "rotate" or "update")
        {
            if (change.VersionId is null) return (RemediationSteps.None, RemediationFallbacks.NotVersioned);
            if (!change.Rotatable) return (RemediationSteps.None, RemediationFallbacks.NotRotatable);
            return (RemediationSteps.RotateForwardThenDisable, null);
        }
        return (RemediationSteps.None, RemediationFallbacks.NotReversible);
    }

    /// <summary>
    /// What a change judged <paramref name="verdict"/> leads to, as the NA states it. An indeterminate judgement always
    /// alerts (<c>fallback</c> <c>indeterminate</c> when the guidance was to cancel). A <c>cancel</c> takes the steps
    /// the record of the change allows: a created resource is disabled; a rotated or updated one with a version is
    /// rotated forward, then the denied version disabled, unless the record says it cannot be rotated; otherwise the
    /// plan falls back to an alert (<c>not_versioned</c>, <c>not_rotatable</c>, <c>not_reversible</c>).
    /// </summary>
    /// <param name="verdict">The judgement's verdict (<c>deny</c>, or <c>indeterminate</c>).</param>
    /// <param name="policies">The policies the judgement applied, with their <c>remediation</c> sections.</param>
    /// <param name="evaluations">The judgement's gate evaluations.</param>
    /// <param name="change">What the record of the change says (<see cref="FactsOf"/>).</param>
    /// <param name="defaults">The NA's defaults; null for its standard ones.</param>
    public static RemediationPlan Plan(
        string verdict, IReadOnlyList<BoundaryPolicy> policies, IReadOnlyList<PolicyGateEvaluation> evaluations, ChangeFacts change,
        RemediationDefaults? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (mode, grace, source) = Guidance(policies, evaluations, defaults);
        if (verdict == "indeterminate")
            return new(RemediationModes.AlertOnly, RemediationSteps.None, mode == RemediationModes.Cancel ? RemediationFallbacks.Indeterminate : null, grace, source);
        if (mode != RemediationModes.Cancel) return new(RemediationModes.AlertOnly, RemediationSteps.None, null, grace, source);
        var (steps, fallback) = CancelSteps(change);
        return new(fallback is null ? RemediationModes.Cancel : RemediationModes.AlertOnly, steps, fallback, grace, source);
    }

    /// <summary>
    /// A decision's plan, once a review denies it or its lifetime ends: from its latest execution evidence, as for a
    /// denied change; for a decision never executed (<paramref name="executed"/> null), an alert (<c>fallback</c>
    /// <c>not_executed</c>) with the guidance's grace.
    /// </summary>
    /// <param name="policies">The policies the decision applied.</param>
    /// <param name="executed">What its latest execution evidence says (<see cref="FactsOf"/>); null when never executed.</param>
    /// <param name="defaults">The NA's defaults; null for its standard ones.</param>
    public static RemediationPlan DecisionPlan(IReadOnlyList<BoundaryPolicy> policies, ChangeFacts? executed, RemediationDefaults? defaults = null)
    {
        if (executed is null)
        {
            var (_, grace, source) = Guidance(policies, [], defaults);
            return new(RemediationModes.AlertOnly, RemediationSteps.None, RemediationFallbacks.NotExecuted, grace, source);
        }
        return Plan("deny", policies, [], executed, defaults);
    }

    /// <summary>
    /// The action, version and rotatability of the record of a change: an observation's <c>action</c>,
    /// <c>version_id</c> and <c>metadata.rotatable</c>; otherwise (execution evidence, a break-glass record)
    /// <c>resource_action</c>, <c>execution_parameters.version_id</c> and <c>execution_parameters.rotatable</c>.
    /// Rotatable unless the record says <c>rotatable: false</c>; an empty version is none.
    /// </summary>
    /// <param name="changeKind">A value of <see cref="ChangeKinds"/>.</param>
    /// <param name="payload">The record of the change, as stored.</param>
    public static ChangeFacts FactsOf(string changeKind, JsonElement payload)
    {
        static JsonElement? Field(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : null;
        var observation = changeKind == ChangeKinds.Observation;
        var extra = Field(payload, observation ? "metadata" : "execution_parameters") is { ValueKind: JsonValueKind.Object } e ? e : default;
        var version = observation ? Field(payload, "version_id") : Field(extra, "version_id");
        var action = Field(payload, observation ? "action" : "resource_action");
        return new(
            action switch
            {
                null or { ValueKind: JsonValueKind.Null } => "",
                { ValueKind: JsonValueKind.String } a => a.GetString()!,
                { } a => a.GetRawText(),
            },
            version is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } id ? id : null,
            Field(extra, "rotatable")?.ValueKind != JsonValueKind.False);
    }

    // ── Due times ───────────────────────────────────────────────────────────

    /// <summary><c>remediation_due_at</c>: a judgement's (or review's) time plus the grace, as the reference prints a time.</summary>
    /// <param name="at">The judgement's or review's time (ISO 8601; a fraction finer than microseconds is cut).</param>
    /// <param name="graceSeconds">The plan's grace.</param>
    /// <exception cref="ArgumentException"><paramref name="at"/> is not a timestamp.</exception>
    public static string DueAt(string at, int graceSeconds) => Print(Micros(at, nameof(at)) + graceSeconds * MicrosPerSecond);

    /// <summary>
    /// When a remediation a review denied is due: the grace from the decision, never later than it was already due (a
    /// review holds a remediation, it does not extend it).
    /// </summary>
    /// <param name="currentDue">When it was due; null when it was not due yet.</param>
    /// <param name="decidedAt">When the review was decided.</param>
    /// <param name="graceSeconds">The plan's grace.</param>
    /// <exception cref="ArgumentException">A time is not a timestamp.</exception>
    public static string DueAfterDenial(string? currentDue, string decidedAt, int graceSeconds)
    {
        var due = Micros(decidedAt, nameof(decidedAt)) + graceSeconds * MicrosPerSecond;
        return Print(currentDue is null ? due : Math.Min(Micros(currentDue, nameof(currentDue)), due));
    }

    /// <summary>When a remediation is due once a waiver ends: its grace again, from the waiver's end.</summary>
    /// <param name="waivedUntil">When the waiver ends.</param>
    /// <param name="graceSeconds">The plan's grace.</param>
    /// <exception cref="ArgumentException"><paramref name="waivedUntil"/> is not a timestamp.</exception>
    public static string DueAfterWaiver(string waivedUntil, int graceSeconds) =>
        Print(Micros(waivedUntil, nameof(waivedUntil)) + graceSeconds * MicrosPerSecond);

    /// <summary>A time's instant; a fraction finer than microseconds is cut, as the reference and TypeScript read it.</summary>
    private static long Micros(string value, string name) =>
        OfflineVerifier.TimestampMicros(FinerFraction().Replace(value ?? throw new ArgumentNullException(name), "$1", 1))
            ?? throw new ArgumentException($"genesismesh: {name} is not an ISO 8601 timestamp: {value}", name);

    [GeneratedRegex(@"(T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6})[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex FinerFraction();

    /// <summary>An instant in microseconds from 0001-01-01 as the reference prints a time: UTC, microseconds when not zero, <c>Z</c>.</summary>
    private static string Print(long micros)
    {
        if (micros < 0 || micros > DateTime.MaxValue.Ticks / 10)
            throw new ArgumentException("genesismesh: the time is outside the years 1 to 9999");
        return Canonical.Timestamp(new DateTimeOffset(micros * 10, TimeSpan.Zero));
    }

    /// <summary>
    /// Why a remediation record does not carry out the plan the NA stated, or null (the NA refuses it as
    /// <c>remediation_steps_mismatch</c>). A planned <c>disable</c> may be carried out as <c>delete</c>.
    /// </summary>
    /// <param name="planMode">The plan's mode.</param>
    /// <param name="planSteps">The plan's steps.</param>
    /// <param name="record">The remediation record.</param>
    public static string? StepsProblem(string planMode, string planSteps, RemediationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (planMode == RemediationModes.AlertOnly)
            return record.Outcome == RemediationOutcomes.Alerted ? null : $"the remediation is alert_only; the record says {record.Outcome} ({record.Steps})";
        if (record.Outcome != RemediationOutcomes.Cancelled) return $"the remediation is a cancel ({planSteps}); the record only alerted";
        if (planSteps == RemediationSteps.Disable && record.Steps is RemediationSteps.Disable or RemediationSteps.Delete) return null;
        return record.Steps == planSteps ? null : $"the remediation is {planSteps}; the record says {record.Steps}";
    }

    // ── Remediation records ─────────────────────────────────────────────────

    /// <summary>
    /// Why a remediation record may carry secret material, or null: its versions and detail, under the NA's metadata
    /// guard (<see cref="EvidenceMetadata.MetadataProblem(JsonElement)"/>).
    /// </summary>
    /// <param name="record">The remediation record.</param>
    public static string? SecretProblem(RemediationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var values = new JsonObject();
        if (record.VersionId is not null) values["version_id"] = record.VersionId;
        if (record.DisabledVersionId is not null) values["disabled_version_id"] = record.DisabledVersionId;
        if (record.Detail is not null) values["detail"] = record.Detail;
        return EvidenceMetadata.MetadataProblem(JsonSerializer.SerializeToElement(values));
    }

    /// <summary>
    /// Whether the NA would admit a signed remediation record from <paramref name="key"/>, as its
    /// <c>check_remediation</c> decides: null when authentic, signed by an active executor key of the record's sovereign
    /// covering its resource, and free of secret material; otherwise the NA's code
    /// (<c>remediation_invalid_signature</c>, <c>remediation_unknown_key</c>, <c>remediation_key_retired</c>,
    /// <c>remediation_out_of_scope</c>, <c>remediation_secret_material</c>). The record's form is
    /// <see cref="RecordProblem"/>'s to check.
    /// </summary>
    /// <param name="record">The signed remediation record.</param>
    /// <param name="key">The registered key whose ID the signature names (as the NA lists it); null when none is.</param>
    public static RemediationProblem? Check(RemediationRecord record, ExecutorKeyRecord? key)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Check(JsonSerializer.SerializeToElement(record), key);
    }

    private static RemediationProblem? Check(JsonElement record, ExecutorKeyRecord? key)
    {
        if (!record.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object
            || !signature.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String)
            return new("remediation_invalid_signature", "record is not signed");
        var executor = record.GetProperty("executor_sovereign_id").GetString();
        if (key is null || key.ExecutorSovereignId != executor)
            return new("remediation_unknown_key", $"signing key is not registered for {executor}");
        if (!OfflineVerifier.VerifyEd25519(OfflineVerifier.OutOfBandCanonical(record), sig.GetString()!, [key.PublicKey]))
            return new("remediation_invalid_signature", "signature does not verify");
        var role = key.Role ?? "executor";
        if (key.RetiredAt is not null) return new("remediation_key_retired", "signing key is retired");
        if (role != "executor") return new("remediation_out_of_scope", $"signing key is an {role} key, not an executor key");
        var resource = record.GetProperty("resource_id").GetString()!;
        if (key.ResourcePrefix is { } prefix && !resource.StartsWith(prefix, StringComparison.Ordinal))
            return new("remediation_out_of_scope", $"signing key covers only resources starting with {prefix}");
        var secret = SecretProblem(record.Deserialize<RemediationRecord>()!);
        return secret is null ? null : new("remediation_secret_material", secret);
    }

    /// <summary>
    /// Why a remediation record as received is not in its exact form, or null: not an object; not a remediation record
    /// (a field missing, of another type or one this SDK does not know, steps that do not fit its outcome and versions);
    /// or not in the form it is signed in (an absent field sent as null, a time not in UTC <c>Z</c> form, a signature with
    /// other fields).
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static string? FormProblem(string recordJson)
    {
        using var doc = StrictJsonReader.Parse(recordJson);
        return FormProblem(doc.RootElement);
    }

    private static string? FormProblem(JsonElement record)
    {
        const string model = "RemediationRecord";
        if (record.ValueKind != JsonValueKind.Object) return "record is not an object";
        if (!OfflineVerifier.ValidOutOfBand(model, record) || StrictFields.UnknownFields(model, record).Count > 0)
            return OfflineVerifier.RemediationStepsProblem(record) ?? "record does not match the RemediationRecord model";
        if (record.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null) || StrictFields.NonCanonicalFields(model, record).Count > 0
            || (record.TryGetProperty("signature", out var signature) && signature.EnumerateObject().Any(p => p.Name is not ("key_id" or "sig"))))
            return "record is not in its exact serialized form";
        return null;
    }

    /// <summary>
    /// A remediation record as received, checked in the NA's order: first its exact form (<c>remediation_malformed</c>,
    /// <see cref="FormProblem(string)"/>), then <see cref="Check(RemediationRecord, ExecutorKeyRecord?)"/>: its signature, key,
    /// scope and secret material. Null when the NA would admit it.
    /// </summary>
    /// <param name="recordJson">The record JSON, as received.</param>
    /// <param name="key">The registered key whose ID the signature names (as the NA lists it); null when none is.</param>
    /// <exception cref="StrictJsonException"><paramref name="recordJson"/> is not strict JSON.</exception>
    public static RemediationProblem? RecordProblem(string recordJson, ExecutorKeyRecord? key)
    {
        using var doc = StrictJsonReader.Parse(recordJson);
        return FormProblem(doc.RootElement) is { } form ? new("remediation_malformed", form) : Check(doc.RootElement, key);
    }

    // ── Reviews ─────────────────────────────────────────────────────────────

    /// <summary>The route a review is requested on.</summary>
    private const string ReviewRequestPath = "/admin/evidence/reviews";

    /// <summary>The route a review is decided on.</summary>
    private static string ReviewDecisionPath(string reviewId) => $"{ReviewRequestPath}/{reviewId}/decide";

    /// <summary>How far an embedded request's timestamp may be from the time the NA recorded: 300 seconds.</summary>
    private const long ApprovalMaxAgeMicros = 300 * MicrosPerSecond;

    /// <summary>The admin signature version the embedded requests are signed under.</summary>
    private const int AdminSignatureVersion = 2;

    /// <summary>
    /// Why the decider of a review is not a second holder, or null: its key and holder differ from the requester's
    /// (<c>review_same_holder_as_requester</c>), and its holder from the observed actor's, where an actor named like
    /// the decider's key or holder counts as the decider (<c>review_same_holder_as_actor</c>). The first that fails
    /// wins.
    /// </summary>
    /// <param name="review">The review, or the decision about to be recorded: its decider, requester and actor.</param>
    public static RemediationProblem? ReviewHolderProblem(ReviewRecord review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.RequestedBy is { } requestedBy
            && (review.DecidedBy == requestedBy || review.DeciderHolder == review.RequesterHolder))
            return new("review_same_holder_as_requester", $"holder {review.DeciderHolder} requested this review; another holder decides it");
        if (review.Actor is { } actor
            && (review.DeciderHolder == review.ActorHolder || actor == review.DecidedBy || actor == review.DeciderHolder))
            return new("review_same_holder_as_actor", $"holder {review.DeciderHolder} is the observed actor ({actor}); another holder decides");
        return null;
    }

    /// <summary>
    /// Why the requester of a review is the change's actor (its key, or its holder), or null: the person who made a
    /// change does not ask for its review (<c>review_requested_by_actor</c>). An actor that maps to no holder cannot be
    /// excluded by holder.
    /// </summary>
    /// <param name="requestedBy">The requester's operator key.</param>
    /// <param name="requesterHolder">The requester's holder.</param>
    /// <param name="actor">The change's actor, as the source reported it; null when it named none.</param>
    /// <param name="actorHolder">The holder the actor maps to; null when none.</param>
    public static RemediationProblem? ReviewRequestProblem(string requestedBy, string requesterHolder, string? actor, string? actorHolder)
    {
        if (actor is not null && (requesterHolder == actorHolder || actor == requestedBy || actor == requesterHolder))
            return new("review_requested_by_actor", $"holder {requesterHolder} is the change's actor ({actor}); another holder requests its review");
        return null;
    }

    /// <summary>Whether the operator's signature verifies over the request it kept (signature version 2), with its public key.</summary>
    /// <param name="approval">The embedded request.</param>
    public static bool AdminApprovalValid(AdminApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Path is not { } path || !path.StartsWith('/') || approval.Method is null || approval.AdminSignature is null
            || approval.PublicKey is null)
            return false;
        var query = new JsonObject();
        foreach (var (name, values) in approval.Query ?? new Dictionary<string, IList<string>>())
            query[name] = new JsonArray([.. (values ?? []).Select(v => (JsonNode?)JsonValue.Create(v))]);
        var payload = new JsonObject
        {
            ["v"] = AdminSignatureVersion,
            ["method"] = approval.Method.ToUpperInvariant(),
            ["path"] = path,
            ["query"] = query,
            ["audience"] = approval.Audience,
            ["body"] = approval.Body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? new JsonObject() : JsonNode.Parse(approval.Body.GetRawText()),
            ["key_id"] = approval.KeyId,
            ["timestamp"] = approval.Timestamp,
            ["nonce"] = approval.Nonce,
        };
        return OfflineVerifier.VerifyEd25519(Canonical.Of(JsonSerializer.SerializeToElement(payload)), approval.AdminSignature, [approval.PublicKey]);
    }

    /// <summary>
    /// The one timestamp grammar of <c>waive_until</c> and the admin timestamps a review embeds: RFC 3339 with an
    /// upper-case <c>T</c>, seconds, an optional fraction of 1 to 6 digits and <c>Z</c> or <c>+HH:MM</c>/<c>-HH:MM</c>.
    /// </summary>
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,6}))?(?:Z|([+-])([0-9]{2}):([0-9]{2}))\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Grammar();

    /// <summary>Microseconds from 0001-01-01 of a timestamp in <see cref="Rfc3339Grammar"/>; null for anything else.</summary>
    private static long? Instant(string? value)
    {
        var m = value is null ? null : Rfc3339Grammar().Match(value);
        if (m is not { Success: true }) return null;
        int N(int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        var (year, month, day, hour, minute, second) = (N(1), N(2), N(3), N(4), N(5), N(6));
        var (offsetHour, offsetMinute) = m.Groups[8].Success ? (N(9), N(10)) : (0, 0);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59 || offsetHour > 23 || offsetMinute > 59)
            return null;
        var offset = (m.Groups[8].Value == "-" ? -1 : 1) * (offsetHour * 60L + offsetMinute) * 60 * MicrosPerSecond;
        var fraction = m.Groups[7].Success ? long.Parse(m.Groups[7].Value.PadRight(6, '0'), CultureInfo.InvariantCulture) : 0;
        return new DateTime(year, month, day, hour, minute, second).Ticks / 10 + fraction - offset;
    }

    /// <summary>
    /// <paramref name="value"/> in UTC as the reference prints a time (<c>2026-10-12T09:00:00.500000Z</c>) when it is an
    /// RFC 3339 timestamp in the grammar <c>waive_until</c> and the admin timestamps a review embeds follow: an upper-case
    /// <c>T</c>, seconds, a fraction of 1 to 6 digits at most, and <c>Z</c> or an offset <c>+HH:MM</c>/<c>-HH:MM</c>. Not
    /// ISO 8601's basic format, week or ordinal dates, <c>24:00</c>, a leap second, an offset with seconds, or a time
    /// without an offset; null for those, and for a time that falls outside the years 1 to 9999 in UTC.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    public static string? Rfc3339(string? value) =>
        Instant(value) is { } at && at >= 0 && at <= DateTime.MaxValue.Ticks / 10 ? Print(at) : null;

    private static string? ApprovalProblem(AdminApproval approval, string who, string keyId, string path, IReadOnlyList<string> audiences, string at)
    {
        if (!AdminApprovalValid(approval)) return $"the {who}'s signature does not verify over its request";
        if (approval.KeyId != keyId) return $"the {who}'s request is signed by {approval.KeyId}, not {keyId}";
        if (approval.Method != "POST" || approval.Path != path || approval.Query?.Count > 0)
            return $"the {who}'s request is {approval.Method} {approval.Path}, not POST {path}";
        if (!audiences.Contains(approval.Audience, StringComparer.Ordinal)) return $"the {who}'s request is for another Network Authority";
        // A recorded time that is not one compares as NaN does in TypeScript: no farther than the bound.
        if (Instant(approval.Timestamp) is not { } signedAt
            || (OfflineVerifier.TimestampMicros(at) is { } recordedAt && Math.Abs(signedAt - recordedAt) > ApprovalMaxAgeMicros))
            return $"the {who}'s request was signed more than 300s from when it was recorded";
        return null;
    }

    /// <summary>
    /// Why the requests embedded in a review do not prove it, or null. Offline, with the review alone and the NA's public
    /// keys (the audience): the decider signed <c>POST /admin/evidence/reviews/&lt;review_id&gt;/decide</c>, without a
    /// query, as <c>decided_by</c>, with exactly the verdict, waiver end and note the review records, within 300
    /// seconds of <c>decided_at</c>; a requester signed <c>POST /admin/evidence/reviews</c> with exactly the subject and
    /// justification it records, within 300 seconds of <c>requested_at</c>. Whether each public key is the operator
    /// key's is for the holder registry to say.
    /// </summary>
    /// <param name="review">The review record.</param>
    /// <param name="naPublicKeys">The NA's public keys: the audiences the requests may name.</param>
    public static string? ReviewApprovalProblem(ReviewRecord review, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        var approval = review.Approval ?? new AdminApproval();
        if (ApprovalProblem(approval, "decider", review.DecidedBy, ReviewDecisionPath(review.ReviewId), naPublicKeys, review.DecidedAt) is { } problem)
            return problem;
        var body = approval.Body.ValueKind == JsonValueKind.Object ? approval.Body : JsonSerializer.SerializeToElement(new JsonObject());
        if (body.EnumerateObject().Any(p => p.Name is not ("verdict" or "waive_until" or "note")))
            return "the decider's request carries fields the review does not record";
        JsonElement? Field(string name) => body.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
        if (!Same(Field("verdict"), review.Verdict) || !Same(Field("note"), review.Note))
            return "the decider's request gives another verdict or note";
        var waiver = Field("waive_until");
        if (waiver is null != review.WaiveUntil is null
            || (waiver is { } w && review.WaiveUntil is { } recorded
                && !(Instant(w.ValueKind == JsonValueKind.String ? w.GetString() : null) is { } given
                     && OfflineVerifier.TimestampMicros(recorded) is { } kept && given == kept)))
            return "the decider's request gives another waiver time (an RFC 3339 timestamp)";
        if (review.RequestApproval is { } request)
            return RequestApprovalProblem(request, review.RequestedBy ?? "", review.SubjectKind, review.SubjectId, review.Justification ?? "",
                review.RequestedAt, naPublicKeys);
        return null;
    }

    /// <summary>A body field equal to a string the review records, or both absent.</summary>
    private static bool Same(JsonElement? field, string? recorded) =>
        field is { } f ? f.ValueKind == JsonValueKind.String && f.GetString() == recorded : recorded is null;

    /// <summary>
    /// Why a requester's embedded request does not prove a review request, or null: signed by the requester as
    /// <c>POST /admin/evidence/reviews</c> for this NA, without a query, with exactly the subject and justification,
    /// within 300 seconds of the time it was recorded.
    /// </summary>
    private static string? RequestApprovalProblem(
        AdminApproval approval, string requestedBy, string subjectKind, string subjectId, string justification, string requestedAt,
        IReadOnlyList<string> naPublicKeys)
    {
        if (ApprovalProblem(approval, "requester", requestedBy, ReviewRequestPath, naPublicKeys, requestedAt) is { } problem) return problem;
        var expected = new Dictionary<string, string>
        {
            ["subject_kind"] = subjectKind,
            ["subject_id"] = subjectId,
            ["justification"] = justification,
        };
        var fields = approval.Body.ValueKind == JsonValueKind.Object ? approval.Body.EnumerateObject().ToList() : [];
        if (fields.Count != 3 || fields.Any(p => !expected.TryGetValue(p.Name, out var want) || !Same(p.Value, want)))
            return "the requester's request names another subject or justification";
        return null;
    }

    /// <summary>
    /// Why a review record's fields do not fit together, or null: <c>waive_until</c> is given exactly for a waiver, after
    /// the decision and within 90 days of it; a requested review names its requester, holder, justification and request,
    /// and one the NA opened none of them.
    /// </summary>
    /// <param name="review">The review record.</param>
    public static string? ReviewRecordProblem(ReviewRecord review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return OfflineVerifier.ReviewConsistencyProblem(JsonSerializer.SerializeToElement(review));
    }

    // ── Policies ────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    /// <summary>
    /// The issues the NA finds in a policy's 1.4.0 sections (the codes of <c>validate_boundary_policy</c>): remediation
    /// guidance names a gate of the policy or <c>"*"</c> (<c>invalid_remediation</c>); an emergency policy selects one
    /// exact capability, other than the exact one it stands for, with a valid justification parameter
    /// (<c>invalid_emergency</c>), required by an enforce-mode <c>required_parameter.v1</c> gate
    /// (<c>emergency_justification_not_required</c>). The NA checks the rest of a policy
    /// (<c>PoliciesClient.Validate</c>).
    /// </summary>
    /// <param name="policy">A signed policy.</param>
    public static IReadOnlyList<PolicyIssue> PolicySectionIssues(BoundaryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return PolicySectionIssues(JsonSerializer.SerializeToElement(policy));
    }

    /// <summary>As <see cref="PolicySectionIssues(BoundaryPolicy)"/>, for a policy as declared, before publication.</summary>
    /// <param name="intent">The policy intent.</param>
    public static IReadOnlyList<PolicyIssue> PolicySectionIssues(BoundaryPolicyIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return PolicySectionIssues(JsonSerializer.SerializeToElement(intent));
    }

    private static List<PolicyIssue> PolicySectionIssues(JsonElement policy)
    {
        var issues = new List<PolicyIssue>();
        var gates = policy.TryGetProperty("gates", out var g) && g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().ToList() : [];
        if (policy.TryGetProperty("remediation", out var remediation) && remediation.ValueKind == JsonValueKind.Object)
        {
            var keys = remediation.EnumerateObject().Select(p => p.Name).ToList();
            if (keys.Count == 0) issues.Add(new PolicyIssue { Code = "invalid_remediation", Message = "remediation guidance names no gate" });
            var gateIds = gates.Select(x => Text(x, "gate_id")).ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys.Order(Canonical.CodePointComparer.Instance))
                if (key != "*" && !gateIds.Contains(key))
                    issues.Add(new PolicyIssue { Code = "invalid_remediation", Message = $"remediation names {key}, which is not a gate of the policy", GateId = key });
        }
        if (!policy.TryGetProperty("emergency", out var emergency) || emergency.ValueKind != JsonValueKind.Object) return issues;
        var capabilities = Capabilities(policy);
        var standsFor = Text(emergency, "stands_for") ?? "";
        if (capabilities.Count != 1 || capabilities[0].EndsWith(".*", StringComparison.Ordinal))
            issues.Add(new PolicyIssue { Code = "invalid_emergency", Message = "an emergency policy selects exactly one capability by its exact ID" });
        else if (capabilities[0] == standsFor)
            issues.Add(new PolicyIssue { Code = "invalid_emergency", Message = "the emergency capability must differ from the capability it stands for" });
        if (standsFor.EndsWith(".*", StringComparison.Ordinal))
            issues.Add(new PolicyIssue { Code = "invalid_emergency", Message = "stands_for is an exact capability ID" });
        var parameter = Text(emergency, "justification_parameter") ?? "";
        if (!Parameter().IsMatch(parameter))
        {
            issues.Add(new PolicyIssue { Code = "invalid_emergency", Message = "justification_parameter must match [A-Za-z0-9_-]{1,64}" });
            return issues;
        }
        var path = "request_parameters." + parameter;
        if (!gates.Any(x => Text(x, "gate_type") == "required_parameter.v1" && (Text(x, "mode") ?? "enforce") == "enforce"
                && x.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object && Text(config, "path") == path))
            issues.Add(new PolicyIssue
            {
                Code = "emergency_justification_not_required",
                Message = $"an emergency policy requires {path} with an enforce-mode required_parameter.v1 gate",
            });
        return issues;
    }

    private static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Capabilities(JsonElement policy) =>
        policy.TryGetProperty("selector", out var selector) && selector.ValueKind == JsonValueKind.Object
            && selector.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
            : [];

    /// <summary>
    /// Emergency capability to the ordinary capability it stands for, from the policies given (those active at
    /// <paramref name="at"/>) whose validity covers <paramref name="at"/>. A capability a policy without an emergency
    /// section also names, exactly or by a <c>prefix.*</c> pattern, is no emergency capability. The NA matches an
    /// observer's report of the ordinary capability to a change made under the emergency one with this map.
    /// </summary>
    /// <param name="policies">The active policies.</param>
    /// <param name="at">The time of the change (ISO 8601).</param>
    public static IReadOnlyDictionary<string, string> EmergencyMap(IReadOnlyList<BoundaryPolicy> policies, string at)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OfflineVerifier.TimestampMicros(at ?? "") is not { } when) return map;
        bool Emergency(BoundaryPolicy p) => p.Emergency.ValueKind == JsonValueKind.Object;
        foreach (var policy in policies)
        {
            var capabilities = policy.Selector?.Capabilities ?? [];
            if (!Emergency(policy) || capabilities.Count != 1
                || !(OfflineVerifier.TimestampMicros(policy.ValidFrom ?? "") is { } from && from <= when
                     && OfflineVerifier.TimestampMicros(policy.ValidUntil ?? "") is { } until && when <= until))
                continue;
            var capability = capabilities[0];
            if (policies.Any(other => !Emergency(other) && Selects(other, capability))) continue;
            map[capability] = Text(policy.Emergency, "stands_for") ?? "";
        }
        return map;
    }

    /// <summary>Whether a policy's selector names <paramref name="capability"/>, exactly or by a <c>prefix.*</c> pattern.</summary>
    private static bool Selects(BoundaryPolicy policy, string capability) =>
        (policy.Selector?.Capabilities ?? []).Any(pattern => pattern == capability
            || (pattern.EndsWith(".*", StringComparison.Ordinal) && capability.StartsWith(pattern[..^1], StringComparison.Ordinal)));
}
