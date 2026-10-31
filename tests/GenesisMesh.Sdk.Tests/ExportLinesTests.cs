using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Reading <c>gm.evidence.event</c> JSON Lines (1.9.0): the Python reference's export, and the lines every
/// implementation refuses (TypeScript <c>parseExportLines</c> and <c>validEvent</c>).
/// </summary>
public class ExportLinesTests
{
    private static readonly Lazy<string> ReferenceExport = new(() => (string)JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "reference", "python-vectors.json")))!["export"]!);

    private static string[] Lines() => ReferenceExport.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void ReadsTheReferenceExport_WithEachPayloadAsReceived()
    {
        var events = OfflineVerifier.ParseExportLines(ReferenceExport.Value);
        Assert.Equal(Lines().Length, events.Count);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Entry.StoreSequence));
        Assert.Contains(events, e => e.Entry.EntryKind == "decision");
        Assert.Contains(events, e => e.Entry.EntryKind == "execution");
        Assert.All(events, e => Assert.Equal(("gm.evidence.event", 1), (e.Schema, e.SchemaVersion)));
        // The payload is kept as the reference stored it: it still has the digest its envelope names.
        Assert.All(events, e => Assert.Equal(e.Entry.PayloadDigest, Sha256(Canonical.FromJson(e.Payload.GetRawText()))));
        Assert.All(events.Zip(Lines()), p =>
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(p.Second)!["payload"], JsonNode.Parse(p.First.Payload.GetRawText()))));
    }

    [Fact]
    public void SkipsBlankLines_AndReadsLinesEndingInCarriageReturns()
    {
        var lines = Lines();
        var text = "\n" + string.Join("\r\n \t\r\n", lines) + "\r\n";
        Assert.Equal(lines.Length, OfflineVerifier.ParseExportLines(text).Count);
        Assert.Equal(lines.Length, OfflineVerifier.ParseExportLines(lines.Prepend("   ")).Count);
        Assert.Empty(OfflineVerifier.ParseExportLines(""));
    }

    /// <summary>The first line of the reference export with <paramref name="path"/> set to <paramref name="json"/>, or removed for null.</summary>
    private static string Changed(string path, string? json)
    {
        var line = JsonNode.Parse(Lines()[0])!.AsObject();
        var parts = path.Split('.');
        var target = parts.Length == 2 ? line[parts[0]]!.AsObject() : line;
        if (json is null) target.Remove(parts[^1]);
        else target[parts[^1]] = JsonNode.Parse(json);
        return line.ToJsonString();
    }

    [Theory]
    [InlineData("schema", "\"gm.evidence.other\"")]
    [InlineData("schema_version", "2")]
    [InlineData("schema_version", "1.0")]
    [InlineData("extra", "1")]
    [InlineData("payload", null)]
    [InlineData("payload", "[]")]
    [InlineData("entry_digest", "null")]
    [InlineData("entry.decision_id", null)]
    [InlineData("entry.prev_entry_digest", null)]
    [InlineData("entry.extra", "null")]
    [InlineData("entry.store_sequence", "0")]
    [InlineData("entry.store_sequence", "1.0")]
    [InlineData("entry.store_sequence", "\"1\"")]
    [InlineData("entry.entry_kind", "null")]
    [InlineData("entry.capability", "7")]
    [InlineData("entry.exec_sequence_no", "1.0")]
    [InlineData("entry.store_sequence", "9007199254740992")]
    [InlineData("entry.store_sequence", "9223372036854775807")]
    [InlineData("entry.exec_sequence_no", "-9007199254740992")]
    [InlineData("entry.resource_sequence", "9007199254740992")]
    [InlineData("entry.observation_sequence", "9007199254740992")]
    [InlineData("entry.resource_sequence", "\"2\"")]
    [InlineData("entry.observation_sequence", "0")]
    [InlineData("entry.record_id", "7")]
    [InlineData("entry.recorded_at", "\"2026-02-30T00:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31 08:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00.1234567Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T24:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00+24:00\"")]
    [InlineData("entry.recorded_at", "\"٢٠٢٦-10-31T08:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00Z\\n\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00.Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00+01\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00+0100\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00-01:60\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00ZZ\"")]
    [InlineData("entry.recorded_at", "\"0000-10-31T08:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-13-01T08:00:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:60:00Z\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:60Z\"")]
    [InlineData("entry.recorded_at", "\"2026\"")]
    public void RefusesALineThatIsNotAnEventOfSchemaVersion1(string path, string? json)
    {
        var text = Lines()[0] + "\n\n" + Changed(path, json);
        var refused = Assert.Throws<JsonException>(() => OfflineVerifier.ParseExportLines(text));
        Assert.IsNotType<StrictJsonException>(refused);
        Assert.Contains("line 3 ", refused.Message);
    }

    [Theory]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T08:00:00.5+23:59\"")]
    [InlineData("entry.recorded_at", "\"2024-02-29T08:00:00-05:00\"")]
    [InlineData("entry.recorded_at", "\"2026-10-31T23:59:59.123456Z\"")]
    [InlineData("entry.record_id", "null")]
    [InlineData("entry.record_id", "\"rec-1\"")]
    [InlineData("entry.observation_sequence", "3")]
    [InlineData("entry.exec_sequence_no", "-1")]
    [InlineData("entry.store_sequence", "9007199254740991")]
    [InlineData("entry.exec_sequence_no", "-9007199254740991")]
    [InlineData("payload", "{}")]
    public void ReadsEveryFormTheReferenceReads(string path, string json) =>
        Assert.Single(OfflineVerifier.ParseExportLines(Changed(path, json)));

    [Fact]
    public void RefusesJsonThatIsNotStrict()
    {
        var line = Lines()[0];
        var duplicated = "{\"schema\":\"gm.evidence.event\"," + line[1..];
        Assert.Equal("duplicate_key", Assert.Throws<StrictJsonException>(() => OfflineVerifier.ParseExportLines(duplicated)).Reason);
        Assert.ThrowsAny<JsonException>(() => OfflineVerifier.ParseExportLines(line + "x"));
    }

    [Theory]
    [InlineData("\u00a0")]
    [InlineData("\f")]
    [InlineData("\v")]
    [InlineData("\u2028")]
    [InlineData("\ufeff")]
    public void TrimsOnlyJsonWhiteSpace(string space)
    {
        // Every implementation trims a line of JSON white space only; other spaces are no JSON.
        var line = Lines()[0];
        Assert.ThrowsAny<JsonException>(() => OfflineVerifier.ParseExportLines(space + line));
        Assert.ThrowsAny<JsonException>(() => OfflineVerifier.ParseExportLines(line + space));
    }
}
