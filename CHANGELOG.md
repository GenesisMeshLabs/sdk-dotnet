# Changelog

All notable changes to `sdk-dotnet` are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions align with the [Genesis Mesh release sequence](https://github.com/GenesisMeshLabs/genesismesh/blob/main/CHANGELOG.md).

---

## [1.6.0] - 2026-10-14

### Added

- **A pre-issued grant's NA signature, checked offline.**
  `OfflineVerifier.VerifyCapabilityGrant` checks a Genesis Mesh 1.6.0
  `CapabilityGrant` against the NA's key history (or
  `NAKeyHistory.ValidAlways` for a plain list of NA keys): signed by the key
  valid at the grant's `issued_at`, with every signed field known and in
  canonical form. Its reasons are the existing `missing_signature`,
  `invalid_signature`, `unknown_field`, `non_canonical_form` and
  `na_key_not_valid_at_time`. It does not check the grant's validity,
  budget, parameter envelope or revocation.
- `NAKeyHistory.RecordTimeFields` places a grant and a time token at their
  `issued_at` and a grant revocation at its `revoked_at`.
- The SDK runs the signed-form and signature vectors of the shared `grants`
  suite, and the new record-time vectors of the `audit` suite.

### Changed

- The embedded field registry lists the Genesis Mesh 1.6.0 records
  (`CapabilityGrant`, `TimeToken`, `TimeTokenRequest`,
  `GrantExecutionEvidence`, `GrantRevocationRecord` and the models they
  nest) and the entry kinds `grant`, `grant_execution` and
  `grant_revocation`.
- The revocation feed's `revoked_grant_ids`, which the NA writes only while
  grants are on, is a known signed field: `StrictFields.UnknownFields` no
  longer reports it for a feed that lists revoked grants.
- Requesting grants, holding time tokens, acting within a grant offline,
  recording its uses and verifying grants in an evidence export are not part
  of this SDK; they live in the TypeScript and Rust SDKs.

## [1.5.0] - 2026-10-13

### Added

- **The NA key history.** `OfflineVerifier.VerifyKeyHistory` and
  `VerifyKeyHistoryResponse` (the body of `GET /na/key-history`) check a genesis block and its key
  successions from the root public key you hold out of band, with the
  reference's reasons in its order. The genesis block is strict like every
  signed root: `genesis_invalid`, `genesis_root_mismatch`, `unknown_field`,
  `non_canonical_form`, `genesis_invalid_signature`. Each succession carries
  three signatures (the root key's, the new key's as proof that it is held,
  and the outgoing key's) and is checked for `payload_invalid`,
  `unknown_field` or `invalid_signature`, `non_canonical_form`,
  `succession_sovereign_mismatch`, `succession_chain_break`,
  `succession_key_reused` (the root key included), `succession_backdated`
  (issued after it takes effect), `succession_not_increasing`,
  `succession_after_expiry`, `succession_invalid_root_signature`,
  `succession_invalid_new_key_signature` and
  `succession_invalid_previous_signature`. The result's `NAKeyHistory` gives
  the keys valid at a time (`KeysAt`), with the 300-second hand-over after a
  succession (`NAKeyHistory.SuccessionHandover`).
  `NAKeyHistory.RecordTimeFields` names the time each NA-signed record read
  alone is placed at.
- **Decisions across a key succession.** `DecisionVerifyOptions.KeyHistory`,
  instead of `OperatorPublicKeys`, requires the signature of the key valid at
  the decision's `decision_made_at` (`na_key_not_valid_at_time` for another
  key of the history, a new reason), and refuses a decision that stays valid
  more than `NAKeyHistory.MaxDecisionLifetime` (300 seconds) past the end of the key that
  signed it and the hand-over (`decision_outlives_key`, a new reason).
  `DecisionVerifyOptions.AtRecordTime` judges an archived decision as of
  `decision_made_at` rather than now.
- **Agreements across a key succession.**
  `OfflineVerifier.VerifyAgreementWithKeyHistory`
  checks each party's signature with the keys valid at the agreement's
  `agreed_terms.valid_from` (signed, unlike `established_at`) and still
  accepted now: an agreement a succeeded key signed stops vouching when that
  key's hand-over ends. `NAKeyHistory.ValidAlways` stands for a party whose
  history you do not hold.
