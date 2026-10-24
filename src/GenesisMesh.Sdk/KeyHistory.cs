using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>
/// One key of an NA's key history and the times it is valid at (v1.5.0). <c>ValidFrom</c> is null for the
/// genesis key (valid from the start of the network); <c>SucceededAt</c> is the next succession's
/// <c>effective_at</c>, null while the key is current; <c>ValidTo</c> is the declared end of validity, enforced
/// for a key named by a succession only.
/// </summary>
public sealed record NAKey(
    int KeySequence, string? KeyId, string PublicKey, DateTimeOffset? ValidFrom, DateTimeOffset? SucceededAt,
    DateTimeOffset? ValidTo, bool ValidToEnforced)
{
    /// <summary>True when a record dated <paramref name="at"/> may carry this key's signature (with the hand-over).</summary>
    public bool Accepts(DateTimeOffset at) =>
        !(ValidFrom is { } start && at < start)
        && !(SucceededAt is { } succeeded && at >= succeeded + NAKeyHistory.SuccessionHandover)
        && !(ValidToEnforced && ValidTo is { } end && at >= end);

    /// <summary>
    /// The key's end (1.8.0): the earlier of its succession and its enforced <c>ValidTo</c>; null for a key with
    /// neither.
    /// </summary>
    public DateTimeOffset? End =>
        new[] { SucceededAt, ValidToEnforced ? ValidTo : null }.OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Min();
}

/// <summary>
/// An NA's keys in order (v1.5.0), from a verified genesis block and its successions
/// (<see cref="OfflineVerifier.VerifyKeyHistory"/>), or keys valid at every time (<see cref="ValidAlways"/>).
/// </summary>
public sealed class NAKeyHistory
{
    /// <summary>
    /// How long after a succession takes effect the outgoing key is still accepted (records signed just before
    /// the hand-over and stored just after it).
    /// </summary>
    public static readonly TimeSpan SuccessionHandover = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The longest lifetime of a decision the NA issues: a decision signed by a key with an end is refused when it
    /// stays valid past that end, the hand-over and this lifetime (<c>decision_outlives_key</c>), so the holder of a
    /// succeeded key cannot mint a long-lived decision dated inside its window.
    /// </summary>
    public static readonly TimeSpan MaxDecisionLifetime = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The vouching horizon's grace (1.8.0): how long past its end and the hand-over a record of a key that has
    /// ended still vouches for the present. Also the most a verifier allows: <see cref="WithVouchingGrace"/> only
    /// shortens it.
    /// </summary>
    public static readonly TimeSpan SuccessionVouchingGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// The field giving the time an NA-signed record read alone is placed at in the key history (from v1.6.0 also a
    /// grant, its revocation and a time token, read alone or nested in a use): the key that may sign it is the key
    /// valid then. In the evidence store every entry is placed at its envelope's <c>recorded_at</c> instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RecordTimeFields = new Dictionary<string, string>
    {
        ["ArchiveRecord"] = "archived_at",
        ["AuditPackManifest"] = "created_at",
        ["BoundaryDecision"] = "decision_made_at",
        ["BoundaryPolicy"] = "issued_at",
        ["CapabilityGrant"] = "issued_at",
        ["GrantRevocationRecord"] = "revoked_at",
        ["JudgementRecord"] = "judged_at",
        ["JustificationProof"] = "proof_issued_at",
        ["MembershipAttestation"] = "issued_at",
        ["QuarantineRecord"] = "quarantined_at",
        ["RecognitionTreaty"] = "issued_at",
        ["RegistryRecord"] = "effective_at",
        ["RetentionCheckpoint"] = "created_at",
        ["ReviewRecord"] = "decided_at",
        ["StoreAnchor"] = "anchored_at",
        ["TimeToken"] = "issued_at",
    };

    public NAKeyHistory(string sovereignId, string? rootPublicKey, IReadOnlyList<NAKey> keys)
        : this(sovereignId, rootPublicKey, keys, SuccessionVouchingGrace)
    {
    }

