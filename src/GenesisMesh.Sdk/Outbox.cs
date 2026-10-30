using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>
/// An outbox refused the operation (1.9.0): none is configured (<c>outbox_required</c>), a file of the outbox could
/// not be read and was moved aside (<c>outbox_file_unreadable</c>), or a file is of another outbox format
/// (<c>outbox_file_other_format</c>).
/// </summary>
public sealed class OutboxException : GenesisMeshException
{
    /// <summary>A refusal with <paramref name="code"/>.</summary>
    /// <param name="code">The reason code.</param>
    /// <param name="detail">What was refused.</param>
    public OutboxException(string code, string detail)
        : base(0, code, detail, "genesismesh: " + detail)
    {
    }
}

/// <summary>
/// The rules the evidence outbox follows (1.9.0), as <see cref="EvidenceStoreClient.Enqueue"/> and
/// <see cref="EvidenceStoreClient.FlushPending"/> apply them: which refusals are final, and when a record is tried again.
/// The TypeScript and Rust SDKs follow the same rules.
/// </summary>
public static class EvidenceOutbox
{
    /// <summary>
    /// The local refusal code of a record whose predecessor in its chain is a dead letter, and which the NA refused for
    /// the gap that predecessor left: it can never be admitted.
    /// </summary>
    public const string PredecessorDeadLettered = "evidence_predecessor_dead_lettered";

    /// <summary>The failure code of an exception in this process before any request was made: retried.</summary>
    public const string LocalError = "local_error";

    /// <summary>The failure code of a submission that got no answer from the NA: retried.</summary>
    public const string NetworkError = "network_error";

    /// <summary>
    /// The NA's refusals that no retry of the same record overcomes. Every other failure is retried: a network error, a
    /// timeout, a <c>5xx</c> or <c>429</c>, an executor key not registered yet, a gap behind a record not admitted yet,
    /// a disabled store, a proxy's error page.
    /// </summary>
    public static IReadOnlySet<string> PermanentRefusals { get; } = new[]
    {
        "invalid_json",
        "invalid_evidence",
        "evidence_malformed",
        "evidence_executor_key_retired",
        "evidence_out_of_scope",
        "evidence_invalid_signature",
        "evidence_decision_denied",
        "evidence_decision_mismatch",
        "evidence_outside_decision_window",
        "evidence_capability_mismatch",
        "evidence_chain_mismatch",
        "resource_chain_mismatch",
        "evidence_conflict",
        "evidence_secret_material",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The failure a submission's exception is, as an entry stores it, and whether a later attempt may succeed. A
    /// refusal is final only when the NA (or this SDK, before sending) refused the record with a code of
    /// <see cref="PermanentRefusals"/>; JSON this SDK could not read is not one, since the NA may have admitted the record.
    /// </summary>
    public static (SubmissionFailure Failure, bool Transient) Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case GenesisMeshException e:
                var refused = PermanentRefusals.Contains(e.Code) && (e.Status == 0 || e.Status is >= 400 and < 500);
                return (new SubmissionFailure
                {
                    Status = e.Status,
                    Code = e.Code,
                    Message = e.Detail,
                    QuarantineId = e.Details.ValueKind == JsonValueKind.Object
                        && e.Details.TryGetProperty("quarantine_id", out var q) && q.ValueKind == JsonValueKind.String
                            ? q.GetString()
                            : null,
                }, !refused);
            case NetworkException:
                return (new SubmissionFailure { Status = 0, Code = NetworkError, Message = error.Message }, true);
            default:
                return (new SubmissionFailure { Status = 0, Code = LocalError, Message = error.Message }, true);
        }
    }

    /// <summary>The wait before the next attempt after <paramref name="attempts"/> failures: 5 s, doubling, at most 15 min.</summary>
    public static TimeSpan RetryDelay(int attempts)
    {
        var doublings = Math.Clamp(attempts - 1, 0, 30);
        var delay = FirstRetry * (1L << doublings);
        return delay < MaxRetry ? delay : MaxRetry;
    }

    /// <summary>
    /// When to try a record again after <paramref name="attempts"/> failures, the last one <paramref name="error"/>: the
    /// backoff, or later when the NA asked for a longer wait (<c>Retry-After</c>, at most 15 minutes).
    /// </summary>
    public static DateTimeOffset NextAttemptAt(int attempts, Exception error, DateTimeOffset now)
    {
        var asked = error is GenesisMeshException { RetryAfter: { } wait } ? wait : TimeSpan.Zero;
        var delay = RetryDelay(attempts);
        if (asked > delay) delay = asked < MaxRetry ? asked : MaxRetry;
        return now + delay;
    }

    /// <summary>A new pending entry for a signed record.</summary>
    public static OutboxEntry Entry(ExecutionEvidence evidence, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new OutboxEntry
        {
            Id = evidence.EvidenceId,
            Evidence = evidence,
            State = OutboxStates.Pending,
            Attempts = 0,
            QueuedAt = Time(now ?? DateTimeOffset.UtcNow),
        };
    }

    /// <summary>A time as entries store it: ISO 8601 in UTC with milliseconds, as JavaScript writes it.</summary>
    internal static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A stored time, or null when there is none or it does not parse.</summary>
    internal static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;
}

