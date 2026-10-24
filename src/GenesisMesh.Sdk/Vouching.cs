using System.Text.Json;

namespace GenesisMesh;

public static partial class OfflineVerifier
{
    // The vouching horizon for NA-signed records (1.8.0): a port of the Python reference's
    // trust.key_succession.na_record_refusal, pinned by the "vouching" conformance suite. A record is placed at the
    // time its kind names (NAVouchingTimeFields), its signature checked over the record as received with the NA keys
    // valid then and, when now is given, the horizon applied: a record whose key ended more than the hand-over and
    // the grace before now no longer vouches for the present (outlives_key). Without now the record is judged at its
    // own time only, as audit packs and exports judge it.

    /// <summary>The record kinds the vouching horizon applies to, and the field each is placed at (1.8.0).</summary>
    public static readonly IReadOnlyDictionary<string, string> NAVouchingTimeFields = new Dictionary<string, string>
    {
        ["CapabilityGrant"] = "issued_at",
        ["MembershipAttestation"] = "issued_at",
        ["RecognitionTreaty"] = "issued_at",
        ["SovereignRevocationFeed"] = "issued_at",
    };

    /// <summary>The longest validity of a grant, and how long after its issue it may start (1.8.0).</summary>
    public static readonly TimeSpan GrantMaxValidity = TimeSpan.FromHours(24);

    /// <summary>The clock skew a grant's start may have either side of that window.</summary>
    public static readonly TimeSpan GrantClockSkew = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Why an NA-signed record of <paramref name="model"/> does not verify, or null when it does (1.8.0). Reasons, in
    /// order: <c>unknown_field</c> or <c>non_canonical_form</c>, <c>na_key_not_valid_at_time</c>,
    /// <c>invalid_signature</c>, <c>record_malformed</c> (signed, but refused by the reference's time rules for its
    /// kind, such as a grant that starts more than 24 hours after its issue), and <c>outlives_key</c> (only with
    /// <paramref name="now"/>).
    /// </summary>
    public static string? NARecordRefusal(string model, string recordJson, NAKeyHistory keyHistory, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        if (!NAVouchingTimeFields.TryGetValue(model, out var field))
            throw new ArgumentException($"{model} is not a kind the vouching horizon applies to", nameof(model));
        using var doc = StrictJsonReader.Parse(recordJson);
        var record = doc.RootElement;
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(field, out var time)
            || time.ValueKind != JsonValueKind.String || !TryParseTimestamp(time.GetString()!, out var at))
            return "invalid_signature";
        var keys = keyHistory.KeysAt(at);
        var signer = keys.FirstOrDefault(k => SignedAsReceived(model, record, new[] { k }));
        if (signer is null)
        {
            var others = keyHistory.AllKeys().Where(k => !keys.Contains(k)).ToList();
            return others.Count > 0 && SignedAsReceived(model, record, others) ? "na_key_not_valid_at_time" : "invalid_signature";
        }
        if (StrictRefusal(model, record, new[] { signer }) is { } refusal) return refusal;
        if (!ModelTimesAccepted(model, record)) return "record_malformed";
        if (now is { } judged && keyHistory.RecordOutlivesKey(signer, judged)) return "outlives_key";
        return null;
    }

    /// <summary>
    /// The reference model's time rules for <paramref name="model"/>: a grant is valid for at most
    /// <see cref="GrantMaxValidity"/> and starts within it of its issue; an attestation or a treaty expires after it
    /// starts, and is not issued after it expires.
    /// </summary>
    private static bool ModelTimesAccepted(string model, JsonElement record)
    {
        bool Times(out DateTimeOffset[] values, params string[] names)
        {
            values = new DateTimeOffset[names.Length];
            for (var i = 0; i < names.Length; i++)
                if (!TryParseTimestamp(Str(record, names[i]) ?? "", out values[i])) return false;
            return true;
        }
        switch (model)
        {
            case "CapabilityGrant":
                {
                    if (!Times(out var t, "issued_at", "valid_from", "valid_until")) return false;
                    var (issued, from, until) = (t[0], t[1], t[2]);
                    return from < until && until - from <= GrantMaxValidity
                        && from >= issued - GrantClockSkew && from <= issued + GrantMaxValidity + GrantClockSkew;
                }
            case "MembershipAttestation":
            case "RecognitionTreaty":
                {
                    if (!Times(out var t, "issued_at", "valid_from", "expires_at")) return false;
                    return t[2] > t[1] && t[0] <= t[2];
                }
            default:
                return true;
        }
    }
}