    /// <summary>A history with the vouching horizon's grace <paramref name="vouchingGrace"/> (1.8.0, 0 to 24 hours).</summary>
    public NAKeyHistory(string sovereignId, string? rootPublicKey, IReadOnlyList<NAKey> keys, TimeSpan vouchingGrace)
    {
        var grace = vouchingGrace;
        if (grace < TimeSpan.Zero || grace > SuccessionVouchingGrace)
            throw new ArgumentOutOfRangeException(nameof(vouchingGrace), "the vouching grace must be 0 to 24 hours");
        SovereignId = sovereignId;
        RootPublicKey = rootPublicKey;
        Keys = keys;
        VouchingGrace = grace;
    }

    public string SovereignId { get; }
    public string? RootPublicKey { get; }
    public IReadOnlyList<NAKey> Keys { get; }

    /// <summary>The vouching horizon's grace of this history (1.8.0): 0 to <see cref="SuccessionVouchingGrace"/>.</summary>
    public TimeSpan VouchingGrace { get; }

    /// <summary>
    /// This history with a shorter grace (1.8.0): a negative grace is 0, and one longer than the current grace keeps
    /// it. Use it for the NA's unsigned <c>vouching_grace_seconds</c>, which may only shorten the grace.
    /// </summary>
    public NAKeyHistory WithVouchingGrace(TimeSpan grace)
    {
        if (grace < TimeSpan.Zero) grace = TimeSpan.Zero;
        if (grace > VouchingGrace) grace = VouchingGrace;
        return new NAKeyHistory(SovereignId, RootPublicKey, Keys, grace);
    }

    /// <summary>
    /// Until when a record signed by <paramref name="publicKey"/> vouches for the present (1.8.0): its key's end, the
    /// hand-over and the grace. Null for a key with no end, or not in this history.
    /// </summary>
    public DateTimeOffset? VouchesUntil(string publicKey) =>
        Keys.FirstOrDefault(k => k.PublicKey == publicKey)?.End is { } end ? end + SuccessionHandover + VouchingGrace : null;

    /// <summary>
    /// True when a record signed by <paramref name="publicKey"/> no longer vouches for the present at
    /// <paramref name="now"/> (1.8.0): its key ended more than the hand-over and the grace ago.
    /// </summary>
    public bool RecordOutlivesKey(string publicKey, DateTimeOffset now) => VouchesUntil(publicKey) is { } until && now >= until;

    /// <summary>
    /// The public keys a record dated <paramref name="at"/> may be signed with and that still vouch at
    /// <paramref name="now"/> (1.8.0): <see cref="KeysAt"/> without the keys whose horizon has passed.
    /// </summary>
    public IReadOnlyList<string> KeysVouchingAt(DateTimeOffset at, DateTimeOffset now) =>
        KeysAt(at).Where(k => !RecordOutlivesKey(k, now)).ToList();

    /// <summary>
    /// Keys valid at every time: what a list of NA keys meant before 1.5.0 (for a party whose history the
    /// verifier does not hold).
    /// </summary>
    public static NAKeyHistory ValidAlways(IEnumerable<string> publicKeys) =>
        new("", null, publicKeys.Select((k, i) => new NAKey(i + 1, null, k, null, null, null, false)).ToList());

    /// <summary>Every public key of the history, in order.</summary>
    public IReadOnlyList<string> AllKeys() => Keys.Select(k => k.PublicKey).ToList();

    /// <summary>The public keys a record dated <paramref name="at"/> may be signed with.</summary>
    public IReadOnlyList<string> KeysAt(DateTimeOffset at) => Keys.Where(k => k.Accepts(at)).Select(k => k.PublicKey).ToList();

