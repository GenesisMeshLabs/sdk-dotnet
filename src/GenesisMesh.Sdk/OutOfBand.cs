using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>What <see cref="ObservationRecorder.Record"/> records: one change seen at its source.</summary>
public sealed class ObservationInput
{
    /// <summary>The resource changed: an identifier, never a value. Required.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>A value of <see cref="ResourceActions"/>. Required.</summary>
    public string Action { get; init; } = "";

    /// <summary>The capability the change exercises, as a governed action would request it. Required.</summary>
    public string Capability { get; init; } = "";

    /// <summary>When the source says the change happened. Give this, or both ends of the window below.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>The start of the window the change happened in, such as the scan before; with <see cref="ChangedNotAfter"/>.</summary>
    public DateTimeOffset? ChangedNotBefore { get; init; }

    /// <summary>The end of the window the change happened in, such as the scan that found it.</summary>
    public DateTimeOffset? ChangedNotAfter { get; init; }

    /// <summary>When the observer saw it; default now.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Who made the change, as the source reported it: a pseudonymous identifier, never a credential.</summary>
    public string? Actor { get; init; }

    /// <summary>Where it was seen, such as <c>cloud-activity-log</c>. Required.</summary>
    public string Source { get; init; } = "";

    /// <summary>The source's ID of the event. Required.</summary>
    public string SourceEventId { get; init; } = "";

    /// <summary>The source's version after the change: execution evidence naming the same <c>version_id</c> matches it.</summary>
    public string? VersionId { get; init; }

    /// <summary>Identifiers, versions and times; never values. Null records <c>{}</c>.</summary>
    public IDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// The record's ID. By default <see cref="ObservationRecorder.ObservationId"/> of the observer, source and source
    /// event, the same for every record of one source event: recorded again with the same fields, it is a
    /// <c>duplicate</c> at the NA.
    /// </summary>
    public string? ObservationId { get; init; }
}

/// <summary>Options of <see cref="ObservationRecorder.FromFinding"/>.</summary>
public sealed class FindingObservationOptions
{
    /// <summary>The capability the change exercises. Required.</summary>
    public string Capability { get; init; } = "";

    /// <summary>The scan before this one: the change happened after it. Required.</summary>
    public DateTimeOffset PreviousScanAt { get; init; }

    /// <summary>This scan: the change happened before it. Required.</summary>
    public DateTimeOffset ScannedAt { get; init; }

    /// <summary>The observation's source; default <c>reconciliation</c>.</summary>
    public string Source { get; init; } = "reconciliation";

    /// <summary>
    /// The action recorded for a status, over the default: <c>unmanaged</c> create, <c>drifted</c> and
    /// <c>present_after_revoke</c> update, <c>missing</c> delete.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Actions { get; init; }
}

/// <summary>
/// Builds and signs observations for one observer (1.9.0), as the TypeScript SDK's <c>ObservationRecorder</c> does: a
/// change seen at its source, made outside the governed path. Submit them with
/// <see cref="EvidenceStoreClient.SubmitObservation"/> or <see cref="EvidenceStoreClient.SubmitObservations"/>.
/// What the NA would refuse is refused before anything is signed.
/// <para>
/// The signer is an observer key, which the platform team registers with the NA for
/// <see cref="ObserverSovereignId"/> with <see cref="RegisterExecutorKeyRequest.Role"/> <c>observer</c>, optionally
/// limited to a resource prefix.
/// </para>
/// </summary>
public sealed class ObservationRecorder
{
    private static readonly Dictionary<string, string> FindingActions = new(StringComparer.Ordinal)
    {
        [ReconciliationStatuses.Unmanaged] = ResourceActions.Create,
        [ReconciliationStatuses.Drifted] = ResourceActions.Update,
        [ReconciliationStatuses.PresentAfterRevoke] = ResourceActions.Update,
        [ReconciliationStatuses.Missing] = ResourceActions.Delete,
    };

    private readonly ISigner _signer;

    /// <summary>A recorder signing with <paramref name="signer"/> for <paramref name="observerSovereignId"/>.</summary>
    /// <param name="observerSovereignId">The sovereign the observer key is registered for.</param>
    /// <param name="signer">The observer key; its <see cref="ISigner.KeyId"/> is the registered key ID.</param>
    /// <exception cref="ArgumentException"><paramref name="observerSovereignId"/> is empty.</exception>
    public ObservationRecorder(string observerSovereignId, ISigner signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(observerSovereignId);
        ArgumentNullException.ThrowIfNull(signer);
        ObserverSovereignId = observerSovereignId;
        _signer = signer;
    }