- The SDK runs the signed-form, key history, keys-at-a-time, record-time and
  decision vectors of the shared `audit` suite.

### Changed

- The embedded field registry lists the Genesis Mesh 1.5.0 records
  (`KeySuccessionRecord`, `ArchiveRecord`, `AuditPackManifest`, the
  `GenesisBlock` and `RecognitionTreaty`) and the entry kinds
  `key_succession` and `archive`.
- Without a key history every verifier behaves as before: a list of NA keys
  is valid at every time. Audit packs and their verification are not part of
  this SDK; they live in the core and in the Rust SDK's
  `genesis-mesh-verify`.

## [1.4.0] - 2026-10-12

### Changed

- The embedded field registry lists the Genesis Mesh 1.4.0 records
  (`RemediationRecord`, `ReviewRecord` with its `AdminApproval`), the entry
  kinds `remediation` and `review`, and the boundary policy sections
  `remediation` (`GateRemediation` by gate), `review_required` and
  `emergency` (`EmergencyCapability`). This SDK does not verify evidence
  exports, so it has nothing to refuse there; the TypeScript and Rust SDKs
  record and verify remediations and reviews.
- `OfflineVerifier.VerifyBoundaryDecision` accepts an expected policy with
  the new sections: they are known fields, signed and bound by the policy
  digest. Before 1.4.0 such a policy was refused as `unknown_field`.
- `OfflineVerifier.PolicyDigest` leaves the new sections out when they are
  `null`, as the reference does, so a policy without them keeps the digest
  it had under 1.3.

## [1.3.1] - 2026-10-11

### Fixed

- Client responses are read with keys matched in their own case only. A key
  in another case named the same property, so
  `{"authorized":false,"Authorized":true}` read as an authorized decision,
  where every other implementation reads `Authorized` as a key it does not
  know. This held for every typed result: decisions, agreements, evidence,
  attestations, policies and intents.
- An integer longer than 20 digits is refused as `integer_out_of_range`
  before it is parsed, as the reference does. A response or record holding
  an integer of millions of digits took seconds to refuse.
- `OfflineVerifier.VerifyBoundaryDecision` answers `missing_signature` only
  when the signature is absent or `null`, as the reference does. A signature
  of another shape (a string, a list, an object without `sig`) is
  `invalid_signature`, and an expired decision with one is
  `decision_expired`. Likewise `VerifyDataAccessIntent` reports
  `Missing intent signature` only for an absent or `null` signature, and
  `Invalid intent signature` otherwise.
- A decision whose `decision_valid_until` does not parse no longer throws
  before its signature and form are checked. It is refused as
  `invalid_signature`, `unknown_field` or `non_canonical_form` when one of
  them applies, as the reference refuses it; the `FormatException` is
  thrown only when none does, so such a decision is never accepted.

### Upgrading