    /// <summary>
    /// True when a decision signed by <paramref name="publicKey"/> stays valid past what that key may vouch for: its
    /// end (its succession, or its enforced valid_to), the hand-over and <see cref="MaxDecisionLifetime"/>.
    /// </summary>
    internal bool DecisionOutlivesKey(string publicKey, DateTimeOffset validUntil)
    {
        var key = Keys.FirstOrDefault(k => k.PublicKey == publicKey);
        return key?.End is { } end && validUntil > end + SuccessionHandover + MaxDecisionLifetime;
    }
}

/// <summary>
/// Outcome of <see cref="OfflineVerifier.VerifyKeyHistory"/>. When not verified, <c>Reason</c> names why and
/// <c>KeySequence</c> the refused record's position (1 for the genesis block); nothing after it is accepted.
/// </summary>
public sealed record KeyHistoryVerificationResult(bool Verified, string? Reason, int? KeySequence, NAKeyHistory? History);

public static partial class OfflineVerifier
{
    // NA key history (v1.5.0): a port of the Python reference (genesis_mesh/trust/key_succession.py) with its
    // reason codes. The genesis block names the NA's first key and the root key that signed it; a
    // KeySuccessionRecord names the next NA key, signed by the root key (kept offline) and by the outgoing key.
    // Validity windows: the genesis key is valid from the start until the first succession takes effect (its
    // valid_to is not enforced); a key named by a succession from its effective_at until the next succession
    // takes effect, never at or after its own new_valid_to; a succeeded key is still accepted for records dated
    // up to SuccessionHandover after the next key took effect. Without a key history every verifier behaves as
    // before: a list of NA keys is valid at every time.

