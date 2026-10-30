using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>What <see cref="ExecutionRecorder.Record"/> records: one execution under an authorizing decision.</summary>
public sealed class RecordExecutionRequest
{
    /// <summary>The verified decision that authorized the execution. Required.</summary>
    public BoundaryDecision? Decision { get; init; }

    /// <summary>The capability executed, such as <c>partner-secret.rotate</c>. Required.</summary>
    public string ExecutedCapability { get; init; } = "";

    /// <summary>A value of <see cref="ExecutionOutcomes"/>. Required.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>
    /// What was used, as metadata: identifiers, settings, versions and times, never a secret value. Null records
    /// <c>{}</c>.
    /// </summary>
    public IDictionary<string, object?>? ExecutionParameters { get; init; }

    /// <summary>A short description of the outcome; counts toward the metadata limit.</summary>
    public string? OutcomeDetail { get; init; }

    /// <summary>The previous record under the same decision; null for the first (sets the sequence and its link).</summary>
    public ExecutionEvidence? PriorRecord { get; init; }

    /// <summary>The resource acted on, such as <c>kv:vault/secret</c>: an identifier, never a value. Set with <see cref="ResourceAction"/>.</summary>
    public string? ResourceId { get; init; }

    /// <summary>A value of <see cref="ResourceActions"/>. Set with <see cref="ResourceId"/>.</summary>
    public string? ResourceAction { get; init; }

    /// <summary>
    /// Where the resource's chain ends, from any decision: <see cref="EvidenceStoreClient.ResourceHead"/>, or
    /// <see cref="ExecutionRecorder.HeadOf"/> of its previous record. Null for the resource's first record.
    /// </summary>
    public ResourceHead? PriorResource { get; init; }

    /// <summary>
    /// When it was executed; default now, but never before the decision. A time given here is used as given: the NA
    /// refuses a record stamped before the decision.
    /// </summary>
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>The record's ID; default a new UUID. Reuse it only to resubmit the same record.</summary>
    public string? EvidenceId { get; init; }
}

/// <summary>
/// Builds and signs ExecutionEvidence for one executor (1.9.0), exactly as the Python reference
/// <c>record_execution</c> does: the record a controller submits after acting on an authorized decision
/// (<see cref="EvidenceStoreClient.Submit"/>). Secret material, and values the NA would refuse as
/// <c>invalid_json</c>, are refused before anything is signed.
/// <para>
/// The signer is the executor key, which the platform team registers with the NA for
/// <see cref="ExecutorSovereignId"/>, optionally limited to a resource prefix
/// (<see cref="RegisterExecutorKeyRequest.ResourcePrefix"/>): the NA refuses a record of such a key for another
/// resource (<c>evidence_out_of_scope</c>).
/// </para>
/// </summary>
public sealed class ExecutionRecorder
{
    private readonly ISigner _signer;

    /// <summary>A recorder signing with <paramref name="signer"/> for <paramref name="executorSovereignId"/>.</summary>
    /// <param name="executorSovereignId">The sovereign executing; its registered executor key must be the signer's.</param>
    /// <param name="signer">The executor key; its <see cref="ISigner.KeyId"/> is the registered key ID.</param>
    /// <exception cref="ArgumentException"><paramref name="executorSovereignId"/> is empty.</exception>
    public ExecutionRecorder(string executorSovereignId, ISigner signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(executorSovereignId);
        ArgumentNullException.ThrowIfNull(signer);
        ExecutorSovereignId = executorSovereignId;
        _signer = signer;
    }

    /// <summary>The sovereign executing.</summary>
    public string ExecutorSovereignId { get; }

    /// <summary>The executor key's ID.</summary>
    public string KeyId => _signer.KeyId;

    /// <summary>
    /// A signed record of one execution. Records under one decision are chained through
    /// <see cref="RecordExecutionRequest.PriorRecord"/>, records of one resource through
    /// <see cref="RecordExecutionRequest.PriorResource"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The decision, capability or outcome is missing, or only one of the resource ID and action is set.
    /// </exception>
    /// <exception cref="SecretMaterialException">
    /// The parameters or the outcome detail hold secret material or exceed <see cref="EvidenceMetadata.MaxMetadataBytes"/>.
    /// </exception>
    /// <exception cref="StrictJsonException">The record holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    public async Task<ExecutionEvidence> Record(RecordExecutionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = request.Decision
            ?? throw new ArgumentException("genesismesh: the authorizing decision is required", nameof(request));
        if (request.ExecutedCapability.Length == 0 || request.Outcome.Length == 0)
            throw new ArgumentException("genesismesh: the executed capability and the outcome are required", nameof(request));
        if ((request.ResourceId is null) != (request.ResourceAction is null))
            throw new ArgumentException("genesismesh: resource_id and resource_action go together", nameof(request));

        var parameters = Element(request.ExecutionParameters ?? new Dictionary<string, object?>());
        if (EvidenceMetadata.CheckMetadataOnly(parameters, request.OutcomeDetail) is { } reason)
            throw new SecretMaterialException(reason);

