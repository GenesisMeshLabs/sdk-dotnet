using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Key successions, archive records, executor keys placed by the store and an audit pack's range with its cited entries
/// (1.9.0), beyond the shared audit suite's 13 <c>verify_export</c> and 2 <c>verify_range</c> vectors: variants of the
/// suite's store the Python reference verified, with its answers. The TypeScript SDK agrees on every case.
/// </summary>
public class AuditExportTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "audit", "export_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    [Fact]
    public void HoldsTheCases() => Assert.Equal(180, CaseIds().Count());

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void VerifiesAsTheReferenceDid(string id)
    {
        var data = Data.Value;
        var c = data.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var pool = data.GetProperty("lines");
        IReadOnlyList<EvidenceEvent> Events(string name) => OfflineVerifier.ParseExportLines(
            string.Join("\n", c.GetProperty(name).EnumerateArray().Select(i => pool[i.GetInt32()].GetString())));
        NAKeyHistory? history = null;
        if (c.TryGetProperty("history", out var h))
        {
            var held = data.GetProperty("key_histories")[h.GetInt32()];
            history = OfflineVerifier.VerifyKeyHistory(held.GetProperty("genesis").GetRawText(),
                held.GetProperty("successions").GetRawText(), held.GetProperty("root_public_key").GetString()!).History!;
        }
        var r = OfflineVerifier.VerifyEvidenceEvents(Events("lines"), new EvidenceVerifyOptions
        {
            NAPublicKeys = c.TryGetProperty("na_public_keys", out var keys) ? keys.EnumerateArray().Select(k => k.GetString()!).ToList() : [],
            KeyHistory = history,
            ExecutorKeys = data.GetProperty("executor_key_sets")[c.GetProperty("executor_keys").GetInt32()].Deserialize<List<ExecutorKeyRecord>>()!,
            Contiguous = c.GetProperty("contiguous").GetBoolean(),
            Prior = c.TryGetProperty("cited", out _) ? Events("cited") : null,
        });
        var expected = c.GetProperty("expected");
        static IEnumerable<(long?, string)> Expected(JsonElement list) => list.EnumerateArray()
            .Select(f => (f[0].ValueKind == JsonValueKind.Null ? (long?)null : f[0].GetInt64(), f[1].GetString()!));
        Assert.Equal(Expected(expected.GetProperty("failures")), r.Failures.Select(f => (f.StoreSequence, f.Reason)));
        Assert.Equal(Expected(expected.GetProperty("warnings")), (r.Warnings ?? []).Select(f => (f.StoreSequence, f.Reason)));
        var counts = new Dictionary<string, long>
        {
            ["decisions"] = r.Decisions,
            ["executions"] = r.Executions,
            ["observations"] = r.Observations,
            ["judgements"] = r.Judgements,
            ["key_successions"] = r.KeySuccessions,
            ["archives"] = r.Archives,
        };
        Assert.Equal(expected.GetProperty("counts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).OrderBy(p => p.Name, StringComparer.Ordinal),
            counts.Where(p => p.Value != 0).Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(r.Failures.Count == 0, r.Verified);
    }

    // ── Key histories built by hand ─────────────────────────────────────────

    /// <summary>The suite's whole store, its executor keys, its key history as verified, and its one succession as received.</summary>
    private static (IReadOnlyList<EvidenceEvent> Events, List<ExecutorKeyRecord> Keys, NAKeyHistory History, string Succession) Store()
    {
        var data = Data.Value;
        var c = data.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "base");
        var pool = data.GetProperty("lines");
        var events = OfflineVerifier.ParseExportLines(string.Join("\n", c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString())));
        var held = data.GetProperty("key_histories")[c.GetProperty("history").GetInt32()];
        var history = OfflineVerifier.VerifyKeyHistory(held.GetProperty("genesis").GetRawText(), held.GetProperty("successions").GetRawText(),
            held.GetProperty("root_public_key").GetString()!).History!;
        return (events, data.GetProperty("executor_key_sets")[c.GetProperty("executor_keys").GetInt32()].Deserialize<List<ExecutorKeyRecord>>()!,
            history, held.GetProperty("successions")[0].GetRawText());
    }

    private static List<(long?, string)> Failures(IReadOnlyList<EvidenceEvent> events, List<ExecutorKeyRecord> keys, NAKeyHistory history) =>
        OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions { KeyHistory = history, ExecutorKeys = keys })
            .Failures.Select(f => (f.StoreSequence, f.Reason)).ToList();

    [Fact]
    public void AVerifiedHistoryHoldsTheDigestsOfItsSuccessions_AndKeepsThemWithAShorterGrace()
    {
        var (events, keys, history, succession) = Store();
        Assert.Equal([OfflineVerifier.SuccessionDigest(succession)], history.SuccessionDigests);
        Assert.Empty(Failures(events, keys, history));
        var shorter = history.WithVouchingGrace(TimeSpan.FromHours(1));
        Assert.Equal(history.SuccessionDigests, shorter.SuccessionDigests);
        Assert.Empty(Failures(events, keys, shorter));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.SuccessionDigest("[]"));
    }

    [Fact]
    public void AHistoryBuiltByHandWithItsRootKey_VerifiesASuccessionEntryOnlyWithItsDigest()
    {
        var (events, keys, history, succession) = Store();
        var bare = new NAKeyHistory(history.SovereignId, history.RootPublicKey, history.Keys);
        Assert.Equal([((long?)6, "succession_not_in_history")], Failures(events, keys, bare));
        var withDigest = new NAKeyHistory(history.SovereignId, history.RootPublicKey, history.Keys)
        {
            SuccessionDigests = [OfflineVerifier.SuccessionDigest(succession)],
        };
        Assert.Empty(Failures(events, keys, withDigest));
    }

    [Fact]
    public void AHistoryBuiltByHandWithoutARootKey_ChecksNoRootSignatureNorItsSuccessions()
    {
        // As TypeScript reads it, an empty root key is none.
        var (events, keys, history, _) = Store();
        Assert.Empty(Failures(events, keys, new NAKeyHistory(history.SovereignId, null, history.Keys)));
        Assert.Empty(Failures(events, keys, new NAKeyHistory(history.SovereignId, "", history.Keys)));
    }
}
