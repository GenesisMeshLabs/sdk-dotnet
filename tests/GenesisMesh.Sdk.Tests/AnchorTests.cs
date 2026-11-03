using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Store anchors offline (1.9.0): runs of anchors verified as a chain, and runs of entries checked against the anchors,
/// variants of the audit suite's world the Python reference judged, with its answers. The TypeScript SDK agrees on
/// every case. Then the arguments, the signed form and the starting checkpoint.
/// </summary>
public class AnchorTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "audit", "anchor_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    private static NAKeyHistory History()
    {
        var h = Data.Value.GetProperty("key_history");
        return OfflineVerifier.VerifyKeyHistory(h.GetProperty("genesis").GetRawText(), h.GetProperty("successions").GetRawText(),
            h.GetProperty("root_public_key").GetString()!).History!;
    }

    private static IReadOnlyList<EvidenceEvent> Events(JsonElement c)
    {
        var pool = Data.Value.GetProperty("lines");
        var lines = c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString()).ToList();
        return lines.Count == 0 ? [] : OfflineVerifier.ParseExportLines(string.Join("\n", lines));
    }

    private static JsonElement Case(string id) =>
        Data.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);

    private static IEnumerable<(long?, string)> Pairs(JsonElement list) => list.EnumerateArray()
        .Select(f => (f[0].ValueKind == JsonValueKind.Null ? (long?)null : f[0].GetInt64(), f[1].GetString()!));

    [Fact]
    public void HoldsTheCases() => Assert.Equal(86, CaseIds().Count());

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void AnswersAsTheReferenceDid(string id)
    {
        var c = Case(id);
        var expected = c.GetProperty("expected");
        var history = History();
        var anchorsJson = c.GetProperty("anchors").GetRawText();
        if (c.GetProperty("kind").GetString() == "chain")
        {
            if (expected.TryGetProperty("raises", out _))
            {
                Assert.Throws<FormatException>(() => OfflineVerifier.ParseAnchors(anchorsJson));
                return;
            }
            var mode = c.GetProperty("keys").GetString();
            var r = OfflineVerifier.VerifyStoreAnchors(OfflineVerifier.ParseAnchors(anchorsJson), new AnchorVerifyOptions
            {
                KeyHistory = mode == "history" ? history : null,
                NAPublicKeys = mode == "list" ? history.AllKeys() : mode == "old" ? [history.Keys[0].PublicKey] : [],
                SkipSignatures = mode == "skip",
                SovereignId = c.GetProperty("sovereign_id").GetString(),
            });
            Assert.Equal(Pairs(expected.GetProperty("failures")), r.Failures.Select(f => (f.AnchorSequence, f.Reason)));
            var summary = expected.GetProperty("summary");
            static long? Number(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();
            Assert.Equal((summary.GetProperty("verified").GetBoolean(), summary.GetProperty("checked_anchors").GetInt64(),
                    Number(summary.GetProperty("first_anchor_sequence")), Number(summary.GetProperty("last_anchor_sequence")),
                    Number(summary.GetProperty("anchored_through_sequence"))),
                (r.Verified, r.CheckedAnchors, r.FirstAnchorSequence, r.LastAnchorSequence, r.AnchoredThroughSequence));
            return;
        }
        var events = Events(c);
        var result = OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
        {
            KeyHistory = history,
            ExecutorKeys = Data.Value.GetProperty("executor_keys").Deserialize<List<ExecutorKeyRecord>>()!,
        });
        OfflineVerifier.CheckEventsAgainstAnchors(events, OfflineVerifier.ParseAnchors(anchorsJson), result, new AnchorCheckOptions
        {
            Partial = c.GetProperty("partial").GetBoolean(),
            FromAnchor = c.GetProperty("from_anchor").GetBoolean(),
        });
        Assert.Equal(Pairs(expected.GetProperty("failures")), result.Failures.Select(f => (f.StoreSequence, f.Reason)));
        Assert.Equal(result.Failures.Count == 0, result.Verified);
        var held = expected.GetProperty("anchors");
        var a = result.Anchors!;
        Assert.Equal((held.GetProperty("anchors_checked").GetInt64(), held.GetProperty("anchors_matched").GetInt64(),
                held.GetProperty("anchors_before_export").GetInt64(), held.GetProperty("anchors_after_export").GetInt64(),
                held.GetProperty("linked_start").GetBoolean(),
                held.GetProperty("anchored_through_sequence").ValueKind == JsonValueKind.Null ? (long?)null : held.GetProperty("anchored_through_sequence").GetInt64(),
                held.GetProperty("unanchored_entries").GetInt64()),
            (a.AnchorsChecked, a.AnchorsMatched, a.AnchorsBeforeExport, a.AnchorsAfterExport, a.LinkedStart, a.AnchoredThroughSequence,
                a.UnanchoredEntries));
    }

    [Fact]
    public void AnAnchorsDigestIsOfItsSignedForm_ThePreviousDigestLeftOutWhenNone()
    {
        var anchors = OfflineVerifier.ParseAnchors(Case("chain-base-history").GetProperty("anchors").GetRawText());
        // Each anchor links to the digest of the one before it.
        Assert.Equal(anchors[1].PreviousAnchorDigest, OfflineVerifier.AnchorDigest(anchors[0]));
        Assert.Equal(anchors[2].PreviousAnchorDigest, OfflineVerifier.AnchorDigest(anchors[1]));
        Assert.DoesNotContain("previous_anchor_digest", OfflineVerifier.AnchorCanonical(anchors[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("signature", OfflineVerifier.AnchorCanonical(anchors[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        var anchors = OfflineVerifier.ParseAnchors(Case("chain-base-history").GetProperty("anchors").GetRawText());
        // No keys: refused, unless the signatures are skipped on purpose.
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyStoreAnchors(anchors, new AnchorVerifyOptions()));
        Assert.True(OfflineVerifier.VerifyStoreAnchors(anchors, new AnchorVerifyOptions { SkipSignatures = true }).Verified);
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyStoreAnchors(anchors,
            new AnchorVerifyOptions { KeyHistory = History(), NAPublicKeys = ["k"] }));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyStoreAnchors(null!, new AnchorVerifyOptions { SkipSignatures = true }));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyStoreAnchors([null!], new AnchorVerifyOptions { SkipSignatures = true }));
        // A typed anchor the reference would not read is refused as ParseAnchors refuses its JSON.
        var bad = OfflineVerifier.ParseAnchors(Case("chain-base-history").GetProperty("anchors").GetRawText())[0];
        bad.AnchoredAt = "2026-01-01T02:00:00+01:00";
        Assert.Throws<ArgumentException>(() => OfflineVerifier.VerifyStoreAnchors([bad], new AnchorVerifyOptions { SkipSignatures = true }));
        Assert.Throws<FormatException>(() => OfflineVerifier.ParseAnchors("{}"));
        Assert.Throws<StrictJsonException>(() => OfflineVerifier.ParseAnchors("[{\"a\":1,\"a\":2}]"));
        var result = new EvidenceStoreVerification { Verified = true };
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckEventsAgainstAnchors([null!], anchors, result));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckEventsAgainstAnchors([], [null!], result));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.CheckEventsAgainstAnchors([], anchors, null!));
        // A typed anchor that is not well formed is refused there too: it never counts as matched.
        var unplaced = OfflineVerifier.ParseAnchors(Case("chain-base-history").GetProperty("anchors").GetRawText())[0];
        unplaced.StoreSequence = 0;
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckEventsAgainstAnchors([], [unplaced], result));
        unplaced.StoreSequence = 5;
        unplaced.EntryDigest = null!;
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckEventsAgainstAnchors([], [unplaced], result));
    }

    [Fact]
    public void AnchorsAreReadFromAnArrayOrFromAPageOfTheAnchorsRoute()
    {
        var list = Case("chain-base-history").GetProperty("anchors").GetRawText();
        var page = "{\"count\":3,\"next_after_anchor\":null,\"anchors\":" + list + "}";
        Assert.Equal(OfflineVerifier.ParseAnchors(list).Select(OfflineVerifier.AnchorDigest),
            OfflineVerifier.ParseAnchors(page).Select(OfflineVerifier.AnchorDigest));
        Assert.Throws<FormatException>(() => OfflineVerifier.ParseAnchors("{\"anchors\":{}}"));
        Assert.Throws<FormatException>(() => OfflineVerifier.ParseAnchors("{\"count\":0}"));
    }

    [Fact]
    public void ARunAfterARetentionStartsFromItsCheckpoint()
    {
        // The audit suite's checkpoint after a prune, and a run whose first entry is right after the entries it removed.
        var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "audit.json")))
            .RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == "range-after-prune").GetProperty("input");
        var checkpoint = OfflineVerifier.ParseExportLines(vector.GetProperty("cited_lines").GetString()!)
            .Single(e => e.Entry.EntryKind == "retention_checkpoint");
        var first = OfflineVerifier.ParseExportLines(vector.GetProperty("lines").GetString()!)[0];
        first.Entry.StoreSequence = checkpoint.Payload.GetProperty("removed_through_sequence").GetInt64() + 1;
        first.Entry.PrevEntryDigest = checkpoint.Payload.GetProperty("last_removed_entry_digest").GetString();
        Assert.Equal(checkpoint.Payload.GetRawText(), OfflineVerifier.StartingCheckpoint([first, checkpoint], first));
        Assert.Null(OfflineVerifier.StartingCheckpoint([first], first));
        // A checkpoint that removed other entries does not explain where the run starts.
        first.Entry.PrevEntryDigest = new string('0', 64);
        Assert.Null(OfflineVerifier.StartingCheckpoint([first, checkpoint], first));
        first.Entry.PrevEntryDigest = checkpoint.Payload.GetProperty("last_removed_entry_digest").GetString();
        first.Entry.StoreSequence++;
        Assert.Null(OfflineVerifier.StartingCheckpoint([first, checkpoint], first));
    }
}
