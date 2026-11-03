using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Remediation and review entries in an export (1.9.0), beyond the shared suite's 28 <c>verify_export</c> vectors:
/// variants of the suite's store the Python reference verified (decisions as subjects, the forward rotation, reviews
/// and the holder registry, envelopes, fields outside a signature, histories, retention, an NA key history), with its
/// answers; those named <c>review-k*</c> come from an independent review's harness. The TypeScript SDK agrees on every
/// case.
/// </summary>
public class RemediationExportTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "remediation", "export_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Fact]
    public void HoldsTheCases()
    {
        Assert.True(CaseIds().Count() > 170);
        Assert.Contains(Data.Value.GetProperty("cases").EnumerateArray(), c => c.TryGetProperty("key_history", out _));
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void VerifiesAsTheReferenceDid(string id)
    {
        var data = Data.Value;
        var c = data.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var pool = data.GetProperty("lines");
        var lines = string.Join("\n", c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString()));
        NAKeyHistory? history = null;
        if (c.TryGetProperty("key_history", out var kh))
        {
            var read = OfflineVerifier.VerifyKeyHistory(kh.GetProperty("genesis").GetRawText(), kh.GetProperty("successions").GetRawText(),
                kh.GetProperty("root_public_key").GetString()!);
            Assert.True(read.Verified, read.Reason);
            history = read.History;
        }
        var keySet = c.TryGetProperty("executor_keys", out var set) ? set.GetInt32() : 0;
        var r = OfflineVerifier.VerifyEvidenceEvents(OfflineVerifier.ParseExportLines(lines), new EvidenceVerifyOptions
        {
            NAPublicKeys = history is null ? data.GetProperty("na_public_keys").EnumerateArray().Select(k => k.GetString()!).ToList() : [],
            KeyHistory = history,
            ExecutorKeys = data.GetProperty("executor_key_sets")[keySet].Deserialize<List<ExecutorKeyRecord>>()!,
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
            ["observations"] = r.Observations,
            ["break_glass"] = r.BreakGlass,
            ["judgements"] = r.Judgements,
            ["quarantined"] = r.Quarantined,
            ["remediations"] = r.Remediations,
            ["reviews"] = r.Reviews,
            ["decisions"] = r.Decisions,
            ["executions"] = r.Executions,
        };
        Assert.Equal(expected.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).OrderBy(p => p.Name, StringComparer.Ordinal),
            counts.Where(p => p.Value != 0).Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal));
    }
}