    /// <summary>The sovereign the observer key is registered for.</summary>
    public string ObserverSovereignId { get; }

    /// <summary>The observer key's ID.</summary>
    public string KeyId => _signer.KeyId;

    /// <summary>
    /// A signed observation of one change. Refused before signing as the NA would refuse it, in its order: a value it
    /// could not read, a record its model does not take, then secret material.
    /// </summary>
    /// <exception cref="OutOfBandRecordException">
    /// <c>observation_malformed</c>: neither a change time nor a whole window is given, or both; the window ends before
    /// it starts; or a field is empty, too long or not one the NA takes. <c>observation_secret_material</c>: the
    /// metadata or the source's strings hold secret material, or exceed <see cref="EvidenceMetadata.MaxMetadataBytes"/>.
    /// </exception>
    /// <exception cref="StrictJsonException">The record holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    public async Task<ObservationRecord> Record(ObservationInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var record = new ObservationRecord
        {
            // A missing source or source event is refused below, as the model refuses it.
            ObservationId = input.ObservationId ?? ObservationId(ObserverSovereignId, input.Source ?? "", input.SourceEventId ?? ""),
            ObserverSovereignId = ObserverSovereignId,
            ResourceId = input.ResourceId,
            Action = input.Action,
            Capability = input.Capability,
            ChangedAt = Time(input.ChangedAt),
            ChangedNotBefore = Time(input.ChangedNotBefore),
            ChangedNotAfter = Time(input.ChangedNotAfter),
            ObservedAt = Canonical.Timestamp(input.ObservedAt ?? DateTimeOffset.UtcNow),
            Actor = input.Actor,
            Source = input.Source!,
            SourceEventId = input.SourceEventId!,
            VersionId = input.VersionId,
            Metadata = ExecutionRecorder.Element(input.Metadata ?? new Dictionary<string, object?>()),
        };
        var unsigned = OutOfBandRecords.Signable(record);
        var window = input.ChangedNotBefore is not null || input.ChangedNotAfter is not null;
        if (input.ChangedAt is not null && window)
            throw new OutOfBandRecordException("observation_malformed",
                "give changed_at, or the changed_not_before and changed_not_after window, not both");
        if (input.ChangedAt is null && (input.ChangedNotBefore is null || input.ChangedNotAfter is null))
            throw new OutOfBandRecordException("observation_malformed",
                "changed_at, or both changed_not_before and changed_not_after, is required");
        if (!OfflineVerifier.ValidOutOfBand("ObservationRecord", unsigned))
        {
            throw new OutOfBandRecordException("observation_malformed",
                Micros(input.ChangedNotBefore) > Micros(input.ChangedNotAfter)
                    ? "changed_not_before is after changed_not_after"
                    : "the observation does not match the ObservationRecord model (a field missing, empty, too long or of another type)");
        }
        if (EvidenceMetadata.ObservationProblem(record) is { } secret)
            throw new OutOfBandRecordException("observation_secret_material", secret);
        record.Signature = await OutOfBandRecords.Sign(unsigned, _signer, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// The default observation ID: the SHA-256, in hex, of what makes an observation one at the NA, its observer
    /// sovereign, source and source event ID, joined by NUL characters. One source event gives one ID.
    /// </summary>
    public static string ObservationId(string observerSovereignId, string source, string sourceEventId)
    {
        ArgumentNullException.ThrowIfNull(observerSovereignId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceEventId);
        return Sha256Hex($"{observerSovereignId}\0{source}\0{sourceEventId}");
    }

    /// <summary>
    /// A reconciliation finding as an observation, or null for a resource in sync. The change is known only within the
    /// window between the two scans, so the NA judges it at both ends of the window. The source event is the scan and
    /// the resource, so a repeated scan does not record the finding twice: signed again, it is the same record, and the
    /// NA answers <c>duplicate</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="FindingObservationOptions.PreviousScanAt"/> or <see cref="FindingObservationOptions.ScannedAt"/> is
    /// not given.
    /// </exception>
    public static ObservationInput? FromFinding(ReconciliationFinding finding, FindingObservationOptions options)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PreviousScanAt == default || options.ScannedAt == default)
            throw new ArgumentException("genesismesh: both scan times, PreviousScanAt and ScannedAt, are required", nameof(options));
        string? action = null;
        if (options.Actions?.TryGetValue(finding.Status, out var chosen) != true || chosen is null)
            FindingActions.TryGetValue(finding.Status, out action);
        else
            action = chosen;
        if (action is null) return null;
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = finding.Status };
        foreach (var (key, value) in finding.Observed?.Metadata ?? new Dictionary<string, object?>()) metadata[key] = value;
        return new ObservationInput
        {
            ResourceId = finding.ResourceId,
            Action = action,
            Capability = options.Capability,
            ChangedNotBefore = options.PreviousScanAt,
            ChangedNotAfter = options.ScannedAt,
            ObservedAt = options.ScannedAt,
            Source = options.Source,
            SourceEventId = Sha256Hex($"{Canonical.Timestamp(options.ScannedAt)}\0{finding.ResourceId}\0{finding.Status}"),
            VersionId = finding.Observed?.Version,
            Metadata = metadata,
        };
    }

    private static string? Time(DateTimeOffset? value) => value is { } at ? Canonical.Timestamp(at) : null;

    /// <summary>A time to the microsecond, as it is signed and compared; null when not given.</summary>
    private static long? Micros(DateTimeOffset? value) => value?.UtcTicks / 10;

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>What <see cref="ExecutionRecorder.SignBreakGlass"/> records: a change made while evaluation failed.</summary>
public sealed class BreakGlassInput
{
    /// <summary>The resource acted on: an identifier, never a value. Required.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>A value of <see cref="ResourceActions"/>. Required.</summary>
    public string ResourceAction { get; init; } = "";

