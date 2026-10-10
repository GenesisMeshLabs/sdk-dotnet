using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace GenesisMesh;

/// <summary>Outcome of <see cref="OfflineVerifier.VerifyAgreement"/>.</summary>
public sealed record AgreementVerificationResult(bool Accepted, string Reason, string? AgreementId);

/// <summary>
/// Outcome of <see cref="OfflineVerifier.VerifyBoundaryDecision"/>. <c>Accepted</c> means the decision
/// verified; <c>Authorized</c> is what it decided (a verified denial is accepted, not authorized).
/// </summary>
public sealed record DecisionVerificationResult(bool Accepted, string Reason, bool Authorized, string? DecisionId);

/// <summary>One reason a data access intent does not comply with a license policy.</summary>
public sealed record DataUsageViolationResult(string ViolationType, string Detail);

/// <summary>Outcome of <see cref="OfflineVerifier.VerifyDataAccessIntent"/>.</summary>
public sealed record DataIntentVerificationResult(bool Valid, string? ViolationReason, IReadOnlyList<DataUsageViolationResult> Violations);

/// <summary>Options for <see cref="OfflineVerifier.VerifyBoundaryDecision"/>.</summary>
public sealed class DecisionVerifyOptions
{
    /// <summary>NA keys that may sign decisions (base64 Ed25519).</summary>
    public IReadOnlyList<string> OperatorPublicKeys { get; init; } = Array.Empty<string>();
    /// <summary>Verification time; default now.</summary>
    public DateTimeOffset? Now { get; init; }
    /// <summary>When set, an embedded freshness proof must verify under these keys.</summary>
    public IReadOnlyList<string>? FreshnessProofIssuerKeys { get; init; }
    /// <summary>Signed BoundaryPolicy JSON; when not null, must equal the decision's policy binding.</summary>
    public IReadOnlyList<string>? ExpectedPolicies { get; init; }
    /// <summary>Signed MembershipAttestation JSON; when set, must match the attestation binding.</summary>
    public string? ExpectedAttestation { get; init; }
}

/// <summary>
/// Offline verification (v0.61): checks signed Genesis Mesh artifacts without calling the
/// Network Authority. Each method is a port of the Python reference and returns the same
/// reason codes (checked by the shared conformance vectors).
/// <para>
/// Pass artifacts as the JSON received from the NA or the signer: signatures cover the canonical
/// form of exactly what was signed, and re-serializing a typed model can change it.
/// </para>
/// </summary>
public static class OfflineVerifier
{
    /// <summary>The fields both parties sign (checked against the field registry).</summary>
    internal static readonly string[] AgreementCanonicalKeys =
    {
        "agreed_terms", "graph_digest", "offer_id", "offerer_evidence",
        "offerer_sovereign_id", "responder_evidence", "responder_sovereign_id",
    };
    private static readonly HashSet<string> AttestationGates = new() { "attestation_status", "attestation_validity" };
    private static readonly HashSet<string> BuiltinGates = new()
    {
        "capability_check", "validity_window", "freshness_check", "freshness_proof",
        "attestation_status", "attestation_validity",
    };
    private static readonly string[] Signature = { "signature" };
    private static readonly string[] Signatures = { "signatures" };
    /// <summary>
    /// The decision fields left out of the signed form when null (checked against the field registry).
    /// </summary>
    internal static readonly string[] DecisionOmittedWhenAbsent = { "policy_binding", "attestation_binding" };

