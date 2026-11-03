using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Remediation and review entries in an export (1.9.0), beyond the shared suite's 28 <c>verify_export</c> vectors:
/// variants of the suite's store the Python reference verified (decisions as subjects, the forward rotation, reviews
/// and the holder registry, envelopes, fields outside a signature, histories, retention), with its answers. The
/// TypeScript SDK agrees on every case.
/// </summary>
public class RemediationExportTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "remediation", "export_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Fact]
    public void HoldsTheCases() => Assert.True(CaseIds().Count() > 100);

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void VerifiesAsTheReferenceDid(string id)
    {
        var data = Data.Value;
        var c = data.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var pool = data.GetProperty("lines");
        var lines = string.Join("\n", c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString()));
        var r = OfflineVerifier.VerifyEvidenceEvents(OfflineVerifier.ParseExportLines(lines), new EvidenceVerifyOptions
        {
            NAPublicKeys = data.GetProperty("na_public_keys").EnumerateArray().Select(k => k.GetString()!).ToList(),
            ExecutorKeys = data.GetProperty("executor_keys").Deserialize<List<ExecutorKeyRecord>>()!,
            Contiguous = c.GetProperty("contiguous").GetBoolean(),
            Checkpoint = c.TryGetProperty("checkpoint", out var checkpoint) ? checkpoint.GetRawText() : null,
        });
        var expected = c.GetProperty("expected");
        static long? Position(JsonElement f) => f[0].ValueKind == JsonValueKind.Null ? null : f[0].GetInt64();
        Assert.Equal(expected.GetProperty("failures").EnumerateArray().Select(f => (Position(f), f[1].GetString()!)),
            r.Failures.Select(f => (f.StoreSequence, f.Reason)).Distinct()
                .OrderBy(f => f.StoreSequence ?? 0).ThenBy(f => f.Reason, StringComparer.Ordinal));
        Assert.Equal(expected.GetProperty("verified").GetBoolean(), r.Verified);
        var counts = new Dictionary<string, long>
        {
            ["observations"] = r.Observations, ["break_glass"] = r.BreakGlass, ["judgements"] = r.Judgements,
            ["quarantined"] = r.Quarantined, ["remediations"] = r.Remediations, ["reviews"] = r.Reviews,
            ["decisions"] = r.Decisions, ["executions"] = r.Executions,
        };
        Assert.Equal(expected.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).OrderBy(p => p.Name, StringComparer.Ordinal),
            counts.Where(p => p.Value != 0).Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal));
    }
}
