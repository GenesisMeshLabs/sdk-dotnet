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
    [JsonPropertyName("offeror_sovereign_id")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfferorSovereignId   { get; set; }

    [JsonPropertyName("responder_sovereign_id")]
    public string  ResponderSovereignId { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public IList<string> Capabilities  { get; set; } = [];

    [JsonPropertyName("roles")]
    public IList<string> Roles          { get; set; } = [];

    [JsonPropertyName("valid_from")]
    public string  ValidFrom            { get; set; } = "";

    [JsonPropertyName("valid_until")]
    public string  ValidUntil           { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string  ExpiresAt            { get; set; } = "";

    [JsonPropertyName("metadata")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("offer_id")]
    public string       OfferId              { get; set; } = "";

    [JsonPropertyName("offerer_sovereign_id")]
    public string       OffererSovereignId   { get; set; } = "";

    [JsonPropertyName("responder_sovereign_id")]
    public string       ResponderSovereignId { get; set; } = "";

    [JsonPropertyName("requested_terms")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  RequestedTerms       { get; set; }

    [JsonPropertyName("agreed_terms")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  AgreedTerms          { get; set; }

    [JsonPropertyName("offerer_evidence")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  OffererEvidence      { get; set; }

    [JsonPropertyName("responder_evidence")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  ResponderEvidence    { get; set; }

    [JsonPropertyName("signatures")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signatures           { get; set; }

    [JsonPropertyName("graph_digest")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      GraphDigest          { get; set; }

    [JsonPropertyName("created_at")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      CreatedAt            { get; set; }

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt            { get; set; } = "";
}

/// <summary>Returned by POST /admin/agreements/accept: the agreement with both parties' signatures.</summary>
public sealed class AgreementRecord : IJsonOnDeserialized
{
    [JsonPropertyName("agreement_id")]
    public string       AgreementId          { get; set; } = "";

    [JsonPropertyName("offer_id")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      OfferId              { get; set; }

    [JsonPropertyName("offerer_sovereign_id")]
    public string       OffererSovereignId   { get; set; } = "";

    [JsonPropertyName("responder_sovereign_id")]
    public string       ResponderSovereignId { get; set; } = "";

    [JsonPropertyName("agreed_terms")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  AgreedTerms          { get; set; }

    [JsonPropertyName("offerer_evidence")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  OffererEvidence      { get; set; }

    [JsonPropertyName("responder_evidence")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  ResponderEvidence    { get; set; }

    [JsonPropertyName("graph_digest")]
    public string       GraphDigest          { get; set; } = "";

    [JsonPropertyName("established_at")]
    public string       EstablishedAt        { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt            { get; set; } = "";

    [JsonPropertyName("signatures")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signatures           { get; set; }

    /// <summary>The capabilities in AgreedTerms, filled in on deserialization.</summary>
    [JsonIgnore, Obsolete("Read agreed_terms (AgreedTerms); this is filled from it.")]
    public IList<string> Capabilities        { get; set; } = [];

    [JsonIgnore, Obsolete("Agreements carry no roles; always empty.")]
    public IList<string> Roles               { get; set; } = [];

    [JsonIgnore, Obsolete("The NA sends no agreement status; always empty.")]
    public string       Status               { get; set; } = "";

    [JsonIgnore, Obsolete("Use EstablishedAt, which this is filled from.")]
    public string       CreatedAt            { get; set; } = "";

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
    [JsonPropertyName("decision_id")]
    public string      DecisionId          { get; set; } = "";

    [JsonPropertyName("context_id")]
    public string      ContextId           { get; set; } = "";

    [JsonPropertyName("agreement_id")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     AgreementId         { get; set; }

    [JsonPropertyName("authorized")]
    public bool        Authorized          { get; set; }

    [JsonPropertyName("denial_reason")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     DenialReason        { get; set; }

    [JsonPropertyName("gate_results")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement GateResults         { get; set; }

    [JsonPropertyName("decision_made_at")]
    public string      DecisionMadeAt      { get; set; } = "";

    [JsonPropertyName("decision_valid_until")]
    public string      DecisionValidUntil  { get; set; } = "";

    [JsonPropertyName("operator_sovereign_id")]
    public string      OperatorSovereignId { get; set; } = "";

    [JsonPropertyName("freshness_proof")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement FreshnessProof      { get; set; }

    [JsonPropertyName("policy_binding")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement PolicyBinding       { get; set; }

    [JsonPropertyName("attestation_binding")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement AttestationBinding  { get; set; }

    [JsonPropertyName("signature")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature           { get; set; }

    [JsonIgnore, Obsolete("Use Authorized, which this is filled from.")]
    public bool        Allowed             { get; set; }

    [JsonIgnore, Obsolete("Use DenialReason, which this is filled from (null when authorized).")]
    public string?     Reason              { get; set; }

    [JsonIgnore, Obsolete("Use DecisionMadeAt, which this is filled from.")]
    public string      IssuedAt            { get; set; } = "";

    [JsonIgnore, Obsolete("Not part of a decision (it is in the ContextRecord); always null.")]
    public string?     RequestingAgentId   { get; set; }

    [JsonIgnore, Obsolete("Not part of a decision; always null.")]
    public string?     TargetAgentId       { get; set; }

    [JsonIgnore, Obsolete("Not part of a decision; always null.")]
    public string?     Capability          { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Allowed  = Authorized;
        Reason   = DenialReason;
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
    [JsonPropertyName("source_sovereign_id")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   SourceSovereignId { get; set; }

    [JsonPropertyName("target_sovereign_id")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   TargetSovereignId { get; set; }

    [JsonPropertyName("verdict")]
    public string    Verdict           { get; set; } = "";

    [JsonPropertyName("reason")]
    public string    Reason            { get; set; } = "";

    [JsonPropertyName("requested_roles")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? RequestedRoles { get; set; }

    [JsonPropertyName("trusted")]              [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool      Trusted           { get; set; }

    [JsonPropertyName("trust_path")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<IDictionary<string, object?>>? TrustPath { get; set; }

    [JsonPropertyName("hop_count")]            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int       HopCount          { get; set; }

    [JsonPropertyName("signals")]              [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<object>? Signals      { get; set; }

    [JsonPropertyName("evaluated_at")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   EvaluatedAt       { get; set; }

    /// <summary>Not read by the NA.</summary>
    [JsonPropertyName("subject_id")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   SubjectId         { get; set; }

    /// <summary>Not read by the NA.</summary>
    [JsonPropertyName("context")]              [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object>? Context { get; set; }
}

/// <summary>Returned by POST /admin/trust-evidence.</summary>
public sealed class TrustEvidence : IJsonOnDeserialized
{
    [JsonPropertyName("evidence_id")]
    public string      EvidenceId        { get; set; } = "";

    [JsonPropertyName("issuer_sovereign_id")]
    public string      IssuerSovereignId { get; set; } = "";

    [JsonPropertyName("source_sovereign_id")]
    public string      SourceSovereignId { get; set; } = "";

    [JsonPropertyName("target_sovereign_id")]
    public string      TargetSovereignId { get; set; } = "";

    [JsonPropertyName("verdict")]
    public string      Verdict           { get; set; } = "";

    [JsonPropertyName("reason")]
    public string      Reason            { get; set; } = "";

    [JsonPropertyName("trusted")]
    public bool        Trusted           { get; set; }

    [JsonPropertyName("hop_count")]
    public int         HopCount          { get; set; }

    [JsonPropertyName("requested_roles")]
    public IList<string> RequestedRoles  { get; set; } = [];

    [JsonPropertyName("signals")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signals           { get; set; }

    [JsonPropertyName("graph_digest")]
    public string      GraphDigest       { get; set; } = "";

    [JsonPropertyName("evaluated_at")]
    public string      EvaluatedAt       { get; set; } = "";

    [JsonPropertyName("issued_at")]
    public string      IssuedAt          { get; set; } = "";

    [JsonPropertyName("issued_by")]
    public string      IssuedBy          { get; set; } = "";

    [JsonPropertyName("metadata")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Metadata          { get; set; }

    [JsonPropertyName("signatures")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signatures        { get; set; }

    [JsonIgnore, Obsolete("Use IssuerSovereignId, which this is filled from.")]
    public string?     IssuerId          { get; set; }

    [JsonIgnore, Obsolete("Use TargetSovereignId, which this is filled from.")]
    public string?     SubjectId         { get; set; }

    [JsonIgnore, Obsolete("Use Signatures; this is filled from its first entry.")]
    public JsonElement Signature         { get; set; }

    [JsonIgnore, Obsolete("Trust evidence names no decision; always null.")]
    public string?     DecisionId        { get; set; }

    [JsonIgnore, Obsolete("Trust evidence does not embed the decision; always undefined.")]
    public JsonElement Decision          { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        IssuerId  = IssuerSovereignId;
        SubjectId = TargetSovereignId;
        Signature = ModelHelpers.FirstSignature(Signatures);
#pragma warning restore CS0618
    }
}

// ── Attestation ───────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/attestations.</summary>
public sealed class MembershipAttestation : IJsonOnDeserialized
{
    [JsonPropertyName("attestation_id")]
    public string       AttestationId      { get; set; } = "";

    [JsonPropertyName("issuer_sovereign_id")]
    public string       IssuerSovereignId  { get; set; } = "";

    [JsonPropertyName("subject_id")]
    public string       SubjectId          { get; set; } = "";

    [JsonPropertyName("subject_public_key")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      SubjectPublicKey   { get; set; }

    [JsonPropertyName("roles")]
    public IList<string> Roles             { get; set; } = [];

    [JsonPropertyName("status")]
    public string       Status             { get; set; } = "";

    [JsonPropertyName("issued_at")]
    public string       IssuedAt           { get; set; } = "";

    [JsonPropertyName("valid_from")]
    public string       ValidFrom          { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt          { get; set; } = "";

    [JsonPropertyName("issued_by")]
    public string       IssuedBy           { get; set; } = "";

    [JsonPropertyName("claims")]            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Claims             { get; set; }

    [JsonPropertyName("signatures")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signatures         { get; set; }

    [JsonIgnore, Obsolete("Use SubjectId, which this is filled from.")]
    public string?      SubjectSovereignId { get; set; }

    [JsonIgnore, Obsolete("Use Signatures; this is filled from its first entry.")]
    public JsonElement  Signature          { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        SubjectSovereignId = SubjectId;
        Signature          = ModelHelpers.FirstSignature(Signatures);
#pragma warning restore CS0618
    }
}

// ── Disclosure ────────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/disclosure/commit.</summary>
public sealed class CapabilityCommitment : IJsonOnDeserialized
{
    [JsonPropertyName("commitment_id")]
    public string      CommitmentId      { get; set; } = "";

    [JsonPropertyName("agreement_id")]
    public string      AgreementId       { get; set; } = "";

    [JsonPropertyName("issuer_sovereign_id")]
    public string      IssuerSovereignId { get; set; } = "";

    [JsonPropertyName("merkle_root")]
    public string      MerkleRoot        { get; set; } = "";

    [JsonPropertyName("capability_count")]
    public int         CapabilityCount   { get; set; }

    [JsonPropertyName("committed_at")]
    public string      CommittedAt       { get; set; } = "";

    [JsonPropertyName("signature")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature         { get; set; }

    [JsonIgnore, Obsolete("Use CommittedAt, which this is filled from.")]
    public string      IssuedAt          { get; set; } = "";

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
    [JsonPropertyName("proof_id")]
    public string      ProofId            { get; set; } = "";

    [JsonPropertyName("commitment_id")]
    public string      CommitmentId       { get; set; } = "";

    [JsonPropertyName("prover_sovereign_id")]
    public string      ProverSovereignId  { get; set; } = "";

    [JsonPropertyName("revealed_capability")]
    public string      RevealedCapability { get; set; } = "";

    [JsonPropertyName("leaf_hash")]
    public string      LeafHash           { get; set; } = "";

    [JsonPropertyName("merkle_path")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MerklePath         { get; set; }

    [JsonPropertyName("proved_at")]
    public string      ProvedAt           { get; set; } = "";

    [JsonIgnore, Obsolete("Use RevealedCapability, which this is filled from.")]
    public string      Capability         { get; set; } = "";

    [JsonIgnore, Obsolete("Use MerklePath, which this is filled from.")]
    public JsonElement Proof              { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Capability = RevealedCapability;
        Proof      = MerklePath;
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
    [JsonPropertyName("vote_id")]
    public string      VoteId               { get; set; } = "";

    [JsonPropertyName("proof_id")]
    public string      ProofId              { get; set; } = "";

    [JsonPropertyName("decision_id")]
    public string      DecisionId           { get; set; } = "";

    [JsonPropertyName("validator_sovereign_id")]
    public string      ValidatorSovereignId { get; set; } = "";

    /// <summary>True approves, false rejects.</summary>
    [JsonPropertyName("vote")]
    public bool        Vote                 { get; set; }

    [JsonPropertyName("reason")]
    public string?     Reason               { get; set; }

    [JsonPropertyName("voted_at")]
    public string      VotedAt              { get; set; } = "";

    /// <summary>Binds the vote to the validator's view of the proof (v0.38); required on approve votes.</summary>
    [JsonPropertyName("context_digest")]
    public string?     ContextDigest        { get; set; }

    [JsonPropertyName("signature")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature            { get; set; }
}

/// <summary>
/// K-of-N approval over a JustificationProof, signed by the assembler (Python <c>ConsensusProof</c>).
/// Returned by POST /admin/consensus/proof.
/// </summary>
public sealed class ConsensusProof
{
    [JsonPropertyName("consensus_id")]
    public string               ConsensusId             { get; set; } = "";

    [JsonPropertyName("proof_id")]
    public string               ProofId                 { get; set; } = "";

    [JsonPropertyName("decision_id")]
    public string               DecisionId              { get; set; } = "";

    /// <summary>K: distinct named validators that must approve.</summary>
    [JsonPropertyName("required_threshold")]
    public int                  RequiredThreshold       { get; set; }

    [JsonPropertyName("validator_sovereign_ids")]
    public IList<string>        ValidatorSovereignIds   { get; set; } = [];

    [JsonPropertyName("votes")]
    public IList<ConsensusVote> Votes                   { get; set; } = [];

    [JsonPropertyName("reached_at")]
    public string               ReachedAt               { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string               ExpiresAt               { get; set; } = "";

    [JsonPropertyName("cascade_assessment_digest")]
    public string?              CascadeAssessmentDigest { get; set; }

    [JsonPropertyName("signature")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement          Signature               { get; set; }
}

/// <summary>
/// Returned by POST /consensus/verify. Reason is one of valid, missing_signature,
/// invalid_assembler_signature, threshold_not_met, invalid_vote_signature, unknown_validator_key,
/// vote_not_in_validator_set, expired, proof_id_mismatch, cascade_detected, missing_context_digest.
/// </summary>
public sealed class ConsensusVerification
{
    [JsonPropertyName("valid")]
    public bool    Valid       { get; set; }

    [JsonPropertyName("reason")]
    public string  Reason      { get; set; } = "";

    [JsonPropertyName("consensus_id")]
    public string? ConsensusId { get; set; }
}

// ── Data Usage ────────────────────────────────────────────────────────────────

/// <summary>A data source descriptor inside a DataAccessIntent.</summary>
public sealed class DataSourceDescriptor
{
    [JsonPropertyName("source_id")]
    public string        SourceId           { get; set; } = "";

    [JsonPropertyName("source_type")]
    public string        SourceType         { get; set; } = "";

    [JsonPropertyName("owner_sovereign_id")]
    public string        OwnerSovereignId   { get; set; } = "";

    [JsonPropertyName("classification_tags")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? ClassificationTags { get; set; }

    [JsonPropertyName("estimated_volume_bytes")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long?          EstimatedVolumeBytes { get; set; }
}

/// <summary>Returned by POST /admin/data-usage/policy and GET /data-usage/policy.</summary>
public sealed class DataLicensePolicy : IJsonOnDeserialized
{
    [JsonPropertyName("policy_id")]
    public string       PolicyId                     { get; set; } = "";

    [JsonPropertyName("licensor_sovereign_id")]
    public string       LicensorSovereignId          { get; set; } = "";

    [JsonPropertyName("licensee_sovereign_id")]
    public string       LicenseeSovereignId          { get; set; } = "";

    [JsonPropertyName("allowed_source_ids")]
    public IList<string> AllowedSourceIds            { get; set; } = [];

    [JsonPropertyName("allowed_access_types")]
    public IList<string> AllowedAccessTypes          { get; set; } = [];

    [JsonPropertyName("prohibited_classification_tags")]
    public IList<string> ProhibitedClassificationTags { get; set; } = [];

    [JsonPropertyName("max_volume_bytes_per_session")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long?        MaxVolumeBytesPerSession     { get; set; }

    [JsonPropertyName("valid_from")]
    public string       ValidFrom                    { get; set; } = "";

    [JsonPropertyName("valid_until")]
    public string       ValidUntil                   { get; set; } = "";

    [JsonPropertyName("signature")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signature                    { get; set; }

    [JsonIgnore, Obsolete("Use LicensorSovereignId, which this is filled from.")]
    public string?      LocalSovereignId             { get; set; }

    [JsonIgnore, Obsolete("Policies name no purposes (see AllowedAccessTypes); always null.")]
    public IList<string>? AllowedPurposes            { get; set; }

    [JsonIgnore, Obsolete("Use ValidFrom, which this is filled from.")]
    public string       IssuedAt                     { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        LocalSovereignId = LicensorSovereignId;
        IssuedAt         = ValidFrom;
#pragma warning restore CS0618
    }
}

/// <summary>Returned by POST /admin/data-usage/intent.</summary>
public sealed class DataAccessIntent : IJsonOnDeserialized
{
    [JsonPropertyName("intent_id")]
    public string       IntentId             { get; set; } = "";

    [JsonPropertyName("agent_sovereign_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      AgentSovereignId     { get; set; }

    [JsonPropertyName("decision_id")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      DecisionId           { get; set; }

    [JsonPropertyName("declared_sources")]
    public IList<DataSourceDescriptor> DeclaredSources { get; set; } = [];

    [JsonPropertyName("declared_access_types")]
    public IList<string> DeclaredAccessTypes { get; set; } = [];

    [JsonPropertyName("estimated_volume_bytes")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long?        EstimatedVolumeBytes { get; set; }

    [JsonPropertyName("declared_at")]
    public string       DeclaredAt           { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt            { get; set; } = "";

    [JsonPropertyName("signature")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signature            { get; set; }

    [JsonIgnore, Obsolete("Use DeclaredSources, which this is filled from.")]
    public IList<DataSourceDescriptor> Sources { get; set; } = [];

    [JsonIgnore, Obsolete("Use DeclaredAccessTypes, which this is filled from.")]
    public IList<string> AccessTypes         { get; set; } = [];

    [JsonIgnore, Obsolete("Intents name no policy; always null.")]
    public string?      PolicyId             { get; set; }

    [JsonIgnore, Obsolete("Intents name no issuer (see AgentSovereignId); always null.")]
    public string?      IssuerId             { get; set; }

    [JsonIgnore, Obsolete("Use DeclaredAt, which this is filled from.")]
    public string       IssuedAt             { get; set; } = "";

    void IJsonOnDeserialized.OnDeserialized()
    {
#pragma warning disable CS0618
        Sources     = DeclaredSources;
        AccessTypes = DeclaredAccessTypes;
        IssuedAt    = DeclaredAt;
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
    [JsonPropertyName("valid")]
    public bool    Valid    { get; set; }

    [JsonPropertyName("accepted")]
    public bool    Accepted { get; set; }

    [JsonPropertyName("reason")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason   { get; set; }

    /// <summary>Boundary verification: whether the verified decision authorizes the action.</summary>
    [JsonPropertyName("authorized")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool?   Authorized        { get; set; }

    [JsonPropertyName("agreement_id")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgreementId       { get; set; }

    [JsonPropertyName("decision_id")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionId        { get; set; }

    [JsonPropertyName("evidence_id")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId        { get; set; }

    [JsonPropertyName("commitment_id")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitmentId      { get; set; }

    [JsonPropertyName("issuer_sovereign_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssuerSovereignId { get; set; }

    [JsonPropertyName("verdict")]             [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict           { get; set; }

    /// <summary>Data-usage verification: the number of violations found.</summary>
    [JsonPropertyName("violation_count")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int?    ViolationCount    { get; set; }

    /// <summary>Data-usage verification: the first violation's reason; Reason is set from it.</summary>
    [JsonPropertyName("violation_reason")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ViolationReason   { get; set; }

    [JsonPropertyName("violations")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Violations    { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        var ok = Valid || Accepted;
        Valid    = ok;
        Accepted = ok;
        Reason ??= ViolationReason;
    }
}

internal static class ModelHelpers
{
    /// <summary>The first entry of a signatures array, or an undefined element.</summary>
    public static JsonElement FirstSignature(JsonElement signatures) =>
        signatures.ValueKind == JsonValueKind.Array && signatures.GetArrayLength() > 0
            ? signatures[0].Clone()
            : default;
}