    private static readonly Regex UtcZone = new(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Verify a genesis block and its successions (a JSON array, as <c>GET /na/key-history</c> serves them; null
    /// for none) in order, and build the key history. <paramref name="rootPublicKey"/> is the root key the
    /// verifier holds out of band: keys in the records prove nothing by themselves.
    /// <para>
    /// Reasons, in the reference order: for the genesis block <c>genesis_invalid</c>,
    /// <c>genesis_root_mismatch</c>, <c>genesis_invalid_signature</c>; for each succession
    /// <c>payload_invalid</c>, <c>invalid_signature</c>, <c>unknown_field</c>, <c>non_canonical_form</c>,
    /// <c>succession_sovereign_mismatch</c>, <c>succession_chain_break</c>, <c>succession_key_reused</c>,
    /// <c>succession_not_increasing</c>, <c>succession_after_expiry</c>,
    /// <c>succession_invalid_root_signature</c>, <c>succession_invalid_previous_signature</c>.
    /// </para>
    /// </summary>
    public static KeyHistoryVerificationResult VerifyKeyHistory(string genesisJson, string? successionsJson, string rootPublicKey)
    {
        if (string.IsNullOrEmpty(rootPublicKey))
            throw new ArgumentException("the root public key, held out of band, is required", nameof(rootPublicKey));
        using var genesis = StrictJsonReader.Parse(genesisJson);
        if (string.IsNullOrWhiteSpace(successionsJson)) return BuildKeyHistory(genesis.RootElement, null, rootPublicKey);
        using var successions = StrictJsonReader.Parse(successionsJson);
        return BuildKeyHistory(genesis.RootElement, successions.RootElement, rootPublicKey);
    }

    /// <summary>
    /// Verify the genesis block and successions of a <c>GET /na/key-history</c> response body
    /// (<see cref="VerifyKeyHistory(string, string?, string)"/>). The response's own keys and root key are not
    /// trusted: the history is rebuilt from <paramref name="rootPublicKey"/>. From 1.8.0 the response's
    /// <c>vouching_grace_seconds</c>, which is unsigned, is taken only to shorten the grace.
    /// </summary>
    public static KeyHistoryVerificationResult VerifyKeyHistoryResponse(string responseJson, string rootPublicKey)
    {
        if (string.IsNullOrEmpty(rootPublicKey))
            throw new ArgumentException("the root public key, held out of band, is required", nameof(rootPublicKey));
        using var doc = StrictJsonReader.Parse(responseJson);
        var body = doc.RootElement;
        if (body.ValueKind != JsonValueKind.Object) throw new FormatException("the key history must be a JSON object");
        body.TryGetProperty("genesis", out var genesis);
        body.TryGetProperty("successions", out var successions);
        var result = BuildKeyHistory(genesis, successions.ValueKind == JsonValueKind.Undefined ? null : successions, rootPublicKey);
        if (result.History is { } history && body.TryGetProperty("vouching_grace_seconds", out var hint)
            && hint.ValueKind == JsonValueKind.Number && hint.TryGetDouble(out var seconds) && seconds >= 0)
            result = result with { History = history.WithVouchingGrace(TimeSpan.FromSeconds(Math.Min(seconds, 86_400))) };
        return result;
    }

    private static KeyHistoryVerificationResult BuildKeyHistory(JsonElement block, JsonElement? successions, string root)
    {
        static KeyHistoryVerificationResult Fail(int position, string reason) => new(false, reason, position, null);
        if (successions is { ValueKind: not (JsonValueKind.Array or JsonValueKind.Null) })
            throw new FormatException("successions must be a JSON array");
        if (block.ValueKind != JsonValueKind.Object || !ValidGenesis(block)) return Fail(1, "genesis_invalid");
        if (Str(block, "root_public_key") != root) return Fail(1, "genesis_root_mismatch");
        // v1.5.0 review fixes: strict like every other signed root.
        if (StrictRefusal("GenesisBlock", block, new[] { root }) is { } form && form != "invalid_signature")
            return Fail(1, form);
        if (!SignedAsReceived("GenesisBlock", block, new[] { root })) return Fail(1, "genesis_invalid_signature");
        var sovereign = Str(block, "network_name")!;
        var na = block.GetProperty("network_authority");
        var keys = new List<NAKey>
        {
            new(1, null, Str(na, "public_key")!, null, null, ParseTimestamp(Str(na, "valid_to")!), false),
        };
        string? previousDigest = null;
        if (successions is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var raw in list.EnumerateArray())
            {
                var previous = keys[^1];
                var (record, reason) = SuccessionRefusal(raw, previous, previousDigest, sovereign, root, keys);
                if (reason is not null || record is null)
                    return Fail(record?.KeySequence ?? previous.KeySequence + 1, reason ?? "payload_invalid");
                keys[^1] = previous with { SucceededAt = record.EffectiveAt, KeyId = previous.KeyId ?? record.PreviousKeyId };
                keys.Add(new NAKey(record.KeySequence, record.NewKeyId, record.NewPublicKey, record.EffectiveAt, null,
                    record.NewValidTo, true));
                previousDigest = record.Digest;
            }
        }
        return new(true, null, null, new NAKeyHistory(sovereign, root, keys));
    }

    private static readonly string[] GenesisStrings = ["network_name", "network_version", "root_public_key"];
    private static readonly string[] GenesisAuthorityTimes = ["valid_from", "valid_to"];

    /// <summary>True when a genesis block has the fields the reference requires, of the types it reads.</summary>
    private static bool ValidGenesis(JsonElement g)
    {
        static bool IsString(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String;
        static bool ListOf(JsonElement obj, string key, Func<JsonElement, bool> item) =>
            !obj.TryGetProperty(key, out var v) || (v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(item));
        if (!GenesisStrings.All(k => IsString(g, k))) return false;
        if (!IsObject(g, "network_authority", out var na) || !IsString(na, "public_key")) return false;
        if (!GenesisAuthorityTimes.All(k => TryParseTimestamp(Str(na, k) ?? "", out _))) return false;
        if (!IsObject(g, "policy_manifest", out var manifest) || !IsString(manifest, "hash")) return false;
        if (manifest.TryGetProperty("url", out var url) && url.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
        return ListOf(g, "allowed_crypto_suites", e => e.ValueKind == JsonValueKind.String)
            && ListOf(g, "allowed_transports", e => e.ValueKind == JsonValueKind.String)
            && ListOf(g, "bootstrap_anchors", e => e.ValueKind == JsonValueKind.Object && IsString(e, "id") && IsString(e, "endpoint"))
            && ListOf(g, "signatures", e => e.ValueKind == JsonValueKind.Object && IsString(e, "key_id") && IsString(e, "sig"));
    }

    private sealed record Succession(
        int KeySequence, string PreviousKeyId, string PreviousPublicKey, string? PreviousDigest, string NewKeyId,
        string NewPublicKey, DateTimeOffset EffectiveAt, DateTimeOffset NewValidTo, DateTimeOffset IssuedAt, string Digest);

    /// <summary>
    /// The parsed record (null when it does not parse) and why it is refused, in the reference order (v1.5.0 review
    /// fixes): <c>payload_invalid</c>; <c>unknown_field</c> or <c>invalid_signature</c>, <c>non_canonical_form</c>;
    /// <c>succession_sovereign_mismatch</c>, <c>succession_chain_break</c>, <c>succession_key_reused</c> (the root key
    /// included), <c>succession_backdated</c>, <c>succession_not_increasing</c>, <c>succession_after_expiry</c>; then
    /// the root key's, the new key's and the outgoing key's signatures.
    /// </summary>
    private static (Succession? Record, string? Reason) SuccessionRefusal(
        JsonElement raw, NAKey previous, string? previousDigest, string sovereign, string root, IReadOnlyList<NAKey> held)
    {
        const string model = "KeySuccessionRecord";
        if (raw.ValueKind != JsonValueKind.Object) return (null, "payload_invalid");
        // Parsed with its unknown fields set aside.
        var record = ParseSuccession(raw);
        if (record is null) return (null, "payload_invalid");
        var signers = ValidPublicKey(record.NewPublicKey)
            ? new[] { root, previous.PublicKey, record.NewPublicKey } : new[] { root, previous.PublicKey };
        if (StrictRefusal(model, raw, signers) is { } form) return (record, form);
        if (Str(raw, "sovereign_id") != sovereign) return (record, "succession_sovereign_mismatch");
        if (record.KeySequence != previous.KeySequence + 1 || record.PreviousPublicKey != previous.PublicKey
            || record.PreviousDigest != previousDigest)
            return (record, "succession_chain_break");
        if (!ValidPublicKey(record.NewPublicKey) || record.NewPublicKey == root || held.Any(k => k.PublicKey == record.NewPublicKey))
            return (record, "succession_key_reused");
        if (record.IssuedAt > record.EffectiveAt) return (record, "succession_backdated");
        if ((previous.ValidFrom is { } start && record.EffectiveAt <= start) || record.NewValidTo <= record.EffectiveAt)
            return (record, "succession_not_increasing");
        if (previous.ValidToEnforced && previous.ValidTo is { } end && record.EffectiveAt > end)
            return (record, "succession_after_expiry");
        if (!SignedAsReceived(model, raw, new[] { root })) return (record, "succession_invalid_root_signature");
        // The new key's signature proves it exists and is held.
        if (!SignedAsReceived(model, raw, new[] { record.NewPublicKey })) return (record, "succession_invalid_new_key_signature");
        if (!SignedAsReceived(model, raw, new[] { previous.PublicKey })) return (record, "succession_invalid_previous_signature");
        return (record, null);
    }

    /// <summary>
    /// Why a record must be refused for its form, or null: with a field this release does not know, or not in
    /// canonical form, <c>unknown_field</c> or <c>non_canonical_form</c> when a signature by one of the signers
    /// verifies over it as received, <c>invalid_signature</c> otherwise.
    /// </summary>
    private static string? StrictRefusal(string model, JsonElement obj, IReadOnlyList<string> signers)
    {
        var unknown = StrictFields.UnknownFields(model, obj).Count > 0;
        if (!unknown && StrictFields.NonCanonicalFields(model, obj).Count == 0) return null;
        if (!SignedAsReceived(model, obj, signers)) return "invalid_signature";
        return unknown ? "unknown_field" : "non_canonical_form";
    }

    /// <summary>
    /// A KeySuccessionRecord read as the reference validates it, its unknown fields set aside; null for
    /// <c>payload_invalid</c>.
    /// </summary>
    private static Succession? ParseSuccession(JsonElement obj)
    {
        string? Text(string key, int min, int max)
        {
            if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString()!;
            var n = s.EnumerateRunes().Count();
            return n >= min && n <= max ? s : null;
        }
        DateTimeOffset? Utc(string key)
        {
            var s = Str(obj, key);
            if (s is null || !UtcZone.IsMatch(s)
                || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                || t.Offset != TimeSpan.Zero)
                return null;
            return t;
        }
        // The reference gives an absent succession_id a fresh one: the form check then refuses the record.
        if (obj.TryGetProperty("succession_id", out _) && Text("succession_id", 1, 128) is null) return null;
        if (Text("sovereign_id", 1, 256) is null) return null;
        string? previousDigest = null;
        if (obj.TryGetProperty("previous_succession_digest", out var d) && d.ValueKind != JsonValueKind.Null
            && (previousDigest = Text("previous_succession_digest", 64, 64)) is null)
            return null;
        if (!obj.TryGetProperty("key_sequence", out var seq) || seq.ValueKind != JsonValueKind.Number
            || !seq.TryGetInt32(out var keySequence) || keySequence < 2)
            return null;
        if (obj.TryGetProperty("signatures", out var sigs) && (sigs.ValueKind != JsonValueKind.Array
            || !sigs.EnumerateArray().All(s => s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("key_id", out var k) && k.ValueKind == JsonValueKind.String
                && s.TryGetProperty("sig", out var v) && v.ValueKind == JsonValueKind.String)))
            return null;
        if (Text("previous_key_id", 1, 128) is not { } previousKeyId || Text("previous_public_key", 1, 128) is not { } previousKey
            || Text("new_key_id", 1, 128) is not { } newKeyId || Text("new_public_key", 1, 128) is not { } newKey
            || Utc("effective_at") is not { } effective || Utc("new_valid_to") is not { } validTo || Utc("issued_at") is not { } issued)
            return null;
        return new Succession(keySequence, previousKeyId, previousKey, previousDigest, newKeyId, newKey, effective, validTo, issued,
            Sha256Hex(ReceivedCanonical("KeySuccessionRecord", obj)));
    }

    /// <summary>True when <paramref name="value"/> is a base64 Ed25519 public key (32 bytes).</summary>
    private static bool ValidPublicKey(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        var buffer = new byte[48];
        return Convert.TryFromBase64String(value, buffer, out var n) && n == 32;
    }

    /// <summary>
    /// The signed form of a record as received, from the field registry: every field but the signature (those
    /// left out when null omitted), or the fixed field list of a root signed over one.
    /// </summary>
    internal static string ReceivedCanonical(string model, JsonElement obj)
    {
        var spec = StrictFields.Model(model);
        if (spec.CanonicalFields is { } fields) return Canonical.OfKeys(obj, fields.ToList());
        return Canonical.Of(obj, spec.SignatureField is { } sig ? new[] { sig } : Array.Empty<string>(), spec.OmitWhenNone.ToList());
    }

    /// <summary>True when a signature on the record verifies over its received form with one of the keys.</summary>
    internal static bool SignedAsReceived(string model, JsonElement obj, IReadOnlyList<string> keys)
    {
        var spec = StrictFields.Model(model);
        var body = ReceivedCanonical(model, obj);
        var sigs = spec.SignatureField switch
        {
            "signatures" => SignaturesOf(obj),
            { } field when IsObject(obj, field, out var one) && Str(one, "sig") is { } s => new[] { s },
            _ => Enumerable.Empty<string>(),
        };
        return sigs.Any(s => VerifyEd25519(body, s, keys));
    }
}