        var prior = request.PriorRecord;
        var record = new ExecutionEvidence
        {
            EvidenceId = request.EvidenceId ?? Guid.NewGuid().ToString(),
            SequenceNo = prior is null ? 1 : prior.SequenceNo + 1,
            DecisionId = decision.DecisionId,
            ContextId = decision.ContextId,
            AgreementId = decision.AgreementId ?? "",
            ExecutorSovereignId = ExecutorSovereignId,
            ExecutedCapability = request.ExecutedCapability,
            ExecutionParameters = parameters,
            ExecutedAt = Canonical.Timestamp(request.ExecutedAt ?? NotBeforeDecision(decision.DecisionMadeAt)),
            Outcome = request.Outcome,
            OutcomeDetail = request.OutcomeDetail,
            PrevEvidenceDigest = prior is null ? null : Digest(prior),
        };
        if (request.ResourceId is not null)
        {
            var head = request.PriorResource;
            record.ResourceId = request.ResourceId;
            record.ResourceAction = request.ResourceAction;
            record.ResourceSequence = head is null ? 1 : head.ResourceSequence + 1;
            record.PrevResourceDigest = head?.RecordDigest;
        }

        var unsigned = Element(record);
        // 1.3.1 in the TypeScript SDK: a value the NA would refuse as invalid_json is refused before signing. The
        // record sits two levels deep in what carries it (a batch, or an export's quarantine record).
        StrictJsonReader.Check("[[" + unsigned.GetRawText() + "]]");
        var keyId = _signer.KeyId;
        var signed = OfflineVerifier.ReceivedCanonical("ExecutionEvidence", unsigned);
        var sig = await _signer.SignAsync(Encoding.UTF8.GetBytes(signed), ct).ConfigureAwait(false);
        if (sig.Length != 64) throw new InvalidOperationException("genesismesh: the signer returned a signature that is not 64 bytes");
        record.Signature = Element(new Dictionary<string, string> { ["key_id"] = keyId, ["sig"] = Convert.ToBase64String(sig) });
        return record;
    }

    /// <summary>The record's <c>digest()</c>, as the next record links to it (<see cref="OfflineVerifier.ExecutionDigest"/>).</summary>
    public static string Digest(ExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return OfflineVerifier.ExecutionDigest(Element(evidence).GetRawText());
    }

    /// <summary>
    /// The resource head a record leaves: its position on the resource chain and its digest. Pass it as the next
    /// record's <see cref="RecordExecutionRequest.PriorResource"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The record names no resource: a record linked to it would be refused (<c>resource_chain_mismatch</c>).
    /// </exception>
    public static ResourceHead HeadOf(ExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ResourceId is null || evidence.ResourceSequence is not { } sequence)
            throw new ArgumentException("genesismesh: the record names no resource, so it is no resource's head", nameof(evidence));
        return new ResourceHead { ResourceSequence = sequence, RecordDigest = Digest(evidence) };
    }

    /// <summary>
    /// Now, but never before the decision: the NA refuses evidence stamped before <c>decision_made_at</c>, as from a
    /// host whose clock is behind the NA's. The NA writes microseconds, which <see cref="Canonical.Timestamp"/> keeps.
    /// </summary>
    private static DateTimeOffset NotBeforeDecision(string decisionMadeAt)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var decided = OfflineVerifier.ParseTimestamp(decisionMadeAt);
            return decided > now ? decided : now;
        }
        catch (FormatException)
        {
            return now;
        }
    }

    /// <summary>
    /// True when <see cref="Record"/> would refuse this metadata: the secret guard refuses it, or it is JSON the NA
    /// would not read alike (checked as deep as a record holds it).
    /// </summary>
    internal static bool Unrecordable(IDictionary<string, object?> executionParameters, string? outcomeDetail)
    {
        try
        {
            var parameters = Element(executionParameters);
            if (EvidenceMetadata.CheckMetadataOnly(parameters, outcomeDetail) is not null) return true;
            var metadata = Element(new Dictionary<string, object?> { ["execution_parameters"] = parameters, ["outcome_detail"] = outcomeDetail });
            StrictJsonReader.Check("[[" + metadata.GetRawText() + "]]");
            return false;
        }
        catch (StrictJsonException)
        {
            return true;
        }
    }

    /// <summary>How a record is written: as request bodies are, with every string checked for a lone surrogate.</summary>
    private static readonly JsonSerializerOptions RecordOptions = new(Auth.SerializerOptions) { Converters = { new WholeStrings() } };

    /// <summary>
    /// <paramref name="value"/> as JSON, as request bodies are written; a value JSON cannot hold is <c>invalid_json</c>,
    /// and a string with an unpaired surrogate <c>lone_surrogate</c>.
    /// </summary>
    private static JsonElement Element(object value)
    {
        try
        {
            return JsonSerializer.SerializeToElement(value, value.GetType(), RecordOptions);
        }
        catch (Exception e) when (e is not StrictJsonException
            && e is ArgumentException or NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new StrictJsonException("invalid_json", e.Message);
        }
    }

    /// <summary>
    /// Strings written whole: System.Text.Json writes an unpaired surrogate as U+FFFD, so a record would sign another
    /// value than the caller's. It is refused instead, as the TypeScript SDK refuses it.
    /// </summary>
    private sealed class WholeStrings : JsonConverter<string>
    {
        // Only written with: records are read with Auth.SerializerOptions.
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Whole(value));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WritePropertyName(Whole(value));

        private static string Whole(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
                else if (char.IsSurrogate(value[i]))
                    throw new StrictJsonException("lone_surrogate", "a string holds an unpaired surrogate");
            }
            return value;
        }
    }
}