- `Auth.SerializerOptions` no longer sets `PropertyNameCaseInsensitive`.
  Code that reads its own JSON with these options needs the exact names the
  models declare (the Network Authority's snake_case names).

## [1.3.0] - 2026-10-10

### Changed

- The embedded field registry lists the Genesis Mesh 1.3.0 records
  (`ObservationRecord`, `BreakGlassRecord`, `JudgementRecord`,
  `QuarantineRecord`, `RegistryRecord`), the entry kinds `observation`,
  `break_glass`, `judgement`, `quarantine` and `registry`, the envelope
  fields `record_id`, `subject_id`, `matched_evidence_id` and
  `observation_sequence`, and the retention checkpoint's
  `observation_heads`. This SDK does not verify evidence exports, so it has
  nothing to refuse; the TypeScript and Rust SDKs verify the new records.

### Fixed

- Verifiers refuse a record that leaves out a field the reference always
  writes (`non_canonical_form`), as the reference does: a decision signed
  without `denial_reason` verified here. A field the reference leaves out
  when absent reads the same whether absent or `null`.
- `StrictFields.UnknownFields(string, string)` reads its JSON strictly (a
  duplicate key is refused), as every other record is read.

## [1.2.0] - 2026-10-10

Coordinated Genesis Mesh v1.2.0 release, *Nothing Lost, Anchored*:
verifiers that refuse what they do not know, strict input and records only
in canonical form.

### Changed (breaking)

- **Verifiers refuse signed fields they do not know.** This SDK used to copy
  every received field into the signed form, so a field a newer signer
  covered verified here and could change what a record means. The package
  now embeds the field registry of signed records (generated from the Python
  reference, shipped in the shared conformance suite `field_registry`).
  Verifiers check the signature over the record as received first; an
  authentic record with a signed field the registry does not list is then
  refused as `unknown_field`, meaning this SDK must be upgraded:
  `OfflineVerifier.VerifyBoundaryDecision` (also for the expected policies
  and attestation), `VerifyAgreement`, `VerifyDataLicensePolicySignature`
  (which returns false) and `VerifyDataAccessIntent` (an
  `intent_exceeds_license` violation naming the field). Only the signed
  projection is checked: the signature, and an agreement's unsigned fields,
  are not. Free-form fields (`claims`, `scope`, `execution_parameters`, ...)
  stay open.

- **Records are valid only in their canonical form.**
  `OfflineVerifier.VerifyBoundaryDecision` and `VerifyAgreement` refuse a
  record signed over a timestamp the reference does not write (`+00:00`
  rather than `Z`, a fraction `.000`) as `non_canonical_form`;
  `VerifyDataLicensePolicySignature` returns false; an intent check reports
  `Not in canonical form: intent` or `...: policy`. Records the NA signs are
  always canonical.
- **JSON is read strictly.** The verifiers, `Canonical.FromJson` and every
  client response refuse JSON every implementation would not read alike with
  `StrictJsonException` (a `JsonException`, as malformed JSON was before),
  whose `Reason` is `duplicate_key` (which `JsonDocument` kept twice),
  `non_finite_number` (`1e400`), `integer_out_of_range` (outside
  `-2**63 .. 2**64 - 1`), `negative_zero`, `lone_surrogate` or
  `invalid_json` (not JSON, a byte order mark, text that is not UTF-8, or
  arrays and objects nested more than `StrictJson.MaxDepth`, 64, deep).
  Client responses are decoded as strict UTF-8 rather than repaired with
  U+FFFD.
- **Canonical timestamps take ASCII digits only.** `CanonicalTimestamp` no
  longer accepts other Unicode digits or a trailing newline.
- `System.Text.Json` writes the float `-0.0` as `-0`, which the Network
  Authority now refuses as `negative_zero`: send `0` instead in request
  values you compute.

### Added

- `StrictJson` (`Check`, `Parse`, `DecodeUtf8`, `MaxDepth`),
  `StrictJsonException` and `StrictFields.CanonicalTimestamp`; the shared
  conformance suite `canonical`.
- `StrictFields.UnknownFields(model, record)`;
  `python scripts/sync_canonical_registry.py` regenerates the embedded
  registry from a new copy of the suite.

## [1.1.1] - 2026-10-09

Coordinated Genesis Mesh v1.1.1 release: security fixes in the Network
Authority. No API change in this SDK.

### Changed

- A 1.1.1 Network Authority decides under an agreement only if two parties it
  recognises signed it, binds the requester and provider to the agreement's
  parties, needs a privileged key to counter an offer, and admits execution
  evidence only in its exact signed form with UTC timestamps. Requests this
  SDK builds are unchanged; see *Upgrading to 1.1.1* in the core upgrade
  guide.

## [1.1.0] - 2026-10-08

Coordinated Genesis Mesh v1.1.0 release: signed container images and a local
governed Network Authority. No API change in this SDK.

### Changed

- The README links *Develop Against a Local Network Authority*: a governed
  Network Authority on a developer's machine, with a privileged setup key and
  a standard controller key (`genesis-mesh` 1.1.0).
- CI pins its actions to commits and runs with a read-only token; the
  security policy follows the core.

## [1.0.2] - 2026-10-05

Coordinated Genesis Mesh v1.0.2 release: fixes from external testing.

### Changed

- **Admin signatures cover the whole request (signature version 2):** the
  client signs the HTTP method, path, query parameters and the target NA's
  public key, read once from `/sovereign.json` or given as
  `ClientOptions.Audience`. Network Authorities from 1.0.2 accept only version
  2 by default.
- **Breaking:** `Auth.BuildAdminHeaders` takes an
  `AdminRequest(Method, Path, Audience, Body, Query)` instead of a body. New:
  `Auth.AdminSigningPayload` and `Auth.AdminSignatureVersion`. Shared
  conformance vectors: `tests/GenesisMesh.Sdk.Tests/testdata/conformance/admin_auth.json`.

### Fixed

- **Typed results carry the Network Authority's field names.** Most response
  models declared names the NA never sends, so their properties were always
  empty: `Agreement.Verify`, `Boundary.Verify` and `Evidence.Verify` always
  reported `Valid == false` (the NA answers `accepted`), and `Boundary.Decide`
  always reported `Allowed == false` (the NA sends `authorized`). The models
  now model the NA's JSON: `BoundaryDecision.Authorized` and `DenialReason`,
  `VerifyResult.Accepted` (`Valid` and `Accepted` are both true when the NA
  accepted, whichever name it used), `AgreementRecord.AgreedTerms`,
  `TrustEvidence.IssuerSovereignId`, `MembershipAttestation.SubjectId`,
  `CapabilityMembershipProof.RevealedCapability`,
  `DataLicensePolicy.LicensorSovereignId`, `DataAccessIntent.DeclaredSources`
  and the other fields the NA sends. The old names still compile, are marked
  `[Obsolete]` and are filled in from the NA's fields on deserialization;
  serializing a deserialized result gives the NA's fields only.
- `TrustDecision` has the fields the NA signs into trust evidence: `Trusted`,
  `HopCount`, `TrustPath`, `RequestedRoles` and `EvaluatedAt`. Evidence built
  from .NET recorded every decision as untrusted with zero hops.
- `OfferRecord` keeps a counter-offer's `AgreedTerms` and `ResponderEvidence`.
- The solution builds again: the `sdk-smoke-dotnet` sandbox signs admin
  requests with an `AdminRequest`.

### Tests

- The unit tests answer with the NA's JSON rather than the SDK's own models.
  `ContractTypesTests` deserializes a live NA's response to every call
  (`testdata/contract/na_responses.json`), and CI runs every method against a
  live Network Authority (`LiveContractTests`, the `live-contract` job).

## [1.0.1] - 2026-10-04

Coordinated Genesis Mesh v1.0.1 release: gateway console fixes. No changes in
this SDK.

## [1.0.0] - 2026-10-04

Coordinated Genesis Mesh v1.0.0 release: the public contract is stable for
the 1.x line and Genesis Mesh is ready for an independently operated pilot.
No functional changes in this SDK; its documented stable surface follows the
1.x compatibility rules.

## [0.65.0] - 2026-10-04

Coordinated Genesis Mesh v0.65.0 release: demo access and a guided tour in the
gateway, and the public reference federated with the live NA. No changes in
this SDK.

## [0.64.1] - 2026-10-03

Coordinated Genesis Mesh v0.64.1 release: the Network Authority republishes an
expiring CRL. No changes in this SDK.

## [0.64.0] - 2026-10-03

Coordinated Genesis Mesh v0.64.0 release: the Rust SDK and the Rust gateway
reach governed-action parity (boundary policies, evidence store, offline
verification). No changes in this SDK.

## [0.63.1] - 2026-10-02

Coordinated Genesis Mesh v0.63.1 release: configurable Network Authority rate
limits. No changes in this SDK.

## [0.63.0] - 2026-10-02

Coordinated Genesis Mesh v0.63.0 release: pilot readiness. No changes in
this SDK.

## [0.62.0] - 2026-10-02

Coordinated Genesis Mesh v0.62.0 release: the v1 public contract and security
review. No changes in this SDK. On the Network Authority, accepting an
agreement (`/admin/agreements/accept`) and creating a data license policy
(`/admin/data-usage/policy`) now require a privileged operator key; a
standard key gets `403 insufficient_operator_tier`.

## [0.61.1] - 2026-10-02

Coordinated Genesis Mesh v0.61.1 release.

### Fixed

- `ConsensusVote` and `ConsensusProof` now match the wire format (Python
  `ValidatorVote` and `ConsensusProof`). They previously used fields the NA
  never sends (`proposal_id`, `decision`, `threshold`, `assembled_at`). A
  Python-signed proof from the `consensus` conformance vectors now round-trips
  to the same canonical JSON.
- `Consensus.Verify` returns `ConsensusVerification` (`Valid`, `Reason`,
  `ConsensusId`).
- README consensus example uses the real request fields.

## [0.61.0] - 2026-10-02

Coordinated Genesis Mesh v0.61.0 release: the cross-language interoperability
proof. The core's interop scenario uses this SDK to verify data access intents
signed by the TypeScript SDK.

### Added

- `OfflineVerifier`: `VerifyAgreement`, `VerifyBoundaryDecision` (signature,
  expiry, freshness proof, policy and attestation bindings),
  `VerifyDataLicensePolicySignature` and `VerifyDataAccessIntent`, with the
  reason codes of the Python reference, plus `PolicyDigest`,
  `AttestationDigest` and `PolicySetDigest`.
- `Canonical`: Python-compatible canonical JSON.
- The shared `interop` conformance vectors (25), all passing.

### Fixed

- Canonical JSON for admin requests now matches Python's: non-ASCII text and
  DEL escaped as `ensure_ascii` does, keys ordered by code point, floats in
  Python's repr (`90.0` kept), integers kept exactly. Bodies with such values
  were rejected by the NA.

## [0.60.0] - 2026-10-01

Coordinated Genesis Mesh v0.60.0 release. No functional changes; the core adds
optional high availability for the Network Authority (PostgreSQL, several
instances behind a load balancer). This SDK keeps using one NA URL, which can
be the load balancer.

## [0.59.1] - 2026-10-01

Coordinated Genesis Mesh v0.59.1 release. No functional changes; the TypeScript
SDK adds attestation-backed evaluation, the boundary policy lifecycle and the
evidence store client. This SDK does not wrap them yet.

## [0.59.0] - 2026-10-01

Coordinated Genesis Mesh v0.59.0 release. No functional changes; the core adds
the Network Authority evidence store, which this SDK does not wrap yet.

## [0.58.1] - 2026-09-29

Coordinated Genesis Mesh v0.58.1 release. No functional changes; the core adds
attestation-backed boundary evaluation, which this SDK does not wrap yet.

## [0.58.0] - 2026-09-29

Coordinated Genesis Mesh v0.58.0 release. No functional changes; the SDK passes its
compatibility tests against the v0.58.0 Network Authority. Version 0.57 was skipped
across the train (see the core `docs/development/versioning.md`).

### Changed

- CI and publishing now fail if this component's version is ahead of the Genesis Mesh core version.

## [0.56.0] - Unreleased

### Changed

- Joined the coordinated Genesis Mesh v0.56.0 release train.
- Added a shared `VERSION` declaration and publishing guard that rejects tags
  which do not match project package metadata.
- Updated the supported security line to `0.56.x`.

---

## [0.55.0] — 2026-06-29

### Added

- `GenesisMeshClient` — unified entry point with 7 domain sub-clients over shared transport
- `AgreementClient` — capability offer, counter, accept, verify
- `BoundaryClient` — boundary decision and verification
- `EvidenceClient` — trust evidence build and verify
- `AttestationClient` — membership attestation issue, revoke, recognition policy
- `DisclosureClient` — selective Merkle capability disclosure, nullifier, prove, verify
- `ConsensusClient` — validator vote, consensus proof assembly and verify
- `DataUsageClient` — data license policy, access intent, get policy, verify
- `Auth` — `CanonicalJson`, `LoadSeed`, `BuildAdminHeaders` (Ed25519 via NSec.Cryptography)
- `Transport` — internal HTTP transport with admin header injection and typed error mapping
- `Errors` — `GenesisMeshException` and typed subclasses for all NA error codes
- `Models` — 16+ protocol record types matching the NA JSON wire format
- 20 unit tests across auth, errors, and all sub-client paths
- NuGet Trusted Publishing workflow (`publish.yml`)
- Smoke test (`sandbox/sdk-smoke-dotnet`) exercising all routes against a live NA

[0.55.0]: https://github.com/GenesisMeshLabs/sdk-dotnet/releases/tag/v0.55.0
