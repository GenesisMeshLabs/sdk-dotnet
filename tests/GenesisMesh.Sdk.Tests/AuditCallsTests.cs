using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Tests;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The calls for store anchors, audit packs and archives (1.9.0): the request each sends, what is retried, the arguments
/// refused before anything is sent, and the answers read.
/// </summary>
public class AuditCallsTests
{
    private sealed record Sent(string Method, string Path, string? Body, bool Admin);

    private static HttpResponseMessage Answer(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refused(int status, string code) =>
        Answer(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "refused" } }.ToJsonString());

    private static GenesisMeshClient Client(List<Sent> sent, Func<HttpRequestMessage, HttpResponseMessage> answer, int retries = 0) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = "TEST",
            Retry = new RetrySettings { Attempts = retries, BaseDelay = TimeSpan.Zero },
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, Uri.UnescapeDataString(req.RequestUri!.PathAndQuery),
                    req.Content?.ReadAsStringAsync().Result, req.Headers.Contains("X-Admin-Key-Id")));
                return answer(req);
            }),
        });

    private static string Anchor(long sequence, long storeSequence) =>
        $$"""{"anchor_sequence":{{sequence}},"anchored_at":"2026-01-15T00:00:00Z","entry_digest":"{{new string('a', 64)}}","issued_by":"na-2","previous_anchor_digest":null,"signature":{"key_id":"na-2","sig":"AAAA"},"sovereign_id":"sovereign-c","store_sequence":{{storeSequence}}}""";

    private static string Page(long? next, params string[] anchors) =>
        $$"""{"count":{{anchors.Length}},"next_after_anchor":{{(next is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},"anchors":[{{string.Join(",", anchors)}}]}""";

    private const string Archive = """{"archive_id":"ar-1","archived_at":"2026-01-16T00:00:00Z","end_anchor_sequence":3,"from_sequence":1,"issued_by":"na-2","issuer_sovereign_id":"sovereign-c","location":"s3://packs/p-1","manifest_sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","pack_id":"p-1","recorded_by":"ops","signature":{"key_id":"na-2","sig":"AAAA"},"through_entry_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","through_sequence":11}""";

    [Fact]
    public async Task Anchors_ReadsAPage()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Answer(200, Page(2, Anchor(1, 5), Anchor(2, 10))));
        var page = await gm.EvidenceStore.Anchors(new AnchorPageParams { AfterAnchor = 0, Limit = 2 });
        Assert.Equal(("GET", "/admin/evidence/anchors?after_anchor=0&limit=2", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal((2L, 2L, 10L, "2026-01-15T00:00:00Z"), (page.Count, page.NextAfterAnchor, page.Anchors[1].StoreSequence, page.Anchors[1].AnchoredAt));
        Assert.Equal("AAAA", page.Anchors[0].Signature.GetProperty("sig").GetString());
        await gm.EvidenceStore.Anchors();
        Assert.Equal("/admin/evidence/anchors", sent[1].Path);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1001, null)]
    [InlineData(null, -1L)]
    public async Task Anchors_RefusesAPageOutOfRange(int? limit, long? after)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Answer(200, Page(null)));
        Assert.Equal("page", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gm.EvidenceStore.Anchors(new AnchorPageParams { Limit = limit, AfterAnchor = after }))).ParamName);
        Assert.Equal("afterAnchor", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gm.EvidenceStore.AllAnchors(-1))).ParamName);
        Assert.Empty(sent);
        await gm.EvidenceStore.Anchors(new AnchorPageParams { Limit = 1000 });
        Assert.Equal("/admin/evidence/anchors?limit=1000", sent[0].Path);
    }

    [Fact]
    public async Task AllAnchors_FollowsThePages()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => Answer(200, req.RequestUri!.Query switch
        {
            var q when q.Contains("after_anchor=0&", StringComparison.Ordinal) => Page(1, Anchor(1, 5)),
            var q when q.Contains("after_anchor=1&", StringComparison.Ordinal) => Page(null, Anchor(2, 10)),
            _ => Page(null),
        }));
        var anchors = await gm.EvidenceStore.AllAnchors();
        Assert.Equal([1L, 2L], anchors.Select(a => a.AnchorSequence));
        Assert.Equal(["/admin/evidence/anchors?after_anchor=0&limit=1000", "/admin/evidence/anchors?after_anchor=1&limit=1000"], sent.Select(s => s.Path));
        Assert.Empty(await gm.EvidenceStore.AllAnchors(7));
        Assert.Equal("/admin/evidence/anchors?after_anchor=7&limit=1000", sent[^1].Path);
    }

    [Theory]
    [InlineData(4L)]
    [InlineData(3L)]
    public async Task AllAnchors_RefusesACursorThatDoesNotAdvance(long second)
    {
        var pages = 0;
        using var gm = Client([], _ => (++pages) switch
        {
            1 => Answer(200, Page(4, Anchor(4, 20))),
            2 => Answer(200, Page(second, Anchor(5, 25))),
            _ => Answer(200, Page(null)),
        });
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => gm.EvidenceStore.AllAnchors(3));
        Assert.Contains("did not advance", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task Anchor_IsRetried_AndReadsAnUnchangedOrEmptyStore()
    {
        var sent = new List<Sent>();
        var calls = 0;
        using var gm = Client(sent, _ => ++calls == 1 ? Refused(503, "unavailable") : Answer(201, $$"""{"anchor":{{Anchor(3, 11)}},"status":"anchored"}"""), retries: 1);
        var anchored = await gm.EvidenceStore.Anchor();
        Assert.Equal(("POST", "/admin/evidence/anchors", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(2, sent.Count);
        Assert.Equal(("anchored", 3L), (anchored.Status, anchored.Anchor!.AnchorSequence));
        Assert.DoesNotContain(JsonNode.Parse(sent[0].Body!)!.AsObject(), p => p.Key is not ("admin_key_id" or "nonce" or "issued_at" or "audience" or "signature"));
        using var empty = Client([], _ => Answer(200, """{"anchor":null,"status":"unchanged"}"""));
        Assert.Equal(("unchanged", (StoreAnchor?)null), ((await empty.EvidenceStore.Anchor()).Status, (await empty.EvidenceStore.Anchor()).Anchor));
    }

    [Fact]
    public async Task AuditPack_SendsTheRange_AndIsNeverReplayed()
    {
        var sent = new List<Sent>();
        var manifest = """{"created_at":"2026-01-16T00:00:00Z","end_anchor_sequence":3,"files":[],"format":"gm.audit.pack","format_version":1,"from_sequence":1,"issued_by":"na-2","pack_id":"p-1","signature":{"key_id":"na-2","sig":"AAAA"},"sovereign_id":"sovereign-c","through_sequence":11}""";
        using var gm = Client(sent, _ => Answer(200, $$$"""{"manifest":{{{manifest}}},"files":{"report.md":"# Audit pack p-1\n","states.json":"{}\n"}}"""));
        var pack = await gm.EvidenceStore.AuditPack(new AuditPackParams
        {
            FromSequence = 2,
            UntilSequence = 9,
            Since = new DateTimeOffset(2026, 1, 2, 3, 0, 0, TimeSpan.FromHours(2)),
            Until = new DateTimeOffset(2026, 1, 3, 0, 0, 0, 500, TimeSpan.Zero),
        });
        Assert.Equal(("POST", "/admin/evidence/audit-pack", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        var body = JsonNode.Parse(sent[0].Body!)!;
        Assert.Equal((2L, 9L, "2026-01-02T01:00:00Z", "2026-01-03T00:00:00.500000Z"),
            (body["from_sequence"]!.GetValue<long>(), body["until_sequence"]!.GetValue<long>(), body["since"]!.GetValue<string>(), body["until"]!.GetValue<string>()));
        Assert.Equal("p-1", pack.Manifest.GetProperty("pack_id").GetString());
        var files = OfflineVerifier.AuditPackFiles(pack);
        Assert.Equal(Encoding.UTF8.GetBytes(manifest + "\n"), files[OfflineVerifier.ManifestFile]);
        Assert.Equal("# Audit pack p-1\n", Encoding.UTF8.GetString(files[OfflineVerifier.ReportFile]));
        await gm.EvidenceStore.AuditPack();
        Assert.Null(JsonNode.Parse(sent[1].Body!)!["from_sequence"]);

        var calls = 0;
        using var busy = Client([], _ => { calls++; return Refused(503, "unavailable"); }, retries: 2);
        await Assert.ThrowsAsync<ServerException>(() => busy.EvidenceStore.AuditPack());
        Assert.Equal(1, calls);
        using var limited = Client([], _ => Refused(429, "rate_limited"));
        await Assert.ThrowsAsync<RateLimitException>(() => limited.EvidenceStore.AuditPack());
        using var unanchored = Client([], _ => Refused(409, "audit_pack_not_anchored"));
        Assert.Equal("audit_pack_not_anchored", (await Assert.ThrowsAsync<ConflictException>(() => unanchored.EvidenceStore.AuditPack())).Code);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(null, 0L)]
    public async Task AuditPack_RefusesAPositionBelowOne(long? from, long? until)
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Answer(200, "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            gm.EvidenceStore.AuditPack(new AuditPackParams { FromSequence = from, UntilSequence = until }));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Archives_ReadsTheRecordsAndHowFarTheyCover()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Answer(200, $$"""{"archives":[{{Archive}}],"archived_through_sequence":11,"require_archive":true}"""));
        var list = await gm.EvidenceStore.Archives();
        Assert.Equal(("GET", "/admin/evidence/archives", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal((11L, true, "p-1", "ops"), (list.ArchivedThroughSequence, list.RequireArchive, list.Archives[0].PackId, list.Archives[0].RecordedBy));
        // An archive record read back keeps its signed form.
        Assert.Equal(Canonical.FromJson(Archive), Canonical.FromJson(JsonSerializer.Serialize(list.Archives[0], Auth.SerializerOptions)));
    }

    [Fact]
    public async Task RecordArchive_SendsTheManifestAsReceived_AndIsRetried()
    {
        var sent = new List<Sent>();
        var calls = 0;
        using var gm = Client(sent, _ => ++calls == 1
            ? Refused(503, "unavailable")
            : Answer(201, $$"""{"status":"recorded","archive":{{Archive}},"archived_through_sequence":11}"""), retries: 1);
        using var manifest = JsonDocument.Parse("""{"pack_id":"p-1","requested_by":null,"files":[],"signature":{"key_id":"na-2","sig":"AAAA"}}""");
        var result = await gm.EvidenceStore.RecordArchive(manifest.RootElement, "s3://packs/p-1");
        Assert.Equal(("POST", "/admin/evidence/archives", true), (sent[0].Method, sent[0].Path, sent[0].Admin));
        Assert.Equal(2, sent.Count);
        var body = JsonNode.Parse(sent[0].Body!)!;
        Assert.Equal(Canonical.FromJson(manifest.RootElement.GetRawText()), Canonical.FromJson(body["manifest"]!.ToJsonString()));
        Assert.Equal("s3://packs/p-1", body["location"]!.GetValue<string>());
        Assert.Equal(("recorded", 11L, "ar-1"), (result.Status, result.ArchivedThroughSequence, result.Archive.ArchiveId));
    }

    [Fact]
    public async Task RecordArchive_RefusesAManifestThatIsNotAnObject_AndALocationOutOfRange()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Answer(200, "{}"));
        using var manifest = JsonDocument.Parse("""{"pack_id":"p-1"}""");
        using var array = JsonDocument.Parse("[]");
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RecordArchive(array.RootElement, "s3://x"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RecordArchive(default, "s3://x"));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RecordArchive(manifest.RootElement, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RecordArchive(manifest.RootElement, new string('x', 1025)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.EvidenceStore.RecordArchive(manifest.RootElement, null!));
        // The NA counts code points: 1024 of them in 2048 UTF-16 units are sent, 1025 are not.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 1025));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.EvidenceStore.RecordArchive(manifest.RootElement, emoji));
        Assert.Empty(sent);
        await gm.EvidenceStore.RecordArchive(manifest.RootElement, new string('x', 1024));
        await gm.EvidenceStore.RecordArchive(manifest.RootElement, emoji[2..]);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task TheCallsReadTheNAsRefusals()
    {
        using var off = Client([], _ => Refused(404, "evidence_store_disabled"));
        Assert.Equal("evidence_store_disabled", (await Assert.ThrowsAsync<NotFoundException>(() => off.EvidenceStore.Archives())).Code);
        using var unknown = Client([], _ => Refused(409, "archive_pack_unknown"));
        using var manifest = JsonDocument.Parse("""{"pack_id":"p-1"}""");
        Assert.Equal("archive_pack_unknown",
            (await Assert.ThrowsAsync<ConflictException>(() => unknown.EvidenceStore.RecordArchive(manifest.RootElement, "s3://x"))).Code);
    }
}