    /// <summary>The capability the evaluation requested. Required.</summary>
    public string Capability { get; init; } = "";

    /// <summary>The attestation the evaluation named, if any.</summary>
    public string? AttestationId { get; init; }

    /// <summary>The evaluation's request parameters. Null records <c>{}</c>.</summary>
    public IDictionary<string, object?>? RequestParameters { get; init; }

    /// <summary>The evaluation's attributes. Null records <c>{}</c>.</summary>
    public IDictionary<string, object?>? Attributes { get; init; }

    /// <summary>Why the controller acted without a decision: 1 to 1024 characters. Required.</summary>
    public string Justification { get; init; } = "";

    /// <summary>The evaluation request that failed, as it was sent; only its digest is recorded. Required.</summary>
    public object? EvaluationRequest { get; init; }

    /// <summary>A value of <see cref="EvaluationFailures"/>. Required.</summary>
    public string EvaluationFailure { get; init; } = "";

    /// <summary>When it was executed; default now.</summary>
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>The outcome, usually a value of <see cref="ExecutionOutcomes"/>: the NA takes any string.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>A short description of the outcome, at most 1024 characters.</summary>
    public string? OutcomeDetail { get; init; }

    /// <summary>What was used, as metadata: identifiers, settings, versions and times. Null records <c>{}</c>.</summary>
    public IDictionary<string, object?>? ExecutionParameters { get; init; }
}

/// <summary>
/// What <see cref="ExecutionRecorder.SignRemediation"/> records (1.9.0): what a controller did about a change whose
/// remediation the NA made due. The subject, change and resource come from the change's remediation state.
/// </summary>
public sealed class RemediationInput
{
    /// <summary>A value of <see cref="RemediationSubjectKinds"/>. Required.</summary>
    public string SubjectKind { get; init; } = "";

    /// <summary>The judgement (or decision) that made the remediation due. Required.</summary>
    public string SubjectId { get; init; } = "";

    /// <summary>A value of <see cref="ChangeKinds"/>. Required.</summary>
    public string ChangeKind { get; init; } = "";

    /// <summary>The record of the change: the observation, break-glass record or execution evidence. Required.</summary>
    public string ChangeId { get; init; } = "";

    /// <summary>The resource: an identifier, never a value. Required.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>A value of <see cref="RemediationSteps"/>. Required.</summary>
    public string Steps { get; init; } = "";

