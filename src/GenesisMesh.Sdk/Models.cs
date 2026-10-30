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
    [JsonPropertyName("denial_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("subject_public_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>When the vote was cast.</summary>
    [JsonPropertyName("voted_at")]
    public string VotedAt { get; set; } = "";

    /// <summary>Binds the vote to the validator's view of the proof (v0.38); required on approve votes.</summary>
    [JsonPropertyName("context_digest")]
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
    [JsonPropertyName("cascade_assessment_digest")]
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
public sealed class HealthStatus
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

    /// <summary>True when the endpoint answered.</summary>
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

internal static class ModelHelpers
{
    /// <summary>The first entry of a signatures array, or an undefined element.</summary>
    public static JsonElement FirstSignature(JsonElement signatures) =>
        signatures.ValueKind == JsonValueKind.Array && signatures.GetArrayLength() > 0
            ? signatures[0].Clone()
            : default;
}
