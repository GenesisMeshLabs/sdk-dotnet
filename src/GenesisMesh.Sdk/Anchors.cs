using System.Text.Json;

namespace GenesisMesh;

/// <summary>Options for <see cref="OfflineVerifier.VerifyStoreAnchors"/>.</summary>
public sealed class AnchorVerifyOptions
{
    /// <summary>The NA's keys (base64 Ed25519), valid at every time. Or give <see cref="KeyHistory"/>.</summary>
    public IReadOnlyList<string> NAPublicKeys { get; init; } = [];

    /// <summary>
    /// The NA's verified key history (<see cref="OfflineVerifier.VerifyKeyHistoryResponse"/>), instead of
    /// <see cref="NAPublicKeys"/>: each anchor must be signed by the key valid at its <c>anchored_at</c>
    /// (<c>na_key_not_valid_at_time</c> for another key of the history).
    /// </summary>
    public NAKeyHistory? KeyHistory { get; init; }

    /// <summary>
    /// True to skip the signatures and check everything else (a check of the store's database without the NA's key).
    /// Without it, <see cref="NAPublicKeys"/> or <see cref="KeyHistory"/> is required.
    /// </summary>
    public bool SkipSignatures { get; init; }

    /// <summary>Whose anchors these are, the genesis block's <c>network_name</c> (<c>anchor_sovereign_mismatch</c>); null to not check.</summary>
    public string? SovereignId { get; init; }
}

/// <summary>Options for <see cref="OfflineVerifier.CheckEventsAgainstAnchors"/>.</summary>
public sealed class AnchorCheckOptions
{
    /// <summary>A deliberate slice of the store: neither end of the run is checked.</summary>
    public bool Partial { get; init; }

    /// <summary>
    /// The run may start right after a held anchor whose digest its first entry names: an export since an anchored
    /// position (v1.3.1), which says nothing of the entries before it.
    /// </summary>
    public bool FromAnchor { get; init; }
}

public static partial class OfflineVerifier
{
    // Store anchors (v1.2.0 in the core, offline here from 1.9.0): NA-signed statements of the evidence store's head.
    // The store chain is hashed, not signed: whoever can write the database can remove entries and rebuild the links.
    // An anchor signs the head's store_sequence and entry digest, and anchors form their own chain, so an auditor who
    // keeps copies of them outside the operator's reach checks an export or an audit pack against them. A port of the
    // reference's verify_store_anchors and check_events_against_anchors, and of the TypeScript SDK's anchors.ts, with
    // their reason codes.

    /// <summary>The form the NA signs: every field but the signature, an absent previous digest left out.</summary>
    /// <param name="anchor">The anchor.</param>
    public static string AnchorCanonical(StoreAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return ReceivedCanonical("StoreAnchor", JsonSerializer.SerializeToElement(anchor));
    }

    /// <summary>SHA-256 hex of the anchor's signed form; the next anchor links to it.</summary>
    /// <param name="anchor">The anchor.</param>
    public static string AnchorDigest(StoreAnchor anchor) => Sha256Hex(AnchorCanonical(anchor));