    /// <summary>A value of <see cref="RemediationOutcomes"/>; default <c>alerted</c> for steps <c>none</c>, <c>cancelled</c> otherwise.</summary>
    public string? Outcome { get; init; }

    /// <summary>For a forward rotation: its execution evidence, stored first.</summary>
    public string? RotationEvidenceId { get; init; }

    /// <summary>For a forward rotation: the version it issued.</summary>
    public string? VersionId { get; init; }

    /// <summary>The version disabled or deleted.</summary>
    public string? DisabledVersionId { get; init; }

    /// <summary>When it was done; default now.</summary>
    public DateTimeOffset? RemediatedAt { get; init; }

    /// <summary>Up to 1024 characters; no secret material.</summary>
    public string? Detail { get; init; }

    /// <summary>The record's ID; default a new UUID.</summary>
    public string? RemediationId { get; init; }
}

/// <summary>Building, checking and signing records of changes outside the governed path.</summary>
internal static class OutOfBandRecords
{
    /// <summary>The unsigned remediation record for <paramref name="input"/>.</summary>
    public static RemediationRecord RemediationOf(RemediationInput input, string executorSovereignId)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new RemediationRecord
        {
            RemediationId = input.RemediationId ?? Guid.NewGuid().ToString(),
            ExecutorSovereignId = executorSovereignId,
            SubjectKind = input.SubjectKind,
            SubjectId = input.SubjectId,
            ChangeKind = input.ChangeKind,
            ChangeId = input.ChangeId,
            ResourceId = input.ResourceId,
            Outcome = input.Outcome ?? (input.Steps == RemediationSteps.None ? RemediationOutcomes.Alerted : RemediationOutcomes.Cancelled),
            Steps = input.Steps,
            RotationEvidenceId = input.RotationEvidenceId,
            VersionId = input.VersionId,
            DisabledVersionId = input.DisabledVersionId,
            RemediatedAt = Canonical.Timestamp(input.RemediatedAt ?? DateTimeOffset.UtcNow),
            Detail = input.Detail,
        };
    }

    /// <summary>
    /// Throws what the NA would refuse an unsigned remediation record for, in its order: a value it could not read
    /// (<see cref="StrictJsonException"/>), a record its model does not take (<c>remediation_malformed</c>: a field
    /// missing, empty, too long or of another type, or steps that do not fit the outcome and versions), then secret
    /// material in its versions or detail (<c>remediation_secret_material</c>). Returns the record as it is signed.
    /// </summary>
    public static JsonElement CheckRemediation(RemediationRecord record)
    {
        var unsigned = Signable(record);
        if (!OfflineVerifier.ValidOutOfBand("RemediationRecord", unsigned))
            throw new OutOfBandRecordException("remediation_malformed", OfflineVerifier.RemediationStepsProblem(unsigned)
                ?? "the record does not match the RemediationRecord model (a field missing, empty, too long or of another type)");
        if (RemediationRules.SecretProblem(record) is { } secret) throw new OutOfBandRecordException("remediation_secret_material", secret);
        return unsigned;
    }

    /// <summary>The reference's limits on a break-glass record's justification and outcome detail, in characters.</summary>
    private const int MaxJustification = 1024;
    private const int MaxOutcomeDetail = 1024;

    /// <summary>The unsigned break-glass record for <paramref name="input"/>, with a new ID.</summary>
    /// <exception cref="ArgumentException">The evaluation request is missing.</exception>
    /// <exception cref="StrictJsonException">A value cannot be written as JSON the NA would read.</exception>
    public static BreakGlassRecord BreakGlass(BreakGlassInput input, string executorSovereignId)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.EvaluationRequest is null)
            throw new ArgumentException("genesismesh: the evaluation request that failed is required", nameof(input));
        var request = Canonical.Of(ExecutionRecorder.Element(input.EvaluationRequest));
        return new BreakGlassRecord
        {
            BreakGlassId = Guid.NewGuid().ToString(),
            ExecutorSovereignId = executorSovereignId,
            ResourceId = input.ResourceId,
            ResourceAction = input.ResourceAction,
            Capability = input.Capability,
            AttestationId = input.AttestationId,
            RequestParameters = ExecutionRecorder.Element(input.RequestParameters ?? new Dictionary<string, object?>()),
            Attributes = ExecutionRecorder.Element(input.Attributes ?? new Dictionary<string, object?>()),
            Justification = input.Justification,
            EvaluationRequestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request))).ToLowerInvariant(),
            EvaluationFailure = input.EvaluationFailure,
            ExecutedAt = Canonical.Timestamp(input.ExecutedAt ?? DateTimeOffset.UtcNow),
            Outcome = input.Outcome,
            OutcomeDetail = input.OutcomeDetail,
            ExecutionParameters = ExecutionRecorder.Element(input.ExecutionParameters ?? new Dictionary<string, object?>()),
        };
    }

    /// <summary>
    /// Throws what the NA would refuse an unsigned break-glass record for, in its order: a value it could not read
    /// (<see cref="StrictJsonException"/>), a record its model does not take (<c>break_glass_malformed</c>; lengths
    /// counted in characters, as the reference counts them), then secret material (<c>break_glass_secret_material</c>).
    /// Returns the record as it is signed.
    /// </summary>
    public static JsonElement CheckBreakGlass(BreakGlassRecord record)
    {
        var unsigned = Signable(record);
        var justification = record.Justification?.EnumerateRunes().Count() ?? -1;
        if (justification < 1 || justification > MaxJustification)
            throw new OutOfBandRecordException("break_glass_malformed", $"a justification of 1 to {MaxJustification} characters is required");
        if (record.OutcomeDetail is not null && record.OutcomeDetail.EnumerateRunes().Count() > MaxOutcomeDetail)
            throw new OutOfBandRecordException("break_glass_malformed", $"outcome_detail is longer than {MaxOutcomeDetail} characters");
        if (!OfflineVerifier.ValidOutOfBand("BreakGlassRecord", unsigned))
            throw new OutOfBandRecordException("break_glass_malformed",
                "the record does not match the BreakGlassRecord model (a field missing, empty, too long or of another type)");
        if (EvidenceMetadata.BreakGlassProblem(record) is { } secret)
            throw new OutOfBandRecordException("break_glass_secret_material", secret);
        return unsigned;
    }

    /// <summary>
    /// Throws <c>break_glass_malformed</c> when what <paramref name="record"/> carries besides the action's report (its
    /// request parameters, attributes and justification) leaves less than <paramref name="reserve"/> bytes of
    /// <see cref="EvidenceMetadata.MaxMetadataBytes"/> for the report, sized as the reference sizes metadata: escaped
    /// as ASCII.
    /// </summary>
    public static void CheckRoom(BreakGlassRecord record, int reserve)
    {
        var carried = ExecutionRecorder.Element(new Dictionary<string, object?>
        {
            ["execution_parameters"] = new Dictionary<string, object?>(),
            ["request_parameters"] = record.RequestParameters,
            ["attributes"] = record.Attributes,
            ["outcome_detail"] = "",
            ["justification"] = record.Justification,
        });
        if (Canonical.Of(carried).Length > EvidenceMetadata.MaxMetadataBytes - reserve)
            throw new OutOfBandRecordException("break_glass_malformed",
                $"the context and justification leave no room for the record within {EvidenceMetadata.MaxMetadataBytes} bytes");
    }

    /// <summary>
    /// The record as JSON, checked as the NA's strict reader reads it two levels deep (in a batch, or an export's
    /// quarantine record).
    /// </summary>
    /// <exception cref="StrictJsonException">The record holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    public static JsonElement Signable(object record)
    {
        var unsigned = ExecutionRecorder.Element(record);
        StrictJsonReader.Check("[[" + unsigned.GetRawText() + "]]");
        return unsigned;
    }

    /// <summary>The signature, <c>{"key_id", "sig"}</c>, over the record's signed form.</summary>
    public static async Task<JsonElement> Sign(JsonElement unsigned, ISigner signer, CancellationToken ct)
    {
        var keyId = signer.KeyId;
        var sig = await signer.SignAsync(Encoding.UTF8.GetBytes(OfflineVerifier.OutOfBandCanonical(unsigned)), ct).ConfigureAwait(false);
        if (sig.Length != 64) throw new InvalidOperationException("genesismesh: the signer returned a signature that is not 64 bytes");
        return ExecutionRecorder.Element(new Dictionary<string, string> { ["key_id"] = keyId, ["sig"] = Convert.ToBase64String(sig) });
    }
}
