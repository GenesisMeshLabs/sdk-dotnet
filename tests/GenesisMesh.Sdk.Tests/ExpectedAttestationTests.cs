using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// 1.9.2: <see cref="OfflineVerifier.VerifyBoundaryDecision"/> checks an expected attestation ID or subject ID
/// without the attestation itself. The decisions are the reference's interop vectors: bd-003 is bound to an
/// attestation, bd-001 to none.
/// </summary>
public class ExpectedAttestationTests
{
    private const string Attestation = "00000000-0000-4000-8000-000000000003";
    private const string Subject = "sovereign-b";

    private static (string Decision, DecisionVerifyOptions Options) Vector(string id,
        string? attestationId = null, string? subjectId = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "interop.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var input = doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Single(v => v.GetProperty("id").GetString() == id).GetProperty("input");
        return (input.GetProperty("decision").GetRawText(), new DecisionVerifyOptions
        {
            OperatorPublicKeys = input.GetProperty("operator_public_keys").EnumerateArray().Select(e => e.GetString()!).ToList(),
            Now = OfflineVerifier.ParseTimestamp(input.GetProperty("now").GetString()!),
            ExpectedAttestationId = attestationId,
            ExpectedSubjectId = subjectId,
        });
    }

    [Theory]
    [InlineData(null, null, true, "authorized")]
    [InlineData(Attestation, Subject, true, "authorized")]
    [InlineData(Attestation, null, true, "authorized")]
    [InlineData(null, Subject, true, "authorized")]
    [InlineData("00000000-0000-4000-8000-000000000099", null, false, "attestation_binding_mismatch")]
    [InlineData(null, "sovereign-c", false, "attestation_binding_mismatch")]
    [InlineData(Attestation, "sovereign-c", false, "attestation_binding_mismatch")]
    public void ChecksTheExpectedAttestationAndSubject(string? attestationId, string? subjectId, bool accepted, string reason)
    {
        var (decision, options) = Vector("bd-003", attestationId, subjectId);
        var r = OfflineVerifier.VerifyBoundaryDecision(decision, options);
        Assert.Equal(accepted, r.Accepted);
        Assert.Equal(reason, r.Reason);
    }

    [Fact]
    public void AnExpectationNeedsAnAttestationBinding()
    {
        var (decision, options) = Vector("bd-001");
        Assert.True(OfflineVerifier.VerifyBoundaryDecision(decision, options).Accepted);
        foreach (var expected in new[] { Vector("bd-001", attestationId: "a").Options, Vector("bd-001", subjectId: Subject).Options })
        {
            var r = OfflineVerifier.VerifyBoundaryDecision(decision, expected);
            Assert.False(r.Accepted);
            Assert.Equal("attestation_binding_missing", r.Reason);
        }
    }
}
