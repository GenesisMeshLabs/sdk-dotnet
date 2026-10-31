using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

// The response types below carry the Network Authority's own JSON field names.
// Before 1.0.2 several declared names the NA never sends, so those properties
// were always empty (and verify results always reported Valid == false). The
// old names are kept as obsolete properties, filled in when a response is
// deserialized, and never serialized: serializing a deserialized record gives
// the NA's fields.

// ── Agreements ────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /admin/agreements/offer.</summary>
public sealed class CapabilityOffer
{
    /// <summary>Not read by the NA: the offer is always made by the NA's own sovereign.</summary>
    [JsonPropertyName("offeror_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfferorSovereignId { get; set; }

    /// <summary>The sovereign the offer is made to. Required.</summary>
    [JsonPropertyName("responder_sovereign_id")]
    public string ResponderSovereignId { get; set; } = "";

    /// <summary>The capabilities requested. Required, at least one.</summary>
    [JsonPropertyName("capabilities")]
    public IList<string> Capabilities { get; set; } = [];

    /// <summary>Not read by the NA: an offer requests capabilities, not roles.</summary>
    [JsonPropertyName("roles")]
    public IList<string> Roles { get; set; } = [];

    /// <summary>Start of the requested terms (ISO 8601). Required, before <see cref="ValidUntil"/>.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>End of the requested terms (ISO 8601). Required.</summary>
    [JsonPropertyName("valid_until")]
    public string ValidUntil { get; set; } = "";

    /// <summary>When the offer lapses unless it is accepted (ISO 8601). Required.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    /// <summary>Not read by the NA.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Returned by POST /admin/agreements/offer (a CapabilityOffer) and by
/// POST /admin/agreements/counter (a CapabilityCounter). Pass an offer to
/// Agreement.Accept. A counter carries AgreedTerms and ResponderEvidence where
/// an offer carries RequestedTerms and CreatedAt.
/// </summary>
public sealed class OfferRecord
{
    /// <summary>Unique offer identifier; a counter carries the ID of the offer it answers.</summary>
    [JsonPropertyName("offer_id")]
    public string OfferId { get; set; } = "";

    /// <summary>The sovereign that made the offer.</summary>
    [JsonPropertyName("offerer_sovereign_id")]
    public string OffererSovereignId { get; set; } = "";

    /// <summary>The sovereign the offer is made to.</summary>
    [JsonPropertyName("responder_sovereign_id")]
    public string ResponderSovereignId { get; set; } = "";

    /// <summary>Offer only: the capabilities, scope and validity window requested.</summary>
    [JsonPropertyName("requested_terms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement RequestedTerms { get; set; }

    /// <summary>Counter only: the terms the responder proposes, a subset of the requested terms.</summary>
    [JsonPropertyName("agreed_terms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement AgreedTerms { get; set; }

    /// <summary>Trust evidence from the offerer toward the responder.</summary>
    [JsonPropertyName("offerer_evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement OffererEvidence { get; set; }

    /// <summary>Counter only: trust evidence from the responder toward the offerer.</summary>
    [JsonPropertyName("responder_evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ResponderEvidence { get; set; }

    /// <summary>Signatures over the canonical body: the offerer's on an offer, the responder's on a counter.</summary>
    [JsonPropertyName("signatures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signatures { get; set; }

    /// <summary>SHA-256 hex of the offerer's recognition graph when the offer was made.</summary>
    [JsonPropertyName("graph_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GraphDigest { get; set; }

    /// <summary>Offer only: when the offer was made.</summary>
    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedAt { get; set; }

    /// <summary>When the offer lapses; a counter inherits the offer's.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";
}

/// <summary>Returned by POST /admin/agreements/accept: the agreement with both parties' signatures.</summary>
public sealed class AgreementRecord : IJsonOnDeserialized
{
    /// <summary>Unique agreement identifier.</summary>
    [JsonPropertyName("agreement_id")]
    public string AgreementId { get; set; } = "";

    /// <summary>The offer the agreement started from.</summary>
    [JsonPropertyName("offer_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfferId { get; set; }

    /// <summary>The sovereign that made the offer.</summary>
    [JsonPropertyName("offerer_sovereign_id")]
    public string OffererSovereignId { get; set; } = "";

    /// <summary>The sovereign that accepted it.</summary>
    [JsonPropertyName("responder_sovereign_id")]
    public string ResponderSovereignId { get; set; } = "";

    /// <summary>The capabilities, scope and validity window both parties signed.</summary>
    [JsonPropertyName("agreed_terms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement AgreedTerms { get; set; }

    /// <summary>Trust evidence from the offerer toward the responder.</summary>
    [JsonPropertyName("offerer_evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement OffererEvidence { get; set; }

    /// <summary>Trust evidence from the responder toward the offerer.</summary>
    [JsonPropertyName("responder_evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ResponderEvidence { get; set; }

    /// <summary>SHA-256 hex of the offerer's recognition graph at agreement time.</summary>
    [JsonPropertyName("graph_digest")]
    public string GraphDigest { get; set; } = "";

    /// <summary>When the agreement was established.</summary>
    [JsonPropertyName("established_at")]
    public string EstablishedAt { get; set; } = "";

    /// <summary>When the agreement ends.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    /// <summary>Both parties' signatures over the canonical agreement body.</summary>
    [JsonPropertyName("signatures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signatures { get; set; }

    /// <summary>The capabilities in AgreedTerms, filled in on deserialization.</summary>
    [JsonIgnore, Obsolete("Read agreed_terms (AgreedTerms); this is filled from it.")]
    public IList<string> Capabilities { get; set; } = [];

    /// <summary>Obsolete: agreements carry no roles; always empty.</summary>
    [JsonIgnore, Obsolete("Agreements carry no roles; always empty.")]
    public IList<string> Roles { get; set; } = [];

    /// <summary>Obsolete: the NA sends no agreement status; always empty.</summary>
    [JsonIgnore, Obsolete("The NA sends no agreement status; always empty.")]
    public string Status { get; set; } = "";

    /// <summary>Obsolete: the same as <see cref="EstablishedAt"/>.</summary>
    [JsonIgnore, Obsolete("Use EstablishedAt, which this is filled from.")]
    public string CreatedAt { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        if (AgreedTerms.ValueKind == JsonValueKind.Object
            && AgreedTerms.TryGetProperty("capabilities", out var caps)
            && caps.ValueKind == JsonValueKind.Array)
        {
            Capabilities = caps.EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        }
        CreatedAt = EstablishedAt;
#pragma warning restore CS0618
    }
}

// ── Boundary ──────────────────────────────────────────────────────────────────

/// <summary>
/// Returned by POST /admin/boundary/decide: the decision the NA signed.
/// Authorized is the answer; DenialReason says why when it is false.
/// </summary>
public sealed class BoundaryDecision : IJsonOnDeserialized
{
    /// <summary>Unique decision identifier.</summary>
    [JsonPropertyName("decision_id")]
    public string DecisionId { get; set; } = "";

    /// <summary>The context record (the request) the decision was made for.</summary>
    [JsonPropertyName("context_id")]
    public string ContextId { get; set; } = "";

    /// <summary>The agreement or delegated agreement the request ran under, if any.</summary>
    [JsonPropertyName("agreement_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgreementId { get; set; }

    /// <summary>True when every gate passed.</summary>
    [JsonPropertyName("authorized")]
    public bool Authorized { get; set; }

    /// <summary>Why the request was denied; null when it is authorized.</summary>
    // Written even when null (1.9.0): the NA signs the field as null, and a decision passed back without it fails its
    // signature.
    [JsonPropertyName("denial_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DenialReason { get; set; }

    /// <summary>Each gate's result, in evaluation order.</summary>
    [JsonPropertyName("gate_results")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement GateResults { get; set; }

    /// <summary>When the decision was made.</summary>
    [JsonPropertyName("decision_made_at")]
    public string DecisionMadeAt { get; set; } = "";

    /// <summary>When this authorization ends: a decision is not a lasting grant.</summary>
    [JsonPropertyName("decision_valid_until")]
    public string DecisionValidUntil { get; set; } = "";

    /// <summary>The operator whose key signed the decision.</summary>
    [JsonPropertyName("operator_sovereign_id")]
    public string OperatorSovereignId { get; set; } = "";

    /// <summary>Proof of a fresh evaluation, when the NA requires one; signed with the decision.</summary>
    [JsonPropertyName("freshness_proof")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement FreshnessProof { get; set; }

    /// <summary>The boundary policy the decision was made under (v0.58); signed when present.</summary>
    [JsonPropertyName("policy_binding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement PolicyBinding { get; set; }

    /// <summary>The membership attestation the decision was made on (v0.58.1); signed when present.</summary>
    [JsonPropertyName("attestation_binding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement AttestationBinding { get; set; }

    /// <summary>The operator's Ed25519 signature over the canonical decision.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }

    /// <summary>Obsolete: the same as <see cref="Authorized"/>.</summary>
    [JsonIgnore, Obsolete("Use Authorized, which this is filled from.")]
    public bool Allowed { get; set; }

    /// <summary>Obsolete: the same as <see cref="DenialReason"/>.</summary>
    [JsonIgnore, Obsolete("Use DenialReason, which this is filled from (null when authorized).")]
    public string? Reason { get; set; }

    /// <summary>Obsolete: the same as <see cref="DecisionMadeAt"/>.</summary>
    [JsonIgnore, Obsolete("Use DecisionMadeAt, which this is filled from.")]
    public string IssuedAt { get; set; } = "";

    /// <summary>Obsolete: not part of a decision; always null.</summary>
    [JsonIgnore, Obsolete("Not part of a decision (it is in the ContextRecord); always null.")]
    public string? RequestingAgentId { get; set; }

    /// <summary>Obsolete: not part of a decision; always null.</summary>
    [JsonIgnore, Obsolete("Not part of a decision; always null.")]
    public string? TargetAgentId { get; set; }

    /// <summary>Obsolete: not part of a decision; always null.</summary>
    [JsonIgnore, Obsolete("Not part of a decision; always null.")]
    public string? Capability { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Allowed = Authorized;
        Reason = DenialReason;
        IssuedAt = DecisionMadeAt;
#pragma warning restore CS0618
    }
}

// ── Evidence ──────────────────────────────────────────────────────────────────

/// <summary>
/// Passed to Evidence.Build — wrapped in {"decision": ...} before posting. The
/// NA signs what it is given: set Trusted, HopCount and the other fields from
/// the decision being recorded.
/// </summary>
public sealed class TrustDecision
{
    /// <summary>The sovereign that evaluated trust. Required by the NA.</summary>
    [JsonPropertyName("source_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSovereignId { get; set; }

    /// <summary>The sovereign whose recognition was evaluated. Required by the NA.</summary>
    [JsonPropertyName("target_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetSovereignId { get; set; }

    /// <summary>allow, warn, block or escalate; the NA refuses any other value (400 invalid_verdict).</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "";

    /// <summary>Machine-readable reason code for the verdict.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>The roles checked against the treaty scope.</summary>
    [JsonPropertyName("requested_roles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? RequestedRoles { get; set; }

    /// <summary>Whether an active recognition path was found.</summary>
    [JsonPropertyName("trusted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Trusted { get; set; }

    /// <summary>The recognition path found, hop by hop.</summary>
    [JsonPropertyName("trust_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<IDictionary<string, object?>>? TrustPath { get; set; }

    /// <summary>The number of treaty hops on the path.</summary>
    [JsonPropertyName("hop_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int HopCount { get; set; }

    /// <summary>The signals behind the verdict, each <c>{code, severity, detail}</c>.</summary>
    [JsonPropertyName("signals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<object>? Signals { get; set; }

    /// <summary>When trust was evaluated (ISO 8601).</summary>
    [JsonPropertyName("evaluated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvaluatedAt { get; set; }

    /// <summary>Not read by the NA.</summary>
    [JsonPropertyName("subject_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectId { get; set; }

    /// <summary>Not read by the NA.</summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object>? Context { get; set; }
}

/// <summary>Returned by POST /admin/trust-evidence.</summary>
public sealed class TrustEvidence : IJsonOnDeserialized
{
    /// <summary>Unique evidence identifier.</summary>
    [JsonPropertyName("evidence_id")]
    public string EvidenceId { get; set; } = "";

    /// <summary>The sovereign that issued and signed the evidence.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    public string IssuerSovereignId { get; set; } = "";

    /// <summary>The sovereign that evaluated trust, usually the issuer.</summary>
    [JsonPropertyName("source_sovereign_id")]
    public string SourceSovereignId { get; set; } = "";

    /// <summary>The sovereign whose recognition was evaluated.</summary>
    [JsonPropertyName("target_sovereign_id")]
    public string TargetSovereignId { get; set; } = "";

    /// <summary>allow, warn, block or escalate.</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "";

    /// <summary>Machine-readable reason code for the verdict.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>Whether an active recognition path was found.</summary>
    [JsonPropertyName("trusted")]
    public bool Trusted { get; set; }

    /// <summary>The number of treaty hops on the path.</summary>
    [JsonPropertyName("hop_count")]
    public int HopCount { get; set; }

    /// <summary>The roles checked against the treaty scope.</summary>
    [JsonPropertyName("requested_roles")]
    public IList<string> RequestedRoles { get; set; } = [];

    /// <summary>The signals behind the verdict, each <c>{code, severity, detail}</c>.</summary>
    [JsonPropertyName("signals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signals { get; set; }

    /// <summary>SHA-256 hex of the recognition graph the evaluation used.</summary>
    [JsonPropertyName("graph_digest")]
    public string GraphDigest { get; set; } = "";

    /// <summary>When trust was evaluated.</summary>
    [JsonPropertyName("evaluated_at")]
    public string EvaluatedAt { get; set; } = "";

    /// <summary>When the evidence was issued.</summary>
    [JsonPropertyName("issued_at")]
    public string IssuedAt { get; set; } = "";

    /// <summary>The issuer's signing key ID.</summary>
    [JsonPropertyName("issued_by")]
    public string IssuedBy { get; set; } = "";

    /// <summary>Operator metadata, if any.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Metadata { get; set; }

    /// <summary>The issuer's signatures over the canonical evidence.</summary>
    [JsonPropertyName("signatures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signatures { get; set; }

    /// <summary>Obsolete: the same as <see cref="IssuerSovereignId"/>.</summary>
    [JsonIgnore, Obsolete("Use IssuerSovereignId, which this is filled from.")]
    public string? IssuerId { get; set; }

    /// <summary>Obsolete: the same as <see cref="TargetSovereignId"/>.</summary>
    [JsonIgnore, Obsolete("Use TargetSovereignId, which this is filled from.")]
    public string? SubjectId { get; set; }

    /// <summary>Obsolete: the first entry of <see cref="Signatures"/>.</summary>
    [JsonIgnore, Obsolete("Use Signatures; this is filled from its first entry.")]
    public JsonElement Signature { get; set; }

    /// <summary>Obsolete: trust evidence names no decision; always null.</summary>
    [JsonIgnore, Obsolete("Trust evidence names no decision; always null.")]
    public string? DecisionId { get; set; }

    /// <summary>Obsolete: trust evidence does not embed the decision; always undefined.</summary>
    [JsonIgnore, Obsolete("Trust evidence does not embed the decision; always undefined.")]
    public JsonElement Decision { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        IssuerId = IssuerSovereignId;
        SubjectId = TargetSovereignId;
        Signature = ModelHelpers.FirstSignature(Signatures);
#pragma warning restore CS0618
    }
}

// ── Attestation ───────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/attestations.</summary>
public sealed class MembershipAttestation : IJsonOnDeserialized
{
    /// <summary>Unique attestation identifier.</summary>
    [JsonPropertyName("attestation_id")]
    public string AttestationId { get; set; } = "";

    /// <summary>The sovereign that issued the attestation.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    public string IssuerSovereignId { get; set; } = "";

    /// <summary>The member: a person, agent or service.</summary>
    [JsonPropertyName("subject_id")]
    public string SubjectId { get; set; } = "";

    /// <summary>The member's public key, when the attestation binds one.</summary>
    // Written even when null (1.9.0): the NA signs the field as null, and a record passed back without it fails its
    // signature.
    [JsonPropertyName("subject_public_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SubjectPublicKey { get; set; }

    /// <summary>The roles the issuer grants the member.</summary>
    [JsonPropertyName("roles")]
    public IList<string> Roles { get; set; } = [];

    /// <summary>The membership status on the issuer's side.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>When the attestation was issued.</summary>
    [JsonPropertyName("issued_at")]
    public string IssuedAt { get; set; } = "";

    /// <summary>Start of the attestation's validity.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>When the attestation expires.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    /// <summary>The issuer's signing key ID.</summary>
    [JsonPropertyName("issued_by")]
    public string IssuedBy { get; set; } = "";

    /// <summary>Scoped claims. Attestation-based boundary decisions read <c>capabilities</c> and <c>apps</c>.</summary>
    [JsonPropertyName("claims")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Claims { get; set; }

    /// <summary>The issuer's signatures over the canonical attestation.</summary>
    [JsonPropertyName("signatures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signatures { get; set; }

    /// <summary>Obsolete: the same as <see cref="SubjectId"/>.</summary>
    [JsonIgnore, Obsolete("Use SubjectId, which this is filled from.")]
    public string? SubjectSovereignId { get; set; }

    /// <summary>Obsolete: the first entry of <see cref="Signatures"/>.</summary>
    [JsonIgnore, Obsolete("Use Signatures; this is filled from its first entry.")]
    public JsonElement Signature { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        SubjectSovereignId = SubjectId;
        Signature = ModelHelpers.FirstSignature(Signatures);
#pragma warning restore CS0618
    }
}

// ── Disclosure ────────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/disclosure/commit.</summary>
public sealed class CapabilityCommitment : IJsonOnDeserialized
{
    /// <summary>Unique commitment identifier.</summary>
    [JsonPropertyName("commitment_id")]
    public string CommitmentId { get; set; } = "";

    /// <summary>The agreement whose capabilities are committed.</summary>
    [JsonPropertyName("agreement_id")]
    public string AgreementId { get; set; } = "";

    /// <summary>The sovereign that built and signed the commitment.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    public string IssuerSovereignId { get; set; } = "";

    /// <summary>SHA-256 hex Merkle root over the sorted capabilities.</summary>
    [JsonPropertyName("merkle_root")]
    public string MerkleRoot { get; set; } = "";

    /// <summary>How many capabilities are committed: the count, not the members.</summary>
    [JsonPropertyName("capability_count")]
    public int CapabilityCount { get; set; }

    /// <summary>When the commitment was made.</summary>
    [JsonPropertyName("committed_at")]
    public string CommittedAt { get; set; } = "";

    /// <summary>The issuer's signature over the canonical commitment.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }

    /// <summary>Obsolete: the same as <see cref="CommittedAt"/>.</summary>
    [JsonIgnore, Obsolete("Use CommittedAt, which this is filled from.")]
    public string IssuedAt { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        IssuedAt = CommittedAt;
#pragma warning restore CS0618
    }
}

/// <summary>Returned by POST /disclosure/prove.</summary>
public sealed class CapabilityMembershipProof : IJsonOnDeserialized
{
    /// <summary>Unique proof identifier.</summary>
    [JsonPropertyName("proof_id")]
    public string ProofId { get; set; } = "";

    /// <summary>The commitment the proof is made against.</summary>
    [JsonPropertyName("commitment_id")]
    public string CommitmentId { get; set; } = "";

    /// <summary>The sovereign presenting the proof.</summary>
    [JsonPropertyName("prover_sovereign_id")]
    public string ProverSovereignId { get; set; } = "";

    /// <summary>The one capability proved to be in the committed set.</summary>
    [JsonPropertyName("revealed_capability")]
    public string RevealedCapability { get; set; } = "";

    /// <summary>SHA-256 hex of the revealed capability.</summary>
    [JsonPropertyName("leaf_hash")]
    public string LeafHash { get; set; } = "";

    /// <summary>The sibling hashes from the leaf to the root.</summary>
    [JsonPropertyName("merkle_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MerklePath { get; set; }

    /// <summary>When the proof was made.</summary>
    [JsonPropertyName("proved_at")]
    public string ProvedAt { get; set; } = "";

    /// <summary>Obsolete: the same as <see cref="RevealedCapability"/>.</summary>
    [JsonIgnore, Obsolete("Use RevealedCapability, which this is filled from.")]
    public string Capability { get; set; } = "";

    /// <summary>Obsolete: the same as <see cref="MerklePath"/>.</summary>
    [JsonIgnore, Obsolete("Use MerklePath, which this is filled from.")]
    public JsonElement Proof { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Capability = RevealedCapability;
        Proof = MerklePath;
#pragma warning restore CS0618
    }
}

// ── Consensus ─────────────────────────────────────────────────────────────────

/// <summary>
/// A validator's signed vote on a JustificationProof (Python <c>ValidatorVote</c>), returned by
/// POST /admin/consensus/vote. Pass it back unchanged when assembling a proof: every field is signed.
/// </summary>
public sealed class ConsensusVote
{
    /// <summary>Unique vote identifier.</summary>
    [JsonPropertyName("vote_id")]
    public string VoteId { get; set; } = "";

    /// <summary>The justification proof voted on.</summary>
    [JsonPropertyName("proof_id")]
    public string ProofId { get; set; } = "";

    /// <summary>The boundary decision the proof is about.</summary>
    [JsonPropertyName("decision_id")]
    public string DecisionId { get; set; } = "";

    /// <summary>The validator that voted.</summary>
    [JsonPropertyName("validator_sovereign_id")]
    public string ValidatorSovereignId { get; set; } = "";

    /// <summary>True approves, false rejects.</summary>
    [JsonPropertyName("vote")]
    public bool Vote { get; set; }

    /// <summary>The validator's reason, if it gave one.</summary>
    // Written even when null (1.9.0): the NA signs the field as null, and a record passed back without it fails its
    // signature.
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Reason { get; set; }

    /// <summary>When the vote was cast.</summary>
    [JsonPropertyName("voted_at")]
    public string VotedAt { get; set; } = "";

    /// <summary>Binds the vote to the validator's view of the proof (v0.38); required on approve votes.</summary>
    // Written even when null (1.9.0): the NA signs the field as null, and a record passed back without it fails its
    // signature.
    [JsonPropertyName("context_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ContextDigest { get; set; }

    /// <summary>The validator's signature over the canonical vote.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary>
/// K-of-N approval over a JustificationProof, signed by the assembler (Python <c>ConsensusProof</c>).
/// Returned by POST /admin/consensus/proof.
/// </summary>
public sealed class ConsensusProof
{
    /// <summary>Unique consensus identifier.</summary>
    [JsonPropertyName("consensus_id")]
    public string ConsensusId { get; set; } = "";

    /// <summary>The justification proof approved.</summary>
    [JsonPropertyName("proof_id")]
    public string ProofId { get; set; } = "";

    /// <summary>The boundary decision the proof is about.</summary>
    [JsonPropertyName("decision_id")]
    public string DecisionId { get; set; } = "";

    /// <summary>K: distinct named validators that must approve.</summary>
    [JsonPropertyName("required_threshold")]
    public int RequiredThreshold { get; set; }

    /// <summary>N: the named validators.</summary>
    [JsonPropertyName("validator_sovereign_ids")]
    public IList<string> ValidatorSovereignIds { get; set; } = [];

    /// <summary>The validators' signed votes.</summary>
    [JsonPropertyName("votes")]
    public IList<ConsensusVote> Votes { get; set; } = [];

    /// <summary>When the threshold was reached.</summary>
    [JsonPropertyName("reached_at")]
    public string ReachedAt { get; set; } = "";

    /// <summary>When the proof lapses.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    /// <summary>Digest of the cascade assessment that cleared the proof; null on proofs before v0.38.</summary>
    // Written even when null (1.9.0): the NA signs the field as null, and a record passed back without it fails its
    // signature.
    [JsonPropertyName("cascade_assessment_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? CascadeAssessmentDigest { get; set; }

    /// <summary>The assembler's signature over the canonical proof.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary>
/// Returned by POST /consensus/verify. Reason is one of valid, missing_signature,
/// invalid_assembler_signature, threshold_not_met, invalid_vote_signature, unknown_validator_key,
/// vote_not_in_validator_set, expired, proof_id_mismatch, cascade_detected, missing_context_digest.
/// </summary>
public sealed class ConsensusVerification
{
    /// <summary>True when the proof verified.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>valid, or why the proof failed (listed on the class).</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>The verified proof's consensus ID.</summary>
    [JsonPropertyName("consensus_id")]
    public string? ConsensusId { get; set; }
}

// ── Data Usage ────────────────────────────────────────────────────────────────

/// <summary>A data source descriptor inside a DataAccessIntent.</summary>
public sealed class DataSourceDescriptor
{
    /// <summary>The data source's identifier. Required.</summary>
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = "";

    /// <summary>personal, proprietary, public or synthetic. Required.</summary>
    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "";

    /// <summary>The sovereign that owns the source. Required.</summary>
    [JsonPropertyName("owner_sovereign_id")]
    public string OwnerSovereignId { get; set; } = "";

    /// <summary>The source's classification tags, checked against a policy's prohibited tags.</summary>
    [JsonPropertyName("classification_tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? ClassificationTags { get; set; }

    /// <summary>
    /// Not part of the NA's descriptor, which drops it: the volume the NA checks is the intent's <see
    /// cref="DataAccessIntent.EstimatedVolumeBytes"/>.
    /// </summary>
    [JsonPropertyName("estimated_volume_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedVolumeBytes { get; set; }
}

/// <summary>Returned by POST /admin/data-usage/policy and GET /data-usage/policy.</summary>
public sealed class DataLicensePolicy : IJsonOnDeserialized
{
    /// <summary>Unique policy identifier.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The sovereign that grants access to its data.</summary>
    [JsonPropertyName("licensor_sovereign_id")]
    public string LicensorSovereignId { get; set; } = "";

    /// <summary>The sovereign allowed to access it.</summary>
    [JsonPropertyName("licensee_sovereign_id")]
    public string LicenseeSovereignId { get; set; } = "";

    /// <summary>The sources the licensee may access; empty denies every source.</summary>
    [JsonPropertyName("allowed_source_ids")]
    public IList<string> AllowedSourceIds { get; set; } = [];

    /// <summary>read, write, derive or train; empty allows reading only.</summary>
    [JsonPropertyName("allowed_access_types")]
    public IList<string> AllowedAccessTypes { get; set; } = [];

    /// <summary>Classification tags the licensee may not access.</summary>
    [JsonPropertyName("prohibited_classification_tags")]
    public IList<string> ProhibitedClassificationTags { get; set; } = [];

    // Written even when null (1.8.0): the NA signs the field as null, and a record passed
    // back without it fails its signature.
    /// <summary>The most bytes the licensee may access in one session; null for no limit.</summary>
    [JsonPropertyName("max_volume_bytes_per_session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? MaxVolumeBytesPerSession { get; set; }

    /// <summary>Start of the policy's validity.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>End of the policy's validity.</summary>
    [JsonPropertyName("valid_until")]
    public string ValidUntil { get; set; } = "";

    /// <summary>The licensor's signature over the canonical policy.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }

    /// <summary>Obsolete: the same as <see cref="LicensorSovereignId"/>.</summary>
    [JsonIgnore, Obsolete("Use LicensorSovereignId, which this is filled from.")]
    public string? LocalSovereignId { get; set; }

    /// <summary>Obsolete: policies name no purposes; always null.</summary>
    [JsonIgnore, Obsolete("Policies name no purposes (see AllowedAccessTypes); always null.")]
    public IList<string>? AllowedPurposes { get; set; }

    /// <summary>Obsolete: the same as <see cref="ValidFrom"/>.</summary>
    [JsonIgnore, Obsolete("Use ValidFrom, which this is filled from.")]
    public string IssuedAt { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        LocalSovereignId = LicensorSovereignId;
        IssuedAt = ValidFrom;
#pragma warning restore CS0618
    }
}

/// <summary>Returned by POST /admin/data-usage/intent.</summary>
public sealed class DataAccessIntent : IJsonOnDeserialized
{
    /// <summary>Unique intent identifier.</summary>
    [JsonPropertyName("intent_id")]
    public string IntentId { get; set; } = "";

    /// <summary>The agent declaring the access.</summary>
    [JsonPropertyName("agent_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentSovereignId { get; set; }

    /// <summary>The boundary decision the access runs under.</summary>
    [JsonPropertyName("decision_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionId { get; set; }

    /// <summary>The sources the agent will access.</summary>
    [JsonPropertyName("declared_sources")]
    public IList<DataSourceDescriptor> DeclaredSources { get; set; } = [];

    /// <summary>How it will access them: read, write, derive or train.</summary>
    [JsonPropertyName("declared_access_types")]
    public IList<string> DeclaredAccessTypes { get; set; } = [];

    // Written even when null (1.8.0): the NA signs the field as null, and a record passed
    // back without it fails its signature.
    /// <summary>Estimated bytes to access; null when not estimated.</summary>
    [JsonPropertyName("estimated_volume_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? EstimatedVolumeBytes { get; set; }

    /// <summary>When the intent was declared.</summary>
    [JsonPropertyName("declared_at")]
    public string DeclaredAt { get; set; } = "";

    /// <summary>When the intent lapses.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "";

    /// <summary>The signature over the canonical intent.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }

    /// <summary>Obsolete: the same as <see cref="DeclaredSources"/>.</summary>
    [JsonIgnore, Obsolete("Use DeclaredSources, which this is filled from.")]
    public IList<DataSourceDescriptor> Sources { get; set; } = [];

    /// <summary>Obsolete: the same as <see cref="DeclaredAccessTypes"/>.</summary>
    [JsonIgnore, Obsolete("Use DeclaredAccessTypes, which this is filled from.")]
    public IList<string> AccessTypes { get; set; } = [];

    /// <summary>Obsolete: intents name no policy; always null.</summary>
    [JsonIgnore, Obsolete("Intents name no policy; always null.")]
    public string? PolicyId { get; set; }

    /// <summary>Obsolete: intents name no issuer (see <see cref="AgentSovereignId"/>); always null.</summary>
    [JsonIgnore, Obsolete("Intents name no issuer (see AgentSovereignId); always null.")]
    public string? IssuerId { get; set; }

    /// <summary>Obsolete: the same as <see cref="DeclaredAt"/>.</summary>
    [JsonIgnore, Obsolete("Use DeclaredAt, which this is filled from.")]
    public string IssuedAt { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Sources = DeclaredSources;
        AccessTypes = DeclaredAccessTypes;
        IssuedAt = DeclaredAt;
#pragma warning restore CS0618
    }
}

// ── Shared ────────────────────────────────────────────────────────────────────

/// <summary>
/// Returned by the public /verify endpoints. Agreement, boundary and trust-evidence
/// verification answer "accepted"; disclosure and data-usage verification answer
/// "valid". Valid and Accepted are both true when the NA accepted the material,
/// whichever name it used. The other properties are set by the endpoints that send them.
/// </summary>
public sealed class VerifyResult : IJsonOnDeserialized
{
    /// <summary>True when the NA accepted the material; always equal to <see cref="Accepted"/>.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>True when the NA accepted the material; always equal to <see cref="Valid"/>.</summary>
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    /// <summary>Why the material was refused, or the endpoint's reason code when it was accepted.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>Boundary verification: whether the verified decision authorizes the action.</summary>
    [JsonPropertyName("authorized")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Authorized { get; set; }

    /// <summary>Agreement verification: the agreement's ID.</summary>
    [JsonPropertyName("agreement_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgreementId { get; set; }

    /// <summary>Boundary verification: the decision's ID.</summary>
    [JsonPropertyName("decision_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionId { get; set; }

    /// <summary>Trust-evidence verification: the evidence's ID.</summary>
    [JsonPropertyName("evidence_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; set; }

    /// <summary>Disclosure verification: the commitment's ID.</summary>
    [JsonPropertyName("commitment_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitmentId { get; set; }

    /// <summary>Trust-evidence verification: the sovereign that issued the evidence.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuerSovereignId { get; set; }

    /// <summary>Trust-evidence verification: the evidence's verdict.</summary>
    [JsonPropertyName("verdict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict { get; set; }

    /// <summary>Data-usage verification: the number of violations found.</summary>
    [JsonPropertyName("violation_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ViolationCount { get; set; }

    /// <summary>Data-usage verification: the first violation's reason; Reason is set from it.</summary>
    [JsonPropertyName("violation_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ViolationReason { get; set; }

    /// <summary>Data-usage verification: each violation found.</summary>
    [JsonPropertyName("violations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Violations { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        var ok = Valid || Accepted;
        Valid = ok;
        Accepted = ok;
        Reason ??= ViolationReason;
    }
}

// ── Boundary evaluation (v0.58) ───────────────────────────────────────────────

/// <summary>
/// Request body for POST /admin/boundary/evaluate: one basis, an <see cref="AttestationId"/> or an
/// <see cref="Agreement"/>, and the request's context.
/// </summary>
public sealed class EvaluateRequest
{
    /// <summary>The capability requested, such as <c>sp-secret.rotate</c>. Required.</summary>
    [JsonPropertyName("requested_capability")]
    public string RequestedCapability { get; set; } = "";

    /// <summary>The basis: an NA-issued membership attestation (v0.58.1). Set this or <see cref="Agreement"/>.</summary>
    [JsonPropertyName("attestation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttestationId { get; set; }

    /// <summary>
    /// The basis: a signed agreement as the NA returned it (from JSON, deserialize it with
    /// <see cref="Auth.SerializerOptions"/>). Set this or <see cref="AttestationId"/>.
    /// </summary>
    [JsonPropertyName("agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgreementRecord? Agreement { get; set; }

    /// <summary>The request's parameters and attributes, which the policy gates read. Never put secret values in it.</summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvaluationContext? Context { get; set; }
}

/// <summary>The context of a boundary evaluation (<see cref="EvaluateRequest.Context"/>).</summary>
public sealed class EvaluationContext
{
    /// <summary>The request's parameters, such as <c>app_id</c> or <c>lifetime_days</c>; gates read them as <c>request_parameters.&lt;name&gt;</c>.</summary>
    [JsonPropertyName("request_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object?>? RequestParameters { get; set; }

    /// <summary>Normalized facts for policy gates, such as <c>owner</c> or <c>environment</c>; gates read them as <c>attributes.&lt;name&gt;</c>.</summary>
    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object?>? Attributes { get; set; }

    /// <summary>Who requests; defaults to the attestation's subject or the agreement's responder.</summary>
    [JsonPropertyName("requester_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequesterSovereignId { get; set; }

    /// <summary>Who provides the capability.</summary>
    [JsonPropertyName("provider_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderSovereignId { get; set; }

    /// <summary>The context record's ID; the NA assigns one when unset.</summary>
    [JsonPropertyName("context_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextId { get; set; }

    /// <summary>The context's freshness sequence, for a basis that requires fresh context.</summary>
    [JsonPropertyName("context_freshness_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextFreshnessSeq { get; set; }

    /// <summary>An agreement basis only: <c>direct</c> (the default), <c>agreement</c> or <c>delegation</c>.</summary>
    [JsonPropertyName("parent_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentKind { get; set; }
}

/// <summary>
/// Returned by POST /admin/boundary/evaluate: the signed decision and its justification proof. A denial is a signed
/// decision with <see cref="BoundaryDecision.Authorized"/> false, not an error.
/// </summary>
public sealed class BoundaryEvaluation
{
    /// <summary>The decision the NA signed, with its policy and attestation bindings.</summary>
    [JsonPropertyName("decision")]
    public BoundaryDecision Decision { get; set; } = new();

    /// <summary>The signed trace of every gate the decision passed or failed, as received.</summary>
    [JsonPropertyName("justification_proof")]
    public JsonElement JustificationProof { get; set; }
}

// ── Boundary policies (v0.58) ─────────────────────────────────────────────────

/// <summary>
/// A boundary policy as declared, for validation and publication (POST /admin/boundary-policies): the NA assigns the
/// version, the issuer and the signature.
/// </summary>
public sealed class BoundaryPolicyIntent
{
    /// <summary>The policy's ID; each publication adds a version of it. Required.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>What the policy is for.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Start of the policy's validity (ISO 8601). Required.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>End of the policy's validity (ISO 8601). Required.</summary>
    [JsonPropertyName("valid_until")]
    public string ValidUntil { get; set; } = "";

    /// <summary>Which requests the policy applies to; every request when unset.</summary>
    [JsonPropertyName("selector")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PolicySelector? Selector { get; set; }

    /// <summary>The gates a request must pass, in order.</summary>
    [JsonPropertyName("gates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<GateSpec>? Gates { get; set; }

    /// <summary>Per gate ID (or <c>*</c> for every other gate), what a DENY by that gate leads to (1.4.0).</summary>
    [JsonPropertyName("remediation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, GateRemediation>? Remediation { get; set; }

    /// <summary>
    /// True when every change allowed under the policy is reviewed by a second holder (1.4.0). Sent only when true: the
    /// NA accepts no other value.
    /// </summary>
    [JsonPropertyName("review_required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReviewRequired { get; set; }

    /// <summary>Marks the policy's capability as the emergency form of an ordinary one (1.4.0).</summary>
    [JsonPropertyName("emergency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmergencyCapability? Emergency { get; set; }
}

/// <summary>What a change judged DENY by a gate leads to (<see cref="BoundaryPolicyIntent.Remediation"/>, 1.4.0).</summary>
public sealed class GateRemediation
{
    /// <summary>
    /// <c>cancel</c>: the controller cancels the change once <see cref="GraceSeconds"/> have passed since the judgement;
    /// <c>alert_only</c>: the denial is recorded and its author told, and nothing is changed.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    /// <summary>How long after the judgement the change is cancelled, in seconds.</summary>
    [JsonPropertyName("grace_seconds")]
    public int GraceSeconds { get; set; }
}

/// <summary>
/// Marks a policy's capability as the emergency form of an ordinary one (<see cref="BoundaryPolicyIntent.Emergency"/>,
/// 1.4.0). The policy must require the justification parameter, every change made under it is reviewed, and what it
/// creates lives at most <see cref="MaxLifetimeSeconds"/>.
/// </summary>
public sealed class EmergencyCapability
{
    /// <summary>The ordinary capability the emergency capability performs.</summary>
    [JsonPropertyName("stands_for")]
    public string StandsFor { get; set; } = "";

    /// <summary>The request parameter that carries the justification.</summary>
    [JsonPropertyName("justification_parameter")]
    public string JustificationParameter { get; set; } = "";

    /// <summary>The longest an emergency change lives, in seconds (at least 60).</summary>
    [JsonPropertyName("max_lifetime_seconds")]
    public int MaxLifetimeSeconds { get; set; }
}

/// <summary>Which requests a policy applies to: a request matches when it matches every list that is not empty.</summary>
public sealed class PolicySelector
{
    /// <summary>Capabilities, with <c>*</c> wildcards such as <c>sp-secret.*</c>.</summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? Capabilities { get; set; }

    /// <summary>Requesting sovereigns.</summary>
    [JsonPropertyName("requester_sovereign_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? RequesterSovereignIds { get; set; }

    /// <summary>Providing sovereigns.</summary>
    [JsonPropertyName("provider_sovereign_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? ProviderSovereignIds { get; set; }

    /// <summary>Agreements.</summary>
    [JsonPropertyName("agreement_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? AgreementIds { get; set; }

    /// <summary>The basis of the request: <c>attestation</c>, <c>direct</c>, <c>agreement</c> or <c>delegation</c>.</summary>
    [JsonPropertyName("parent_kinds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? ParentKinds { get; set; }

    /// <summary>Request parameters and the values they must equal.</summary>
    [JsonPropertyName("parameter_equals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, IList<object?>>? ParameterEquals { get; set; }
}

/// <summary>One gate of a policy.</summary>
public sealed class GateSpec
{
    /// <summary>The gate's ID within the policy.</summary>
    [JsonPropertyName("gate_id")]
    public string GateId { get; set; } = "";

    /// <summary>A gate type installed on the NA, such as <c>attestation_claim.v1</c> or <c>max_value.v1</c>.</summary>
    [JsonPropertyName("gate_type")]
    public string GateType { get; set; } = "";

    /// <summary>The gate's position: gates run in ascending order.</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary><c>enforce</c> (the default) or <c>observe</c>: an observed failure is reported but does not deny.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>The gate type's settings, such as <c>{"path": "request_parameters.lifetime_days", "max": 90}</c>.</summary>
    [JsonPropertyName("config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object?>? Config { get; set; }

    /// <summary>True to show the gate's input in the justification proof.</summary>
    [JsonPropertyName("disclose_input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiscloseInput { get; set; }
}

/// <summary>
/// A boundary policy version as the NA signed and stored it (POST /admin/boundary-policies). Pass it back unchanged:
/// every field but the signature is signed.
/// </summary>
public sealed class BoundaryPolicy
{
    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The version, from 1.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>What the policy is for.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Start of the policy's validity.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>End of the policy's validity.</summary>
    [JsonPropertyName("valid_until")]
    public string ValidUntil { get; set; } = "";

    /// <summary>Which requests the policy applies to, every list present.</summary>
    [JsonPropertyName("selector")]
    public PolicySelector Selector { get; set; } = new();

    /// <summary>The gates, every field present.</summary>
    [JsonPropertyName("gates")]
    public IList<GateSpec> Gates { get; set; } = [];

    /// <summary>What a DENY by a gate leads to (1.4.0), as signed (raw, so it is passed back exactly); signed when present.</summary>
    [JsonPropertyName("remediation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Remediation { get; set; }

    /// <summary>Present (true) when changes under the policy need a second holder's review (1.4.0); signed when present.</summary>
    [JsonPropertyName("review_required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ReviewRequired { get; set; }

    /// <summary>The policy's emergency capability (1.4.0), as signed; signed when present.</summary>
    [JsonPropertyName("emergency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Emergency { get; set; }

    /// <summary>When the NA signed this version.</summary>
    [JsonPropertyName("issued_at")]
    public string IssuedAt { get; set; } = "";

    /// <summary>The NA's signing key ID.</summary>
    [JsonPropertyName("issued_by")]
    public string IssuedBy { get; set; } = "";

    /// <summary>The NA's sovereign ID.</summary>
    [JsonPropertyName("issuer_sovereign_id")]
    public string IssuerSovereignId { get; set; } = "";

    /// <summary>The NA's signature over the canonical policy.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary>Returned by POST /admin/boundary-policies/validate: a dry run against the NA's installed gate types.</summary>
public sealed class PolicyValidation
{
    /// <summary>True when the intent would publish.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>What would refuse it; empty when valid.</summary>
    [JsonPropertyName("issues")]
    public IList<PolicyIssue> Issues { get; set; } = [];

    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The version a publication would create.</summary>
    [JsonPropertyName("next_version")]
    public int NextVersion { get; set; }
}

/// <summary>One problem with a policy intent; publishing refuses the intent with these in the error's details.</summary>
public sealed class PolicyIssue
{
    /// <summary>The problem's code, such as <c>unknown_gate_type</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    /// <summary>What is wrong.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>The gate the problem is in, if any.</summary>
    [JsonPropertyName("gate_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GateId { get; set; }
}

/// <summary>One stored version of a policy (GET /admin/boundary-policies and a policy's history).</summary>
public sealed class PolicyVersionSummary
{
    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>True when this version is active.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>SHA-256 hex of the signed policy (<see cref="OfflineVerifier.PolicyDigest"/>).</summary>
    [JsonPropertyName("policy_digest")]
    public string PolicyDigest { get; set; } = "";

    /// <summary>When the version was stored.</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    /// <summary>When it was last activated; null when never.</summary>
    [JsonPropertyName("activated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ActivatedAt { get; set; }

    /// <summary>When it was last deactivated; null when never.</summary>
    [JsonPropertyName("deactivated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DeactivatedAt { get; set; }

    /// <summary>False when the stored version no longer matches its signature or digest.</summary>
    [JsonPropertyName("integrity_ok")]
    public bool IntegrityOk { get; set; }

    /// <summary>What the policy is for.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Start of the version's validity.</summary>
    [JsonPropertyName("valid_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidFrom { get; set; }

    /// <summary>End of the version's validity.</summary>
    [JsonPropertyName("valid_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidUntil { get; set; }

    /// <summary>How many gates the version has.</summary>
    [JsonPropertyName("gate_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GateCount { get; set; }

    /// <summary>True when the version's selector matches every request.</summary>
    [JsonPropertyName("global")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Global { get; set; }

    /// <summary>The signed policy; in a policy's history only.</summary>
    [JsonPropertyName("policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoundaryPolicy? Policy { get; set; }
}

/// <summary>Returned by GET /admin/boundary-policies/active: the active set, its health and the enforcement mode.</summary>
public sealed class ActivePolicyStatus
{
    /// <summary><c>required</c> or <c>optional</c>: whether every boundary decision needs an active policy.</summary>
    [JsonPropertyName("enforcement")]
    public string Enforcement { get; set; } = "";

    /// <summary>False when an active policy fails verification: every policy-aware evaluation then denies.</summary>
    [JsonPropertyName("policy_set_healthy")]
    public bool PolicySetHealthy { get; set; }

    /// <summary>The active policies that fail verification, and why.</summary>
    [JsonPropertyName("problems")]
    public IList<PolicyProblem> Problems { get; set; } = [];

    /// <summary>The gate types installed on the NA.</summary>
    [JsonPropertyName("registry_gate_types")]
    public IList<string> RegistryGateTypes { get; set; } = [];

    /// <summary>The active policies.</summary>
    [JsonPropertyName("active")]
    public IList<ActivePolicy> Active { get; set; } = [];
}

/// <summary>An active policy that fails verification (<see cref="ActivePolicyStatus.Problems"/>).</summary>
public sealed class PolicyProblem
{
    /// <summary>The policy, as <c>policy_id</c> and version.</summary>
    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "";

    /// <summary>Why it fails.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>One active policy (<see cref="ActivePolicyStatus.Active"/>).</summary>
public sealed class ActivePolicy
{
    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The active version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>SHA-256 hex of the signed policy.</summary>
    [JsonPropertyName("policy_digest")]
    public string PolicyDigest { get; set; } = "";

    /// <summary>Start of the version's validity.</summary>
    [JsonPropertyName("valid_from")]
    public string ValidFrom { get; set; } = "";

    /// <summary>End of the version's validity.</summary>
    [JsonPropertyName("valid_until")]
    public string ValidUntil { get; set; } = "";

    /// <summary>How many gates the version has.</summary>
    [JsonPropertyName("gate_count")]
    public int GateCount { get; set; }

    /// <summary>Which requests it applies to.</summary>
    [JsonPropertyName("selector")]
    public PolicySelector Selector { get; set; } = new();

    /// <summary>The signed policy.</summary>
    [JsonPropertyName("policy")]
    public BoundaryPolicy Policy { get; set; } = new();
}

/// <summary>Returned by GET /admin/boundary-policies/{policy_id}/history: every version, newest first.</summary>
public sealed class PolicyHistory
{
    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>Every version, with its signed body.</summary>
    [JsonPropertyName("versions")]
    public IList<PolicyVersionSummary> Versions { get; set; } = [];
}

/// <summary>Returned by activating or deactivating a policy version.</summary>
public sealed class PolicyActivation
{
    /// <summary>The policy's ID.</summary>
    [JsonPropertyName("policy_id")]
    public string PolicyId { get; set; } = "";

    /// <summary>The version activated or deactivated.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Activation only: the version that was active before; null when none was.</summary>
    [JsonPropertyName("previous_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PreviousVersion { get; set; }

    /// <summary>True after an activation, false after a deactivation.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

/// <summary>Returned by POST /boundary-policies/verify.</summary>
public sealed class PolicyVerification
{
    /// <summary>True when the signature verifies.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary><c>valid</c>, or why the policy was refused, such as <c>invalid_signature</c>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>The policy's ID, when it could be read.</summary>
    [JsonPropertyName("policy_id")]
    public string? PolicyId { get; set; }

    /// <summary>The policy's version, when it could be read.</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>SHA-256 hex of the policy as received, when it could be read.</summary>
    [JsonPropertyName("policy_digest")]
    public string? PolicyDigest { get; set; }
}

/// <summary>The body of GET /admin/boundary-policies.</summary>
internal sealed class PolicyList
{
    [JsonPropertyName("policies")]
    public IReadOnlyList<PolicyVersionSummary> Policies { get; set; } = [];
}

// ── Health (v0.60) ────────────────────────────────────────────────────────────

/// <summary>Returned by GET /healthz: the process is alive, without checking its dependencies.</summary>
public sealed class LivenessStatus
{
    /// <summary><c>ok</c> when the process answers.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>
/// Returned by GET /readyz: whether this NA instance can serve. <see cref="Ready"/> is false when the NA answered
/// 503 <c>service_not_ready</c>; the failing checks are then in the other properties.
/// </summary>
public sealed class Readiness
{
    /// <summary>True when the instance is ready.</summary>
    [JsonIgnore]
    public bool Ready => Status == "ready";

    /// <summary><c>ready</c>, or <c>not_ready</c> when the NA answered 503.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The instance that answered, as host and process.</summary>
    [JsonPropertyName("instance")]
    public string Instance { get; set; } = "";

    /// <summary><c>on</c> when several instances share one database.</summary>
    [JsonPropertyName("ha_mode")]
    public string HaMode { get; set; } = "";

    /// <summary>The database: writable at the expected schema version.</summary>
    [JsonPropertyName("database")]
    public ReadinessDatabase Database { get; set; } = new();

    /// <summary>The NA signing key this instance loaded.</summary>
    [JsonPropertyName("signing_key")]
    public ReadinessSigningKey SigningKey { get; set; } = new();

    /// <summary>The NA key's state (1.5.0): anything but <c>signing</c> is not ready.</summary>
    [JsonPropertyName("na_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReadinessNAKey? NAKeyState { get; set; }

    /// <summary>Records due to be signed again by the current NA key, and whether that is pending.</summary>
    [JsonPropertyName("reissue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Reissue { get; set; }

    /// <summary><c>memory</c>, or <c>database</c> when instances share the rate limits (required in HA mode).</summary>
    [JsonPropertyName("rate_limiter")]
    public string RateLimiter { get; set; } = "";

    /// <summary>The SQLite path, or the PostgreSQL URL without its password; only when ready.</summary>
    [JsonPropertyName("db_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DbPath { get; set; }
}

/// <summary>The database check of <see cref="Readiness"/>.</summary>
public sealed class ReadinessDatabase
{
    /// <summary><c>sqlite</c> or <c>postgres</c>.</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "";

    /// <summary>True when the database accepted a write.</summary>
    [JsonPropertyName("writable")]
    public bool Writable { get; set; }

    /// <summary>The schema version found; null when the database could not be read.</summary>
    [JsonPropertyName("schema_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; set; }

    /// <summary>The schema version this NA needs.</summary>
    [JsonPropertyName("expected_schema_version")]
    public int ExpectedSchemaVersion { get; set; }

    /// <summary>When not ready: <c>schema_version_mismatch</c> or the error's type.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>The signing key check of <see cref="Readiness"/>.</summary>
public sealed class ReadinessSigningKey
{
    /// <summary>The NA key's ID.</summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";

    /// <summary>Where the key is held: <c>file</c>, <c>env</c> or <c>azure-keyvault</c>.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>SHA-256 (hex) of the NA public key, the same on every instance of one NA.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";
}

/// <summary>The NA key's state in <see cref="Readiness"/> (1.5.0).</summary>
public sealed class ReadinessNAKey
{
    /// <summary>
    /// <c>signing</c>; <c>pending</c>, a new key started before its succession takes effect; or <c>stopped</c>,
    /// succeeded or expired.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>The key's position in the key history.</summary>
    [JsonPropertyName("key_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeySequence { get; set; }

    /// <summary>A pending key: when it starts signing.</summary>
    [JsonPropertyName("signs_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignsFrom { get; set; }
}

/// <summary>Returned by GET /health: the network, its version and the state of policies and evidence.</summary>
public sealed class NAHealth
{
    /// <summary><c>healthy</c> when the NA answers.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The network's name.</summary>
    [JsonPropertyName("network")]
    public string Network { get; set; } = "";

    /// <summary>The network's version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary><c>unhealthy</c> when an active boundary policy fails verification: every policy-aware evaluation then denies.</summary>
    [JsonPropertyName("boundary_policies")]
    public string BoundaryPolicies { get; set; } = "";

    /// <summary><c>required</c> or <c>optional</c>: whether every boundary decision needs an active policy.</summary>
    [JsonPropertyName("boundary_policy_enforcement")]
    public string BoundaryPolicyEnforcement { get; set; } = "";

    /// <summary>The evidence store's mode, such as <c>on</c> or <c>off</c>.</summary>
    [JsonPropertyName("evidence_store")]
    public string EvidenceStore { get; set; } = "";
}

/// <summary>The readiness of one configured endpoint (<see cref="HealthClient.Endpoints"/>).</summary>
public sealed class EndpointReadiness
{
    /// <summary>The endpoint probed.</summary>
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    /// <summary>True when the endpoint answered; false also when it answered 200 with a body that is not strict JSON.</summary>
    [JsonPropertyName("reachable")]
    public bool Reachable { get; set; }

    /// <summary>True when it answered ready.</summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    /// <summary>Its readiness checks, when it sent them.</summary>
    [JsonPropertyName("readiness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Readiness? Readiness { get; set; }

    /// <summary>Why it is not ready: the HTTP status, or the connection error.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

// ── NA key history (v1.5.0) ───────────────────────────────────────────────────

/// <summary>
/// Returned by GET /na/key-history: the signed genesis block, every key succession and each key's validity. It
/// proves nothing by itself: <see cref="NetworkAuthorityClient.VerifiedKeyHistory"/> rebuilds the history from the
/// root key the caller holds.
/// </summary>
public sealed class KeyHistoryView
{
    /// <summary>The NA's sovereign ID.</summary>
    [JsonPropertyName("sovereign_id")]
    public string SovereignId { get; set; } = "";

    /// <summary>The root key the NA names: compare it with the one you hold, never trust it alone.</summary>
    [JsonPropertyName("root_public_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RootPublicKey { get; set; }

    /// <summary>How long after a succession the outgoing key is still accepted, in seconds.</summary>
    [JsonPropertyName("handover_seconds")]
    public int HandoverSeconds { get; set; }

    /// <summary>The signed genesis block, as received.</summary>
    [JsonPropertyName("genesis")]
    public JsonElement Genesis { get; set; }

    /// <summary>The signed key successions, oldest first, as received.</summary>
    [JsonPropertyName("successions")]
    public JsonElement Successions { get; set; }

    /// <summary>Each key and its validity, as the NA computes it.</summary>
    [JsonPropertyName("keys")]
    public IList<KeyHistoryKey> Keys { get; set; } = [];

    /// <summary>The key sequence of the key this NA signs with now.</summary>
    [JsonPropertyName("signing_key_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? SigningKeySequence { get; set; }

    /// <summary>The NA's vouching grace in seconds (1.8.0). Unsigned: verification takes it only to shorten the grace.</summary>
    [JsonPropertyName("vouching_grace_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VouchingGraceSeconds { get; set; }
}

/// <summary>One key of <see cref="KeyHistoryView"/>, as the NA describes it.</summary>
public sealed class KeyHistoryKey
{
    /// <summary>True for the key the NA signs with now.</summary>
    [JsonPropertyName("current")]
    public bool Current { get; set; }

    /// <summary>The key's ID.</summary>
    [JsonPropertyName("key_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? KeyId { get; set; }

    /// <summary>The key's position in the history, from 1 for the genesis key.</summary>
    [JsonPropertyName("key_sequence")]
    public int KeySequence { get; set; }

    /// <summary>The key's public key (base64).</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = "";

    /// <summary>When the next key took effect; null while this key is current.</summary>
    [JsonPropertyName("succeeded_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SucceededAt { get; set; }

    /// <summary>When the key became valid; null for the genesis key.</summary>
    [JsonPropertyName("valid_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidFrom { get; set; }

    /// <summary>The key's declared end of validity.</summary>
    [JsonPropertyName("valid_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ValidTo { get; set; }

    /// <summary>True when <see cref="ValidTo"/> is enforced: only for a key named by a succession.</summary>
    [JsonPropertyName("valid_to_enforced")]
    public bool ValidToEnforced { get; set; }
}

/// <summary>The outcomes an executor records (<see cref="ExecutionEvidence.Outcome"/>).</summary>
public static class ExecutionOutcomes
{
    /// <summary>The action completed.</summary>
    public const string Success = "success";
    /// <summary>The action failed; nothing changed.</summary>
    public const string Failure = "failure";
    /// <summary>The action completed in part.</summary>
    public const string Partial = "partial";
}

/// <summary>What an execution did to its resource (<see cref="ExecutionEvidence.ResourceAction"/>).</summary>
public static class ResourceActions
{
    /// <summary>The resource was created.</summary>
    public const string Create = "create";
    /// <summary>The resource's credential was replaced.</summary>
    public const string Rotate = "rotate";
    /// <summary>The resource was revoked.</summary>
    public const string Revoke = "revoke";
    /// <summary>The resource was changed otherwise.</summary>
    public const string Update = "update";
    /// <summary>The resource was deleted.</summary>
    public const string Delete = "delete";
}

/// <summary>
/// An executor's signed record of one execution under a decision (Python <c>ExecutionEvidence</c>), built by
/// <see cref="ExecutionRecorder"/> and submitted with <see cref="EvidenceStoreClient.Submit"/>. Records under one
/// decision form a chain (<see cref="SequenceNo"/>, <see cref="PrevEvidenceDigest"/>); since v0.59 a record may also
/// name the resource it acted on and link to the resource's previous record from any decision.
/// <para>
/// Metadata only: identifiers and settings, never a secret value. The resource fields are left out when null, as the
/// NA signs them; every other nullable field is written as null, so a record passed back keeps its signed form.
/// </para>
/// </summary>
public sealed class ExecutionEvidence
{
    /// <summary>The record's ID (a UUID); a resubmission with the same ID and content is a duplicate.</summary>
    [JsonPropertyName("evidence_id")]
    public string EvidenceId { get; set; } = "";

    /// <summary>Position under the decision, from 1.</summary>
    [JsonPropertyName("sequence_no")]
    public long SequenceNo { get; set; }

    /// <summary>The decision that authorized the execution.</summary>
    [JsonPropertyName("decision_id")]
    public string DecisionId { get; set; } = "";

    /// <summary>The decision's context record.</summary>
    [JsonPropertyName("context_id")]
    public string ContextId { get; set; } = "";

    /// <summary>The agreement or attestation the decision ran under.</summary>
    [JsonPropertyName("agreement_id")]
    public string AgreementId { get; set; } = "";

    /// <summary>The sovereign that executed; its signing key must be registered for it.</summary>
    [JsonPropertyName("executor_sovereign_id")]
    public string ExecutorSovereignId { get; set; } = "";

    /// <summary>The capability executed: the decision's capability.</summary>
    [JsonPropertyName("executed_capability")]
    public string ExecutedCapability { get; set; } = "";

    /// <summary>What was used, as metadata (an object); the NA refuses secret material in it.</summary>
    [JsonPropertyName("execution_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ExecutionParameters { get; set; }

    /// <summary>When it was executed, in canonical form; never before the decision.</summary>
    [JsonPropertyName("executed_at")]
    public string ExecutedAt { get; set; } = "";

    /// <summary>A value of <see cref="ExecutionOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    /// <summary>A short description of the outcome, or null; counts toward the metadata limit.</summary>
    [JsonPropertyName("outcome_detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? OutcomeDetail { get; set; }

    /// <summary>The digest of the previous record under the decision; null for the first.</summary>
    [JsonPropertyName("prev_evidence_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrevEvidenceDigest { get; set; }

    /// <summary>The resource acted on, such as <c>kv:vault/secret</c>: an identifier, never a value (v0.59).</summary>
    [JsonPropertyName("resource_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; set; }

    /// <summary>A value of <see cref="ResourceActions"/>; set with <see cref="ResourceId"/>.</summary>
    [JsonPropertyName("resource_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceAction { get; set; }

    /// <summary>Position in the resource's history across decisions, from 1; set with <see cref="ResourceId"/>.</summary>
    [JsonPropertyName("resource_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ResourceSequence { get; set; }

    /// <summary>The digest of the resource's previous record; null for its first.</summary>
    [JsonPropertyName("prev_resource_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrevResourceDigest { get; set; }

    /// <summary>The executor's signature, <c>{"key_id", "sig"}</c>, over the record without it; undefined until signed.</summary>
    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature { get; set; }
}

/// <summary>Where the next record of a resource must link: the position and digest of its latest record.</summary>
public sealed class ResourceHead
{
    /// <summary>The latest record's position on the resource chain.</summary>
    [JsonPropertyName("resource_sequence")]
    public long ResourceSequence { get; set; }

    /// <summary>The latest record's digest (<see cref="ExecutionRecorder.Digest"/>).</summary>
    [JsonPropertyName("record_digest")]
    public string RecordDigest { get; set; } = "";
}

/// <summary>Returned by GET /admin/evidence/resource-heads/&lt;resource_id&gt;: the NA's head of one resource chain.</summary>
public sealed class StoredResourceHead
{
    /// <summary>The resource.</summary>
    [JsonPropertyName("resource_id")]
    public string ResourceId { get; set; } = "";

    /// <summary>The head's position on the resource chain.</summary>
    [JsonPropertyName("resource_sequence")]
    public long ResourceSequence { get; set; }

    /// <summary>The head's digest: what the next record's <c>prev_resource_digest</c> must be.</summary>
    [JsonPropertyName("record_digest")]
    public string RecordDigest { get; set; } = "";

    /// <summary>
    /// v1.6.0: what the head is, <c>execution</c>, <c>grant_execution</c> or <c>retention_checkpoint</c> (retention
    /// removed every stored record); absent from an older NA.
    /// </summary>
    [JsonPropertyName("entry_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryKind { get; set; }

    /// <summary>v1.6.0: the head's <c>evidence_id</c> or <c>use_id</c>; null for a retention checkpoint.</summary>
    [JsonPropertyName("record_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordId { get; set; }

    /// <summary>The head as the next record links to it.</summary>
    public ResourceHead ToHead() => new() { ResourceSequence = ResourceSequence, RecordDigest = RecordDigest };
}

/// <summary>
/// The evidence store's envelope of one entry (Python <c>EvidenceStoreEntry</c>). Every entry commits to the one
/// before it; the fields after <see cref="PayloadDigest"/> index the payload for search.
/// </summary>
public sealed class EvidenceStoreEntry
{
    /// <summary>The entry's position in the store, from 1.</summary>
    [JsonPropertyName("store_sequence")]
    public long StoreSequence { get; set; }

    /// <summary>What the payload is, such as <c>decision</c>, <c>execution</c> or <c>break_glass</c>.</summary>
    [JsonPropertyName("entry_kind")]
    public string EntryKind { get; set; } = "";

    /// <summary>When the NA stored the entry.</summary>
    [JsonPropertyName("recorded_at")]
    public string RecordedAt { get; set; } = "";

    /// <summary>SHA-256 hex of the payload's canonical JSON.</summary>
    [JsonPropertyName("payload_digest")]
    public string PayloadDigest { get; set; } = "";

    /// <summary>The previous entry's digest; null for the first entry.</summary>
    [JsonPropertyName("prev_entry_digest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrevEntryDigest { get; set; }

    /// <summary>The decision the entry belongs to, when it has one.</summary>
    [JsonPropertyName("decision_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DecisionId { get; set; }

    /// <summary>The decision's context record, when it has one.</summary>
    [JsonPropertyName("context_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ContextId { get; set; }

    /// <summary>The vendor (attestation subject), when known.</summary>
    [JsonPropertyName("vendor_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? VendorId { get; set; }

    /// <summary>The attestation the decision rested on, when it had one.</summary>
    [JsonPropertyName("attestation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AttestationId { get; set; }

    /// <summary>The capability requested or executed.</summary>
    [JsonPropertyName("capability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Capability { get; set; }

    /// <summary><c>authorized</c> or <c>denied</c> for a decision; the executor's outcome for an execution.</summary>
    [JsonPropertyName("outcome")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Outcome { get; set; }

    /// <summary>The execution record's ID, for an execution.</summary>
    [JsonPropertyName("evidence_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EvidenceId { get; set; }

    /// <summary>The executor, for an execution.</summary>
    [JsonPropertyName("executor_sovereign_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ExecutorSovereignId { get; set; }

    /// <summary>The execution record's position under its decision.</summary>
    [JsonPropertyName("exec_sequence_no")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExecSequenceNo { get; set; }

    /// <summary>The resource acted on, when the record names one.</summary>
    [JsonPropertyName("resource_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ResourceId { get; set; }

    /// <summary>What was done to the resource.</summary>
    [JsonPropertyName("resource_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ResourceAction { get; set; }

    /// <summary>The record's position on the resource chain.</summary>
    [JsonPropertyName("resource_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ResourceSequence { get; set; }

    /// <summary>v1.3.0, left out when absent: the ID of a record other than a decision or execution.</summary>
    [JsonPropertyName("record_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordId { get; set; }

    /// <summary>v1.3.0, left out when absent: the observation or break-glass record a judgement judges.</summary>
    [JsonPropertyName("subject_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubjectId { get; set; }

    /// <summary>v1.3.0, left out when absent: the execution record a judgement matched.</summary>
    [JsonPropertyName("matched_evidence_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchedEvidenceId { get; set; }

    /// <summary>v1.3.0, left out when absent: an observation's position among the resource's observations.</summary>
    [JsonPropertyName("observation_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ObservationSequence { get; set; }
}

/// <summary>One stored entry with its payload, as search and export return it (schema <c>gm.evidence.event</c>).</summary>
public sealed class EvidenceEvent
{
    /// <summary>Always <c>gm.evidence.event</c>.</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "";

    /// <summary>Always 1.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    /// <summary>The store's envelope.</summary>
    [JsonPropertyName("entry")]
    public EvidenceStoreEntry Entry { get; set; } = new();

    /// <summary>SHA-256 hex of the envelope's canonical JSON.</summary>
    [JsonPropertyName("entry_digest")]
    public string EntryDigest { get; set; } = "";

    /// <summary>
    /// The stored record as signed, such as an <see cref="ExecutionEvidence"/> for an <c>execution</c> entry. Read it
    /// with <c>Deserialize&lt;ExecutionEvidence&gt;(Auth.SerializerOptions)</c>, or verify it as received.
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

/// <summary>Returned by GET /admin/evidence: one page of matching entries.</summary>
public sealed class EvidenceSearchResult
{
    /// <summary>The number of entries on this page.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>The cursor for the next page (<see cref="EvidenceSearchParams.AfterSequence"/>); null on the last.</summary>
    [JsonPropertyName("next_after_sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? NextAfterSequence { get; set; }

    /// <summary>The entries, in store order.</summary>
    [JsonPropertyName("entries")]
    public List<EvidenceEvent> Entries { get; set; } = [];
}

/// <summary>Filters for <see cref="EvidenceStoreClient.Search"/>; every filter left null matches everything.</summary>
public sealed class EvidenceSearchParams
{
    /// <summary>The vendor (attestation subject).</summary>
    public string? VendorId { get; init; }
    /// <summary>The attestation a decision rested on.</summary>
    public string? AttestationId { get; init; }
    /// <summary>The capability requested or executed.</summary>
    public string? Capability { get; init; }
    /// <summary>The resource acted on.</summary>
    public string? ResourceId { get; init; }
    /// <summary><c>authorized</c> or <c>denied</c> for decisions; the executor's outcome for executions.</summary>
    public string? Outcome { get; init; }
    /// <summary>The entry kind, such as <c>execution</c>.</summary>
    public string? EntryKind { get; init; }
    /// <summary>The decision.</summary>
    public string? DecisionId { get; init; }
    /// <summary>v1.6.0: a grant's uses and its revocation.</summary>
    public string? GrantId { get; init; }
    /// <summary>The earliest <c>recorded_at</c>, ISO 8601.</summary>
    public string? Since { get; init; }
    /// <summary>The latest <c>recorded_at</c>, ISO 8601.</summary>
    public string? Until { get; init; }
    /// <summary>Entries after this store sequence: the paging cursor.</summary>
    public long? AfterSequence { get; init; }
    /// <summary>1 to 1000 entries a page; the NA's default is 100.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// The latest state of one resource, from its stored execution evidence and (1.3.1) break-glass records
/// (<see cref="EvidenceStoreClient.ResourceStates"/>), for reconciliation against a cloud inventory.
/// </summary>
public sealed class ResourceState
{
    /// <summary>The resource.</summary>
    public string ResourceId { get; init; } = "";
    /// <summary>The newest execution record's position on the resource chain; 0 with only break-glass records.</summary>
    public long ResourceSequence { get; init; }
    /// <summary>The newest execution record's digest; empty with only break-glass records.</summary>
    public string RecordDigest { get; init; } = "";
    /// <summary>The latest change's action.</summary>
    public string? LastAction { get; init; }
    /// <summary>The latest change's outcome.</summary>
    public string LastOutcome { get; init; } = "";
    /// <summary>The action of the latest change that succeeded.</summary>
    public string? LastSuccessAction { get; init; }
    /// <summary>The parameters of the latest change that succeeded; undefined when none did.</summary>
    public JsonElement LastSuccessParameters { get; init; }
    /// <summary>When the latest change was made.</summary>
    public string LastExecutedAt { get; init; } = "";
    /// <summary>The newest execution record's decision; empty with only break-glass records.</summary>
    public string LastDecisionId { get; init; } = "";
    /// <summary>The break-glass record of the latest change, when it was one (1.3.1).</summary>
    public string? LastBreakGlassId { get; init; }
}

/// <summary>A key to register with <see cref="EvidenceStoreClient.RegisterExecutorKey"/> (privileged).</summary>
public sealed class RegisterExecutorKeyRequest
{
    /// <summary>The key's ID, as the records it signs name it.</summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";

    /// <summary>The raw 32-byte Ed25519 public key, base64.</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = "";

    /// <summary>The sovereign the key signs for.</summary>
    [JsonPropertyName("executor_sovereign_id")]
    public string ExecutorSovereignId { get; set; } = "";

    /// <summary>v1.3.0: <c>executor</c> (the NA's default when null) or <c>observer</c>, which signs observations only.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// v1.3.0: the key signs only for resources whose ID starts with this (1 to 256 characters); null for any. The NA
    /// refuses a record of such a key without a resource or outside the prefix (<c>evidence_out_of_scope</c>).
    /// </summary>
    [JsonPropertyName("resource_prefix")]
    public string? ResourcePrefix { get; set; }
}

/// <summary>Returned by registering an executor key.</summary>
public sealed class ExecutorKeyState
{
    /// <summary>The key's ID.</summary>
    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";

    /// <summary>The sovereign the key signs for.</summary>
    [JsonPropertyName("executor_sovereign_id")]
    public string ExecutorSovereignId { get; set; } = "";

    /// <summary>True while the key is not retired.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>v1.3.0: <c>executor</c> or <c>observer</c>.</summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>v1.3.0, left out when absent: the prefix the key is limited to.</summary>
    [JsonPropertyName("resource_prefix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourcePrefix { get; set; }
}

/// <summary>Returned by POST /evidence/execution.</summary>
public sealed class EvidenceSubmission
{
    /// <summary><c>recorded</c>, or <c>duplicate</c> for an identical resubmission: safe to retry.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The store's envelope of the record.</summary>
    [JsonPropertyName("entry")]
    public EvidenceStoreEntry Entry { get; set; } = new();

    /// <summary>SHA-256 hex of the envelope's canonical JSON.</summary>
    [JsonPropertyName("entry_digest")]
    public string EntryDigest { get; set; } = "";
}

// ── The evidence outbox (1.9.0) ───────────────────────────────────────────────

/// <summary>The states of an outbox entry (<see cref="OutboxEntry.State"/>).</summary>
public static class OutboxStates
{
    /// <summary>To submit: <see cref="EvidenceStoreClient.FlushPending"/> submits it.</summary>
    public const string Pending = "pending";
    /// <summary>Refused for good: kept with the refusal, never dropped and never submitted again.</summary>
    public const string DeadLetter = "dead_letter";
}

/// <summary>Why the NA, or the transport, refused a submission (<see cref="OutboxEntry.LastError"/>).</summary>
public sealed class SubmissionFailure
{
    /// <summary>The HTTP status; 0 when no answer came (a network error or a timeout) or this SDK refused the record.</summary>
    [JsonPropertyName("status")]
    public int Status { get; init; }

    /// <summary>
    /// The NA's code; <c>network_error</c> when the NA could not be reached, <c>local_error</c> for an exception in
    /// this process before any request (<see cref="EvidenceOutbox.LocalError"/>).
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>The NA's message, or the exception's.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    /// <summary>The NA's quarantine entry, when it kept the refused record as one (<c>error.details.quarantine_id</c>).</summary>
    [JsonPropertyName("quarantine_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuarantineId { get; init; }
}

/// <summary>An entry an outbox stores (<see cref="IOutbox{TEntry}"/>), found by its ID.</summary>
public interface IOutboxEntry
{
    /// <summary>The entry's ID: the ID of the record it holds.</summary>
    string Id { get; }
}

/// <summary>
/// One signed execution record in the evidence outbox (1.9.0), until the NA admits it. The TypeScript and Rust SDKs
/// read and write the same JSON. Entries do not change: the client stores a new one (<c>with</c>) for each attempt.
/// </summary>
public sealed record OutboxEntry : IOutboxEntry
{
    /// <summary>The record's <see cref="ExecutionEvidence.EvidenceId"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>The signed record, exactly as it will be submitted.</summary>
    [JsonPropertyName("evidence")]
    public ExecutionEvidence Evidence { get; init; } = new();

    /// <summary>A value of <see cref="OutboxStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = OutboxStates.Pending;

    /// <summary>Submissions attempted so far.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    /// <summary>When the record was handed to the outbox, ISO 8601.</summary>
    [JsonPropertyName("queued_at")]
    public string QueuedAt { get; init; } = "";

    /// <summary>The earliest time a flush retries it, ISO 8601; null while no attempt has failed.</summary>
    [JsonPropertyName("next_attempt_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NextAttemptAt { get; init; }

    /// <summary>Why the last attempt failed; null while none has.</summary>
    [JsonPropertyName("last_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public SubmissionFailure? LastError { get; init; }
}

/// <summary>
/// Durable storage for outbox entries (1.9.0), such as a database table or a Cosmos DB container when the
/// <see cref="FileOutbox"/> does not fit. Each method is durable once its task completes, <see cref="List"/> returns
/// the entries in the order they were added, and one store serves one process at a time. The entries hold signed
/// metadata only, never secret values, but the store must be private.
/// </summary>
/// <typeparam name="TEntry">The entries stored.</typeparam>
public interface IOutbox<TEntry> where TEntry : class, IOutboxEntry
{
    /// <summary>Stores a new entry; refuses an <see cref="IOutboxEntry.Id"/> already stored.</summary>
    Task Add(TEntry entry, CancellationToken ct = default);

    /// <summary>Replaces the stored entry with the same ID; does nothing when it is gone.</summary>
    Task Update(TEntry entry, CancellationToken ct = default);

    /// <summary>Removes an entry; does nothing when it is gone.</summary>
    Task Remove(string id, CancellationToken ct = default);

    /// <summary>Every entry, in the order added.</summary>
    Task<IReadOnlyList<TEntry>> List(CancellationToken ct = default);
}

/// <summary>
/// What became of a record handed to the outbox (<see cref="EvidenceStoreClient.Enqueue"/>): the NA's acknowledgement
/// when it admitted the record, otherwise the entry holding it (<see cref="OutboxStates.Pending"/> or
/// <see cref="OutboxStates.DeadLetter"/>).
/// </summary>
public sealed class Delivery
{
    /// <summary>The NA's acknowledgement; null when it did not admit the record.</summary>
    public EvidenceSubmission? Submission { get; init; }

    /// <summary>The entry holding the record; null when the NA admitted it.</summary>
    public OutboxEntry? Queued { get; init; }
}

/// <summary>What one <see cref="EvidenceStoreClient.FlushPending"/> run did.</summary>
public sealed class FlushResult
{
    /// <summary>Entries the NA admitted (or already held), now removed from the outbox.</summary>
    public IReadOnlyList<OutboxEntry> Admitted { get; init; } = [];

    /// <summary>Entries still pending, in order: not yet due, behind a pending record, or failed again.</summary>
    public IReadOnlyList<OutboxEntry> Pending { get; init; } = [];

    /// <summary>Entries this run moved to the dead letters.</summary>
    public IReadOnlyList<OutboxEntry> DeadLettered { get; init; } = [];
}

/// <summary>Options of <see cref="EvidenceStoreClient.FlushPending"/>.</summary>
public sealed class FlushOptions
{
    /// <summary>Also retries entries whose backoff has not run out, such as right after the NA is back.</summary>
    public bool IgnoreBackoff { get; init; }
}

// ── Reconciliation (1.9.0) ────────────────────────────────────────────────────

/// <summary>One resource as seen in the cloud, such as a Key Vault secret or an application's credential.</summary>
public sealed class ObservedResource
{
    /// <summary>The resource's ID, as its records name it.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>False when the resource was looked up and is gone.</summary>
    public bool Exists { get; init; }

    /// <summary>The version seen in the cloud, compared with the recorded version (<see cref="ReconcileOptions.VersionKey"/>).</summary>
    public string? Version { get; init; }

    /// <summary>Anything the controller wants back with the finding; never secret values.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>The values of <see cref="ReconciliationFinding.Status"/>.</summary>
public static class ReconciliationStatuses
{
    /// <summary>Exists in the cloud with no successful governed change.</summary>
    public const string Unmanaged = "unmanaged";
    /// <summary>The cloud version differs from the last governed change.</summary>
    public const string Drifted = "drifted";
    /// <summary>Still present after a governed revoke or delete.</summary>
    public const string PresentAfterRevoke = "present_after_revoke";
    /// <summary>Governed as live, but not found in the cloud.</summary>
    public const string Missing = "missing";
    /// <summary>The cloud agrees with the governed history.</summary>
    public const string InSync = "in_sync";
}

/// <summary>One resource compared with its governed history (<see cref="Governance.ReconcileResources"/>).</summary>
public sealed class ReconciliationFinding
{
    /// <summary>The resource.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>A value of <see cref="ReconciliationStatuses"/>.</summary>
    public string Status { get; init; } = "";

    /// <summary>The resource as observed; null when the inventory does not list it.</summary>
    public ObservedResource? Observed { get; init; }

    /// <summary>The resource's governed state; null when it has none.</summary>
    public ResourceState? Recorded { get; init; }

    /// <summary>What differs, or why it agrees.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>Options of <see cref="Governance.ReconcileResources"/>.</summary>
public sealed class ReconcileOptions
{
    /// <summary>The execution parameter holding the version the controller recorded. Default <c>secret_version</c>.</summary>
    public string VersionKey { get; init; } = "secret_version";

    /// <summary>
    /// The inventory lists every governed resource in scope, so a resource governed as live and absent from it is
    /// <see cref="ReconciliationStatuses.Missing"/>.
    /// </summary>
    public bool CompleteInventory { get; init; }

    /// <summary>With <see cref="CompleteInventory"/>: only resources whose ID starts with this, such as <c>kv:vault/</c>.</summary>
    public string? ScopePrefix { get; init; }
}

internal static class ModelHelpers
{
    /// <summary>The first entry of a signatures array, or an undefined element.</summary>
    public static JsonElement FirstSignature(JsonElement signatures) =>
        signatures.ValueKind == JsonValueKind.Array && signatures.GetArrayLength() > 0
            ? signatures[0].Clone()
            : default;
}
