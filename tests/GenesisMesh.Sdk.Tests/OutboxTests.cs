using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>The outbox stores (1.9.0) and the rules the evidence outbox follows.</summary>
public sealed class OutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gm-outbox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir => Path.Combine(_root, "outbox");

    private static OutboxEntry Entry(string id, int attempts = 0) => new()
    {
        Id = id,
        Evidence = new ExecutionEvidence { EvidenceId = id, Outcome = "success" },
        State = OutboxStates.Pending,
        Attempts = attempts,
        QueuedAt = "2026-10-09T00:00:00.000Z",
    };

    public static TheoryData<string> Stores => new() { "file", "memory" };

    private IOutbox<OutboxEntry> Make(string kind) => kind == "file" ? new FileOutbox(Dir) : new MemoryOutbox<OutboxEntry>();

    // ── Both stores ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ListsInTheOrderAdded_UpdatesAndRemovesById(string kind)
    {
        var outbox = Make(kind);
        foreach (var id in new[] { "b", "a", "c" }) await outbox.Add(Entry(id));
        await outbox.Update(Entry("a", attempts: 2));
        await outbox.Remove("b");
        await outbox.Remove("missing");
        await outbox.Update(Entry("missing"));
        Assert.Equal(new[] { ("a", 2), ("c", 0) }, (await outbox.List()).Select(e => (e.Id, e.Attempts)));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RefusesASecondEntryWithTheSameId(string kind)
    {
        var outbox = Make(kind);
        await outbox.Add(Entry("a"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.Add(Entry("a")));
        Assert.Contains("exists", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ReturnsCopies(string kind)
    {
        var outbox = Make(kind);
        var added = Entry("a");
        await outbox.Add(added);
        added.Evidence.Outcome = "changed after adding";
        var listed = Assert.Single(await outbox.List());
        listed.Evidence.Outcome = "changed after listing";
        Assert.Equal("success", Assert.Single(await outbox.List()).Evidence.Outcome);
    }

    // ── FileOutbox ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FileOutbox_KeepsEntriesAcrossInstances()
    {
        await new FileOutbox(Dir).Add(Entry("a"));
        await new FileOutbox(Dir).Add(Entry("b"));
        Assert.Equal(new[] { "a", "b" }, (await new FileOutbox(Dir).List()).Select(e => e.Id));
    }

    [Fact]
    public async Task FileOutbox_KeepsTheOrderAfterEarlierEntriesAreRemoved()
    {
        var outbox = new FileOutbox(Dir);
        await outbox.Add(Entry("a"));
        await outbox.Add(Entry("b"));
        await outbox.Remove("a");
        await outbox.Add(Entry("a"));
        Assert.Equal(new[] { "b", "a" }, (await outbox.List()).Select(e => e.Id));
    }

    [Fact]
    public async Task FileOutbox_NamesFilesBySequenceAndId_HashingIdsThatAreNotPlain()
    {
        var outbox = new FileOutbox(Dir);
        await outbox.Add(Entry("3f2a-uuid"));
        await outbox.Add(Entry("../escape"));
        var files = Files();
        Assert.Equal("000000000001-3f2a-uuid.json", files[0]);
        Assert.Matches("^000000000002-[0-9a-f]{64}\\.json$", files[1]);
        Assert.Equal(new[] { "3f2a-uuid", "../escape" }, (await outbox.List()).Select(e => e.Id));
        await outbox.Remove("../escape");
        Assert.Single(Files());
        Assert.Equal(Dir, outbox.DirectoryPath);
    }

    [Fact]
    public async Task FileOutbox_WritesAVersionedFormat_WithEveryFieldOfTheEntry()
    {
        await new FileOutbox(Dir).Add(Entry("a"));
        var text = File.ReadAllText(Path.Combine(Dir, "000000000001-a.json"));
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        var body = JsonNode.Parse(text)!;
        Assert.Equal("gm.evidence.outbox.v1", (string?)body["format"]);
        var entry = body["entry"]!.AsObject();
        Assert.Equal(("a", "pending"), ((string?)entry["id"], (string?)entry["state"]));
        // Null fields are written, as the TypeScript and Rust SDKs write them.
        Assert.True(entry.ContainsKey("next_attempt_at") && entry["next_attempt_at"] is null);
        Assert.True(entry.ContainsKey("last_error") && entry["last_error"] is null);
    }

    [Fact]
    public async Task FileOutbox_RecoversAnInterruptedAdd_AndDropsAnUnfinishedUpdateOrPartialWrite()
    {
        await new FileOutbox(Dir).Add(Entry("a"));
        static string FileOf(OutboxEntry e) =>
            $$"""{"format":"gm.evidence.outbox.v1","entry":{{JsonSerializer.Serialize(e, Auth.SerializerOptions)}}}""";
        // An add flushed but not renamed, an update not renamed, and a write cut short.
        File.WriteAllText(Path.Combine(Dir, ".000000000002-b.json.0a1b2c.tmp"), FileOf(Entry("b")));
        File.WriteAllText(Path.Combine(Dir, ".000000000001-a.json.3d4e5f.tmp"), FileOf(Entry("a", attempts: 5)));
        File.WriteAllText(Path.Combine(Dir, ".000000000003-c.json.6a7b8c.tmp"), "{\"form");
        var listed = await new FileOutbox(Dir).List();
        Assert.Equal(new[] { ("a", 0), ("b", 0) }, listed.Select(e => (e.Id, e.Attempts)));
        Assert.Equal(new[] { "000000000001-a.json", "000000000002-b.json" }, Files());
    }

    [Fact]
    public async Task FileOutbox_MovesAnUnreadableEntryAside_Once()
    {
        await new FileOutbox(Dir).Add(Entry("a"));
        File.WriteAllText(Path.Combine(Dir, "000000000002-b.json"), "{not json");
        var outbox = new FileOutbox(Dir);
        var error = await Assert.ThrowsAsync<OutboxException>(() => outbox.List());
        Assert.Equal("outbox_file_unreadable", error.Code);
        Assert.Contains("000000000002-b.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("000000000002-b.json.unreadable", Files());
        // The next read goes on without it.
        Assert.Equal("a", Assert.Single(await outbox.List()).Id);
    }

    [Fact]
    public async Task FileOutbox_RefusesAFileOfAnotherFormat_AndLeavesItInPlace()
    {
        await new FileOutbox(Dir).Add(Entry("a"));
        File.WriteAllText(Path.Combine(Dir, "000000000002-b.json"), """{"format":"other"}""");
        var error = await Assert.ThrowsAsync<OutboxException>(() => new FileOutbox(Dir).List());
        Assert.Equal("outbox_file_other_format", error.Code);
        Assert.Contains("gm.evidence.outbox.v1", error.Message, StringComparison.Ordinal);
        Assert.Contains("000000000002-b.json", Files());
    }

    [Fact]
    public async Task FileOutbox_MovesAsideARecordWithAFieldThisSdkDoesNotKnow()
    {
        await new FileOutbox(Dir).List();
        // Read into the typed record, the field would be dropped and the record would no longer match its signature.
        File.WriteAllText(Path.Combine(Dir, "000000000001-e-1.json"), """
            {"format":"gm.evidence.outbox.v1","entry":{"id":"e-1","evidence":{"evidence_id":"e-1","from_the_future":1},
            "state":"pending","attempts":0,"queued_at":"2026-10-09T12:00:00.000Z","next_attempt_at":null,"last_error":null}}
            """);
        var error = await Assert.ThrowsAsync<OutboxException>(() => new FileOutbox(Dir).List());
        Assert.Equal("outbox_file_unreadable", error.Code);
    }

    [Fact]
    public async Task FileOutbox_ReadsEntriesTheRustSdkWrites()
    {
        await new FileOutbox(Dir).List();
        // As FileOutbox in the Rust SDK writes it: serde_json, two-space indent.
        File.WriteAllText(Path.Combine(Dir, "000000000001-e-1.json"), """
            {
              "format": "gm.evidence.outbox.v1",
              "entry": {
                "id": "e-1",
                "evidence": { "evidence_id": "e-1" },
                "state": "dead_letter",
                "attempts": 3,
                "queued_at": "2026-10-09T12:00:00.000Z",
                "next_attempt_at": null,
                "last_error": { "status": 409, "code": "evidence_conflict", "message": "taken" }
              }
            }
            """);
        var entry = Assert.Single(await new FileOutbox(Dir).List());
        Assert.Equal(("e-1", "e-1", OutboxStates.DeadLetter, 3), (entry.Id, entry.Evidence.EvidenceId, entry.State, entry.Attempts));
        Assert.Equal((409, "evidence_conflict", "taken"), (entry.LastError!.Status, entry.LastError.Code, entry.LastError.Message));
        Assert.Null(entry.NextAttemptAt);
    }

    [Fact]
    public async Task FileOutbox_KeepsTheDirectoryAndEntriesPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        await new FileOutbox(Dir).Add(Entry("a"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Dir, "000000000001-a.json")));
    }

    [Fact]
    public async Task FileOutbox_RunsConcurrentChangesOneAtATime()
    {
        var outbox = new FileOutbox(Dir);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => outbox.Add(Entry("e" + i))));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? outbox.Remove("e" + i) : outbox.Update(Entry("e" + i, attempts: 1))));
        var listed = await new FileOutbox(Dir).List();
        Assert.Equal(10, listed.Count);
        Assert.All(listed, e => Assert.Equal(1, e.Attempts));
    }

    [Fact]
    public async Task FileOutbox_LeavesNoTemporaryFile_WhenAWriteFails()
    {
        var outbox = new FileOutbox(Dir);
        await outbox.Add(Entry("a"));
        // The entry's file is now a directory: the rename onto it fails.
        var file = Path.Combine(Dir, "000000000001-a.json");
        File.Delete(file);
        Directory.CreateDirectory(file);
        await Assert.ThrowsAnyAsync<Exception>(() => outbox.Update(Entry("a", attempts: 1)));
        Assert.Equal(new[] { "000000000001-a.json" }, Directory.EnumerateFileSystemEntries(Dir).Select(Path.GetFileName));
        // The outbox goes on: its view of the entry did not change.
        Assert.Equal(0, Assert.Single(await outbox.List()).Attempts);
    }

    [Fact]
    public async Task FileOutbox_DoesNotStartAChangeWhoseTokenIsCancelled()
    {
        var outbox = new FileOutbox(Dir);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.Add(Entry("a"), cancelled.Token));
        Assert.Empty(await outbox.List());
    }

    private string[] Files() => Directory.EnumerateFiles(Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // ── The rules ────────────────────────────────────────────────────────────

    public static TheoryData<Exception, bool> Errors => new()
    {
        { new NetworkException(new HttpRequestException("down")), true },
        { new RateLimitException(429, "rate_limited", "slow down"), true },
        { new ServerException(502, "bad_gateway", "bad gateway"), true },
        { new ConflictException(409, "retention_in_progress", "busy"), true },
        { new NotFoundException(404, "evidence_store_disabled", "disabled"), true },
        { new ValidationException(422, "evidence_unknown_executor", "unknown key"), true },
        { new ValidationException(422, "evidence_chain_gap", "gap"), true },
        { new ClientErrorException(408, "", "Request Timeout"), true },
        { new InvalidOperationException("socket hang up"), true },
        // A response this SDK could not read: the NA may have admitted the record.
        { new StrictJsonException("duplicate_key", "\"status\""), true },
        { new ConflictException(409, "evidence_conflict", "taken"), false },
        { new BadRequestException(400, "evidence_malformed", "malformed"), false },
        { new BadRequestException(400, "invalid_evidence", "not an object"), false },
        { new ValidationException(422, "evidence_outside_decision_window", "outside"), false },
        { new ValidationException(422, "resource_chain_mismatch", "mismatch"), false },
        { new SecretMaterialException("field"), false },
        // A final code on a 5xx is not final: it is no answer to the record.
        { new ServerException(503, "evidence_conflict", "proxy"), true },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Classify_TellsTransientFailuresFromFinalRefusals(Exception error, bool transient) =>
        Assert.Equal(transient, EvidenceOutbox.Classify(error).Transient);

    [Fact]
    public void Classify_KeepsTheStatusCodeMessageAndQuarantine()
    {
        var conflict = EvidenceOutbox.Classify(new ConflictException(409, "evidence_conflict", "taken")).Failure;
        Assert.Equal((409, "evidence_conflict", "taken", (string?)null), (conflict.Status, conflict.Code, conflict.Message, conflict.QuarantineId));

        var quarantined = (ValidationException)ErrorMapping.Parse(422,
            """{"error":{"code":"evidence_out_of_scope","message":"outside","details":{"quarantine_id":"q-1"}}}""");
        Assert.Equal("q-1", EvidenceOutbox.Classify(quarantined).Failure.QuarantineId);

        var network = EvidenceOutbox.Classify(new NetworkException(new HttpRequestException("down"))).Failure;
        Assert.Equal((0, EvidenceOutbox.NetworkError), (network.Status, network.Code));
        var local = EvidenceOutbox.Classify(new InvalidOperationException("disk")).Failure;
        Assert.Equal((0, EvidenceOutbox.LocalError, "disk"), (local.Status, local.Code, local.Message));
        Assert.Throws<ArgumentNullException>(() => EvidenceOutbox.Classify(null!));
    }

    [Fact]
    public void RetryDelay_DoublesFrom5SecondsUpTo15Minutes() =>
        Assert.Equal(new[] { 5, 10, 20, 640, 900, 900, 900 },
            new[] { 1, 2, 3, 8, 9, 50, int.MaxValue }.Select(a => (int)EvidenceOutbox.RetryDelay(a).TotalSeconds));

    [Fact]
    public void NextAttemptAt_WaitsAsLongAsTheNaAsks_AtMost15Minutes()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        TimeSpan After(TimeSpan? retryAfter)
        {
            var error = (GenesisMeshException)ErrorMapping.Parse(503, "{}", retryAfter);
            return EvidenceOutbox.NextAttemptAt(1, error, now) - now;
        }
        Assert.Equal(TimeSpan.FromSeconds(5), After(null));
        Assert.Equal(TimeSpan.FromSeconds(5), After(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(120), After(TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromMinutes(15), After(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromSeconds(20), EvidenceOutbox.NextAttemptAt(3, new InvalidOperationException(), now) - now);
    }

    [Fact]
    public void Entry_IsPendingWithNoAttempts_QueuedNow()
    {
        var evidence = new ExecutionEvidence { EvidenceId = "e-1" };
        var entry = EvidenceOutbox.Entry(evidence, new DateTimeOffset(2026, 10, 30, 12, 0, 0, 123, TimeSpan.Zero));
        Assert.Equal(("e-1", OutboxStates.Pending, 0, "2026-10-30T12:00:00.123Z"), (entry.Id, entry.State, entry.Attempts, entry.QueuedAt));
        Assert.Same(evidence, entry.Evidence);
        Assert.Null(entry.NextAttemptAt);
        Assert.Null(entry.LastError);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", EvidenceOutbox.Entry(evidence).QueuedAt);
        Assert.True(EvidenceOutbox.PermanentRefusals.Contains("evidence_conflict"));
    }
}
