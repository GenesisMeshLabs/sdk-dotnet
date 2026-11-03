using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Audit packs offline (1.9.0): the frozen 1.5.0 pack and a pack of the whole store, as the Python reference writes
/// them, changed file by file and field by field (with and without the NA key) and verified with every option, one case
/// for each outcome; and reports rendered from records of every kind with odd values. The reference judged each case.
/// Then the pack's files, entry digests, archive records and the arguments.
/// </summary>
public class AuditPackTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "audit", "pack_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    private static JsonElement Case(string id) =>
        Data.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);

    private static byte[] Body(JsonElement stored) => stored.TryGetProperty("text", out var text)
        ? Encoding.UTF8.GetBytes(text.GetString()!)
        : Convert.FromBase64String(stored.GetProperty("base64").GetString()!);

    private static Dictionary<string, byte[]> Base(string name) =>
        Data.Value.GetProperty("bases").GetProperty(name).EnumerateObject().ToDictionary(f => f.Name, f => Body(f.Value));

    private static Dictionary<string, byte[]> Frozen() => Base("frozen");

    private static string RootKey() => Case("frozen").GetProperty("options").GetProperty("root_public_key").GetString()!;

    private static IReadOnlyList<StoreAnchor> HeldAnchors() =>
        OfflineVerifier.ParseAnchors(Case("frozen").GetProperty("options").GetProperty("held_anchors").GetRawText());

    private static NAKeyHistory History()
    {
        var files = Frozen();
        using var history = JsonDocument.Parse(files[OfflineVerifier.KeyHistoryFile]);
        return OfflineVerifier.VerifyKeyHistory(Encoding.UTF8.GetString(files[OfflineVerifier.GenesisFile]),
            history.RootElement.GetProperty("successions").GetRawText(), RootKey()).History!;
    }

    private static bool Given(JsonElement input, string name, JsonValueKind kind, out JsonElement value) =>
        input.TryGetProperty(name, out value) && value.ValueKind == kind;

    private static string Instant(JsonNode? value) => Canonical.Timestamp(OfflineVerifier.ParseTimestamp(value!.GetValue<string>()));

    /// <summary>The outcome as the reference reports it: findings as sorted (file, entry, reason), range times as instants.</summary>
    private static string Outcome(JsonNode outcome)
    {
        static JsonArray Sorted(JsonNode? findings) => new(findings!.AsArray()
            .Select(f => new JsonArray(f![0]?.DeepClone(), f[1]?.DeepClone(), f[2]!.DeepClone()))
            .OrderBy(f => f.ToJsonString(), StringComparer.Ordinal).ToArray<JsonNode?>());
        outcome["failures"] = Sorted(outcome["failures"]);
        outcome["warnings"] = Sorted(outcome["warnings"]);
        if (outcome["range"] is JsonObject range)
            foreach (var name in new[] { "first_recorded_at", "last_recorded_at" }) range[name] = Instant(range[name]);
        return Canonical.FromJson(outcome.ToJsonString());
    }

    private static PackVerification Verify(Dictionary<string, byte[]> files, JsonElement options)
    {
        var index = 0;
        return OfflineVerifier.VerifyAuditPack(files, new VerifyAuditPackOptions
        {
            RootPublicKey = options.GetProperty("root_public_key").GetString()!,
            HeldAnchors = Given(options, "held_anchors", JsonValueKind.Array, out var held) ? OfflineVerifier.ParseAnchors(held.GetRawText()) : null,
            ExecutorKeys = Given(options, "executor_keys", JsonValueKind.Array, out var keys) ? keys.Deserialize<List<ExecutorKeyRecord>>()! : [],
            Changes = Given(options, "changes", JsonValueKind.Array, out var rows)
                ? rows.EnumerateArray().Select(row => new Change
                {
                    Index = ++index,
                    ResourceId = row.GetProperty("resource_id").GetString()!,
                    Action = row.GetProperty("action").GetString()!,
                    ChangedAt = row.GetProperty("changed_at").GetString()!,
                    Source = row.TryGetProperty("source", out var s) ? s.GetString() : null,
                    SourceEventId = row.TryGetProperty("source_event_id", out var e) ? e.GetString() : null,
                    Actor = row.TryGetProperty("actor", out var a) ? a.GetString() : null,
                }).ToList()
                : null,
            Tolerance = Given(options, "tolerance_seconds", JsonValueKind.Number, out var t) ? TimeSpan.FromSeconds(t.GetInt32()) : TimeSpan.FromSeconds(300),
            Period = Given(options, "period", JsonValueKind.Array, out var p)
                ? (DateTimeOffset.Parse(p[0].GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(p[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                : null,
            PriorEntries = Given(options, "prior_entries", JsonValueKind.Object, out var prior)
                ? prior.EnumerateObject().ToDictionary(e => long.Parse(e.Name, System.Globalization.CultureInfo.InvariantCulture), e => e.Value.GetString()!)
                : null,
        });
    }

    [Fact]
    public void HoldsTheCases() => Assert.Equal(315, CaseIds().Count());

    private static readonly Lazy<JsonElement> GrantPacks = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "audit", "grant_packs.json"))).RootElement);

    public static IEnumerable<object[]> GrantPackIds() =>
        GrantPacks.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    /// <summary>
    /// Packs holding grants (v1.6.0), written by the core's NA (as the TypeScript SDK's <c>grant-packs.json</c>): a grant
    /// under an agreement, whose pack holds <c>trust/agreements.jsonl</c>, four refused variants, and a range after a
    /// quarantined use, which cites the grant's whole chain. Each with the reference's verdict.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrantPackIds))]
    public void APackHoldingGrantsAsTheReferenceJudgedIt(string id)
    {
        var c = GrantPacks.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var files = c.GetProperty("files").EnumerateObject().ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Value.GetString()!));
        var r = OfflineVerifier.VerifyAuditPack(files, new VerifyAuditPackOptions
        {
            RootPublicKey = c.GetProperty("root_public_key").GetString()!,
            HeldAnchors = OfflineVerifier.ParseAnchors(c.GetProperty("held_anchors").GetRawText()),
        });
        Assert.Equal(c.GetProperty("verified").GetBoolean(), r.Verified);
        Assert.Equal(c.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()), OfflineVerifier.PackReasons(r));
        if (id == "agreement-valid")
        {
            Assert.Contains(OfflineVerifier.AgreementsFile, files.Keys);
            Assert.Equal((1, 1), (r.Counts["grants"], r.Counts["grant_executions"]));
        }
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void AnswersAsTheReferenceDid(string id)
    {
        var c = Case(id);
        if (c.GetProperty("kind").GetString() == "render")
        {
            var i = c.GetProperty("input");
            var h = i.GetProperty("header");
            string? Text(string name) => h.GetProperty(name).ValueKind == JsonValueKind.String ? h.GetProperty(name).GetString() : null;
            long? Number(string name) => h.GetProperty(name).ValueKind == JsonValueKind.Number ? h.GetProperty(name).GetInt64() : null;
            var header = new ReportHeader
            {
                PackId = Text("pack_id")!,
                SovereignId = Text("sovereign_id")!,
                FromSequence = Number("from_sequence")!.Value,
                ThroughSequence = Number("through_sequence")!.Value,
                StartAnchorSequence = Number("start_anchor_sequence"),
                EndAnchorSequence = Number("end_anchor_sequence")!.Value,
                CreatedAt = Text("created_at")!,
                RequestedBy = Text("requested_by"),
                PeriodFrom = Text("period_from"),
                PeriodUntil = Text("period_until"),
            };
            Assert.Equal(c.GetProperty("expected").GetString(), OfflineVerifier.RenderReport(header,
                OfflineVerifier.ParseExportLines(i.GetProperty("lines").GetString()!), i.GetProperty("checks"),
                Given(i, "states", JsonValueKind.Object, out var states) ? states : null,
                Given(i, "notices", JsonValueKind.Object, out var notices) ? notices : null));
            return;
        }
        var files = Base(c.GetProperty("base").GetString()!);
        foreach (var change in c.GetProperty("changed").EnumerateObject())
        {
            if (change.Value.ValueKind == JsonValueKind.Null) files.Remove(change.Name);
            else files[change.Name] = Body(change.Value);
        }
        var r = Verify(files, c.GetProperty("options"));
        var got = JsonSerializer.SerializeToNode(new Dictionary<string, object?>
        {
            ["verified"] = r.Verified,
            ["failures"] = r.Failures.Select(f => new object?[] { f.File, f.StoreSequence, f.Reason }).ToList(),
            ["warnings"] = r.Warnings.Select(f => new object?[] { f.File, f.StoreSequence, f.Reason }).ToList(),
            ["counts"] = r.Counts,
            ["range"] = r.Range,
            ["period"] = r.Period,
            ["anchors"] = r.Anchors,
            ["keys"] = r.Keys.Select(k => new object?[] { k.KeySequence, k.KeyId }).ToList(),
            ["report_differences"] = r.ReportDifferences,
            ["completeness"] = r.Completeness,
        })!;
        Assert.Equal(Outcome(JsonNode.Parse(c.GetProperty("expected").GetRawText())!), Outcome(got));
        Assert.Equal(r.Failures.Select(f => f.Reason).Distinct().Order(StringComparer.Ordinal), OfflineVerifier.PackReasons(r));
    }

    [Fact]
    public void APackSavedToADirectoryReadsBackAndVerifies()
    {
        var files = Frozen();
        var directory = Directory.CreateTempSubdirectory("gm-pack-").FullName;
        try
        {
            foreach (var (path, body) in files)
            {
                var target = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, body);
            }
            var read = OfflineVerifier.ReadAuditPackDirectory(directory);
            Assert.Equal(files.Keys.Order(StringComparer.Ordinal), read.Keys.Order(StringComparer.Ordinal));
            Assert.All(files, f => Assert.Equal(f.Value, read[f.Key]));
            var r = OfflineVerifier.VerifyAuditPack(read, new VerifyAuditPackOptions { RootPublicKey = RootKey(), HeldAnchors = HeldAnchors() });
            Assert.True(r.Verified, string.Join(", ", OfflineVerifier.PackReasons(r)));
            Assert.Equal([1, 2], r.Keys.Select(k => k.KeySequence));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheFilesOfTheNAsAnswerAreThePack()
    {
        var files = Frozen();
        using var manifest = JsonDocument.Parse(files[OfflineVerifier.ManifestFile]);
        var response = new AuditPackResponse
        {
            Manifest = manifest.RootElement.Clone(),
            Files = files.Where(f => f.Key != OfflineVerifier.ManifestFile).ToDictionary(f => f.Key, f => Encoding.UTF8.GetString(f.Value)),
        };
        var written = OfflineVerifier.AuditPackFiles(response);
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), written.Keys.Order(StringComparer.Ordinal));
        Assert.All(files, f => Assert.Equal(f.Value, written[f.Key]));
        // The NA's JSON may name absent fields null and its signature more than its two fields: the manifest is written
        // as the NA writes it.
        var node = JsonNode.Parse(files[OfflineVerifier.ManifestFile])!.AsObject();
        node["period_from"] = null;
        node["signature"]!["note"] = "x";
        var loose = OfflineVerifier.AuditPackFiles(new AuditPackResponse { Manifest = JsonSerializer.SerializeToElement(node), Files = response.Files });
        Assert.Equal(files[OfflineVerifier.ManifestFile], loose[OfflineVerifier.ManifestFile]);
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.AuditPackFiles(null!));
        using var reply = JsonDocument.Parse("{\"manifest\": " + Encoding.UTF8.GetString(files[OfflineVerifier.ManifestFile]) + ", \"files\": {\"report.md\": \"x\"}}");
        Assert.Equal("x", reply.RootElement.Deserialize<AuditPackResponse>()!.Files["report.md"]);
    }

    [Fact]
    public void APacksFilesAreCanonicalJsonWithTextKept()
    {
        using var doc = JsonDocument.Parse("""{"b": "Zürich ✓", "a": [1, 2.0]}""");
        Assert.Equal("{\"a\":[1,2.0],\"b\":\"Zürich ✓\"}\n", Encoding.UTF8.GetString(OfflineVerifier.PackJsonBytes(doc.RootElement)));
        Assert.Equal(13, OfflineVerifier.PackFiles.Count);
        Assert.Equal([OfflineVerifier.AgreementsFile], OfflineVerifier.OptionalPackFiles);
    }

    [Fact]
    public void APacksEntriesAreItsPriorEntries()
    {
        var files = Frozen();
        var digests = OfflineVerifier.PackEntryDigests(files);
        Assert.Equal([6L, 7, 8, 9, 10, 11], digests.Keys.Order());
        var entries = OfflineVerifier.ParseExportLines(Encoding.UTF8.GetString(files[OfflineVerifier.EntriesFile]));
        Assert.Equal(entries[^1].EntryDigest, digests[11]);
        files.Remove(OfflineVerifier.EntriesFile);
        Assert.Throws<ArgumentException>(() => OfflineVerifier.PackEntryDigests(files));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.PackEntryDigests(null!));
    }

    [Fact]
    public void ARecordSignedByTheKeyValidAtItsTime()
    {
        var history = History();
        var files = Frozen();
        using var policy = JsonDocument.Parse(Encoding.UTF8.GetString(files[OfflineVerifier.PoliciesFile]).Split('\n')[0]);
        var issuedAt = policy.RootElement.GetProperty("issued_at").GetString()!;
        Assert.Null(OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", policy.RootElement, history, issuedAt));
        // Placed after the succession (the policy was signed by the outgoing key): another key of the history signed it.
        Assert.Equal("na_key_not_valid_at_time", OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", policy.RootElement, history, "2026-03-01T00:00:00Z"));
        Assert.Equal("na_key_not_valid_at_time", OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", policy.RootElement, history, "soon"));
        using var array = JsonDocument.Parse("[]");
        Assert.Equal("invalid_signature", OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", array.RootElement, history, issuedAt));
        var changed = JsonNode.Parse(policy.RootElement.GetRawText())!;
        changed["description"] = "changed";
        Assert.Equal("invalid_signature", OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", JsonSerializer.SerializeToElement(changed), history, issuedAt));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", policy.RootElement, null!, issuedAt));
    }

    [Fact]
    public void AnArchiveRecordIsSignedByTheNA()
    {
        var history = History();
        var archive = OfflineVerifier.ParseExportLines(Encoding.UTF8.GetString(Frozen()[OfflineVerifier.EntriesFile]))
            .Single(e => e.Entry.EntryKind == "archive").Payload.GetRawText();
        Assert.True(OfflineVerifier.VerifyArchiveRecord(archive, history));
        Assert.True(OfflineVerifier.VerifyArchiveRecord(archive, history.AllKeys()));
        Assert.False(OfflineVerifier.VerifyArchiveRecord(archive, [history.AllKeys()[0]]));
        var node = JsonNode.Parse(archive)!;
        node["location"] = "elsewhere";
        Assert.False(OfflineVerifier.VerifyArchiveRecord(node.ToJsonString(), history));
        node = JsonNode.Parse(archive)!;
        node["note"] = "x";
        Assert.False(OfflineVerifier.VerifyArchiveRecord(node.ToJsonString(), history));
        node = JsonNode.Parse(archive)!;
        node["archived_at"] = "soon";
        Assert.False(OfflineVerifier.VerifyArchiveRecord(node.ToJsonString(), history));
        node = JsonNode.Parse(archive)!;
        node["archived_at"] = "2026-01-01T00:00:00Z";
        Assert.False(OfflineVerifier.VerifyArchiveRecord(node.ToJsonString(), history));
        Assert.Throws<StrictJsonException>(() => OfflineVerifier.VerifyArchiveRecord("{", history));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyArchiveRecord(archive, (NAKeyHistory)null!));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyArchiveRecord(archive, (IReadOnlyList<string>)null!));
        var record = JsonSerializer.Deserialize<ArchiveRecord>(archive)!;
        Assert.Equal(record.ArchiveId, JsonNode.Parse(archive)!["archive_id"]!.GetValue<string>());
    }

    [Fact]
    public void TheReportsHeaderIsTheManifestsInUtc()
    {
        var manifest = JsonSerializer.Deserialize<AuditPackManifest>(Frozen()[OfflineVerifier.ManifestFile])!;
        manifest.PeriodFrom = "2026-01-02T01:00:00+01:00";
        manifest.PeriodUntil = "2026-01-03T00:00:00.5Z";
        var header = OfflineVerifier.ReportHeaderOf(manifest);
        Assert.Equal(("2026-01-02T00:00:00Z", "2026-01-03T00:00:00.500000Z"), (header.PeriodFrom, header.PeriodUntil));
        Assert.Equal(manifest.PackId, header.PackId);
        Assert.Equal(OfflineVerifier.PackFormat, manifest.Format);
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ReportHeaderOf(null!));
        Assert.Equal("", OfflineVerifier.ReportCell(null));
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        var options = new VerifyAuditPackOptions { RootPublicKey = RootKey() };
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyAuditPack(null!, options));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyAuditPack(Frozen(), null!));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.VerifyAuditPack(Frozen(), new VerifyAuditPackOptions { RootPublicKey = null! }));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ReadAuditPackDirectory(null!));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.PackReasons(null!));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.RegistryKeys(null!, History()));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.RegistryKeys([], null!));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.RegistryKeys([null!], History()));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.AuditPackFiles(new AuditPackResponse()));
        using var record = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ReceivedSignatureReason("BoundaryPolicy", record.RootElement, History(), null!));
        using var checks = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.RenderReport(null!, [], checks.RootElement));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.RenderReport(OfflineVerifier.ReportHeaderOf(new AuditPackManifest()), null!, checks.RootElement));
    }
}
