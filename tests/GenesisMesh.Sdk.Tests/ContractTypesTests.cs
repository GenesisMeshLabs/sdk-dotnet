using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// The response models must model exactly what the Network Authority sends.
/// testdata/contract/na_responses.json holds a live NA's response to each SDK call
/// (captured by the live contract check); each one is deserialized into the model the
/// SDK method returns. Before 1.0.2 most of these models declared names the NA never
/// sends, so their properties were always empty.
/// </summary>
public class ContractTypesTests
{
    private static readonly string ResponsesPath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "contract", "na_responses.json");

    private static JsonElement Response(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ResponsesPath));
        return doc.RootElement.GetProperty(name).Clone();
    }

    private static T Read<T>(string name) =>
        JsonSerializer.Deserialize<T>(Response(name).GetRawText(), Auth.SerializerOptions)!;

    /// <summary>JSON names of a model: true when required (never omitted when writing).</summary>
    internal static Dictionary<string, bool> JsonFields(Type type)
    {
        var fields = new Dictionary<string, bool>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            var ignore = prop.GetCustomAttribute<JsonIgnoreAttribute>();
            if (name is null || ignore?.Condition == JsonIgnoreCondition.Always) continue;
            fields[name] = ignore is null;
        }
        return fields;
    }

    private static SortedSet<string> Keys(JsonElement obj) =>
        new(obj.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);

    /// <summary>
    /// Where <paramref name="raw"/> differs from <paramref name="type"/> at any depth (1.9.0): a key no model declares,
    /// or a key a model requires that was not sent. A model requires a key it always writes: one without a
    /// <see cref="JsonIgnoreAttribute"/>, or written even when null (<see cref="JsonIgnoreCondition.Never"/>). Models are
    /// followed through their properties, lists and dictionaries; a <see cref="JsonElement"/> is not looked into.
    /// </summary>
    internal static List<string> Unmodeled(JsonNode? raw, Type type, string path = "$")
    {
        var problems = new List<string>();
        Walk(raw, type, path, problems);
        return problems;
    }

    private static void Walk(JsonNode? raw, Type type, string path, List<string> problems)
    {
        if (raw is null) return;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (raw is JsonObject map)
                foreach (var (key, value) in map) Walk(value, type.GetGenericArguments()[1], $"{path}.{key}", problems);
            return;
        }
        var items = type == typeof(string) ? null : type.GetInterfaces().Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (items is not null)
        {
            if (raw is JsonArray list)
                for (var i = 0; i < list.Count; i++) Walk(list[i], items.GetGenericArguments()[0], $"{path}[{i}]", problems);
            return;
        }
        if (!type.IsClass || type.Namespace != "GenesisMesh" || raw is not JsonObject obj) return;
        var declared = new Dictionary<string, (Type Type, bool Required)>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            var ignore = prop.GetCustomAttribute<JsonIgnoreAttribute>();
            if (name is null || ignore?.Condition == JsonIgnoreCondition.Always) continue;
            declared[name] = (prop.PropertyType, ignore is null || ignore.Condition == JsonIgnoreCondition.Never);
        }
        foreach (var (key, value) in obj)
        {
            if (declared.TryGetValue(key, out var field)) Walk(value, field.Type, $"{path}.{key}", problems);
            else problems.Add($"{path}.{key}: not modeled");
        }
        problems.AddRange(declared.Where(f => f.Value.Required && !obj.ContainsKey(f.Key)).Select(f => $"{path}.{f.Key}: declared but not sent"));
    }

    public static TheoryData<string, Type> Models => new()
    {
        { "agreement_offer", typeof(OfferRecord) },
        { "agreement_counter", typeof(OfferRecord) },
        { "agreement_accept", typeof(AgreementRecord) },
        { "boundary_decide", typeof(BoundaryDecision) },
        { "evidence_build", typeof(TrustEvidence) },
        { "disclosure_commit", typeof(CapabilityCommitment) },
        { "disclosure_prove", typeof(CapabilityMembershipProof) },
        { "data_usage_policy", typeof(DataLicensePolicy) },
        { "data_usage_intent", typeof(DataAccessIntent) },
        { "consensus_vote", typeof(ConsensusVote) },
        { "consensus_proof", typeof(ConsensusProof) },
        { "consensus_verify", typeof(ConsensusVerification) },
        { "attestation_issue", typeof(MembershipAttestation) },
        { "health_liveness", typeof(LivenessStatus) },
        { "health_readiness", typeof(Readiness) },
        { "health_status", typeof(NAHealth) },
        { "na_key_history", typeof(KeyHistoryView) },
        { "policy_validate", typeof(PolicyValidation) },
        { "policy_publish", typeof(BoundaryPolicy) },
        { "policy_activate", typeof(PolicyActivation) },
        { "policy_deactivate", typeof(PolicyActivation) },
        { "policy_active", typeof(ActivePolicyStatus) },
        { "policy_history", typeof(PolicyHistory) },
        { "policy_verify", typeof(PolicyVerification) },
        { "policy_list_item", typeof(PolicyVersionSummary) },
        { "boundary_evaluate", typeof(BoundaryEvaluation) },
        { "boundary_evaluate_decision", typeof(BoundaryDecision) },
        { "evidence_register_executor_key", typeof(ExecutorKeyState) },
        { "evidence_submit", typeof(EvidenceSubmission) },
        { "evidence_resource_head", typeof(StoredResourceHead) },
        { "evidence_search", typeof(EvidenceSearchResult) },
        { "evidence_execution_record", typeof(ExecutionEvidence) },
        { "evidence_status", typeof(EvidenceStoreStatus) },
        { "evidence_verify_store", typeof(EvidenceStoreVerification) },
        { "evidence_resource_history", typeof(ResourceHistory) },
        { "evidence_vendor_history", typeof(VendorHistory) },
        { "evidence_export_event", typeof(EvidenceEvent) },
        { "evidence_executor_key_record", typeof(ExecutorKeyRecord) },
        { "evidence_retire_executor_key", typeof(ExecutorKeyState) },
        { "evidence_retention", typeof(RetentionResult) },
        { "attestation_get", typeof(AttestationRecord) },
        { "attestation_list", typeof(AttestationList) },
        { "attestation_count", typeof(AttestationList) },
        { "attestation_verification", typeof(AttestationVerification) },
        { "recognition_policy", typeof(RecognitionPolicy) },
        { "revocation_feed", typeof(SovereignRevocationFeed) },
    };

    // 1.9.0: the objects inside each response too, such as the verification and its anchors in a history.
    [Theory]
    [MemberData(nameof(Models))]
    public void NestedObjectsModelTheNAResponses(string name, Type model)
    {
        var problems = Unmodeled(JsonNode.Parse(Response(name).GetRawText()), model, name);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void ResponseModelsModelTheNAResponses(string name, Type model)
    {
        var raw = Response(name);
        var sent = Keys(raw);
        var declared = JsonFields(model);

        Assert.DoesNotContain(sent, k => !declared.ContainsKey(k));             // keys the model drops
        Assert.DoesNotContain(declared, f => f.Value && !sent.Contains(f.Key)); // names the NA never sends

        // Serializing the deserialized model gives back the NA's fields and none of the
        // obsolete names. (The SDK's serializer options may omit null values.)
        var value = JsonSerializer.Deserialize(raw.GetRawText(), model, Auth.SerializerOptions)!;
        using var again = JsonDocument.Parse(JsonSerializer.Serialize(value, model, Auth.SerializerOptions));
        var written = Keys(again.RootElement);
        Assert.Subset(sent, written);
        Assert.Superset(
            new SortedSet<string>(
                raw.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).Select(p => p.Name),
                StringComparer.Ordinal),
            written);
    }

    // Records the caller passes back to the NA (to a verify route, or into the next call) are
    // checked against their signatures: serializing the typed record must give the canonical
    // form the NA signed. Before 1.8.0 the data usage records dropped their null fields
    // (estimated_volume_bytes, max_volume_bytes_per_session), and the NA refused the intent.
    [Theory]
    [InlineData("agreement_offer", typeof(OfferRecord))]
    [InlineData("agreement_counter", typeof(OfferRecord))]
    [InlineData("agreement_accept", typeof(AgreementRecord))]
    [InlineData("evidence_build", typeof(TrustEvidence))]
    [InlineData("disclosure_commit", typeof(CapabilityCommitment))]
    [InlineData("disclosure_prove", typeof(CapabilityMembershipProof))]
    [InlineData("data_usage_policy", typeof(DataLicensePolicy))]
    [InlineData("data_usage_intent", typeof(DataAccessIntent))]
    [InlineData("consensus_vote", typeof(ConsensusVote))]
    [InlineData("consensus_proof", typeof(ConsensusProof))]
    // 1.9.0: a decision kept its null denial_reason only from 1.9.0, so a typed decision passed back verifies.
    [InlineData("boundary_decide", typeof(BoundaryDecision))]
    [InlineData("boundary_evaluate_decision", typeof(BoundaryDecision))]
    [InlineData("boundary_evaluate_denied_decision", typeof(BoundaryDecision))]
    [InlineData("boundary_evaluate", typeof(BoundaryEvaluation))]
    [InlineData("policy_publish", typeof(BoundaryPolicy))]
    [InlineData("attestation_issue", typeof(MembershipAttestation))]
    // 1.9.0: execution evidence read from the store and passed back, as the prior record of the next one.
    [InlineData("evidence_execution_record", typeof(ExecutionEvidence))]
    // 1.9.0: an attestation as stored, and the revocation feed, read and verified.
    [InlineData("attestation_get", typeof(AttestationRecord))]
    [InlineData("revocation_feed", typeof(SovereignRevocationFeed))]
    public void PassedBackRecordsKeepTheirSignedForm(string name, Type model)
    {
        var raw = Response(name);
        var value = JsonSerializer.Deserialize(raw.GetRawText(), model, Auth.SerializerOptions)!;
        Assert.Equal(Canonical.FromJson(raw.GetRawText()),
            Canonical.FromJson(JsonSerializer.Serialize(value, model, Auth.SerializerOptions)));
    }

    [Theory]
    [InlineData("agreement_verify")]
    [InlineData("boundary_verify")]
    [InlineData("evidence_verify")]
    [InlineData("disclosure_verify")]
    [InlineData("data_usage_verify")]
    public void VerifyResultReadsEitherAnswer(string name)
    {
        var raw = Response(name);
        var result = Read<VerifyResult>(name);
        Assert.True(result.Valid, raw.GetRawText());
        Assert.True(result.Accepted, raw.GetRawText());
        var declared = JsonFields(typeof(VerifyResult));
        Assert.DoesNotContain(Keys(raw), k => !declared.ContainsKey(k));
    }

    [Fact]
    public void VerifyResultReadsRejectionsAndViolations()
    {
        var rejected = JsonSerializer.Deserialize<VerifyResult>(
            """{"accepted": false, "reason": "invalid_signature"}""", Auth.SerializerOptions)!;
        Assert.False(rejected.Valid);
        Assert.False(rejected.Accepted);
        Assert.Equal("invalid_signature", rejected.Reason);

        var violation = JsonSerializer.Deserialize<VerifyResult>(
            """{"valid": false, "violation_count": 1, "violation_reason": "source_not_allowed", "violations": [{}]}""",
            Auth.SerializerOptions)!;
        Assert.False(violation.Valid);
        Assert.Equal("source_not_allowed", violation.Reason);
        Assert.Equal(1, violation.ViolationCount);

        var boundary = Read<VerifyResult>("boundary_verify");
        Assert.True(boundary.Authorized);
        Assert.False(string.IsNullOrEmpty(boundary.DecisionId));
    }

