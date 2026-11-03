using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>One file of an audit pack: its <c>/</c>-separated relative path, SHA-256 and size.</summary>
public sealed record PackFile
{
    /// <summary>The path, relative to the pack, with <c>/</c> separators.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>SHA-256 hex of the file's bytes.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>The file's size in bytes.</summary>
    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }
}

/// <summary><c>manifest.json</c>: every other file of a pack and its digest, signed by the NA key valid at <c>created_at</c> (v1.5.0).</summary>
public sealed class AuditPackManifest
{
    /// <summary><c>gm.audit.pack</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = OfflineVerifier.PackFormat;

    /// <summary>1.</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = OfflineVerifier.PackFormatVersion;

    /// <summary>The pack's ID.</summary>
    [JsonPropertyName("pack_id")]
    public string PackId { get; set; } = "";

    /// <summary>The NA's sovereign.</summary>
    [JsonPropertyName("sovereign_id")]
    public string SovereignId { get; set; } = "";

    /// <summary>When the NA wrote the pack.</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    /// <summary>Who asked for it; null when not named.</summary>
    [JsonPropertyName("requested_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedBy { get; set; }

    /// <summary>The range's first store position.</summary>
    [JsonPropertyName("from_sequence")]
    public long FromSequence { get; set; }

    /// <summary>The range's last store position.</summary>
    [JsonPropertyName("through_sequence")]
    public long ThroughSequence { get; set; }

    /// <summary>The anchor that names the entry before the range; null when the range starts at entry 1 or after a checkpoint.</summary>
    [JsonPropertyName("start_anchor_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartAnchorSequence { get; set; }

    /// <summary>The anchor that names the range's last entry.</summary>
    [JsonPropertyName("end_anchor_sequence")]
    public long EndAnchorSequence { get; set; }

    /// <summary>The start of the period the pack was asked for, when it was.</summary>
    [JsonPropertyName("period_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PeriodFrom { get; set; }

    /// <summary>The end of the period the pack was asked for, when it was.</summary>
    [JsonPropertyName("period_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PeriodUntil { get; set; }

    /// <summary>Every other file of the pack.</summary>
    [JsonPropertyName("files")]
    public List<PackFile> Files { get; set; } = [];

    /// <summary>The NA key's ID.</summary>
    [JsonPropertyName("issued_by")]
    public string IssuedBy { get; set; } = "";

    /// <summary>The NA's signature, as received.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary>A pack written to immutable storage and read back intact (entry kind <c>archive</c>), signed by the NA (v1.5.0).</summary>
public sealed class ArchiveRecord
{
    /// <summary>The record's ID.</summary>
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = "";

    /// <summary>The archived pack's ID.</summary>
    [JsonPropertyName("pack_id")]
    public string PackId { get; set; } = "";

    /// <summary>The pack's first store position.</summary>
    [JsonPropertyName("from_sequence")]
    public long FromSequence { get; set; }

    /// <summary>The pack's last store position.</summary>
    [JsonPropertyName("through_sequence")]
    public long ThroughSequence { get; set; }

    /// <summary>The digest of the pack's last entry.</summary>
    [JsonPropertyName("through_entry_digest")]
    public string ThroughEntryDigest { get; set; } = "";

    /// <summary>The anchor that names the pack's last entry.</summary>
    [JsonPropertyName("end_anchor_sequence")]
    public long EndAnchorSequence { get; set; }

    /// <summary>SHA-256 hex of the pack's <c>manifest.json</c> as written.</summary>
    [JsonPropertyName("manifest_sha256")]
    public string ManifestSha256 { get; set; } = "";

    /// <summary>Where the pack is kept.</summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = "";

    /// <summary>When it was archived.</summary>
    [JsonPropertyName("archived_at")]
    public string ArchivedAt { get; set; } = "";

    /// <summary>Who recorded it; null when not named.</summary>
    [JsonPropertyName("recorded_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordedBy { get; set; }

    /// <summary>The NA's sovereign.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    public string IssuerSovereignId { get; set; } = "";

    /// <summary>The NA key's ID.</summary>
    [JsonPropertyName("issued_by")]
    public string IssuedBy { get; set; } = "";

    /// <summary>The NA's signature, as received.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary><c>POST /admin/evidence/audit-pack</c>: the signed manifest and every other file as text.</summary>
public sealed class AuditPackResponse
{
    /// <summary>The manifest, as received.</summary>
    [JsonPropertyName("manifest")]
    public JsonElement Manifest { get; set; }

    /// <summary>Every other file of the pack, by path, as text.</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = [];
}

/// <summary>One finding of <see cref="OfflineVerifier.VerifyAuditPack"/>.</summary>
public sealed record PackFinding
{
    /// <summary>The pack file the finding is reported against (<c>known-anchors</c> for the anchors held, <c>changes</c>); null for none.</summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }

    /// <summary>The store position; null for none.</summary>
    [JsonPropertyName("store_sequence")]
    public long? StoreSequence { get; init; }

    /// <summary>The reason code.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>More about it; may be empty.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";
}

/// <summary>The range an audit pack's entries hold.</summary>
public sealed record PackRange
{
    /// <summary>The first entry's store position.</summary>
    [JsonPropertyName("from_sequence")]
    public required long FromSequence { get; init; }

    /// <summary>The last entry's store position.</summary>
    [JsonPropertyName("through_sequence")]
    public required long ThroughSequence { get; init; }

    /// <summary>How many entries.</summary>
    [JsonPropertyName("entries")]
    public required int Entries { get; init; }

    /// <summary>When the first entry was recorded.</summary>
    [JsonPropertyName("first_recorded_at")]
    public required string FirstRecordedAt { get; init; }

    /// <summary>When the last entry was recorded.</summary>
    [JsonPropertyName("last_recorded_at")]
    public required string LastRecordedAt { get; init; }
}

/// <summary>The period an audit pack covers, and the ones it was asked for (<c>Requested</c>) and checked against (<c>Asked</c>).</summary>
public sealed record PackPeriod
{
    /// <summary>From its start anchor (or the checkpoint it starts after, or its first entry).</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>To its end anchor.</summary>
    [JsonPropertyName("until")]
    public required string Until { get; init; }

    /// <summary>The start the pack was asked for, as its manifest says.</summary>
    [JsonPropertyName("requested_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedFrom { get; init; }

    /// <summary>The end the pack was asked for, as its manifest says.</summary>
    [JsonPropertyName("requested_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedUntil { get; init; }

    /// <summary>The start of <see cref="VerifyAuditPackOptions.Period"/>.</summary>
    [JsonPropertyName("asked_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AskedFrom { get; init; }

    /// <summary>The end of <see cref="VerifyAuditPackOptions.Period"/>.</summary>
    [JsonPropertyName("asked_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AskedUntil { get; init; }
}

/// <summary>A line where <c>report.md</c> differs from the report the pack renders to.</summary>
public sealed record ReportDifference
{
    /// <summary>The line, from 1.</summary>
    [JsonPropertyName("line")]
    public required int Line { get; init; }

    /// <summary>The pack's line; null past its end.</summary>
    [JsonPropertyName("pack")]
    public string? Pack { get; init; }

    /// <summary>The rendered line; null past its end.</summary>
    [JsonPropertyName("verifier")]
    public string? Verifier { get; init; }
}

/// <summary>What <see cref="OfflineVerifier.VerifyAuditPack"/> found (v1.5.0).</summary>
public sealed class PackVerification
{
    /// <summary>True when nothing failed.</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; set; } = true;

    /// <summary>What failed, in the order found.</summary>
    [JsonPropertyName("failures")]
    public List<PackFinding> Failures { get; } = [];

    /// <summary><c>no_held_anchors</c>, <c>cited_entry_unanchored</c> and other findings that do not fail the pack.</summary>
    [JsonPropertyName("warnings")]
    public List<PackFinding> Warnings { get; } = [];

    /// <summary>The counts of the entries' verification.</summary>
    [JsonPropertyName("counts")]
    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>The range the entries hold; null before they are read.</summary>
    [JsonPropertyName("range")]
    public PackRange? Range { get; set; }

    /// <summary>How the entries tie to the anchors held; null without them.</summary>
    [JsonPropertyName("anchors")]
    public AnchorSummary? Anchors { get; set; }

    /// <summary>The NA's keys, as the key history verified from the root key gives them.</summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<NAKey> Keys { get; set; } = [];

    /// <summary>The period the pack covers; null before it is known.</summary>
    [JsonPropertyName("period")]
    public PackPeriod? Period { get; set; }

    /// <summary>The lines where <c>report.md</c> differs from the report the pack renders to (<c>report_mismatch</c>).</summary>
    [JsonPropertyName("report_differences")]
    public List<ReportDifference> ReportDifferences { get; } = [];

    /// <summary>With <see cref="VerifyAuditPackOptions.Changes"/>: the completeness check.</summary>
    [JsonPropertyName("completeness")]
    public CompletenessReport? Completeness { get; set; }
}

/// <summary>Options for <see cref="OfflineVerifier.VerifyAuditPack"/>.</summary>
public sealed class VerifyAuditPackOptions
{
    /// <summary>The root public key, held out of band (<c>root.pub</c>): the NA's keys are taken from the key history only through it.</summary>
    public required string RootPublicKey { get; init; }

    /// <summary>
    /// The anchors the verifier holds, its copies kept outside the operator's reach. Null verifies the pack's integrity
    /// only, not that nothing was removed, and says so (the warning <c>no_held_anchors</c>).
    /// </summary>
    public IReadOnlyList<StoreAnchor>? HeldAnchors { get; init; }

    /// <summary>Executor and observer keys supplied out of band, for a store without registry records (a key the pack's registry records place wins).</summary>
    public IReadOnlyList<ExecutorKeyRecord> ExecutorKeys { get; init; } = [];

    /// <summary>Changes from a log outside Genesis Mesh: runs the completeness check (<c>change_unmatched</c>).</summary>
    public IReadOnlyList<Change>? Changes { get; init; }

    /// <summary>The completeness check's time tolerance: 300 seconds by default.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>A period the pack must cover (<c>pack_period_not_covered</c>); null for none.</summary>
    public (DateTimeOffset From, DateTimeOffset Until)? Period { get; init; }

    /// <summary>
    /// The entries of packs verified before, by store position (<see cref="OfflineVerifier.PackEntryDigests"/>): the
    /// entries this pack cites must equal them (<c>cited_entry_mismatch</c>).
    /// </summary>
    public IReadOnlyDictionary<long, string>? PriorEntries { get; init; }
}

/// <summary>What <see cref="OfflineVerifier.RenderReport"/> reads of a manifest (<see cref="OfflineVerifier.ReportHeaderOf"/>).</summary>
public sealed record ReportHeader
{
    /// <summary>The pack's ID.</summary>
    public required string PackId { get; init; }

    /// <summary>The NA's sovereign.</summary>
    public required string SovereignId { get; init; }

    /// <summary>The range's first store position.</summary>
    public required long FromSequence { get; init; }

    /// <summary>The range's last store position.</summary>
    public required long ThroughSequence { get; init; }

    /// <summary>The start anchor; null for none.</summary>
    public long? StartAnchorSequence { get; init; }

    /// <summary>The end anchor.</summary>
    public required long EndAnchorSequence { get; init; }

    /// <summary>When the pack was written, in UTC as the reference prints a time.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Who asked for it; null when not named.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>The start of the period it was asked for; null for none.</summary>
    public string? PeriodFrom { get; init; }

    /// <summary>The end of the period it was asked for; null for none.</summary>
    public string? PeriodUntil { get; init; }
}

public static partial class OfflineVerifier
{
    // Audit packs (v1.5.0; offline here from 1.9.0), as the reference's verify_audit_pack and the TypeScript SDK's
    // verifyAuditPack: one range of the evidence store between two anchors, everything the range's records cite, the
    // NA's key history and a manifest the NA signs, verified with no network and no private key, from what the auditor
    // holds itself: the root public key and its copies of the anchors. Keys inside the pack prove nothing alone: NA keys
    // are accepted only through the key history, from the root key down; executor and observer keys only through registry
    // records the NA signed, or keys the auditor supplies.

    /// <summary>A pack's format.</summary>
    public const string PackFormat = "gm.audit.pack";

    /// <summary>The format version this SDK reads.</summary>
    public const int PackFormatVersion = 1;

    /// <summary>The manifest: every other file and its digest, signed by the NA.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The report a reader sees first, rendered from the pack's files.</summary>
    public const string ReportFile = "report.md";

    /// <summary>The NA's own verification of the range when it wrote the pack.</summary>
    public const string ChecksFile = "checks.json";

    /// <summary>The genesis block.</summary>
    public const string GenesisFile = "genesis.json";

    /// <summary>The NA's key successions.</summary>
    public const string KeyHistoryFile = "keys/na-key-history.json";

    /// <summary>The executor keys the NA lists.</summary>
    public const string ExecutorKeysFile = "keys/executor-keys.json";

    /// <summary>The NA's anchors around the range.</summary>
    public const string AnchorsFile = "anchors.jsonl";

    /// <summary>The range's entries.</summary>
    public const string EntriesFile = "entries.jsonl";

    /// <summary>The entries before the range its records cite.</summary>
    public const string CitedFile = "cited.jsonl";

    /// <summary>The policies the range's records are bound to.</summary>
    public const string PoliciesFile = "policies.jsonl";

    /// <summary>The attestations they rest on.</summary>
    public const string AttestationsFile = "trust/attestations.jsonl";

    /// <summary>The treaties that vouch for other sovereigns' keys.</summary>
    public const string TreatiesFile = "trust/treaties.jsonl";

    /// <summary>The revocation feeds.</summary>
    public const string FeedsFile = "trust/feeds.jsonl";

    /// <summary>The NA's remediation states and notices at pack time (operational records, not signed), which <c>report.md</c> shows.</summary>
    public const string StatesFile = "states.json";

    /// <summary>v1.6.0: the agreements the pack's grants rest on, each as received; held only when a grant rests on one.</summary>
    public const string AgreementsFile = "trust/agreements.jsonl";

    /// <summary>Every file of a pack (format version 1) but the manifest, which lists them.</summary>
    public static readonly IReadOnlyList<string> PackFiles =
    [
        AnchorsFile, ChecksFile, CitedFile, EntriesFile, ExecutorKeysFile, GenesisFile, KeyHistoryFile,
        PoliciesFile, ReportFile, StatesFile, AttestationsFile, FeedsFile, TreatiesFile,
    ];

    /// <summary>Files of the format a pack holds only when it has something to put in them (v1.6.0).</summary>
    public static readonly IReadOnlyList<string> OptionalPackFiles = [AgreementsFile];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A JSON file of a pack: canonical JSON with non-ASCII text kept, and a final newline.</summary>
    /// <param name="value">The value.</param>
    public static byte[] PackJsonBytes(JsonElement value) => StrictUtf8.GetBytes(Canonical.OfUnescaped(value) + "\n");

    /// <summary>The files of a pack the NA returned, with <c>manifest.json</c> written as the NA writes it: ready to save or verify.</summary>
    /// <param name="response">The NA's answer.</param>
    /// <exception cref="ArgumentException">The answer holds no manifest object.</exception>
    public static IReadOnlyDictionary<string, byte[]> AuditPackFiles(AuditPackResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Manifest.ValueKind != JsonValueKind.Object) throw new ArgumentException("the answer holds no manifest", nameof(response));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, text) in response.Files) files[path] = StrictUtf8.GetBytes(text);
        files[ManifestFile] = PackJsonBytes(WithoutNulls(response.Manifest));
        return files;
    }

    /// <summary>Every file under <paramref name="directory"/>, by its <c>/</c>-separated relative path.</summary>
    /// <param name="directory">The pack's directory.</param>
    public static IReadOnlyDictionary<string, byte[]> ReadAuditPackDirectory(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(path);
        return files;
    }

    /// <summary>The entries a pack holds, by store position: their digests (for <see cref="VerifyAuditPackOptions.PriorEntries"/>).</summary>
    /// <param name="files">The pack's files.</param>
    /// <exception cref="ArgumentException">The pack holds no <c>entries.jsonl</c>.</exception>
    /// <exception cref="JsonException">A line is not an evidence event.</exception>
    public static IReadOnlyDictionary<long, string> PackEntryDigests(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.TryGetValue(EntriesFile, out var body)) throw new ArgumentException("the pack holds no entries.jsonl", nameof(files));
        return PackEvents(body).ToDictionary(e => e.Entry.StoreSequence, e => EntryDigest(e.Entry));
    }

    /// <summary>The failure reason codes of a pack's verification, once each, sorted (what two verifiers compare).</summary>
    /// <param name="result">The verification.</param>
    public static IReadOnlyList<string> PackReasons(PackVerification result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Failures.Select(f => f.Reason).Distinct().Order(Canonical.CodePointComparer.Instance).ToList();
    }

    /// <summary>
    /// Why a record's signature does not verify, over the record as received, with the NA key valid at
    /// <paramref name="at"/>, or null: <c>unknown_field</c> or <c>non_canonical_form</c> for a record its signer signed
    /// in another form, <c>na_key_not_valid_at_time</c> for one signed by another key of the history,
    /// <c>invalid_signature</c> otherwise.
    /// </summary>
    /// <param name="model">The record's model, as the field registry names it.</param>
    /// <param name="record">The record, as received.</param>
    /// <param name="history">The NA's verified key history.</param>
    /// <param name="at">The time the record is placed at.</param>
    public static string? ReceivedSignatureReason(string model, JsonElement record, NAKeyHistory history, string at)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(at);
        if (record.ValueKind != JsonValueKind.Object) return "invalid_signature";
        var keys = TryReadTimestamp(at, out var when) ? history.KeysAt(Instant(when)) : [];
        if (SignedAsReceived(model, record, keys)) return StrictRefusal(model, record, keys);
        var others = history.AllKeys().Where(k => !keys.Contains(k, StringComparer.Ordinal)).ToList();
        return others.Count > 0 && SignedAsReceived(model, record, others) ? "na_key_not_valid_at_time" : "invalid_signature";
    }

    /// <summary>As <see cref="ReceivedSignatureReason"/>, for keys with no time (a treaty's subject keys), of a record in its model's shape.</summary>
    private static string? ReceivedBy(string model, JsonElement record, IReadOnlyList<string> keys) =>
        SignedAsReceived(model, record, keys) ? StrictRefusal(model, record, keys) : "invalid_signature";

    /// <summary>
    /// Whether an archive record is well formed and signed by the NA, in the reference's form, with no field this SDK
    /// does not know (v1.5.0).
    /// </summary>
    /// <param name="archiveJson">The ArchiveRecord JSON, as received.</param>
    /// <param name="naPublicKeys">The NA's keys, valid at every time.</param>
    /// <exception cref="StrictJsonException"><paramref name="archiveJson"/> is not strict JSON.</exception>
    public static bool VerifyArchiveRecord(string archiveJson, IReadOnlyList<string> naPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(naPublicKeys);
        return ArchiveSigned(archiveJson, _ => naPublicKeys);
    }

    /// <summary>As <see cref="VerifyArchiveRecord(string, IReadOnlyList{string})"/>, signed by the key of the NA's verified history valid at its <c>archived_at</c>.</summary>
    /// <param name="archiveJson">The ArchiveRecord JSON, as received.</param>
    /// <param name="keyHistory">The NA's verified key history.</param>
    /// <exception cref="StrictJsonException"><paramref name="archiveJson"/> is not strict JSON.</exception>
    public static bool VerifyArchiveRecord(string archiveJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        // Its shape holds a valid archived_at.
        return ArchiveSigned(archiveJson, r => keyHistory.KeysAt(Instant(TimestampMicros(Str(r, "archived_at")!)!.Value)));
    }

    private static bool ArchiveSigned(string archiveJson, Func<JsonElement, IReadOnlyList<string>> keys)
    {
        using var doc = StrictJsonReader.Parse(archiveJson);
        var record = doc.RootElement;
        return Shapes.Archive(record) && StrictFields.UnknownFields("ArchiveRecord", record).Count == 0
            && StrictFields.NonCanonicalFields("ArchiveRecord", record).Count == 0 && OutOfBandSignedBy(record, keys(record));
    }

    /// <summary>v1.5.0: the store positions of the entries retention carried forward, in an unbroken run right after a retention checkpoint.</summary>
    private static HashSet<long> CarriedSequences(IEnumerable<EvidenceEvent> events)
    {
        var carried = new HashSet<long>();
        EvidenceStoreEntry? previous = null;
        var carrying = false;
        foreach (var e in events.OrderBy(e => e.Entry.StoreSequence))
        {
            var entry = e.Entry;
            var adjacent = previous is not null && entry.StoreSequence == previous.StoreSequence + 1;
            carrying = CarriedKinds.Contains(entry.EntryKind) && adjacent && (previous!.EntryKind == "retention_checkpoint" || carrying);
            if (carrying) carried.Add(entry.StoreSequence);
            previous = entry;
        }
        return carried;
    }

    /// <summary>
    /// Executor and observer keys from the registry records the NA signed (by the key valid then), each placed where the
    /// store registered and retired it: by store position, or by <c>effective_at</c> for a record backfilled at an
    /// upgrade (<c>reconstructed</c>) or carried past retention. The first registration of a key places it.
    /// </summary>
    /// <param name="events">The entries, cited ones included.</param>
    /// <param name="history">The NA's verified key history.</param>
    /// <exception cref="ArgumentException">An event is null or has no entry.</exception>
    public static IReadOnlyDictionary<string, ExecutorKeyRecord> RegistryKeys(IReadOnlyList<EvidenceEvent> events, NAKeyHistory history)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(history);
        if (events.Any(e => e?.Entry is null)) throw new ArgumentException("an event is null or has no entry", nameof(events));
        var ordered = events.OrderBy(e => e.Entry.StoreSequence).ToList();
        var carried = CarriedSequences(ordered);
        var keys = new Dictionary<string, ExecutorKeyRecord>(StringComparer.Ordinal);
        foreach (var e in ordered)
        {
            if (e.Entry.EntryKind != "registry") continue;
            var r = e.Payload;
            if (!Shapes.Registry(r) || StrictFields.UnknownFields("RegistryRecord", r).Count > 0) continue;
            if (ReceivedSignatureReason("RegistryRecord", r, history, e.Entry.RecordedAt) is not null) continue;
            var seq = e.Entry.StoreSequence;
            var byTime = Member(r, "reconstructed") is { ValueKind: JsonValueKind.True } || carried.Contains(seq);
            var keyId = Str(r, "key_id") ?? "";
            var publicKey = Str(r, "public_key");
            var sovereign = Str(r, "executor_sovereign_id");
            if (Str(r, "event") == "executor_key_registered" && keyId.Length > 0 && publicKey is { Length: > 0 } && sovereign is { Length: > 0 })
            {
                if (keys.TryGetValue(keyId, out var held) && held.PublicKey == publicKey) continue;
                keys[keyId] = new ExecutorKeyRecord
                {
                    KeyId = keyId,
                    PublicKey = publicKey,
                    ExecutorSovereignId = sovereign,
                    Role = Str(r, "key_role") is { Length: > 0 } role ? role : "executor",
                    ResourcePrefix = Str(r, "resource_prefix"),
                    RegisteredSequence = byTime ? null : seq,
                    RegisteredAtTime = byTime ? Str(r, "effective_at") : null,
                };
            }
            else if (Str(r, "event") == "executor_key_retired" && keyId.Length > 0 && keys.TryGetValue(keyId, out var retired)
                     && retired.RetiredSequence is null && retired.RetiredAtTime is null)
            {
                keys[keyId] = new ExecutorKeyRecord
                {
                    KeyId = retired.KeyId,
                    PublicKey = retired.PublicKey,
                    ExecutorSovereignId = retired.ExecutorSovereignId,
                    Role = retired.Role,
                    ResourcePrefix = retired.ResourcePrefix,
                    RegisteredSequence = retired.RegisteredSequence,
                    RegisteredAtTime = retired.RegisteredAtTime,
                    RetiredSequence = byTime ? null : seq,
                    RetiredAtTime = byTime ? Str(r, "effective_at") : null,
                };
            }
        }
        return keys;
    }

    // ── Reading a pack's files ──────────────────────────────────────────────

    private static string PackText(byte[]? body) =>
        StrictUtf8.GetString(body ?? throw new InvalidDataException("missing file"));

    private static JsonElement PackJson(byte[]? body)
    {
        using var doc = StrictJsonReader.Parse(PackText(body));
        return doc.RootElement.Clone();
    }

    /// <summary>JSON Lines: one value per LF-separated line, blank lines (of spaces, tabs and CRs) ignored.</summary>
    private static List<JsonElement> PackJsonLines(byte[]? body)
    {
        var items = new List<JsonElement>();
        foreach (var line in PackText(body).Split('\n'))
        {
            if (line.Trim(' ', '\t', '\r').Length == 0) continue;
            using var doc = StrictJsonReader.Parse(line);
            items.Add(doc.RootElement.Clone());
        }
        return items;
    }

    private static IReadOnlyList<EvidenceEvent> PackEvents(byte[]? body) => ParseExportLines(PackText(body).Split('\n'));

    /// <summary>
    /// A signed record as the reference writes it: without its null members, integral numbers written as integers, its
    /// signature's <c>key_id</c> and <c>sig</c> only.
    /// </summary>
    private static JsonElement WithoutNulls(JsonElement value)
    {
        var node = Integral(value)!.AsObject();
        foreach (var name in node.Where(p => p.Value is null).Select(p => p.Key).ToList()) node.Remove(name);
        if (node["signature"] is JsonObject signature)
            foreach (var name in signature.Select(p => p.Key).Where(k => k is not ("key_id" or "sig")).ToList()) signature.Remove(name);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>A timestamp as the reference writes it in a report: UTC, six fraction digits when not zero, <c>Z</c>.</summary>
    private static string Iso(string value) => TryReadTimestamp(value, out var at) ? Canonical.Timestamp(Instant(at)) : value;

    private static string Iso(long micros) => Canonical.Timestamp(Instant(micros));

    /// <summary>A time read and checked already: an entry's, an anchor's, a manifest's or a checkpoint's.</summary>
    private static long MicrosOf(string value) => TimestampMicros(value)!.Value;

    // ── Verifying a pack ────────────────────────────────────────────────────

    private sealed class PackRun(PackVerification result)
    {
        public PackVerification Result => result;

        public void Fail(string? file, long? storeSequence, string reason, string detail = "")
        {
            result.Verified = false;
            result.Failures.Add(new PackFinding { File = file, StoreSequence = storeSequence, Reason = reason, Detail = detail });
        }

        public void Warn(string? file, long? storeSequence, string reason, string detail = "") =>
            result.Warnings.Add(new PackFinding { File = file, StoreSequence = storeSequence, Reason = reason, Detail = detail });
    }

    private static (AuditPackManifest? Manifest, JsonElement Raw) CheckManifest(IReadOnlyDictionary<string, byte[]> files, PackRun run)
    {
        if (!files.TryGetValue(ManifestFile, out var raw))
        {
            run.Fail(ManifestFile, null, "manifest_missing");
            return (null, default);
        }
        JsonElement data;
        try
        {
            data = PackJson(raw);
            if (data.ValueKind == JsonValueKind.Object && StrictFields.UnknownFields("AuditPackManifest", data).Count > 0)
                throw new InvalidDataException("unknown field");
            if (!Shapes.Manifest(data)) throw new InvalidDataException("not an audit pack manifest");
        }
        catch (Exception e) when (e is InvalidDataException or StrictJsonException or DecoderFallbackException)
        {
            run.Fail(ManifestFile, null, "manifest_invalid", e.Message);
            return (null, default);
        }
        var manifest = ReadPayload<AuditPackManifest>(data)!;
        // The bytes the NA writes: canonical JSON of the record, integers as integers, timestamps in canonical form.
        if (StrictFields.NonCanonicalFields("AuditPackManifest", data).Count > 0 || !PackJsonBytes(WithoutNulls(data)).AsSpan().SequenceEqual(raw))
            run.Fail(ManifestFile, null, "manifest_invalid", "not in canonical form");
        var listed = new Dictionary<string, PackFile>(StringComparer.Ordinal);
        foreach (var item in manifest.Files)
        {
            if (listed.ContainsKey(item.Path) || item.Path == ManifestFile)
                run.Fail(ManifestFile, null, "manifest_invalid", $"lists {item.Path} twice or itself");
            listed[item.Path] = item;
        }
        foreach (var path in PackFiles)
            if (!listed.ContainsKey(path)) run.Fail(ManifestFile, null, "manifest_file_missing", $"{path} is not listed");
        foreach (var path in listed.Keys.Order(Canonical.CodePointComparer.Instance))
        {
            if (!PackFiles.Contains(path) && !OptionalPackFiles.Contains(path))
            {
                // A pack holds the files of its format and nothing else.
                run.Fail(ManifestFile, null, "manifest_unknown_file", $"{path} is not a file of the format");
                continue;
            }
            if (!files.TryGetValue(path, out var body)) run.Fail(path, null, "manifest_file_missing");
            else if (!string.Equals(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), listed[path].Sha256, StringComparison.Ordinal) || body.LongLength != listed[path].Bytes)
                run.Fail(path, null, "manifest_digest_mismatch");
        }
        foreach (var path in files.Keys.Order(Canonical.CodePointComparer.Instance))
            if (path != ManifestFile && !listed.ContainsKey(path)) run.Fail(path, null, "manifest_unlisted_file");
        return (manifest, data);
    }

    /// <summary>Two anchors are the same when their wire forms are, timestamps compared as instants.</summary>
    private static bool SameAnchor(StoreAnchor a, StoreAnchor b) =>
        a.AnchorSequence == b.AnchorSequence && a.SovereignId == b.SovereignId && a.StoreSequence == b.StoreSequence
        && a.EntryDigest == b.EntryDigest && a.PreviousAnchorDigest == b.PreviousAnchorDigest && a.IssuedBy == b.IssuedBy
        && MicrosOf(a.AnchoredAt) == MicrosOf(b.AnchoredAt)
        && (a.Signature.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? b.Signature.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            : b.Signature.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && Canonical.Of(a.Signature) == Canonical.Of(b.Signature));

    /// <summary>
    /// Verifies an audit pack offline, as the reference's <c>verify_audit_pack</c> does, each failure named by a reason
    /// code, in its order:
    /// <list type="number">
    /// <item>the manifest (<c>manifest_missing</c>, <c>manifest_invalid</c>, <c>manifest_unknown_file</c>,
    /// <c>manifest_file_missing</c>, <c>manifest_digest_mismatch</c>, <c>manifest_unlisted_file</c>), signed as received
    /// by the NA key valid at its <c>created_at</c> (<c>manifest_invalid_signature</c>): a pack whose manifest fails is
    /// not checked further;</item>
    /// <item>the key history, from the root key down (<c>genesis_*</c>, <c>succession_*</c>, <c>key_history_invalid</c>);</item>
    /// <item>the anchors (the anchor reasons, <c>pack_end_anchor_mismatch</c>, <c>pack_end_anchor_not_held</c>,
    /// <c>pack_start_not_tied</c>, <c>manifest_backdated</c>, <c>anchor_mismatch</c>, <c>anchor_entry_missing</c>,
    /// <c>export_not_linked_to_anchors</c>; without held anchors the warning <c>no_held_anchors</c>), and each cited
    /// entry against a prior pack or a held anchor (<c>cited_entry_mismatch</c>; the warning
    /// <c>cited_entry_unanchored</c>);</item>
    /// <item>the entries (<c>entries_invalid</c>, <c>pack_range_mismatch</c>, <c>cited_out_of_range</c>, then every
    /// reason of <see cref="VerifyEvidenceEvents"/>), with the keys the pack's registry records place;</item>
    /// <item>what the records rest on, each checked as received (<c>cited_trust_invalid</c>,
    /// <c>policy_invalid_signature</c>, <c>treaty_invalid_signature</c>, <c>attestation_invalid_signature</c>,
    /// <c>feed_invalid_signature</c>, <c>cited_policy_missing</c>, <c>cited_policy_mismatch</c>,
    /// <c>cited_attestation_missing</c>, <c>cited_attestation_mismatch</c>, and for a grant's agreement
    /// <c>cited_agreement_missing</c>, <c>cited_agreement_mismatch</c>, <c>agreement_invalid_signature</c>);</item>
    /// <item>the report, rendered from the pack's own files (<c>report_mismatch</c>);</item>
    /// <item>with <see cref="VerifyAuditPackOptions.Period"/>, that the pack covers it (<c>pack_period_not_covered</c>);</item>
    /// <item>with <see cref="VerifyAuditPackOptions.Changes"/>, completeness (<c>change_unmatched</c>; the warnings
    /// <c>changes_outside_period</c> and <c>change_duplicate</c>).</item>
    /// </list>
    /// Decisions are judged as of their own time: one that expired since it was made and used is not a failure. Nothing
    /// is thrown for a bad file: each finding is a failure.
    /// </summary>
    /// <param name="files">The pack's files by their <c>/</c>-separated relative paths (<see cref="ReadAuditPackDirectory"/>, <see cref="AuditPackFiles"/>).</param>
    /// <param name="options">The root key and anchors held, and what else to check.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A held anchor is not well formed, or a key or change given is not one (as <see cref="VerifyEvidenceEvents"/> and
    /// <see cref="CheckCompleteness"/> refuse them).
    /// </exception>
    public static PackVerification VerifyAuditPack(IReadOnlyDictionary<string, byte[]> files, VerifyAuditPackOptions options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RootPublicKey, nameof(options));
        var result = new PackVerification();
        var run = new PackRun(result);
        var (manifest, manifestRaw) = CheckManifest(files, run);

        // 2. The key history, from the root key down.
        KeyHistoryVerificationResult keyed;
        try
        {
            var genesis = PackJson(files.GetValueOrDefault(GenesisFile));
            var keyHistory = PackJson(files.GetValueOrDefault(KeyHistoryFile));
            if (keyHistory.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the key history is not an object");
            var successions = keyHistory.TryGetProperty("successions", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed.GetRawText() : "[]";
            keyed = VerifyKeyHistory(genesis.GetRawText(), successions, options.RootPublicKey);
        }
        catch (Exception e) when (e is InvalidDataException or StrictJsonException or DecoderFallbackException or FormatException)
        {
            run.Fail(KeyHistoryFile, null, "key_history_invalid", e.Message);
            return result;
        }
        if (!keyed.Verified || keyed.History is not { } history)
        {
            run.Fail(keyed.KeySequence == 1 ? GenesisFile : KeyHistoryFile, null, keyed.Reason!, $"key {keyed.KeySequence}");
            return result;
        }
        result.Keys = history.Keys;
        if (manifest is not null)
        {
            if (manifest.SovereignId != history.SovereignId) run.Fail(ManifestFile, null, "manifest_invalid", "names another sovereign");
            // Over the manifest as received, not a form with its defaults filled in.
            if (ReceivedSignatureReason("AuditPackManifest", manifestRaw, history, manifest.CreatedAt) is { } reason)
                run.Fail(ManifestFile, null, reason == "invalid_signature" ? "manifest_invalid_signature" : reason);
        }
        // Files that do not match a manifest the NA signed are not the pack: nothing else is checked.
        if (manifest is null || !result.Verified) return result;

        // 3 and 4. Anchors and entries.
        List<StoreAnchor> packAnchors;
        IReadOnlyList<EvidenceEvent> entries;
        IReadOnlyList<EvidenceEvent> cited;
        try
        {
            var anchors = PackJsonLines(files.GetValueOrDefault(AnchorsFile));
            if (!anchors.All(Shapes.Anchor)) throw new InvalidDataException("an anchor is not a StoreAnchor");
            packAnchors = anchors.Select(a => ReadPayload<StoreAnchor>(a)!).OrderBy(a => a.AnchorSequence).ToList();
            entries = PackEvents(files.GetValueOrDefault(EntriesFile));
            cited = PackEvents(files.GetValueOrDefault(CitedFile));
        }
        catch (Exception e) when (e is InvalidDataException or StrictJsonException or DecoderFallbackException or JsonException)
        {
            run.Fail(EntriesFile, null, "entries_invalid", e.Message);
            return result;
        }
        if (entries.Count == 0)
        {
            run.Fail(EntriesFile, null, "pack_range_mismatch", "the pack holds no entry");
            return result;
        }
        var (head, tail) = (entries[0], entries[^1]);
        var (first, last) = (head.Entry.StoreSequence, tail.Entry.StoreSequence);
        var (fromSequence, through) = (manifest.FromSequence, manifest.ThroughSequence);
        result.Range = new PackRange
        {
            FromSequence = first,
            ThroughSequence = last,
            Entries = entries.Count,
            FirstRecordedAt = head.Entry.RecordedAt,
            LastRecordedAt = tail.Entry.RecordedAt,
        };
        if (first != fromSequence || last != through || entries.Count != through - fromSequence + 1)
            run.Fail(EntriesFile, null, "pack_range_mismatch", $"the entries run {first}..{last}, the manifest says {fromSequence}..{through}");
        foreach (var e in cited)
            if (e.Entry.StoreSequence >= fromSequence) run.Fail(CitedFile, e.Entry.StoreSequence, "cited_out_of_range");

        var chain = VerifyStoreAnchors(packAnchors, new AnchorVerifyOptions { KeyHistory = history, SovereignId = history.SovereignId });
        foreach (var f in chain.Failures) run.Fail(AnchorsFile, null, f.Reason, $"anchor {f.AnchorSequence}");
        var bySequence = new Dictionary<long, StoreAnchor>();
        foreach (var a in packAnchors) bySequence[a.AnchorSequence] = a;
        var end = bySequence.GetValueOrDefault(manifest.EndAnchorSequence);
        if (end is null || end.StoreSequence != last || end.EntryDigest != EntryDigest(tail.Entry))
            run.Fail(AnchorsFile, last, "pack_end_anchor_mismatch", "no anchor of the pack names the range's last entry");
        // A start anchor names the entry before the range (the manifest's shape refuses 0).
        var startAnchor = manifest.StartAnchorSequence is { } s ? bySequence.GetValueOrDefault(s) : null;
        if (manifest.StartAnchorSequence is not null
            && (startAnchor is null || startAnchor.StoreSequence != first - 1 || startAnchor.EntryDigest != head.Entry.PrevEntryDigest))
            run.Fail(AnchorsFile, first, "pack_start_not_tied", "the start anchor does not name the entry before the range");
        // The NA writes a pack after its end anchor and its last entry.
        var latest = Math.Max(MicrosOf(tail.Entry.RecordedAt), end is not null ? MicrosOf(end.AnchoredAt) : long.MinValue);
        if (MicrosOf(manifest.CreatedAt) < latest)
            run.Fail(ManifestFile, null, "manifest_backdated", $"created at {Iso(manifest.CreatedAt)}, before {Iso(latest)}");

        // The start checkpoint: a retention checkpoint (in the range or cited) that removed the entries before it.
        var startCheckpoint = StartingCheckpoint(entries.Concat(cited).Reverse().ToList(), head);
        if (first == 1 && head.Entry.PrevEntryDigest is not null) run.Fail(EntriesFile, 1, "store_chain_break", "entry 1 names a previous entry");

        var held = options.HeldAnchors;
        if (held is null)
        {
            run.Warn(null, null, "no_held_anchors", "verified without anchors held out of band: the pack's integrity, not that nothing was removed");
        }
        else
        {
            var heldChain = VerifyStoreAnchors(held, new AnchorVerifyOptions { KeyHistory = history, SovereignId = history.SovereignId });
            foreach (var f in heldChain.Failures) run.Fail("known-anchors", null, f.Reason, $"held anchor {f.AnchorSequence}");
            var heldBySequence = new Dictionary<long, StoreAnchor>();
            foreach (var a in held) heldBySequence[a.AnchorSequence] = a;
            foreach (var anchor in packAnchors)
                if (heldBySequence.TryGetValue(anchor.AnchorSequence, out var mine) && !SameAnchor(mine, anchor))
                    run.Fail(AnchorsFile, anchor.StoreSequence, "anchor_mismatch", $"anchor {anchor.AnchorSequence} differs from the one held");
            if (end is not null && !heldBySequence.ContainsKey(end.AnchorSequence))
                run.Fail(AnchorsFile, last, "pack_end_anchor_not_held", $"anchor {end.AnchorSequence} is not among the anchors held");
            var tied = new EvidenceStoreVerification { Verified = true };
            CheckEventsAgainstAnchors(entries, held.Where(a => a.StoreSequence <= last).ToList(), tied, new AnchorCheckOptions { FromAnchor = true });
            foreach (var f in tied.Failures) run.Fail(EntriesFile, f.StoreSequence, f.Reason, f.Detail);
            result.Anchors = tied.Anchors;
        }

        // A cited entry is checked against what the verifier holds at its position: an entry of a pack verified before,
        // or an anchor naming that very entry. Neither: a warning.
        var heldAt = new Dictionary<long, StoreAnchor>();
        foreach (var a in held ?? []) heldAt[a.StoreSequence] = a;
        foreach (var e in cited)
        {
            var seq = e.Entry.StoreSequence;
            var digest = EntryDigest(e.Entry);
            string? known = null;
            var isKnown = options.PriorEntries?.TryGetValue(seq, out known) == true;
            var isAnchored = heldAt.TryGetValue(seq, out var anchor);
            if (isKnown && known != digest)
                run.Fail(CitedFile, seq, "cited_entry_mismatch", "differs from the entry a prior pack holds");
            else if (isAnchored && anchor!.EntryDigest != digest)
                run.Fail(CitedFile, seq, "cited_entry_mismatch", $"differs from held anchor {anchor.AnchorSequence}");
            else if (!isKnown && !isAnchored)
                run.Warn(CitedFile, seq, "cited_entry_unanchored", "signed, but neither a prior pack nor a held anchor covers this entry");
        }

        // 4. Executor keys: keys supplied out of band, and registry records the NA signed (which win).
        var keys = new Dictionary<string, ExecutorKeyRecord>(StringComparer.Ordinal);
        foreach (var key in options.ExecutorKeys) keys[key.KeyId] = key;
        foreach (var (id, key) in RegistryKeys(cited.Concat(entries).ToList(), history)) keys[id] = key;
        var verification = VerifyEvidenceEvents(entries, new EvidenceVerifyOptions
        {
            KeyHistory = history,
            ExecutorKeys = keys.Values.ToList(),
            Contiguous = true,
            Checkpoint = startCheckpoint,
            Prior = cited,
        });
        string FileOf(long? seq) => seq is { } s2 && s2 < fromSequence ? CitedFile : EntriesFile;
        foreach (var f in verification.Failures) run.Fail(FileOf(f.StoreSequence), f.StoreSequence, f.Reason, f.Detail);
        foreach (var w in verification.Warnings ?? []) run.Warn(FileOf(w.StoreSequence), w.StoreSequence, w.Reason, w.Detail);
        foreach (var (name, count) in VerificationCounts(verification)) result.Counts[name] = count;

        // 5. What the records cite outside the store.
        CheckCitedTrust(files, entries, cited, fromSequence, history, run);

        // 6. The report renders from the pack's own files.
        CheckReport(files, manifest, entries, run);

        // 7. The period the pack covers: from its start anchor (or the retention checkpoint it starts after, or its first
        // entry, at the start of the store) to its end anchor.
        long coveredFrom;
        if (startAnchor is not null) coveredFrom = MicrosOf(startAnchor.AnchoredAt);
        else if (startCheckpoint is not null)
        {
            using var cp = JsonDocument.Parse(startCheckpoint);
            coveredFrom = MicrosOf(Str(cp.RootElement, "created_at")!);
        }
        else coveredFrom = MicrosOf(head.Entry.RecordedAt);
        var coveredUntil = end is not null ? MicrosOf(end.AnchoredAt) : MicrosOf(tail.Entry.RecordedAt);
        var period = new PackPeriod
        {
            From = Iso(coveredFrom),
            Until = Iso(coveredUntil),
            RequestedFrom = manifest.PeriodFrom is { Length: > 0 } requestedFrom ? Iso(requestedFrom) : null,
            RequestedUntil = manifest.PeriodUntil is { Length: > 0 } requestedUntil ? Iso(requestedUntil) : null,
        };
        if (options.Period is { } wanted)
        {
            var (wantedFrom, wantedUntil) = (wanted.From.UtcTicks / 10, wanted.Until.UtcTicks / 10);
            period = period with { AskedFrom = Iso(wantedFrom), AskedUntil = Iso(wantedUntil) };
            if (wantedFrom < coveredFrom || wantedUntil > coveredUntil)
                run.Fail(null, null, "pack_period_not_covered", $"the pack covers {period.From} to {period.Until}");
        }
        result.Period = period;

        // 8. Completeness.
        if (options.Changes is { } changes)
        {
            var report = CheckCompleteness(changes, entries, cited, new CompletenessOptions
            {
                From = Instant(coveredFrom),
                To = Instant(coveredUntil),
                Tolerance = options.Tolerance,
            });
            result.Completeness = report;
            static string Dash(string? value) => value is { Length: > 0 } ? value : "-";
            foreach (var item in report.UnmatchedChanges)
                run.Fail("changes", null, "change_unmatched",
                    $"{Dash(item.Source)} {Dash(item.SourceEventId)} {item.ResourceId} {item.Action} {item.ChangedAt}");
            if (report.OutsidePeriod.Count > 0)
                run.Warn("changes", null, "changes_outside_period",
                    $"{report.OutsidePeriod.Count} change(s) dated outside the period the pack covers ({period.From} to {period.Until})");
            foreach (var item in report.DuplicateChanges)
                run.Warn("changes", null, "change_duplicate", $"change {item.Index} repeats {item.Source} {item.SourceEventId}");
        }
        return result;
    }

    /// <summary>The counts of an export's verification, by the names the reference gives them; the optional ones when not 0.</summary>
    private static IEnumerable<(string, long)> VerificationCounts(EvidenceStoreVerification v)
    {
        yield return ("checked_entries", v.CheckedEntries);
        yield return ("decisions", v.Decisions);
        yield return ("executions", v.Executions);
        foreach (var (name, count) in new (string, long)[]
                 {
                     ("observations", v.Observations), ("break_glass", v.BreakGlass), ("judgements", v.Judgements),
                     ("quarantined", v.Quarantined), ("remediations", v.Remediations), ("reviews", v.Reviews),
                     ("key_successions", v.KeySuccessions), ("archives", v.Archives), ("grants", v.Grants),
                     ("grant_executions", v.GrantExecutions), ("grant_revocations", v.GrantRevocations),
                 })
            if (count > 0) yield return (name, count);
    }

    /// <summary><c>report.md</c> is the report <see cref="RenderReport"/> writes from the pack's own files (<c>report_mismatch</c>).</summary>
    private static void CheckReport(IReadOnlyDictionary<string, byte[]> files, AuditPackManifest manifest, IReadOnlyList<EvidenceEvent> entries, PackRun run)
    {
        JsonElement checks, states;
        string claimed;
        try
        {
            checks = PackJson(files.GetValueOrDefault(ChecksFile));
            states = PackJson(files.GetValueOrDefault(StatesFile));
            claimed = PackText(files.GetValueOrDefault(ReportFile));
        }
        catch (Exception e) when (e is InvalidDataException or StrictJsonException or DecoderFallbackException)
        {
            run.Fail(ReportFile, null, "report_mismatch", $"cannot be rendered: {e.Message}");
            return;
        }
        if (checks.ValueKind != JsonValueKind.Object || states.ValueKind != JsonValueKind.Object)
        {
            run.Fail(ReportFile, null, "report_mismatch", "checks.json and states.json are JSON objects");
            return;
        }
        var expected = RenderReport(ReportHeaderOf(manifest), entries, checks,
            IsObject(states, "states", out var s) ? s : null, IsObject(states, "notices", out var n) ? n : null);
        if (expected == claimed) return;
        var (ours, theirs) = (expected.Split('\n'), claimed.Split('\n'));
        for (var i = 0; i < Math.Max(ours.Length, theirs.Length); i++)
        {
            var mine = i < ours.Length ? ours[i] : null;
            var pack = i < theirs.Length ? theirs[i] : null;
            if (mine != pack) run.Result.ReportDifferences.Add(new ReportDifference { Line = i + 1, Pack = pack, Verifier = mine });
        }
        run.Fail(ReportFile, null, "report_mismatch", $"{run.Result.ReportDifferences.Count} line(s) differ from the report the pack renders to");
    }

    /// <summary>Policies, attestations, treaties and feeds the records rest on (outside the store), each checked as received.</summary>
    private static void CheckCitedTrust(IReadOnlyDictionary<string, byte[]> files, IReadOnlyList<EvidenceEvent> entries, IReadOnlyList<EvidenceEvent> cited,
        long fromSequence, NAKeyHistory history, PackRun run)
    {
        List<JsonElement> policies, attestations, treaties, feeds;
        var agreements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            List<JsonElement> Read(string path, Func<JsonElement, bool> valid, string? model = null)
            {
                var items = PackJsonLines(files.GetValueOrDefault(path));
                if (!items.All(item => valid(item) && (model is null || StrictFields.UnknownFields(model, item).Count == 0)))
                    throw new InvalidDataException($"{path}: a record is not in its model's shape");
                return items;
            }
            policies = Read(PoliciesFile, Shapes.PolicyRecord, "BoundaryPolicy");
            attestations = Read(AttestationsFile, Shapes.AttestationRecord);
            treaties = Read(TreatiesFile, Shapes.Treaty);
            feeds = Read(FeedsFile, Shapes.Feed);
            // v1.6.0: optional; each agreement as received, by its ID.
            if (files.TryGetValue(AgreementsFile, out var held))
                foreach (var raw in PackJsonLines(held))
                    if (raw.ValueKind == JsonValueKind.Object) agreements[PythonStr(Member(raw, "agreement_id"))] = raw;
        }
        catch (Exception e) when (e is InvalidDataException or StrictJsonException or DecoderFallbackException)
        {
            run.Fail(PoliciesFile, null, "cited_trust_invalid", e.Message);
            return;
        }
        static string Named(string reason, string invalid) => reason == "invalid_signature" ? invalid : reason;
        var byVersion = new Dictionary<(string?, long?), JsonElement>();
        foreach (var policy in policies)
        {
            if (ReceivedSignatureReason("BoundaryPolicy", policy, history, Str(policy, "issued_at")!) is { } reason)
                run.Fail(PoliciesFile, null, Named(reason, "policy_invalid_signature"), $"{Str(policy, "policy_id")} v{PayloadInteger(policy, "version")}");
            byVersion[(Str(policy, "policy_id"), PayloadInteger(policy, "version"))] = policy;
        }
        var own = history.SovereignId;
        var issuerKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var treaty in treaties)
        {
            if (ReceivedSignatureReason("RecognitionTreaty", treaty, history, Str(treaty, "issued_at")!) is { } reason)
            {
                run.Fail(TreatiesFile, null, Named(reason, "treaty_invalid_signature"), Str(treaty, "treaty_id")!);
                continue;
            }
            var subject = Str(treaty, "subject_sovereign_id")!;
            if (!issuerKeys.TryGetValue(subject, out var list)) issuerKeys[subject] = list = [];
            list.AddRange(treaty.GetProperty("subject_public_keys").EnumerateArray().Select(k => k.GetString()!));
        }
        IReadOnlyList<string> KeysOf(string sovereign) => issuerKeys.TryGetValue(sovereign, out var list) ? list : [];
        var byAttestation = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attestation in attestations)
        {
            var issuer = Str(attestation, "issuer_sovereign_id")!;
            var reason = issuer == own
                ? ReceivedSignatureReason("MembershipAttestation", attestation, history, Str(attestation, "issued_at")!)
                : ReceivedBy("MembershipAttestation", attestation, KeysOf(issuer));
            if (reason is not null) run.Fail(AttestationsFile, null, Named(reason, "attestation_invalid_signature"), Str(attestation, "attestation_id")!);
            byAttestation[Str(attestation, "attestation_id")!] = attestation;
        }
        foreach (var feed in feeds)
            if (ReceivedBy("SovereignRevocationFeed", feed, KeysOf(Str(feed, "issuer_sovereign_id")!)) is { } reason)
                run.Fail(FeedsFile, null, Named(reason, "feed_invalid_signature"), Str(feed, "feed_id")!);

        foreach (var e in cited.Concat(entries))
        {
            var kind = e.Entry.EntryKind;
            var seq = e.Entry.StoreSequence;
            var where = seq < fromSequence ? CitedFile : EntriesFile;
            JsonElement? binding = null;
            if (kind == "decision")
            {
                if (!e.Payload.TryGetProperty("decision", out var decision) || !Shapes.Decision(decision)) continue;
                binding = Member(decision, "policy_binding");
                if (IsObject(decision, "attestation_binding", out var attestationBinding) && Truthy(Member(attestationBinding, "attestation_digest")))
                {
                    var id = Str(attestationBinding, "attestation_id") ?? "";
                    if (!byAttestation.TryGetValue(id, out var bound))
                    {
                        run.Fail(where, seq, "cited_attestation_missing", id);
                    }
                    else
                    {
                        // As of the decision's own time: its expiry since then says nothing against it.
                        var verdict = VerifyBoundaryDecision(decision.GetRawText(), new DecisionVerifyOptions
                        {
                            KeyHistory = history,
                            ExpectedAttestation = bound.GetRawText(),
                            AtRecordTime = true,
                        });
                        if (verdict.Reason is "attestation_binding_mismatch" or "attestation_binding_missing")
                            run.Fail(where, seq, "cited_attestation_mismatch", Str(bound, "attestation_id")!);
                    }
                }
            }
            else if (kind == "judgement")
            {
                if (!Shapes.Judgement(e.Payload)) continue;
                binding = Member(e.Payload, "policy_binding");
            }
            else if (kind == "grant")
            {
                // v1.6.0: the policy versions a grant is bound to, and the attestation or agreement it rests on.
                if (!Shapes.Grant(e.Payload)) continue;
                var grant = e.Payload;
                binding = Member(grant, "policy_binding");
                var basisId = Str(grant, "basis_id")!;
                if (Str(grant, "basis_kind") == "attestation")
                {
                    if (!byAttestation.TryGetValue(basisId, out var bound)) run.Fail(where, seq, "cited_attestation_missing", basisId);
                    else if (AttestationDigest(bound.GetRawText()) != Str(grant, "basis_digest")) run.Fail(where, seq, "cited_attestation_mismatch", basisId);
                }
                else if (AgreementProblem(grant, agreements.TryGetValue(basisId, out var agreement) ? agreement : null, history, issuerKeys) is { } problem)
                {
                    run.Fail(problem == "cited_agreement_missing" ? where : AgreementsFile, seq, problem, basisId);
                }
            }
            if (binding is not { ValueKind: JsonValueKind.Object } held || !IsArray(held, "policies", out var applied)) continue;
            var refs = applied.EnumerateArray()
                .Select(a => (Str(a, "policy_id") ?? "", a.TryGetProperty("version", out var v) ? v.GetRawText() : "null", Str(a, "policy_digest") ?? ""))
                .ToList();
            if (PolicySetDigest(refs) != Str(held, "policy_set_digest")) run.Fail(where, seq, "cited_policy_mismatch", "policy_set_digest");
            foreach (var a in applied.EnumerateArray())
            {
                var name = $"{Str(a, "policy_id")} v{PayloadInteger(a, "version")}";
                if (!byVersion.TryGetValue((Str(a, "policy_id"), PayloadInteger(a, "version")), out var policy))
                    run.Fail(where, seq, "cited_policy_missing", name);
                else if (PolicyDigest(policy.GetRawText()) != Str(a, "policy_digest"))
                    run.Fail(where, seq, "cited_policy_mismatch", name);
            }
        }
    }

    private static bool IsArray(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;

    /// <summary>
    /// Why the agreement a grant rests on does not hold up in a pack, or null (v1.6.0): <c>cited_agreement_missing</c>
    /// when the pack does not hold it; <c>cited_agreement_mismatch</c> when it is not the one the grant's
    /// <c>basis_digest</c> names, or its parties are not the grant's; <c>agreement_invalid_signature</c> when its two
    /// parties did not sign it, as received, with keys the pack vouches for (the NA's key valid at
    /// <c>agreed_terms.valid_from</c> for its own sovereign, a treaty's subject keys for another; an agreement the NA
    /// offered and accepted itself carries the NA's signature for both, which vouches only while the pack carries a treaty
    /// for the responder).
    /// </summary>
    private static string? AgreementProblem(JsonElement grant, JsonElement? raw, NAKeyHistory history, Dictionary<string, List<string>> treatyKeys)
    {
        if (raw is not { } agreement) return "cited_agreement_missing";
        if (!(agreement.ValueKind == JsonValueKind.Object && Str(agreement, "agreement_id") is not null
              && Str(agreement, "offerer_sovereign_id") is not null && Str(agreement, "responder_sovereign_id") is not null
              && IsObject(agreement, "agreed_terms", out var terms) && Str(terms, "valid_from") is { } validFrom && IsTimestamp(validFrom)
              && IsArray(agreement, "signatures", out _)))
            return "cited_agreement_mismatch";
        var digest = Sha256Hex(Canonical.OfKeys(agreement, AgreementCanonicalKeys));
        var (offerer, responder) = (Str(agreement, "offerer_sovereign_id")!, Str(agreement, "responder_sovereign_id")!);
        if (digest != Str(grant, "basis_digest") || responder != Str(grant, "requester_sovereign_id") || offerer != Str(grant, "provider_sovereign_id"))
            return "cited_agreement_mismatch";
        var own = history.SovereignId;
        IReadOnlyList<string> naKeys = TryReadTimestamp(validFrom, out var at) ? history.KeysAt(Instant(at)) : [];
        IReadOnlyList<string> KeysOf(string party) => party == own
            ? naKeys
            : (treatyKeys.TryGetValue(party, out var list) ? list : []).Where(k => !naKeys.Contains(k, StringComparer.Ordinal)).ToList();
        var pairs = new List<(IReadOnlyList<string>, IReadOnlyList<string>)> { (KeysOf(offerer), KeysOf(responder)) };
        // The NA-offered-and-accepted exception holds, as on the NA's routes, only while the responder holds a treaty from
        // this NA (one the pack carries).
        if (offerer == own && treatyKeys.TryGetValue(responder, out var treaty) && treaty.Count > 0) pairs.Add((naKeys, naKeys));
        foreach (var (offererKeys, responderKeys) in pairs)
            if (offererKeys.Count > 0 && responderKeys.Count > 0 && VerifyAgreement(agreement.GetRawText(), offererKeys, responderKeys).Accepted)
                return null;
        return "agreement_invalid_signature";
    }

    // ── The report ──────────────────────────────────────────────────────────

    /// <summary>
    /// One value of the report, on one line, as the reference writes it: a backslash is written <c>\\</c>, a <c>|</c>
    /// <c>\|</c>; a line feed, carriage return or tab <c>\n</c>, <c>\r</c>, <c>\t</c>; any other control character
    /// (U+0000 to U+001F, U+007F to U+009F) and U+2028, U+2029 <c>\uXXXX</c> (upper-case hex). A string is written as
    /// itself, an integer in decimal (an integral number below 2^53 too), a boolean <c>true</c> or <c>false</c>, an
    /// absent value empty, anything else as canonical JSON with non-ASCII text kept.
    /// </summary>
    /// <param name="value">The value, as read from JSON; null for absent.</param>
    public static string ReportCell(JsonElement? value)
    {
        var raw = value switch
        {
            null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
            { ValueKind: JsonValueKind.True } => "true",
            { ValueKind: JsonValueKind.False } => "false",
            { ValueKind: JsonValueKind.String } v => v.GetString()!,
            { ValueKind: JsonValueKind.Number } v => NumberCell(v),
            { } v => Canonical.OfUnescaped(v),
        };
        return EscapeCell(raw);
    }

    private static string NumberCell(JsonElement v)
    {
        var text = v.GetRawText();
        // A Python int, in decimal (-0 is 0).
        if (text.All(c => c is >= '0' and <= '9' or '-')) return text == "-0" ? "0" : text;
        // Strict JSON holds finite numbers only.
        var d = v.GetDouble();
        return double.IsInteger(d) && Math.Abs(d) < 9007199254740992d
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : Canonical.NumberLiteral(text);
    }

    private static string EscapeCell(string raw)
    {
        // By UTF-16 unit: nothing escaped lies outside the BMP, and a lone surrogate is kept as the reference keeps it.
        var sb = new StringBuilder();
        foreach (var code in raw)
        {
            switch (code)
            {
                case '\\': sb.Append(@"\\"); break;
                case '|': sb.Append(@"\|"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (code < 0x20 || code is >= '\x7f' and <= '\x9f' or '\u2028' or '\u2029')
                        sb.Append(@"\u").Append(((int)code).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(code);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A cell of a row: a value read from JSON, a string or a store position.</summary>
    private static string Cell(object? value) => value switch
    {
        null => "",
        JsonElement e => ReportCell(e),
        string s => EscapeCell(s),
        _ => ((long)value).ToString(CultureInfo.InvariantCulture),
    };

    private static List<string> Table(string[] headers, List<object?[]> rows)
    {
        if (rows.Count == 0) return ["None.", ""];
        var lines = new List<string> { $"| {string.Join(" | ", headers)} |", "|" + string.Join("|", headers.Select(_ => "---")) + "|" };
        lines.AddRange(rows.Select(row => "| " + string.Join(" | ", row.Select(Cell)) + " |"));
        lines.Add("");
        return lines;
    }

    /// <summary>What <see cref="RenderReport"/> reads of a manifest: its times in UTC as the reference prints them.</summary>
    /// <param name="manifest">The manifest.</param>
    public static ReportHeader ReportHeaderOf(AuditPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new ReportHeader
        {
            PackId = manifest.PackId,
            SovereignId = manifest.SovereignId,
            FromSequence = manifest.FromSequence,
            ThroughSequence = manifest.ThroughSequence,
            StartAnchorSequence = manifest.StartAnchorSequence,
            EndAnchorSequence = manifest.EndAnchorSequence,
            CreatedAt = Iso(manifest.CreatedAt),
            RequestedBy = manifest.RequestedBy,
            PeriodFrom = manifest.PeriodFrom is { Length: > 0 } from ? Iso(from) : null,
            PeriodUntil = manifest.PeriodUntil is { Length: > 0 } until ? Iso(until) : null,
        };
    }

    /// <summary>
    /// <c>report.md</c>, byte for byte as the reference renders it from the manifest, the entries, <c>checks.json</c> and
    /// <c>states.json</c> (its <c>states</c> and <c>notices</c>).
    /// </summary>
    /// <param name="header">The manifest, as <see cref="ReportHeaderOf"/> reads it.</param>
    /// <param name="entries">The range's entries.</param>
    /// <param name="checks">The NA's own verification at pack time (<c>checks.json</c>).</param>
    /// <param name="states">The NA's remediation states by subject; null for none.</param>
    /// <param name="notices">When each actor was told; null for none.</param>
    public static string RenderReport(ReportHeader header, IReadOnlyList<EvidenceEvent> entries, JsonElement checks, JsonElement? states = null,
        JsonElement? notices = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);
        var judgements = new Dictionary<(string, string), JsonElement>();
        foreach (var e in entries)
            if (e.Entry.EntryKind == "judgement")
                judgements[(PythonStr(Member(e.Payload, "subject_kind")), PythonStr(Member(e.Payload, "subject_id")))] = e.Payload;
        var lines = new List<string>
        {
            $"# Audit pack {Cell(header.PackId)}", "",
            $"- Sovereign: {Cell(header.SovereignId)}",
            $"- Range: store entries {Cell(header.FromSequence)} to {Cell(header.ThroughSequence)}, ending at anchor {Cell(header.EndAnchorSequence)}"
                + (header.StartAnchorSequence is { } start && start != 0 ? $", starting after anchor {Cell(start)}" : ""),
            $"- Created: {Cell(header.CreatedAt)}" + (header.RequestedBy is { Length: > 0 } by ? $" for {Cell(by)}" : ""),
        };
        if (header.PeriodFrom is { Length: > 0 } || header.PeriodUntil is { Length: > 0 })
            lines.Add($"- Asked for: {Cell(header.PeriodFrom is { Length: > 0 } f ? f : "the start")} to {Cell(header.PeriodUntil is { Length: > 0 } u ? u : "the last anchor")}");
        lines.AddRange(
        [
            "",
            "Verify it with `genesis-mesh audit verify <dir> --anchor <root public key> --known-anchors <anchors>`, "
                + "from the root key and the anchors you hold. The remediation states and notices below are the "
                + "Network Authority's operational records at pack time (states.json), not signed records.", "",
            "## Checks", "",
            $"Verified by the Network Authority when it wrote the pack, against its own anchors: **{(Truthy(Member(checks, "verified")) ? "yes" : "no")}**.", "",
        ]);
        var failures = IsArray(checks, "failures", out var listed) ? listed.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).ToList() : [];
        lines.AddRange(Table(["Reason", "File", "Entry", "Detail"],
            failures.Select(f => new object?[] { Member(f, "reason"), Member(f, "file"), Member(f, "store_sequence"), Member(f, "detail") }).ToList()));
        var counts = IsObject(checks, "counts", out var c) && Truthy(c) ? c : default;
        lines.AddRange(["## Totals", ""]);
        lines.AddRange(Table(["Records", "Count"], counts.ValueKind == JsonValueKind.Object
            ? counts.EnumerateObject().Where(p => Truthy(p.Value)).OrderBy(p => p.Name, Canonical.CodePointComparer.Instance)
                .Select(p => new object?[] { p.Name, p.Value }).ToList()
            : []));

        var changes = new List<object?[]>();
        var denials = new List<object?[]>();
        var remediations = new List<object?[]>();
        var reviews = new List<object?[]>();
        var breakGlass = new List<object?[]>();
        var quarantined = new List<object?[]>();
        var others = new List<object?[]>();
        var grants = new List<object?[]>();
        var grantRevocations = new List<object?[]>();
        JsonElement? StateOf(JsonElement? subject) =>
            states is { ValueKind: JsonValueKind.Object } held && held.TryGetProperty(PythonStr(subject), out var value) && value.ValueKind == JsonValueKind.Object
                ? Member(value, "state")
                : null;
        static object? Or(params object?[] values)
        {
            for (var i = 0; i < values.Length - 1; i++)
                if (values[i] is JsonElement e ? Truthy(e) : values[i] is string { Length: > 0 }) return values[i];
            return values[^1];
        }
        foreach (var e in entries)
        {
            var p = e.Payload;
            var kind = e.Entry.EntryKind;
            long seq = e.Entry.StoreSequence;
            JsonElement? P(string name) => Member(p, name);
            switch (kind)
            {
                case "execution":
                    changes.Add([seq, kind, P("resource_id"), P("resource_action"), P("executed_at"), "prior_decision",
                        Or(StateOf(P("decision_id")), "recorded"), P("executor_sovereign_id")]);
                    break;
                case "grant_execution":
                    // v1.6.0: a change made under a grant, governed by it.
                    changes.Add([seq, kind, P("resource_id"), P("resource_action"), P("executed_at"), "grant", "recorded", P("executor_sovereign_id")]);
                    break;
                case "grant":
                    grants.Add([seq, P("grant_id"), P("bearer_sovereign_id"), P("capabilities"), P("valid_from"), P("valid_until"), P("max_uses")]);
                    break;
                case "grant_revocation":
                    grantRevocations.Add([seq, P("grant_id"), P("reason"), P("revoked_at")]);
                    break;
                case "observation":
                case "break_glass":
                    {
                        var recordId = PythonStr(Or(P("observation_id"), P("break_glass_id")) as JsonElement?);
                        var hasJudgement = judgements.TryGetValue((kind, recordId), out var judgement);
                        // An empty judgement is one, but names no state or governance (Python's truthiness).
                        var named = hasJudgement && Truthy(judgement);
                        object? state = named ? StateOf(Member(judgement, "judgement_id")) : null;
                        if (state is null or JsonElement { ValueKind: JsonValueKind.Null })
                        {
                            state = !hasJudgement ? "observed"
                                : Str(judgement, "governed_by") is "prior_decision" or "grant" && Str(judgement, "verdict") != "deny" ? "matched"
                                : PythonStr(Member(judgement, "verdict")) switch { "allow" => "judged_allowed", "deny" => "judged_denied", _ => "indeterminate" };
                        }
                        changes.Add([seq, kind, P("resource_id"), Or(P("action"), P("resource_action")),
                            Or(P("changed_at"), P("changed_not_after"), P("executed_at")), named ? Member(judgement, "governed_by") : null, state, P("actor")]);
                        if (kind == "break_glass")
                            breakGlass.Add([seq, P("resource_id"), P("capability"), P("executed_at"), P("evaluation_failure"), P("justification")]);
                        break;
                    }
                case "decision":
                    {
                        var decision = Truthy(P("decision")) ? p.GetProperty("decision") : default;
                        if (Member(decision, "authorized") is { ValueKind: JsonValueKind.False })
                            denials.Add([seq, "decision", Member(decision, "decision_id"), Member(decision, "denial_reason")]);
                        break;
                    }
                case "judgement" when Str(p, "verdict") is "deny" or "indeterminate":
                    {
                        var reason = Or(P("reason"), "");
                        var text = reason is string s ? s : reason is JsonElement { ValueKind: JsonValueKind.String } r ? r.GetString()! : PythonStr(reason as JsonElement?);
                        denials.Add([seq, "judgement", P("judgement_id"), $"{PythonStr(P("verdict"))}: {text}"]);
                        break;
                    }
                case "remediation":
                    remediations.Add([seq, P("subject_kind"), P("subject_id"), P("outcome"), P("steps"), P("remediated_at"), P("executor_sovereign_id")]);
                    break;
                case "review":
                    reviews.Add([seq, P("subject_id"), P("verdict"), P("requested_by"), P("requester_holder"), P("decided_by"),
                        P("decider_holder"), P("decided_at")]);
                    break;
                case "quarantine":
                    quarantined.Add([seq, P("record_kind"), P("resource_id"), P("rejection_code"), P("quarantined_at")]);
                    break;
                case "key_succession":
                case "archive":
                    others.Add([seq, kind, Or(P("succession_id"), P("archive_id")), Or(P("effective_at"), P("archived_at"))]);
                    break;
            }
        }
        var noticeRows = notices is { ValueKind: JsonValueKind.Object } given
            ? given.EnumerateObject().Where(n => n.Value.ValueKind == JsonValueKind.Object).OrderBy(n => n.Name, Canonical.CodePointComparer.Instance)
                .Select(n => new object?[] { n.Name, Member(n.Value, "notified_at"), Member(n.Value, "notified_by") }).ToList()
            : [];
        var sections = new List<(string, string[], List<object?[]>)>
        {
            ("Changes", ["Entry", "Kind", "Resource", "Action", "Changed at", "Governed by", "State", "Actor"], changes),
            ("Denials", ["Entry", "Record", "ID", "Reason"], denials),
            ("Remediations", ["Entry", "Subject kind", "Subject", "Outcome", "Steps", "At", "Executor"], remediations),
            ("Reviews and deciders", ["Entry", "Subject", "Verdict", "Requested by", "Requester holder", "Decided by", "Decider holder", "Decided at"], reviews),
            ("Notices", ["Subject", "Notified at", "By"], noticeRows),
            ("Break-glass uses", ["Entry", "Resource", "Capability", "Executed at", "Failure", "Justification"], breakGlass),
            ("Quarantined records", ["Entry", "Record kind", "Resource", "Rejection", "At"], quarantined),
            ("Key successions and archives", ["Entry", "Kind", "ID", "At"], others),
        };
        // v1.6.0: grants and their revocations, only in a pack that holds them (a pack without them renders as in 1.5).
        if (grants.Count > 0) sections.Add(("Grants", ["Entry", "Grant", "Bearer", "Capabilities", "Valid from", "Valid until", "Budget"], grants));
        if (grantRevocations.Count > 0) sections.Add(("Grant revocations", ["Entry", "Grant", "Reason", "Revoked at"], grantRevocations));
        foreach (var (title, headers, rows) in sections)
        {
            lines.AddRange([$"## {title}", ""]);
            lines.AddRange(Table(headers, rows));
        }
        return string.Join("\n", lines).TrimEnd('\n') + "\n";
    }
}
