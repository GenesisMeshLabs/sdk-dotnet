using System.Globalization;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Grants, their uses and revocations in an export (1.9.0), beyond the shared suite's 26 <c>verify_export</c> vectors:
/// variants of the suite's store the Python reference verified (entries dropped, swapped, or held from mid-store; uses
/// signed again with their rules' boundaries, other tokens, keys and envelopes; grants and revocations changed; under a
/// key history or after a retention checkpoint), with its answers. The TypeScript SDK agrees on every case but one, a use
/// without a resource under an empty resource prefix, which it lets through. Integers written <c>1.0</c>, which the
/// reference refuses as non-canonical, are read as both SDKs read them, never thrown on.
/// </summary>
public class GrantExportTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "grants", "export_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() => Ids("cases");

    public static IEnumerable<object[]> SpellingCaseIds() => Ids("spelling_cases");

    private static IEnumerable<object[]> Ids(string list) =>
        Data.Value.GetProperty(list).EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Fact]
    public void HoldsTheCases() => Assert.Equal((168, 6), (CaseIds().Count(), SpellingCaseIds().Count()));

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void VerifiesAsTheReferenceDid(string id) => VerifiesAsExpected("cases", id);

    [Theory]
    [MemberData(nameof(SpellingCaseIds))]
    public void ReadsAnIntegerWrittenWithAFractionAsTheSdksDo(string id) => VerifiesAsExpected("spelling_cases", id);

    private static void VerifiesAsExpected(string list, string id)
    {
        var data = Data.Value;
        var c = data.GetProperty(list).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var pool = data.GetProperty("lines");
        var lines = string.Join("\n", c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString()));
        var history = c.TryGetProperty("history", out var h) ? History(h) : null;
        var r = OfflineVerifier.VerifyEvidenceEvents(OfflineVerifier.ParseExportLines(lines), new EvidenceVerifyOptions
        {
            NAPublicKeys = history is null ? data.GetProperty("na_public_keys").EnumerateArray().Select(k => k.GetString()!).ToList() : [],
            KeyHistory = history,
            ExecutorKeys = data.GetProperty("executor_key_sets")[c.GetProperty("executor_keys").GetInt32()].Deserialize<List<ExecutorKeyRecord>>()!,
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
            ["executions"] = r.Executions,
            ["observations"] = r.Observations,
            ["judgements"] = r.Judgements,
            ["quarantined"] = r.Quarantined,
            ["grants"] = r.Grants,
            ["grant_executions"] = r.GrantExecutions,
            ["grant_revocations"] = r.GrantRevocations,
        };
        Assert.Equal(expected.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).OrderBy(p => p.Name, StringComparer.Ordinal),
            counts.Where(p => p.Value != 0).Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    private static NAKeyHistory History(JsonElement h)
    {
        static DateTimeOffset? At(JsonElement key, string name) => key.GetProperty(name).GetString() is { } at
            ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture)
            : null;
        return new NAKeyHistory(h.GetProperty("sovereign_id").GetString()!, null,
            h.GetProperty("keys").EnumerateArray().Select(k => new NAKey(k.GetProperty("key_sequence").GetInt32(),
                k.GetProperty("key_id").GetString(), k.GetProperty("public_key").GetString()!, At(k, "valid_from"),
                At(k, "succeeded_at"), At(k, "valid_to"), k.GetProperty("valid_to_enforced").GetBoolean())).ToList());
    }
}