    /// <summary>
    /// Store anchors read from their JSON (an array, as <c>GET /admin/evidence/anchors</c> serves them), each well formed
    /// as the reference reads one: positive positions, a UTC <c>anchored_at</c>, and no field the anchor does not have.
    /// </summary>
    /// <param name="anchorsJson">A JSON array of anchors.</param>
    /// <exception cref="StrictJsonException"><paramref name="anchorsJson"/> is not strict JSON.</exception>
    /// <exception cref="FormatException">It is not an array, or an item is not a well-formed anchor.</exception>
    public static IReadOnlyList<StoreAnchor> ParseAnchors(string anchorsJson)
    {
        using var doc = StrictJsonReader.Parse(anchorsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("store anchors are a JSON array");
        var anchors = new List<StoreAnchor>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!Shapes.Anchor(item)) throw new FormatException("invalid store anchor");
            anchors.Add(ReadPayload<StoreAnchor>(item)!);
        }
        return anchors;
    }

    /// <summary>
    /// Verifies a run of anchors in <c>anchor_sequence</c> order, as the reference's <c>verify_store_anchors</c> does:
    /// each signature (<c>anchor_invalid_signature</c>; with a key history, <c>na_key_not_valid_at_time</c> for a key
    /// not valid at its <c>anchored_at</c>), the sovereign (<c>anchor_sovereign_mismatch</c>), the anchor chain
    /// (<c>anchor_sequence_gap</c>, <c>anchor_chain_break</c>) and heads that move forward (<c>anchor_not_increasing</c>).
    /// The run need not start at 1, as an auditor may hold only recent anchors; one that starts at 1 starts with no
    /// previous anchor.
    /// </summary>
    /// <param name="anchors">The anchors, in order.</param>
    /// <param name="options">The NA's keys, or none checked; whose anchors these are.</param>
    /// <exception cref="ArgumentNullException"><paramref name="anchors"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// An anchor is null or not well formed (as <see cref="ParseAnchors"/> reads one); or neither keys nor a key history
    /// are given without <see cref="AnchorVerifyOptions.SkipSignatures"/>, or both are.
    /// </exception>
    public static AnchorVerification VerifyStoreAnchors(IReadOnlyList<StoreAnchor> anchors, AnchorVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.NAPublicKeys, nameof(options));
        if (options.KeyHistory is not null && options.NAPublicKeys.Count > 0)
            throw new ArgumentException("pass NAPublicKeys or KeyHistory, not both", nameof(options));
        if (!options.SkipSignatures && options.KeyHistory is null && options.NAPublicKeys.Count == 0)
            throw new ArgumentException("pass NAPublicKeys or KeyHistory, or SkipSignatures to check no signature", nameof(options));
        var records = anchors.Select(a => a is not null && JsonSerializer.SerializeToElement(a) is var e && Shapes.Anchor(e)
            ? e
            : throw new ArgumentException("invalid store anchor", nameof(anchors))).ToList();
        var result = new AnchorVerification { Verified = true };
        void Fail(long n, string reason, string detail = "")
        {
            result.Verified = false;
            result.Failures.Add(new AnchorFailure { AnchorSequence = n, Reason = reason, Detail = detail });
        }
        var all = options.KeyHistory?.AllKeys() ?? options.NAPublicKeys;
        StoreAnchor? previous = null;
        long previousAt = 0;
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            var n = anchor.AnchorSequence;
            result.CheckedAnchors++;
            result.FirstAnchorSequence ??= n;
            result.LastAnchorSequence = n;
            TryReadTimestamp(anchor.AnchoredAt, out var at);
            if (!options.SkipSignatures)
            {
                var valid = options.KeyHistory?.KeysAt(Instant(at)) ?? options.NAPublicKeys;
                if (!SignedAsReceived("StoreAnchor", records[i], valid))
                {
                    var others = all.Where(k => !valid.Contains(k, StringComparer.Ordinal)).ToList();
                    Fail(n, others.Count > 0 && SignedAsReceived("StoreAnchor", records[i], others) ? "na_key_not_valid_at_time" : "anchor_invalid_signature");
                }
            }
            if (options.SovereignId is { } sovereign && anchor.SovereignId != sovereign) Fail(n, "anchor_sovereign_mismatch", anchor.SovereignId);
            if (previous is null)
            {
                if (n == 1 && anchor.PreviousAnchorDigest is not null) Fail(n, "anchor_chain_break", "the first anchor names a previous anchor");
            }
            else
            {
                if (n != previous.AnchorSequence + 1) Fail(n, "anchor_sequence_gap", $"after {previous.AnchorSequence}");
                else if (anchor.PreviousAnchorDigest != AnchorDigest(previous)) Fail(n, "anchor_chain_break");
                if (anchor.StoreSequence <= previous.StoreSequence || at < previousAt) Fail(n, "anchor_not_increasing");
            }
            result.AnchoredThroughSequence = Math.Max(result.AnchoredThroughSequence ?? 0, anchor.StoreSequence);
            previous = anchor;
            previousAt = at;
        }
        return result;
    }

    /// <summary>
    /// The retention checkpoint in a run that explains where it starts, as received: the one whose last removed entry is
    /// the one right before <paramref name="first"/>. Pass it as <see cref="EvidenceVerifyOptions.Checkpoint"/>. Null
    /// when the run holds none.
    /// </summary>
    /// <param name="events">The run's entries.</param>
    /// <param name="first">The run's first entry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> or <paramref name="first"/> is null.</exception>
    public static string? StartingCheckpoint(IReadOnlyList<EvidenceEvent> events, EvidenceEvent first)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(first);
        foreach (var e in events)
        {
            if (e?.Entry is not { EntryKind: "retention_checkpoint" } || !Shapes.Checkpoint(e.Payload)) continue;
            if (PayloadInteger(e.Payload, "removed_through_sequence") == first.Entry.StoreSequence - 1
                && Str(e.Payload, "last_removed_entry_digest") == first.Entry.PrevEntryDigest)
                return e.Payload.GetRawText();
        }
        return null;
    }

    /// <summary>
    /// Checks a contiguous run of entries against anchors held out of band, into <paramref name="result"/> (from
    /// <see cref="VerifyEvidenceEvents"/>), as the reference's <c>check_events_against_anchors</c> does. The run is tied
    /// at its start: entry 1, a retention checkpoint in the run, or with <see cref="AnchorCheckOptions.FromAnchor"/> a held
    /// anchor its first entry continues from (otherwise <c>export_not_linked_to_anchors</c>); and at its end
    /// (<c>export_ends_before_anchor</c> for an anchor after its last entry). Inside it, each anchor names its entry's
    /// digest (<c>anchor_mismatch</c>, <c>anchor_entry_missing</c>). <see cref="EvidenceStoreVerification.Anchors"/> sums
    /// it up.
    /// </summary>
    /// <param name="events">The run's entries.</param>
    /// <param name="anchors">The anchors held (verify them first, <see cref="VerifyStoreAnchors"/>).</param>
    /// <param name="result">The run's verification, which gets the failures and the summary.</param>
    /// <param name="options">A deliberate slice, or a run from an anchor; null for neither.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/>, <paramref name="anchors"/> or <paramref name="result"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="events"/> or <paramref name="anchors"/> holds a null.</exception>
    public static void CheckEventsAgainstAnchors(IReadOnlyList<EvidenceEvent> events, IReadOnlyList<StoreAnchor> anchors,
        EvidenceStoreVerification result, AnchorCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(result);
        if (events.Any(e => e?.Entry is null)) throw new ArgumentException("events holds a null", nameof(events));
        if (anchors.Any(a => a is null)) throw new ArgumentException("anchors holds a null", nameof(anchors));
        options ??= new AnchorCheckOptions();
        void Fail(long? sequence, string reason, string detail = "")
        {
            result.Verified = false;
            result.Failures.Add(new EvidenceVerificationFailure { StoreSequence = sequence, Reason = reason, Detail = detail });
        }
        var bySequence = new Dictionary<long, EvidenceEvent>();
        foreach (var e in events) bySequence[e.Entry.StoreSequence] = e;
        var ordered = events.OrderBy(e => e.Entry.StoreSequence).ToList();
        var first = ordered.Count > 0 ? ordered[0] : null;
        long? firstSequence = first?.Entry.StoreSequence;
        long? lastSequence = ordered.Count > 0 ? ordered[^1].Entry.StoreSequence : null;
        long matched = 0, before = 0, beyond = 0;
        long? anchoredThrough = null;
        var linkedStart = first is not null && (firstSequence == 1 || StartingCheckpoint(ordered, first) is not null);
        long? afterAnchor = null;
        foreach (var anchor in anchors)
        {
            var seq = anchor.StoreSequence;
            if (first is null || seq > lastSequence)
            {
                beyond++;
                continue;
            }
            if (seq == firstSequence - 1)
            {
                if (first.Entry.PrevEntryDigest == anchor.EntryDigest)
                {
                    afterAnchor = anchor.AnchorSequence;
                    linkedStart = linkedStart || options.FromAnchor;
                    matched++;
                    anchoredThrough = Math.Max(anchoredThrough ?? 0, seq);
                }
                else
                {
                    Fail(firstSequence, "anchor_mismatch", $"the run does not continue from anchor {anchor.AnchorSequence}");
                }
                continue;
            }
            if (seq < firstSequence)
            {
                before++;
                continue;
            }
            if (!bySequence.TryGetValue(seq, out var held))
            {
                Fail(seq, "anchor_entry_missing", $"anchor {anchor.AnchorSequence}");
            }
            else if (held.EntryDigest != anchor.EntryDigest || EntryDigest(held.Entry) != anchor.EntryDigest)
            {
                Fail(seq, "anchor_mismatch", $"anchor {anchor.AnchorSequence}");
            }
            else
            {
                matched++;
                anchoredThrough = Math.Max(anchoredThrough ?? 0, seq);
            }
        }
        // A held anchor commits to every entry before the one it names: a run not tied at its start leaves entries the
        // anchors prove exist unaccounted for, whether the anchors fall before the run or inside it.
        if (anchors.Count > 0 && first is not null && !linkedStart && !options.Partial)
        {
            var why = afterAnchor is { } after
                ? $"the export starts right after held anchor {after}, which proves the entries before it existed, not "
                  + "that they are kept: export from entry 1 (with the retention checkpoint that removed entries, if any), or "
                  + "verify it as an export since that anchor (FromAnchor)"
                : $"the export starts at entry {firstSequence} and holds neither entry 1 nor the retention checkpoint that removed the "
                  + $"entries before it ({before} held anchor(s) before it): export from entry 1, or include that checkpoint's entry";
            Fail(firstSequence, "export_not_linked_to_anchors", why);
        }
        if (beyond > 0 && !options.Partial) Fail(lastSequence, "export_ends_before_anchor", $"{beyond} anchor(s) after the last entry");
        var floor = anchoredThrough ?? 0;
        result.Anchors = new AnchorSummary
        {
            AnchorsChecked = anchors.Count,
            AnchorsMatched = matched,
            AnchorsBeforeExport = before,
            AnchorsAfterExport = beyond,
            LinkedStart = linkedStart,
            AnchoredThroughSequence = anchoredThrough,
            UnanchoredEntries = bySequence.Keys.Count(seq => seq > floor),
        };
    }
}
