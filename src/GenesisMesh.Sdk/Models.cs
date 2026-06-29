using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

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

/// <summary>Returned by POST /admin/agreements/offer. Pass to Agreement.Accept.</summary>
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

    [JsonPropertyName("offerer_evidence")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  OffererEvidence      { get; set; }

    [JsonPropertyName("signatures")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signatures           { get; set; }

    [JsonPropertyName("graph_digest")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      GraphDigest          { get; set; }

    [JsonPropertyName("created_at")]
    public string       CreatedAt            { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt            { get; set; } = "";
}

/// <summary>Returned by POST /admin/agreements/accept.</summary>
public sealed class AgreementRecord
{
    [JsonPropertyName("agreement_id")]
    public string       AgreementId          { get; set; } = "";

    [JsonPropertyName("offer_id")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      OfferId              { get; set; }

    [JsonPropertyName("offerer_sovereign_id")]
    public string       OffererSovereignId   { get; set; } = "";

    [JsonPropertyName("responder_sovereign_id")]
    public string       ResponderSovereignId { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public IList<string> Capabilities        { get; set; } = [];

    [JsonPropertyName("roles")]
    public IList<string> Roles               { get; set; } = [];

    [JsonPropertyName("status")]
    public string       Status               { get; set; } = "";

    [JsonPropertyName("signatures")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signatures           { get; set; }

    [JsonPropertyName("created_at")]
    public string       CreatedAt            { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt            { get; set; } = "";
}

// ── Boundary ──────────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/boundary/decide.</summary>
public sealed class BoundaryDecision
{
    [JsonPropertyName("decision_id")]
    public string      DecisionId         { get; set; } = "";

    [JsonPropertyName("agreement_id")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     AgreementId        { get; set; }

    [JsonPropertyName("requesting_agent_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     RequestingAgentId  { get; set; }

    [JsonPropertyName("target_agent_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     TargetAgentId      { get; set; }

    [JsonPropertyName("capability")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     Capability         { get; set; }

    [JsonPropertyName("allowed")]
    public bool        Allowed            { get; set; }

    [JsonPropertyName("reason")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     Reason             { get; set; }

    [JsonPropertyName("signature")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature          { get; set; }

    [JsonPropertyName("issued_at")]
    public string      IssuedAt           { get; set; } = "";
}

// ── Evidence ──────────────────────────────────────────────────────────────────

/// <summary>Passed to Evidence.Build — wrapped in {"decision": ...} before posting.</summary>
public sealed class TrustDecision
{
    [JsonPropertyName("source_sovereign_id")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   SourceSovereignId { get; set; }

    [JsonPropertyName("target_sovereign_id")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   TargetSovereignId { get; set; }

    [JsonPropertyName("subject_id")]           [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   SubjectId         { get; set; }

    [JsonPropertyName("verdict")]
    public string    Verdict           { get; set; } = "";

    [JsonPropertyName("reason")]
    public string    Reason            { get; set; } = "";

    [JsonPropertyName("signals")]              [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<object>? Signals      { get; set; }

    [JsonPropertyName("context")]              [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object>? Context { get; set; }
}

/// <summary>Returned by POST /admin/trust-evidence.</summary>
public sealed class TrustEvidence
{
    [JsonPropertyName("evidence_id")]
    public string      EvidenceId { get; set; } = "";

    [JsonPropertyName("decision_id")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     DecisionId { get; set; }

    [JsonPropertyName("subject_id")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     SubjectId  { get; set; }

    [JsonPropertyName("verdict")]
    public string      Verdict    { get; set; } = "";

    [JsonPropertyName("decision")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Decision   { get; set; }

    [JsonPropertyName("signature")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature  { get; set; }

    [JsonPropertyName("issuer_id")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?     IssuerId   { get; set; }

    [JsonPropertyName("issued_at")]
    public string      IssuedAt   { get; set; } = "";
}

// ── Attestation ───────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/attestations.</summary>
public sealed class MembershipAttestation
{
    [JsonPropertyName("attestation_id")]
    public string       AttestationId      { get; set; } = "";

    [JsonPropertyName("subject_sovereign_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      SubjectSovereignId { get; set; }

    [JsonPropertyName("roles")]
    public IList<string> Roles             { get; set; } = [];

    [JsonPropertyName("signature")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signature          { get; set; }

    [JsonPropertyName("issued_at")]
    public string       IssuedAt           { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string       ExpiresAt          { get; set; } = "";
}

// ── Disclosure ────────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/disclosure/commit.</summary>
public sealed class CapabilityCommitment
{
    [JsonPropertyName("commitment_id")]
    public string      CommitmentId { get; set; } = "";

    [JsonPropertyName("merkle_root")]
    public string      MerkleRoot   { get; set; } = "";

    [JsonPropertyName("signature")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature     { get; set; }

    [JsonPropertyName("issued_at")]
    public string      IssuedAt      { get; set; } = "";
}

/// <summary>Returned by POST /disclosure/prove.</summary>
public sealed class CapabilityMembershipProof
{
    [JsonPropertyName("commitment_id")]
    public string      CommitmentId { get; set; } = "";

    [JsonPropertyName("capability")]
    public string      Capability   { get; set; } = "";

    [JsonPropertyName("proof")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Proof         { get; set; }

    [JsonPropertyName("leaf_hash")]
    public string      LeafHash      { get; set; } = "";
}

// ── Consensus ─────────────────────────────────────────────────────────────────

/// <summary>Returned by POST /admin/consensus/vote.</summary>
public sealed class ConsensusVote
{
    [JsonPropertyName("vote_id")]
    public string      VoteId      { get; set; } = "";

    [JsonPropertyName("proposal_id")]
    public string      ProposalId  { get; set; } = "";

    [JsonPropertyName("validator_id")]
    public string      ValidatorId { get; set; } = "";

    [JsonPropertyName("decision")]
    public string      Decision    { get; set; } = "";

    [JsonPropertyName("signature")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Signature    { get; set; }

    [JsonPropertyName("cast_at")]
    public string      CastAt      { get; set; } = "";
}

/// <summary>Returned by POST /admin/consensus/proof.</summary>
public sealed class ConsensusProof
{
    [JsonPropertyName("proof_id")]
    public string             ProofId      { get; set; } = "";

    [JsonPropertyName("proposal_id")]
    public string             ProposalId   { get; set; } = "";

    [JsonPropertyName("threshold")]
    public int                Threshold    { get; set; }

    [JsonPropertyName("votes")]
    public IList<ConsensusVote> Votes      { get; set; } = [];

    [JsonPropertyName("signature")]         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement          Signature  { get; set; }

    [JsonPropertyName("assembled_at")]
    public string               AssembledAt { get; set; } = "";
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

/// <summary>Returned by POST /admin/data-usage/policy.</summary>
public sealed class DataLicensePolicy
{
    [JsonPropertyName("policy_id")]
    public string       PolicyId         { get; set; } = "";

    [JsonPropertyName("local_sovereign_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      LocalSovereignId { get; set; }

    [JsonPropertyName("allowed_purposes")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? AllowedPurposes { get; set; }

    [JsonPropertyName("signature")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signature         { get; set; }

    [JsonPropertyName("issued_at")]
    public string       IssuedAt          { get; set; } = "";
}

/// <summary>Returned by POST /admin/data-usage/intent.</summary>
public sealed class DataAccessIntent
{
    [JsonPropertyName("intent_id")]
    public string       IntentId         { get; set; } = "";

    [JsonPropertyName("agent_sovereign_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      AgentSovereignId { get; set; }

    [JsonPropertyName("decision_id")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      DecisionId        { get; set; }

    [JsonPropertyName("sources")]
    public IList<DataSourceDescriptor> Sources { get; set; } = [];

    [JsonPropertyName("access_types")]
    public IList<string> AccessTypes      { get; set; } = [];

    [JsonPropertyName("policy_id")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      PolicyId           { get; set; }

    [JsonPropertyName("issuer_id")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?      IssuerId           { get; set; }

    [JsonPropertyName("signature")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement  Signature          { get; set; }

    [JsonPropertyName("issued_at")]
    public string       IssuedAt           { get; set; } = "";
}

// ── Shared ────────────────────────────────────────────────────────────────────

/// <summary>Returned by all public /verify endpoints.</summary>
public sealed class VerifyResult
{
    [JsonPropertyName("valid")]
    public bool    Valid   { get; set; }

    [JsonPropertyName("reason")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason  { get; set; }
}
