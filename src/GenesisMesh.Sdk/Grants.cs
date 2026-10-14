using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// Outcome of <see cref="OfflineVerifier.VerifyCapabilityGrant"/>. <c>Valid</c> means the grant is the NA's
/// (<c>Reason</c> <c>accepted</c>); otherwise <c>Reason</c> names why not.
/// </summary>
public sealed record GrantVerificationResult(bool Valid, string Reason, string? GrantId);

public static partial class OfflineVerifier
{
    // Pre-issued grants (v1.6.0): a CapabilityGrant is issued by the NA before the bearer acts and lets the bearer
    // act within it while the NA is unreachable. Requesting grants, holding time tokens, acting within a grant and
    // recording its uses live in the TypeScript and Rust SDKs; this SDK checks that a grant it is shown is the NA's.

    /// <summary>
    /// v1.6.0: check a CapabilityGrant's NA signature offline, as received, with the NA's key history
    /// (<see cref="VerifyKeyHistory(string, string?, string)"/>; <see cref="NAKeyHistory.ValidAlways"/> for a plain
    /// list of NA keys). Reasons, in the order <see cref="VerifyBoundaryDecision"/> checks them:
    /// <c>missing_signature</c>, <c>invalid_signature</c> (no key of the history signed it), <c>unknown_field</c>,
    /// <c>non_canonical_form</c>, then <c>na_key_not_valid_at_time</c> (signed by a key of the history that is not
    /// valid at the grant's <c>issued_at</c>). The grant's limits (validity, budget, envelope) and its revocation are
    /// not checked here: they are the NA's and the holder's.
    /// </summary>
    public static GrantVerificationResult VerifyCapabilityGrant(string grantJson, NAKeyHistory keyHistory)
    {
        ArgumentNullException.ThrowIfNull(keyHistory);
        const string model = "CapabilityGrant";
        using var doc = StrictJson.Parse(grantJson);
        var g = doc.RootElement;
        if (g.ValueKind != JsonValueKind.Object) throw new ArgumentException("a grant must be a JSON object", nameof(grantJson));
        GrantVerificationResult Result(string reason) => new(reason == "accepted", reason, Str(g, "grant_id"));

        if (SignatureAbsent(g)) return Result("missing_signature");
        if (!SignedAsReceived(model, g, keyHistory.AllKeys())) return Result("invalid_signature");
        if (StrictFields.UnknownFields(model, g).Count > 0) return Result("unknown_field");
        if (StrictFields.NonCanonicalFields(model, g).Count > 0) return Result("non_canonical_form");
        var issuedAt = ParseTimestamp(Str(g, NAKeyHistory.RecordTimeFields[model]) ?? "");
        if (!SignedAsReceived(model, g, keyHistory.KeysAt(issuedAt))) return Result("na_key_not_valid_at_time");
        return Result("accepted");
    }
}