#pragma warning disable CS0618 // the obsolete names are filled from the NA's fields
    [Fact]
    public void ObsoleteNamesAreFilledFromTheNAFields()
    {
        var decision = Read<BoundaryDecision>("boundary_decide");
        Assert.True(decision.Authorized);
        Assert.True(decision.Allowed);
        Assert.Equal(decision.DecisionMadeAt, decision.IssuedAt);
        Assert.NotEqual("", decision.IssuedAt);

        var agreement = Read<AgreementRecord>("agreement_accept");
        Assert.NotEmpty(agreement.Capabilities);
        Assert.Equal(agreement.EstablishedAt, agreement.CreatedAt);

        var attestation = Read<MembershipAttestation>("attestation_issue");
        Assert.Equal(attestation.SubjectId, attestation.SubjectSovereignId);
        Assert.Equal(JsonValueKind.Object, attestation.Signature.ValueKind);

        var evidence = Read<TrustEvidence>("evidence_build");
        Assert.Equal(evidence.IssuerSovereignId, evidence.IssuerId);
        Assert.Equal(evidence.TargetSovereignId, evidence.SubjectId);
        Assert.Equal(JsonValueKind.Object, evidence.Signature.ValueKind);

        var proof = Read<CapabilityMembershipProof>("disclosure_prove");
        Assert.Equal(proof.RevealedCapability, proof.Capability);
        Assert.Equal(JsonValueKind.Array, proof.Proof.ValueKind);

        var commitment = Read<CapabilityCommitment>("disclosure_commit");
        Assert.Equal(commitment.CommittedAt, commitment.IssuedAt);

        var policy = Read<DataLicensePolicy>("data_usage_policy");
        Assert.Equal(policy.LicensorSovereignId, policy.LocalSovereignId);

        var intent = Read<DataAccessIntent>("data_usage_intent");
        Assert.Equal(intent.DeclaredSources.Count, intent.Sources.Count);
        Assert.NotEmpty(intent.AccessTypes);
    }
#pragma warning restore CS0618
}
