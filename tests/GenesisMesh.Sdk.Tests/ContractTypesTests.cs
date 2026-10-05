using System.Reflection;
using System.Text.Json;
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
    };

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