/// <summary>
/// An outbox in memory (1.9.0). Not durable: everything in it is lost when the process exits. For tests only. It
/// stores and returns copies.
/// </summary>
/// <typeparam name="TEntry">The entries stored, such as <see cref="OutboxEntry"/>.</typeparam>
public sealed class MemoryOutbox<TEntry> : IOutbox<TEntry> where TEntry : class, IOutboxEntry
{
    private readonly object _lock = new();
    private readonly List<TEntry> _entries = [];

    /// <inheritdoc/>
    public Task Add(TEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (_entries.Exists(e => e.Id == entry.Id))
                return Task.FromException(new InvalidOperationException($"genesismesh: outbox entry {entry.Id} exists"));
            _entries.Add(JsonCopy.Of(entry));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Update(TEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var i = _entries.FindIndex(e => e.Id == entry.Id);
            if (i >= 0) _entries[i] = JsonCopy.Of(entry);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Remove(string id, CancellationToken ct = default)
    {
        lock (_lock) _entries.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TEntry>> List(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<TEntry>>(_entries.Select(JsonCopy.Of).ToList());
    }
}

/// <summary>
/// The default evidence outbox (1.9.0): one JSON file per entry in a directory, written to a temporary file, flushed to
/// disk and renamed into place. File names keep the order entries were added. The TypeScript and Rust SDKs read and
/// write the same files (<c>gm.evidence.outbox.v1</c>), so a controller can move between them.
/// <para>
/// One process uses a directory at a time: it is read once, then kept in memory, and nothing stops a second process
/// from opening it too (each would keep its own view). On that first read, a temporary file a crash left is recovered
/// (an entry that was being added) or removed (an update that did not finish). A file that cannot be read, or whose
/// record has a field this SDK does not know, is moved aside as <c>&lt;name&gt;.unreadable</c>: that read throws
/// <c>outbox_file_unreadable</c> naming it (<see cref="OutboxException"/>), and the next read goes on without it. A
/// file of another outbox format is refused and left in place.
/// </para>
/// <para>
/// A directory the outbox creates is <c>0700</c> and its files <c>0600</c> on Linux and macOS; on Windows, and for a
/// directory that already exists, restrict access to it yourself. Unlike the TypeScript SDK, .NET cannot flush a
/// directory to disk, so on Linux a rename may be lost in a power failure before the file system writes it.
/// </para>
/// </summary>
public sealed class FileOutbox : IOutbox<OutboxEntry>
{
    internal const string Format = "gm.evidence.outbox.v1";
    private readonly JsonFileOutbox<OutboxEntry> _files;

    /// <summary>An outbox in <paramref name="directory"/>, created on first use.</summary>
    /// <param name="directory">The directory; private to the controller, on durable storage.</param>
    public FileOutbox(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        DirectoryPath = directory;
        _files = new JsonFileOutbox<OutboxEntry>(directory, Format, IsEntry);
    }

    /// <summary>The outbox's directory.</summary>
    public string DirectoryPath { get; }

    /// <inheritdoc/>
    public Task Add(OutboxEntry entry, CancellationToken ct = default) => _files.Add(entry, ct);

    /// <inheritdoc/>
    public Task Update(OutboxEntry entry, CancellationToken ct = default) => _files.Update(entry, ct);

    /// <inheritdoc/>
    public Task Remove(string id, CancellationToken ct = default) => _files.Remove(id, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboxEntry>> List(CancellationToken ct = default) => _files.List(ct);

    /// <summary>
    /// An entry holds a record object, without a field this SDK does not know: reading it would drop the field, and the
    /// record would no longer match its signature.
    /// </summary>
    private static bool IsEntry(JsonElement entry) =>
        entry.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Object
        && StrictFields.UnknownFields("ExecutionEvidence", evidence).Count == 0;
}

/// <summary>Copies of entries, through their JSON: an outbox never shares an entry with its caller.</summary>
internal static class JsonCopy
{
    internal static T Of<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Auth.SerializerOptions), Auth.SerializerOptions)!;
}

/// <summary>One JSON file per entry in a directory; see <see cref="FileOutbox"/>.</summary>
internal sealed partial class JsonFileOutbox<TEntry> where TEntry : class, IOutboxEntry
{
    private const string Unreadable = ".unreadable";
    private readonly string _directory;
    private readonly string _format;
    private readonly Func<JsonElement, bool> _isEntry;
    private readonly object _lock = new();
    /// <summary>
    /// The last operation queued: every operation runs alone, after the one before it, so an update never writes a
    /// file back over one a removal deleted. It never fails.
    /// </summary>
    private Task _tail = Task.CompletedTask;
    private Dictionary<string, Stored>? _stored;
    /// <summary>The highest file sequence used, so an add never scans every entry.</summary>
    private long _lastSequence;

    private sealed class Stored(string file, long sequence, TEntry entry)
    {
        public string File { get; } = file;
        public long Sequence { get; } = sequence;
        public TEntry Entry { get; set; } = entry;
    }

    private enum Kind { Entry, OtherFormat, Unreadable }

    [GeneratedRegex(@"^(\d{12})-([A-Za-z0-9-]{1,64})\.json$")]
    private static partial Regex EntryFile();

    [GeneratedRegex(@"^\.(\d{12}-[A-Za-z0-9-]{1,64}\.json)\.[0-9a-f]+\.tmp$")]
    private static partial Regex TempFile();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex PlainId();

    internal JsonFileOutbox(string directory, string format, Func<JsonElement, bool> isEntry)
    {
        _directory = directory;
        _format = format;
        _isEntry = isEntry;
    }

    internal Task Add(TEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Serial(async () =>
        {
            var stored = await Load(ct).ConfigureAwait(false);
            var stem = FileStem(entry.Id);
            if (stored.ContainsKey(stem)) throw new InvalidOperationException($"genesismesh: outbox entry {entry.Id} exists");
            var sequence = ++_lastSequence;
            var file = sequence.ToString("D12", CultureInfo.InvariantCulture) + "-" + stem + ".json";
            await Write(file, entry).ConfigureAwait(false);
            stored[stem] = new Stored(file, sequence, JsonCopy.Of(entry));
            return true;
        }, ct);
    }

    internal Task Update(TEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Serial(async () =>
        {
            if (!(await Load(ct).ConfigureAwait(false)).TryGetValue(FileStem(entry.Id), out var found)) return false;
            await Write(found.File, entry).ConfigureAwait(false);
            found.Entry = JsonCopy.Of(entry);
            return true;
        }, ct);
    }

    internal Task Remove(string id, CancellationToken ct) =>
        Serial(async () =>
        {
            var stored = await Load(ct).ConfigureAwait(false);
            var stem = FileStem(id);
            if (!stored.TryGetValue(stem, out var found)) return false;
            File.Delete(Path.Combine(_directory, found.File));
            stored.Remove(stem);
            return true;
        }, ct);

    internal Task<IReadOnlyList<TEntry>> List(CancellationToken ct) =>
        Serial(async () => (IReadOnlyList<TEntry>)(await Load(ct).ConfigureAwait(false)).Values
            .OrderBy(s => s.Sequence).ThenBy(s => s.File, StringComparer.Ordinal)
            .Select(s => JsonCopy.Of(s.Entry))
            .ToList(), ct);

    /// <summary>Runs <paramref name="operation"/> once every operation queued before it has ended.</summary>
    private Task<T> Serial<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        lock (_lock)
        {
            var run = After(_tail, operation, ct);
            _tail = run.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return run;
        }
    }

    private static async Task<T> After<T>(Task before, Func<Task<T>> operation, CancellationToken ct)
    {
        // Not cancelled while waiting: the next operation must not start before this one's turn has passed.
        await before.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await operation().ConfigureAwait(false);
    }

    private async Task<Dictionary<string, Stored>> Load(CancellationToken ct)
    {
        if (_stored is not null) return _stored;
        var stored = await Read(ct).ConfigureAwait(false);
        foreach (var s in stored.Values) _lastSequence = Math.Max(_lastSequence, s.Sequence);
        return _stored = stored;
    }

    private async Task<Dictionary<string, Stored>> Read(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var names = Names();
        var recovered = false;
        foreach (var name in names)
        {
            var temp = TempFile().Match(name);
            if (!temp.Success) continue;
            var path = Path.Combine(_directory, name);
            var target = temp.Groups[1].Value;
            // An add synced but not renamed is recovered; an update not renamed, or a write cut short, is dropped.
            var kind = names.Contains(target) ? Kind.Unreadable : Parse(await ReadText(path, ct).ConfigureAwait(false)).Kind;
            if (kind == Kind.Entry) File.Move(path, Path.Combine(_directory, target));
            else File.Delete(path);
            recovered = true;
        }
        if (recovered) names = Names();

        var stored = new Dictionary<string, Stored>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var file in names.Order(StringComparer.Ordinal))
        {
            var match = EntryFile().Match(file);
            if (!match.Success) continue;
            var path = Path.Combine(_directory, file);
            var (kind, entry) = Parse(await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false));
            if (kind == Kind.OtherFormat)
                throw new OutboxException("outbox_file_other_format", $"outbox file {file} in {_directory} is not {_format}");
            if (kind == Kind.Unreadable)
            {
                // Moved aside, so one bad file does not stop every action.
                File.Move(path, path + Unreadable, overwrite: true);
                unreadable.Add(file);
                continue;
            }
            stored[match.Groups[2].Value] = new Stored(file, long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), entry!);
        }
        if (unreadable.Count > 0)
        {
            // Once: the next read finds the outbox without them.
            var many = unreadable.Count > 1;
            throw new OutboxException("outbox_file_unreadable",
                $"outbox file{(many ? "s" : "")} {string.Join(", ", unreadable)} in {_directory} could not be read as {_format} "
                + $"and {(many ? "were" : "was")} moved aside (*{Unreadable}); the record{(many ? "s" : "")} held there will not be submitted");
        }
        return stored;
    }

    private HashSet<string> Names() =>
        Directory.EnumerateFiles(_directory).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.Ordinal);

    private static async Task<string> ReadText(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private async Task Write(string file, TEntry entry)
    {
        var temp = Path.Combine(_directory, "." + file + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".tmp");
        // Canonical JSON keeps the record's numbers as they were read, so a record another SDK wrote keeps the form it
        // was signed over.
        var text = "{\"entry\":" + Canonical.FromJson(JsonSerializer.Serialize(entry, Auth.SerializerOptions))
            + ",\"format\":" + JsonSerializer.Serialize(_format) + "}\n";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            var stream = new FileStream(temp, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path.Combine(_directory, file), overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // The next first read removes it.
            }
            throw;
        }
    }

    /// <summary>The entry in a file's text, or what keeps it from being one.</summary>
    private (Kind Kind, TEntry? Entry) Parse(string text)
    {
        try
        {
            using var doc = StrictJsonReader.Parse(text);
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("format", out var format)) return (Kind.Unreadable, null);
            if (format.ValueKind != JsonValueKind.String || format.GetString() != _format)
                return (format.ValueKind == JsonValueKind.String ? Kind.OtherFormat : Kind.Unreadable, null);
            if (!body.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !_isEntry(entry))
                return (Kind.Unreadable, null);
            return entry.Deserialize<TEntry>(Auth.SerializerOptions) is { } read ? (Kind.Entry, read) : (Kind.Unreadable, null);
        }
        catch (JsonException)
        {
            return (Kind.Unreadable, null);
        }
    }

    /// <summary>A file-name-safe stem for an entry ID: the ID itself when it is a plain identifier.</summary>
    private static string FileStem(string id) =>
        PlainId().IsMatch(id) ? id : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
}