    // ── primitives ─────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="signatureB64"/> verifies <paramref name="message"/> under any key.</summary>
    public static bool VerifyEd25519(string message, string signatureB64, IEnumerable<string> publicKeys)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureB64); }
        catch (FormatException) { return false; }
        if (sig.Length != 64) return false;
        var data = Encoding.UTF8.GetBytes(message);
        foreach (var keyB64 in publicKeys)
        {
            try
            {
                var raw = Convert.FromBase64String(keyB64);
                if (raw.Length != 32) continue;
                var key = PublicKey.Import(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey);
                if (SignatureAlgorithm.Ed25519.Verify(key, data, sig)) return true;
            }
            catch (FormatException) { }
        }
        return false;
    }

    /// <summary>Parse an ISO 8601 timestamp as written by the NA (microseconds; Z, offset, or naive UTC).</summary>
    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static bool TryParseTimestamp(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool IsObject(JsonElement obj, string key, out JsonElement value) =>
        obj.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? SignatureOf(JsonElement obj) =>
        IsObject(obj, "signature", out var sig) ? Str(sig, "sig") : null;

    /// <summary>True when a record has no signature: the field is absent or null (v1.3.1, as the reference reads it).</summary>
    private static bool SignatureAbsent(JsonElement obj) =>
        !obj.TryGetProperty("signature", out var sig) || sig.ValueKind == JsonValueKind.Null;

    private static IEnumerable<string> SignaturesOf(JsonElement obj)
    {
        if (!obj.TryGetProperty("signatures", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && Str(item, "sig") is { } s) yield return s;
    }

    private static IEnumerable<string> Strings(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) yield return item.GetString()!;
    }

    // ── digests ────────────────────────────────────────────────────────────

    /// <summary>MembershipAttestation.digest(): SHA-256 of the canonical body without signatures.</summary>
    public static string AttestationDigest(string attestationJson)
    {
        using var doc = StrictJson.Parse(attestationJson);
        return Sha256Hex(Canonical.Of(doc.RootElement, Signatures));
    }

    /// <summary>BoundaryPolicy.digest(): SHA-256 of the canonical body without the signature.</summary>
    public static string PolicyDigest(string policyJson)
    {
        using var doc = StrictJson.Parse(policyJson);
        return Sha256Hex(Canonical.Of(doc.RootElement, Signature));
    }

    /// <summary>Digest of the ordered (policy_id, version, policy_digest) list of a PolicyBinding.</summary>
    public static string PolicySetDigest(IEnumerable<(string PolicyId, string VersionLiteral, string PolicyDigest)> refs)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var (id, version, digest) in refs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('[');
            Canonical.WriteString(sb, id);
            sb.Append(',').Append(Canonical.NumberLiteral(version)).Append(',');
            Canonical.WriteString(sb, digest);
            sb.Append(']');
        }
        return Sha256Hex(sb.Append(']').ToString());
    }

    // ── agreements ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verify an AgreementRecord's dual signatures and optional graph binding.
    /// Reasons: accepted, missing_offerer_signature, invalid_offerer_signature,
    /// missing_responder_signature, invalid_responder_signature, graph_digest_mismatch.
    /// </summary>
    public static AgreementVerificationResult VerifyAgreement(
        string agreementJson, IReadOnlyList<string> offererPublicKeys, IReadOnlyList<string> responderPublicKeys,
        string? expectedGraphDigest = null)
    {
        using var doc = StrictJson.Parse(agreementJson);
        var a = doc.RootElement;
        var id = Str(a, "agreement_id");
        var sigs = SignaturesOf(a).ToList();
        if (sigs.Count == 0) return new(false, "missing_offerer_signature", id);
        var canonical = Canonical.OfKeys(a, AgreementCanonicalKeys);
        bool AnyValid(IReadOnlyList<string> keys) => sigs.Any(s => VerifyEd25519(canonical, s, keys));
        if (!AnyValid(offererPublicKeys)) return new(false, "invalid_offerer_signature", id);
        if (!AnyValid(responderPublicKeys))
            return new(false, sigs.Count < 2 ? "missing_responder_signature" : "invalid_responder_signature", id);
        if (expectedGraphDigest is not null && Str(a, "graph_digest") != expectedGraphDigest)
            return new(false, "graph_digest_mismatch", id);
        // v1.2.0: an authentic agreement with a signed field this SDK does not know (StrictFields).
        if (StrictFields.UnknownFields("AgreementRecord", a).Count > 0) return new(false, "unknown_field", id);
        // v1.2.0: an agreement signed over a form the reference does not write.
        if (StrictFields.NonCanonicalFields("AgreementRecord", a).Count > 0) return new(false, "non_canonical_form", id);
        return new(true, "accepted", id);
    }

    // ── boundary decisions ─────────────────────────────────────────────────

    /// <summary>Verify a BoundaryDecision's signature, expiry and bindings offline.</summary>
    public static DecisionVerificationResult VerifyBoundaryDecision(string decisionJson, DecisionVerifyOptions options)
    {
        using var doc = StrictJson.Parse(decisionJson);
        var d = doc.RootElement;
        var authorized = Bool(d, "authorized");
        var decisionId = Str(d, "decision_id");
        DecisionVerificationResult Result(bool accepted, string reason, bool auth) => new(accepted, reason, auth, decisionId);
        DecisionVerificationResult Reject(string reason) => Result(false, reason, authorized);

        // v1.3.1: missing only when absent or null, as in the reference; a signature of another shape
        // fails as invalid_signature.
        if (SignatureAbsent(d)) return Reject("missing_signature");
        var sig = SignatureOf(d) ?? "";
        var now = options.Now ?? DateTimeOffset.UtcNow;
        // v1.3.1: an expiry that does not parse is left to the signature and form checks, as in the
        // reference; when they pass, it is refused as before (FormatException).
        var expiry = Str(d, "decision_valid_until") ?? "";
        var expiryParses = TryParseTimestamp(expiry, out var validUntil);
        if (expiryParses && now > validUntil) return Reject("decision_expired");
        if (!VerifyEd25519(Canonical.Of(d, Signature, DecisionOmittedWhenAbsent), sig, options.OperatorPublicKeys))
            return Reject("invalid_signature");
        // v1.2.0: an authentic decision with a signed field this SDK does not know, or expected inputs
        // it cannot read, is refused by name: upgrade this SDK (StrictFields).
        if (StrictFields.UnknownFields("BoundaryDecision", d).Count > 0
            || (options.ExpectedPolicies ?? Array.Empty<string>()).Any(p => StrictFields.UnknownFields("BoundaryPolicy", p).Count > 0)
            || (options.ExpectedAttestation is not null
                && StrictFields.UnknownFields("MembershipAttestation", options.ExpectedAttestation).Count > 0))
            return Reject("unknown_field");
        // v1.2.0: a decision signed over a form the reference does not write.
        if (StrictFields.NonCanonicalFields("BoundaryDecision", d).Count > 0) return Reject("non_canonical_form");
        if (!expiryParses) throw new FormatException($"decision_valid_until \"{expiry}\" is not a timestamp");

        if (IsObject(d, "freshness_proof", out var proof) && options.FreshnessProofIssuerKeys is { Count: > 0 } issuers)
        {
            var proofSig = SignatureOf(proof);
            if (proofSig is null || !VerifyEd25519(Canonical.Of(proof, Signature), proofSig, issuers))
                return Reject("freshness_proof_invalid_signature");
            if (ParseTimestamp(Str(proof, "proof_valid_until") ?? "") < ParseTimestamp(Str(d, "decision_made_at") ?? ""))
                return Reject("freshness_proof_expired");
        }

        var hasBinding = IsObject(d, "policy_binding", out var binding);
        if (options.ExpectedPolicies is not null)
        {
            if (!hasBinding) return Reject("policy_binding_missing");
            var expected = options.ExpectedPolicies
                .Select(json =>
                {
                    using var p = StrictJson.Parse(json);
                    var version = p.RootElement.GetProperty("version");
                    return (Id: Str(p.RootElement, "policy_id") ?? "", Version: version.GetRawText(),
                        VersionValue: version.GetInt64(), Digest: Sha256Hex(Canonical.Of(p.RootElement, Signature)));
                })
                .OrderBy(e => e.Id, Canonical.CodePointComparer.Instance).ThenBy(e => e.VersionValue)
                .Select(e => (e.Id, e.Version, e.Digest)).ToList();
            var bound = new List<(string, string, string)>();
            if (binding.TryGetProperty("policies", out var applied) && applied.ValueKind == JsonValueKind.Array)
                foreach (var a in applied.EnumerateArray())
                    bound.Add((Str(a, "policy_id") ?? "", a.GetProperty("version").GetRawText(), Str(a, "policy_digest") ?? ""));
            if (!expected.SequenceEqual(bound) || PolicySetDigest(bound) != Str(binding, "policy_set_digest"))
                return Reject("policy_binding_mismatch");
        }

        if (options.ExpectedAttestation is not null)
        {
            if (!IsObject(d, "attestation_binding", out var ab)) return Reject("attestation_binding_missing");
            using var att = StrictJson.Parse(options.ExpectedAttestation);
            var e = att.RootElement;
            if (Str(ab, "attestation_id") != Str(e, "attestation_id") || Str(ab, "subject_id") != Str(e, "subject_id")
                || Str(ab, "issuer_sovereign_id") != Str(e, "issuer_sovereign_id")
                || Str(ab, "attestation_digest") != AttestationDigest(options.ExpectedAttestation))
                return Reject("attestation_binding_mismatch");
        }

        if (!authorized)
        {
            var builtinFailed = false;
            if (d.TryGetProperty("gate_results", out var gates) && gates.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gates.EnumerateArray())
                {
                    if (Bool(g, "passed")) continue;
                    var name = Str(g, "gate_name") ?? "";
                    if (AttestationGates.Contains(name)) return Result(true, "unauthorized_attestation_basis", false);
                    if (BuiltinGates.Contains(name)) builtinFailed = true;
                }
            }
            if (hasBinding && !builtinFailed)
            {
                if (Str(binding, "resolution_status") == "failed")
                    return Result(true, "unauthorized_policy_resolution_failed", false);
                if (binding.TryGetProperty("gate_evaluations", out var evals) && evals.ValueKind == JsonValueKind.Array
                    && evals.EnumerateArray().Any(e => Str(e, "mode") == "enforce" && !Bool(e, "passed")))
                    return Result(true, "unauthorized_policy_gate_failure", false);
            }
            var denial = Str(d, "denial_reason") ?? "";
            if (denial.Contains("capability", StringComparison.Ordinal)) return Result(true, "unauthorized_capability_out_of_scope", false);
            if (denial.Contains("validity", StringComparison.Ordinal) || denial.Contains("window", StringComparison.Ordinal))
                return Result(true, "unauthorized_outside_validity_window", false);
            if (denial.Contains("freshness", StringComparison.Ordinal)) return Result(true, "unauthorized_insufficient_freshness", false);
            return Result(true, "unauthorized_gate_failure", false);
        }
        return Result(true, "authorized", true);
    }

    // ── data usage ─────────────────────────────────────────────────────────

    /// <summary>True when the licensor signed the DataLicensePolicy.</summary>
    public static bool VerifyDataLicensePolicySignature(string policyJson, IReadOnlyList<string> licensorPublicKeys)
    {
        using var doc = StrictJson.Parse(policyJson);
        var sig = SignatureOf(doc.RootElement);
        return sig is not null && VerifyEd25519(Canonical.Of(doc.RootElement, Signature), sig, licensorPublicKeys)
            && StrictFields.UnknownFields("DataLicensePolicy", doc.RootElement).Count == 0
            && StrictFields.NonCanonicalFields("DataLicensePolicy", doc.RootElement).Count == 0;
    }

    /// <summary>
    /// Check an agent-signed DataAccessIntent against a DataLicensePolicy at <paramref name="at"/>
    /// (default now): the agent signature, then expiry, licensed sources, prohibited classifications,
    /// permitted access types and the volume cap, in the reference order.
    /// </summary>
    public static DataIntentVerificationResult VerifyDataAccessIntent(
        string intentJson, string policyJson, IReadOnlyList<string> agentPublicKeys, DateTimeOffset? at = null)
    {
        using var intentDoc = StrictJson.Parse(intentJson);
        using var policyDoc = StrictJson.Parse(policyJson);
        var intent = intentDoc.RootElement;
        var policy = policyDoc.RootElement;
        var t = at ?? DateTimeOffset.UtcNow;
        static DataIntentVerificationResult Fail(List<DataUsageViolationResult> v) => new(false, v[0].ViolationType, v);

        // v1.2.0: fields this SDK does not know, as the reference reports them (StrictFields).
        var unknown = StrictFields.UnknownFields("DataAccessIntent", intent, "");
        var loose = StrictFields.NonCanonicalFields("DataAccessIntent", intent);
        if ((unknown.Count > 0 || loose.Count > 0) && (SignatureOf(intent) is not { } signed
                                  || !VerifyEd25519(Canonical.Of(intent, Signature), signed, agentPublicKeys)))
            return Fail(new() { new("intent_exceeds_license", "Invalid intent signature") });
        unknown.AddRange(StrictFields.UnknownFields("DataLicensePolicy", policy, "policy."));
        unknown.Sort(Canonical.CodePointComparer.Instance);
        if (unknown.Count > 0)
            return Fail(new() { new("intent_exceeds_license", "Unknown field: " + string.Join(", ", unknown)) });
        if (loose.Count > 0) return Fail(new() { new("intent_exceeds_license", "Not in canonical form: intent") });
        if (StrictFields.NonCanonicalFields("DataLicensePolicy", policy).Count > 0)
            return Fail(new() { new("intent_exceeds_license", "Not in canonical form: policy") });
        // v1.3.1: missing only when absent or null, as in the reference.
        if (SignatureAbsent(intent)) return Fail(new() { new("intent_exceeds_license", "Missing intent signature") });
        if (!VerifyEd25519(Canonical.Of(intent, Signature), SignatureOf(intent) ?? "", agentPublicKeys))
            return Fail(new() { new("intent_exceeds_license", "Invalid intent signature") });

        var violations = new List<DataUsageViolationResult>();
        if (t > ParseTimestamp(Str(intent, "expires_at") ?? ""))
            violations.Add(new("intent_expired", $"Intent expired at {Str(intent, "expires_at")}"));
        if (t > ParseTimestamp(Str(policy, "valid_until") ?? "") || t < ParseTimestamp(Str(policy, "valid_from") ?? ""))
            violations.Add(new("policy_expired", "Policy not valid at verification time"));
        var allowed = Strings(policy, "allowed_source_ids").ToHashSet(StringComparer.Ordinal);
        var prohibited = Strings(policy, "prohibited_classification_tags").ToHashSet(StringComparer.Ordinal);
        if (intent.TryGetProperty("declared_sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var src in sources.EnumerateArray())
            {
                var id = Str(src, "source_id") ?? "";
                if (!allowed.Contains(id))
                    violations.Add(new("source_not_licensed", $"Source '{id}' not in allowed_source_ids"));
                var overlap = Strings(src, "classification_tags").Where(prohibited.Contains)
                    .OrderBy(x => x, Canonical.CodePointComparer.Instance).ToList();
                if (overlap.Count > 0)
                    violations.Add(new("prohibited_classification", $"Source '{id}' has prohibited tags: {string.Join(", ", overlap)}"));
            }
        }
        var allowedTypes = Strings(policy, "allowed_access_types").ToHashSet(StringComparer.Ordinal);
        if (allowedTypes.Count == 0) allowedTypes.Add("read");
        foreach (var accessType in Strings(intent, "declared_access_types"))
            if (!allowedTypes.Contains(accessType))
                violations.Add(new("access_type_not_permitted", $"Access type '{accessType}' not in allowed_access_types"));
        if (policy.TryGetProperty("max_volume_bytes_per_session", out var max) && max.ValueKind == JsonValueKind.Number
            && intent.TryGetProperty("estimated_volume_bytes", out var vol) && vol.ValueKind == JsonValueKind.Number
            && vol.GetInt64() > max.GetInt64())
            violations.Add(new("volume_cap_exceeded", $"Volume {vol.GetInt64()} > max {max.GetInt64()}"));
        return violations.Count > 0 ? Fail(violations) : new(true, null, Array.Empty<DataUsageViolationResult>());
    }
}
