# Changelog

All notable changes to `sdk-dotnet` are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions align with the [Genesis Mesh release sequence](https://github.com/GenesisMeshLabs/genesismesh/blob/main/CHANGELOG.md).

---

## [1.9.2] - 2026-11-08

### Added

- **`OfflineVerifier.VerifyBoundaryDecision` checks an expected attestation or
  subject.** `DecisionVerifyOptions.ExpectedAttestationId` and
  `ExpectedSubjectId`, when set, must equal the decision's signed
  `attestation_binding`. Another attestation or subject fails with
  `attestation_binding_mismatch`, and a decision without an attestation binding
  with `attestation_binding_missing`. A relying party that does not hold the
  attestation can now check that a decision was made for the subject it
  expects. From 1.9.2 the Network Authority makes attestation-basis decisions
  only for the attestation's subject (core 1.9.2).

## [1.9.1] - 2026-11-04

### Changed

- Released with Genesis Mesh 1.9.1. Nothing changes in this SDK.

## [1.9.0] - 2026-11-04

### Added

- **Reading and verifying attestations.** `client.Attestation` reads a stored attestation with its current status (`Get`), lists attestations (`List`: signed with a key, the records; without one, the count), reads the active recognition policy (`GetPolicy`), asks the NA to verify an attestation against a policy (`Verify`), and reads the NA's signed revocation feed (`RevocationFeed`). Typed results: `AttestationRecord` (with v1.8.0 `ReissuedFrom` and `SupersededBy`), `AttestationList`, `AttestationVerification`, `RecognitionPolicy`, `RecognizedIssuer` and `SovereignRevocationFeed`, which keeps its signed form.
- **Anchors, audit packs and archives** (NA 1.2.0 and 1.5.0, as the TypeScript SDK's `anchors`, `allAnchors`, `anchor`, `auditPack`, `archives` and `recordArchive`):
  - `EvidenceStoreClient.Anchors` (`AnchorPageParams`, `AnchorPage`) and `AllAnchors`, which follows the pages and refuses a cursor that does not advance; `Anchor` (`AnchorResult`), retried and failed over;
  - `EvidenceStoreClient.AuditPack` (`AuditPackParams`: store positions, and `Since` and `Until` sent in UTC), never replayed;
  - `EvidenceStoreClient.Archives` (`ArchiveList`) and `RecordArchive` (`ArchiveResult`), which sends the manifest as received and is retried; its location is 1 to 1024 characters counted in code points, as the NA counts them.
- **Contract:** the five responses are recorded and checked at every depth, and the live contract check anchors the store, verifies an audit pack offline from the anchors it holds and records its archive.
- **The completeness check of audit packs** (protocol 1.5.0, as the reference's `genesis_mesh.workflows.audit_pack` and the TypeScript SDK's `audit-pack.ts`):
  - `OfflineVerifier.ParseRfc3339`: an RFC 3339 `date-time` in UTC as the reference prints it; a date without a time, a space for `T`, a missing offset, a leap second or an out-of-range field is refused;
  - `OfflineVerifier.ParseChanges`: changes from a log outside Genesis Mesh, as CSV (a header row naming `ChangeColumns`, read as Python's `csv` module reads it: a quote opens a quoted field only at the start of a field) or JSON (a list, or `{"changes": [...]}`), each a `Change`;
  - `OfflineVerifier.CheckCompleteness` (`CompletenessOptions`): matches the changes against a pack's records, by source event and then per resource and action for the most pairs and the least total distance within the tolerance, an observation and the record it matched being one change. `CompletenessReport` lists the pairs, the changes inside the period no record matches, those outside it, duplicates, and the records no change matched.
- **Conformance:** the `audit` suite's 13 `changes_file` and 5 `completeness` vectors run.
- **Every entry kind of the store in an export** (protocol 1.5.0): `OfflineVerifier.VerifyEvidenceEvents` verifies `key_succession` and `archive` entries, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes, and counts them (`KeySuccessions`, `Archives`). A kind from a later release still fails as `unknown_entry_kind`:
  - a key succession is signed by its outgoing key, an NA key, and by its new key (`succession_invalid_new_key_signature`); given the NA's key history with its root key, by the root key (`succession_invalid_root_signature`), and it is one of that history's successions (`succession_not_in_history`). It was not issued after it takes effect, and the store recorded it at least a minute before (`succession_backdated`);
  - an archive record is signed by the NA, under its keys or the key its history held when the entry was stored.
- **The successions of a key history:** `NAKeyHistory.SuccessionDigests`, filled by `VerifyKeyHistory`, and `OfflineVerifier.SuccessionDigest`, as TypeScript's `successionDigest`. A history built by hand with a root key verifies a store's key succession entries only with them.
- **Executor and observer keys placed by the store** (protocol 1.5.0): `ExecutorKeyRecord.RegisteredSequence`, `RegisteredAtTime`, `RetiredSequence` and `RetiredAtTime`, as an audit pack's registry records place a key. Given them, a record verifies only with a key registered before it (`evidence_unknown_executor`) and not retired before it (`evidence_executor_key_retired`); `OfflineVerifier.KeyPlacementProblem` is that rule. A placement time that cannot be read is refused (`ArgumentException`), so it never leaves a retired key unretired.
- **A range of an audit pack** (protocol 1.5.0): `EvidenceVerifyOptions.Prior`, the pack's cited entries. They seed what the range rests on and are verified as records, not as a run; their failures fail the range, the detail prefixed `cited`. The range then holds every chain from its start, and a record it cites that neither holds is `cited_record_missing`.
- **Conformance:** the `audit` suite's 13 `verify_export` and 2 `verify_range` vectors run, and the `field_registry` suite's 17 `entry_kind` vectors.
- **Audit packs offline** (protocol 1.5.0, as the reference's `verify_audit_pack` and the TypeScript SDK's `audit-pack.ts`):
  - `OfflineVerifier.VerifyAuditPack` (`VerifyAuditPackOptions`, `PackVerification`): a pack's manifest, the NA key history from the root key, the anchors held, the cited entries, the entries with the keys the pack's registry records place, the policies, attestations, treaties, feeds and agreements the records rest on, the report, the period asked for and the completeness check, each failure named by the reference's reason code, its file and its entry;
  - `ReadAuditPackDirectory`, `AuditPackFiles` (`AuditPackResponse`: the manifest written as the NA writes it), `PackJsonBytes`, `PackEntryDigests` and `PackReasons`; `AuditPackManifest`, `PackFile` and the file names (`PackFiles`, `OptionalPackFiles`);
  - `OfflineVerifier.RenderReport` (`ReportHeaderOf`) and `ReportCell`: `report.md` byte for byte as the reference renders it;
  - `OfflineVerifier.RegistryKeys`: executor and observer keys from the registry records the NA signed, placed by store position or by time;
  - `OfflineVerifier.ReceivedSignatureReason` and `VerifyArchiveRecord` (`ArchiveRecord`).
- **Conformance:** the `audit` suite's 22 `pack`, 1 `registry_keys` and 1 `report_cell` vectors run: every vector of the suite runs.
- **Break-glass in governed actions.** `Governance.GovernedAction` with `BreakGlassOptions` still runs the action when the NA cannot be reached, as the TypeScript SDK's `governedAction` with `breakGlass` does. The action gets a null decision, and its break-glass record, signed by the executor key with the justification, is kept in the record outbox; the NA judges it after the fact. The result is a `GovernedOutcome<T>`: `Governed` when the NA answered, `BreakGlass` (`BreakGlassResult<T>`) when it did not.
  - Only an outage breaks the glass: no answer, a timeout, a 5xx or a 429, whatever its body holds, so a proxy's error page does too (`Governance.EvaluationFailure`). A denial, a decision that fails verification, a refusal, a success that is not strict JSON, an evaluation answer whose body is cut off or stalls, `admin_auth_throttled`, `evidence_store_unavailable` and a cancellation never do. The NA key lookup counts by its status, even when its body cannot be read (below).
  - Everything break-glass needs is checked before anything is evaluated: a record outbox that can be read, a resource, an attestation basis, a requested capability, and a context and justification without secret material that leave 2048 bytes of the metadata limit for the report.
  - Once the action ran, its record is always kept: refused metadata is dropped (`BreakGlassResult.Dropped`), an outcome detail is cut to 1024 characters, and a failed action is recorded as a failure before its error is rethrown. A record the record outbox cannot keep is on the exception (`EvidenceNotKeptException.BreakGlassRecord`, `GovernedActionException.BreakGlassRecord`).
- **An answer whose body cannot be read is told from no answer.** `NetworkException.Code` is `network_error` for a request that got no answer, and `response_body_unreadable` when the NA answered but the body was cut off, stalled past the request timeout (which still covers the whole answer) or was larger than the client's `MaxResponseContentBufferSize`, as the TypeScript SDK names them; the outbox records that code. A key lookup whose body cannot be read counts by its status: a success is no answer and moves to the next instance, any other status is `na_public_key_unavailable` with it, where the TypeScript SDK names every such lookup `na_public_key_unavailable`.
- **Reading and administering the evidence store.** `client.EvidenceStore` reads the store's status (`Status`, with its anchoring and record counts), the NA's verification of every entry (`Verify`), one resource's history and one vendor's decisions with the NA's verification (`ResourceHistory`, `VendorHistory`), and the export, as text, parsed, or page after page from a cursor (`ExportText`, `Export`, `ExportAll`). It lists executor keys, retired ones included (`ListExecutorKeys`), retires one (`RetireExecutorKey`, privileged), applies retention (`ApplyRetention`, privileged, with `throughSequence`), and reads the latest retention checkpoint (`LatestCheckpoint`). The typed results model every field the NA sends, nested ones included, and a `RetentionCheckpoint` written back keeps its signed form.
- **`OfflineVerifier.ParseExportLines`** reads `gm.evidence.event` JSON Lines: strict JSON, schema version 1, and exactly the reference's envelope, as every implementation reads it.
- **The partner lifecycle** now shows the NA verifying the resource's history and the partner's decisions, the standard key refused key retirement and retention, and, at offboarding, a retired executor key that signs no new record while the store still verifies.
- **Execution evidence.** `ExecutionRecorder` builds and signs the record a controller submits after acting on an authorized decision, as the Python reference `record_execution` does: the same canonical form and signature, checked against records the reference signed. Records chain under their decision (`PriorRecord`) and on their resource across decisions (`PriorResource`; `ExecutionRecorder.HeadOf`, which refuses a record without a resource, and `Digest`). `ExecutionOutcomes` and `ResourceActions` name the values. `executed_at` is now, but never before the decision. Secret material (`SecretMaterialException`) and values the NA would refuse as `invalid_json` (`StrictJsonException`) are refused before anything is signed. `ISigner` signs for an executor key too.
- **The evidence store client.** `client.EvidenceStore` submits a signed record (`Submit`, `POST /evidence/execution`; a resubmission is `duplicate`, and a record built on a stale resource head is refused with 409 `evidence_conflict`), reads where a resource's chain ends (`ResourceHead`, `StoredResourceHead`), searches stored entries (`Search`, and `Iterate` over the pages), gives each resource's latest change from its execution evidence and break-glass records (`ResourceStates`), and registers executor keys, optionally limited to a resource prefix (`RegisterExecutorKey`, privileged).
- **Offline checks.** `OfflineVerifier.VerifyExecutionSignature` checks a record under its executor's key. `VerifyJustificationSignature` checks an evaluation's justification proof under the NA's keys, or under the key of its history valid when the proof was issued. Both refuse a record with a field this SDK does not know or a form the reference does not write. `ExecutionDigest` gives a record's digest as received.
- **`Canonical.Timestamp`** writes a time as the NA does: UTC, microseconds when not zero, `Z`.
- **Verifying an evidence export offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies an export, or a history (`Contiguous = false`), as the reference's `verify_evidence_events` does, with its reason codes: each envelope and its digests, the store chain, every signature (the NA's under its keys or its key history, the executor's under the registered keys, retired ones included), each execution record against its decision, and the decision and resource chains. A retention checkpoint continues the chains (`EvidenceVerifyOptions.Checkpoint`). It verifies decisions, justification proofs, execution evidence, retention checkpoints and the records of changes outside the governed path (the other kinds of the store: see below); a kind from a later release fails as `unknown_entry_kind`. `OfflineVerifier.VerifyRetentionCheckpoint` checks a checkpoint's NA signature.
- **Conformance:** the shared `field_registry` suite's `export` vectors run.
- **The live tests** verify the NA's export offline against the NA's own verification, under its key and its key history, and the partner lifecycle verifies the resource's and the partner's histories offline.
- **Governed actions.** `Governance.GovernedAction` makes one governed change, as the TypeScript SDK's `governedAction` does: it evaluates the request, verifies the decision offline (its signature and expiry, its binding to exactly the expected policies and to the expected attestation, its context, and again after the resource's head is read), runs the action only on an authorized decision, and signs and submits the execution evidence on the resource's chain. The decision is verified as the NA sent it, so a field this SDK does not know is refused by name (`unknown_field`). A denial returns without running the action. An action that throws is recorded as `failure` (`action failed`) and its error rethrown; when that record fails too, `GovernedActionException` carries both errors. A decision that fails verification throws `DecisionVerificationException` with the reason.
- **The evidence outbox.** With `ClientOptions.Outbox`, every record is kept until the NA admits it: a failed submission leaves it pending with a backoff (5 seconds to 15 minutes, or the NA's `Retry-After`), and a refusal no retry overcomes keeps it as a dead letter with the NA's code. A resource with pending records chains from the newest of them. After the action, secret material in its report is recorded without the refused fields (`MetadataRefusedException`, `Governance.WithoutRefusedMetadata`), and a record that cannot be kept throws `EvidenceNotKeptException`; both carry the action's value. `EvidenceStore.Enqueue`, `FlushPending` and `PendingHead` work the outbox; `EvidenceOutbox` holds its rules (`PermanentRefusals`, `Classify`, `RetryDelay`, `NextAttemptAt`). `FileOutbox` stores one JSON file per entry in the TypeScript and Rust SDKs' format; `MemoryOutbox` is for tests; `IOutbox<TEntry>` is the contract for a store of your own, such as a database.
- **Decision summary and reconciliation.** `Governance.SummarizeDecision` gives a decision's enforced and observe-mode failures apart, the policies applied and the attestation. `Governance.ReconcileResources` compares a cloud inventory with `EvidenceStore.ResourceStates`: unmanaged, drifted, present after a revoke, missing, or in sync.
- **`GenesisMeshException`** has a protected constructor with an inner exception.
- **Pre-issued grants, the NA's calls** (protocol 1.6.0, beta routes), as the TypeScript SDK's `GrantsClient`, with the NA's grants on (`GenesisMeshClient.Grants`):
  - `IssueGrant` (`IssueGrantRequest`), not retried: a second call would issue a second grant;
  - `Grants` (a page) and `AllGrants` (every page), filtered by status or bearer key; `Grant`, one grant with its uses left, policy versions and revocation;
  - `GrantUses` (a page) and `AllGrantUses` (every page): a grant's use chain, newest first, quarantined positions included;
  - `RevokeGrant`;
  - `RequestTimeToken`, for a request signed with the bearer key, returning the token exactly as the NA signed it;
  - `SubmitGrantUse`, safe to retry.
- **Models:** `IssuedGrant`, `GrantSummary`, `GrantDetail`, `GrantPolicyVersion`, `GrantList`, `GrantListParams`, `GrantUseSummary`, `GrantUses`, `GrantUsesParams`, `GrantRevoked` and `GrantStatuses`. `RecordSubmission.ResourceSequence` is the place the NA gave a grant use in its resource's chain.
- **Grant uses in the record outbox:** `EnqueueRecord(GrantExecutionEvidence)` and `EvidenceOutbox.RecordEntry(GrantExecutionEvidence)`.
- **`GrantNumber.Of`:** an envelope's bounds from a `long`, a `BigInteger` (up to 2^64 - 1) or a `double` (an integral one as an integer).
- **Grants, their uses and revocations in an export** (protocol 1.6.0): `OfflineVerifier.VerifyEvidenceEvents` verifies the `grant`, `grant_execution` and `grant_revocation` entries, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes, and counts them (`Grants`, `GrantExecutions`, `GrantRevocations`):
  - a grant and its revocation are signed by the NA; a revocation is of a grant the run holds (`grant_missing`), naming its digest (`grant_mismatch`), once (`duplicate_grant_revocation`);
  - a use is signed by its grant's bearer key, a registered executor key of its sovereign, within the key's resource prefix (`grant_use_out_of_scope`; a use without a resource is outside any prefix, the empty one included); it continues its grant's use chain, whose head may be a quarantined use that continued it (`grant_use_chain_break`), names a grant the run holds and its digest (`grant_missing`, `grant_bearer_mismatch`, `grant_mismatch`), keeps the use rules against what the run holds (`GrantRules.UseProblem`), and takes the next place of its resource's chain (`resource_chain_break`; one without a resource has none: `envelope_mismatch`). Its envelope's `exec_sequence_no` names its place by value, as TypeScript's strict equality compares it;
  - no use shares an execution record's or a break-glass record's ID (`duplicate_record`), and a judgement may match a use;
  - an integer written with a fraction or an exponent (`1.0`, `1e0`) is read as the integer, as TypeScript reads it: a failure of the entry, never an exception.
- **Conformance:** the `grants` suite's 26 `verify_export` vectors run, so every vector of it an SDK can run does, and 168 variants of its store the Python reference verified, some under an NA key history or after a retention checkpoint (`testdata/grants/export_cases.json`). The live tests verify the NA's whole export, grants included, offline.
- **`GrantHolder`** (protocol 1.6.0), as the TypeScript SDK's: the holder of a pre-issued grant acts within it while the NA cannot be reached (`Act`), keeps each use in the record outbox, and on reconnect reads the revocation feed, asks for a fresh time token and uploads its uses (`Sync`, `RefreshRevocationFeed`, `RefreshTimeToken`, `UpdateRevocationFeed`).
  - Before each change it checks the grant offline in the NA's order, and refuses a request outside it (`GrantRefusedException`): nothing runs.
  - When the grant has ended, its budget or time token is spent, or it is revoked, it falls back to a governed action with break-glass (`GrantFallback`): an NA that answers decides, one out of reach leaves the change to break-glass. Without a fallback: `GrantUnusableException`.
  - A use the NA quarantines after it continued the chain keeps its place and stops counting against the budget (a conflict, a chain mismatch or an over-budget refusal still counts); uses are never dated before their time token or the previous use; calls run one at a time, and the state changes whole.
  - `GrantHolderState` is the JSON the TypeScript SDK's holder keeps (`Persist`, `State`); `GrantStateNotKeptException` when it cannot be persisted.
- **Types:** `GrantHolderOptions`, `GrantActRequest`, `GrantFallback`, `GrantAuthority`, `GrantActResult<T>`, `GrantUseResult<T>`, `GrantFallbackResult<T>`, `GrantSyncResult`, `GrantFallbackCodes`, `GrantPaths`. `EvidenceNotKeptException.GrantUse` is the signed use a holder could not keep.
- **Pre-issued grants, offline** (protocol 1.6.0), as the TypeScript SDK's `grants.ts`:
  - the records `CapabilityGrant`, `TimeToken`, `TimeTokenRequest`, `GrantExecutionEvidence` and `GrantRevocationRecord`, with `GrantLimits`, `GrantRevocationReasons`, `GrantUseOutcomes` and `GrantUseRejectionCodes` (in the NA's order, and the ones it quarantines);
  - `GrantNumber`, the one number rule: an integral number is an integer, integers fit 64 bits, comparisons are exact;
  - `GrantRules`: `Parameter`, `EnvelopeShapeProblem`, `ParameterValueProblem`, `EnvelopeProblem`, `ScalarEquals`, `InRange`, `UseProblem` (with `UseCheck`, `PreviousUse`, `TokenHead`, `UseProblem`), `ContinuesChain` (`UseChainHead`), `NextPreviousUse`, `UseSecretProblem`, `GrantDigest`, `UseDigest` and `RecordDigest`;
  - `OfflineVerifier.VerifyTimeToken`, `VerifyGrantRevocation`, `VerifyGrantUse`, `VerifyTimeTokenRequest` and `TokenSignatureValid`, and `VerifyCapabilityGrant` under a list of NA keys;
  - `GrantRecords.UseRecord`, `CheckUse`, `SignUse` and `SignTimeTokenRequest` (`GrantUseInput`, `TimeTokenRequestInput`), which sign as the reference signs and refuse first what the NA would refuse (`GrantRecordException`).
- **Conformance:** the `grants` suite's `envelope_parameter`, `envelope` and `use` vectors run (53), and 1,510 cases the Python reference judged: envelope parameters, envelope problems, use problems at microsecond boundaries, the five records verified with their digests, and the secret guard.
- **Changes outside the governed path.** These calls need an NA with its out-of-band records on.
  - `ObservationRecorder` builds and signs an observation: a change seen at its source, signed by an observer key. Its ID is derived from the source event (`ObservationRecorder.ObservationId`), and `ObservationRecorder.FromFinding` turns a reconciliation finding into one, once per scan.
  - `ExecutionRecorder.SignBreakGlass` signs a break-glass record: a change a controller made while evaluation failed, with its justification.
  - Both refuse before signing what the NA would refuse (`OutOfBandRecordException`: `*_malformed`, `*_secret_material`), with the NA's metadata guard (`EvidenceMetadata.MetadataProblem`, `ObservationProblem`, `BreakGlassProblem`).
- **Out-of-band store calls.** `client.EvidenceStore` gains:
  - submitting the records: `SubmitObservation`, `SubmitObservations` (up to 100, one answer each) and `SubmitBreakGlass`;
  - judging them once: `JudgeObservation`, `JudgeBreakGlass`;
  - reading a resource's changes: `ResourceChanges`, `AllResourceChanges`;
  - managing operator-key holders: `OperatorHolders`, `ProposeHolder` and `ApproveHolder`.
- **Typed records:** `ObservationRecord`, `BreakGlassRecord`, `JudgementRecord`, `QuarantineRecord` and `RegistryRecord`, each keeping its signed form. Typed results: `RecordSubmission`, `ObservationBatchResult`, `JudgementSubmission`, `ResourceChanges`, `OperatorKeyHolder` and `HolderChange`.
- **Offline:** `OfflineVerifier.VerifyOutOfBandRecord` (under keys or the NA's key history), `OutOfBandCanonical` and `OutOfBandDigest`.
- **Conformance:** the shared `out_of_band` suite's `canonical_form`, `verify_record` and `metadata_problem` vectors run. `verify_export` comes with the out-of-band kinds of `VerifyEvidenceEvents`.
- **The partner credential lifecycle, live.** `PartnerLifecycleTests` runs a partner controller on a standard key against a live NA with `--policy-enforcement required` (the policy-free decide route refused): the platform team's setup with the privileged key, then create, rotate, retire and revoke through governed actions with an outbox, the denials and refusals, emergency removal under the security team's attestation after the partner's is revoked, and reconciliation. It runs in the `Live contract` job, next to `LiveContractTests`.
- **`scripts/contract_na.py`** has a second operator key, `controller` (standard tier), and `--policy-enforcement required`. `scripts/check_trx.py` takes `--only-test` more than once.
- **Policy-aware evaluation.** `client.Boundary.Evaluate(new EvaluateRequest { AttestationId = ..., RequestedCapability = ..., Context = ... })` asks for a decision under the active boundary policies (`POST /admin/boundary/evaluate`). It returns the signed decision and its justification proof. Exactly one basis is required: an attestation (`AttestationId`) or an agreement (`Agreement`, an `AgreementRecord`). A context holding secret material, or more than 16 KiB of metadata, is refused with `SecretMaterialException` before anything is signed or sent; the guard is the SDK's own, as in the TypeScript SDK. The quick start uses it instead of a raw admin call.
- **The boundary policy lifecycle.** `client.Policy` provides `Validate`, `Publish`, `Activate` (an older version is the rollback), `Deactivate`, `List`, `Active`, `History` and `Verify`. The typed `BoundaryPolicyIntent` and `BoundaryPolicy` come with their selector, gates and the 1.4.0 sections (`GateRemediation`, `ReviewRequired`, which is sent only when true, and `EmergencyCapability`). `Verify` takes the `BoundaryPolicy` as the NA returned it. `Publish` refuses an invalid intent with 400 `boundary_policy_invalid`, its issues in the exception's `Details`.
- **`EvidenceMetadata`.** `CheckMetadataOnly` and `SecretMaterial` are the NA's guard on metadata: at most 16 KiB, and no field named like a secret or holding a PEM block, a key or a token.
- **The record outbox.** `ClientOptions.RecordOutbox` keeps signed observations and break-glass records until the NA admits them, as `ClientOptions.Outbox` keeps execution records.
  - `EvidenceStore.EnqueueRecord` stores a record and submits it. A transient failure leaves it pending with a backoff, and a final refusal (`EvidenceOutbox.RecordPermanentRefusals`) keeps it as a dead letter, with the NA's quarantine entry when it kept the record.
  - `EvidenceStore.FlushRecords` submits what is pending: break-glass records first, then grant uses, observations in batches of up to 100, then remediation records. A batch the NA refuses whole, or that is too large for it (413), is split until the record it refuses is found. A throttled batch (429) halves the client's batches from then on, and a `Retry-After` holds every record back, 5 seconds to 15 minutes.
  - A record is removed only on an answer that says the NA holds it (`recorded`, `duplicate` or `quarantined`). An empty answer, another status, or a batch answer that does not give each observation of the batch one result, by its index, leaves the records pending (`response_body_unreadable`). The TypeScript SDK removes a record on any success but a refusal, and leaves an observation a batch answer does not name pending, on its own.
  - A record one call is submitting is left to it by every other call of the client. A flush leaves it pending; an enqueue of the same record returns it as `Queued` once it is stored, and until then waits for that call. A flush also leaves to another call a record that call stored a new state for since the flush read the outbox: pending as that call stored it, or left out once admitted or dead-lettered, so the flush never stores an older state over the newer one. The TypeScript SDK does neither: its `enqueueRecord` always submits, and its flush sends what it read.
  - `FileRecordOutbox` stores entries in the TypeScript and Rust SDKs' format (`gm.evidence.record-outbox.v1`), keeping each record as it was read. A grant use or remediation record one of them kept there is submitted too.
  - `RecordOutboxEntry`, `RecordOutboxKinds`, `RecordDelivery`, `RecordFlushResult`, `EvidenceOutbox.RecordEntry` and `EvidenceOutbox.ClassifyRecord`.
- **Remediation and review calls** (protocol 1.4.0), as in the TypeScript SDK, with the NA's remediation records on:
  - `EvidenceStoreClient.SubmitRemediation`, safe to retry;
  - the due queue: `Remediations` (a page) and `AllRemediations` (every page), filtered by a kept state or `review_pending`;
  - `Remediation`, one subject's state with its reviews and stored records;
  - `MarkNotified`, a notice to the change's actor;
  - `RequestReview`, `Review` and `DecideReview`.
- **Models:** `RemediationStatus`, `RemediationDetail`, `RemediationQueuePage`, `RemediationQueueParams`, `ReviewSummary`, `ReviewDetail`, `ReviewRequest`, `ReviewDecision`, `ReviewDecided` and `StoredRecord<TPayload>`. `RecordSubmission.Remediation` is the subject's state once a remediation record is recorded.
- **Remediation records in the record outbox:** `EnqueueRecord(RemediationRecord)` and `EvidenceOutbox.RecordEntry(RemediationRecord)`.
- **`RemediationInput.For`:** the subject, change and resource of a remediation state; `RemediationInput` is a record, so the versions and detail are added with `with`.
- **`Governance.CancelByRotatingForward`,** as TypeScript's `cancelByRotatingForward`: a denied change to a versioned resource is cancelled by a governed forward rotation, then the denied version is disabled, and both are recorded. The state, the plan and the denied version are checked before anything runs, and the record before the disable. Types: `RotateForwardOptions<T>`, `RotatedVersion<T>`, `RotateForwardResult<T>` and `RemediationNotRecordedException` (`remediation_not_recorded`, with the rotation's evidence and the signed record).
- **`Governance.UrgentAction`,** as TypeScript's `urgentAction`: the ordinary capability first, then the emergency one with the justification on a DENY, then break-glass naming the emergency capability when the NA cannot be reached. Types: `EmergencyOptions`, `UrgentActionResult<T>` and `UrgentPaths`.
- **`TransitionRefusedException`:** a state that takes no remediation record, refused before anything runs, with the NA's code.
- **Remediation and review entries, verified offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies an export's remediation and review records, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes:
  - a remediation record is signed by a registered executor key of its sovereign; a review by the NA, under its keys or the key its history held when the entry was stored; each envelope names what its record says (`envelope_mismatch`);
  - one remediation per judgement or decision (`duplicate_remediation`), of the change, resource and version its subject names (`remediation_subject_mismatch`), its subject held before it in a run from its start (`remediation_subject_missing`);
  - the forward rotation it cites: held before it (`remediation_rotation_missing`), of the same resource (`remediation_rotation_mismatch`), by the record's own executor (`remediation_rotation_other_executor`), after the change (`remediation_rotation_before_change`), cited once (`remediation_rotation_reused`);
  - a review is proved by the requests it embeds (`review_approval_invalid`), each signed with the public key and holder the NA-signed holder registry recorded for its operator key, when the run holds a holder record for that key (`review_approval_key_mismatch`), decided by a second holder (`review_same_holder`), of its subject as stored (`review_subject_mismatch`, `review_subject_missing`), and no waiver follows a remediation (`review_waiver_after_remediation`);
  - execution evidence never rests on a remediation or review record (`evidence_cites_judgement`).

  The result counts them (`Remediations`, `Reviews`). Key succession, archive and grant entries are verified too, from 1.9.0.
- **`RemediationRules.RotationProblem`** and `RotationFacts`, as TypeScript's `rotationProblem`: whether the forward rotation a remediation record cites can carry it.
- **Conformance:** the shared `remediation` suite's 28 `verify_export` vectors run; all 340 of its vectors now run. 177 further variants of the suite's store, with the answers of the reference's `verify_evidence_events`, run as unit tests (`testdata/remediation/export_cases.json`), some under an NA key history.
- **Remediation and reviews, offline** (protocol 1.4.0), as in the TypeScript SDK:
  - `RemediationRules`, the NA's rules as pure functions with the reference's codes: the state table (`NextState`, `JudgementState`), plans (`Guidance`, `Plan`, `DecisionPlan`, `FactsOf`), due times (`DueAt`, `DueAfterDenial`, `DueAfterWaiver`), `StepsProblem`, the checks of a remediation record (`Check`, `FormProblem`, `RecordProblem`, `SecretProblem`), of a review (`ReviewApprovalProblem`, `ReviewHolderProblem`, `ReviewRequestProblem`, `ReviewRecordProblem`, `AdminApprovalValid`, `Rfc3339`), and of a policy's 1.4.0 sections (`PolicySectionIssues`, `EmergencyMap`).
  - `RemediationRecord`, `ReviewRecord`, `AdminApproval` and `PolicyGateEvaluation`, with their values (`RemediationStates`, `RemediationEvents`, `RemediationSteps` and the others).
  - `ExecutionRecorder.SignRemediation`, which refuses what the NA would refuse before signing (`remediation_malformed`, `remediation_secret_material`).
  - `OfflineVerifier.VerifyRemediationRecord` and `VerifyReviewRecord`; `VerifyOutOfBandRecord` and `OutOfBandCanonical` take both records too.
  - The remediation and review calls, the cancel pair and urgent changes come later in 1.9.0, and the export's remediation and review entries (with TypeScript's `rotationProblem`) after them.
- **The `remediation` conformance suite** (1.4.0): every kind but `verify_export` runs (312 vectors).
- **Signature verifiers of NA records**, as the TypeScript SDK's: `OfflineVerifier.VerifyDecisionSignature`, `VerifyPolicySignature`, `VerifyAttestationSignature`, `VerifyTreatySignature` and `VerifyRevocationFeedSignature`, and `RevocationFeedSigner`.
  - Each takes the NA's keys, or its key history: then the key valid at the record's time.
  - With a time to judge at, an attestation, a treaty or a revocation feed must still vouch then (the vouching horizon).
  - A record must have no unknown field and be in canonical form as this SDK checks it: its timestamps, and every field the reference always writes. The TypeScript SDK checks unknown fields only, and not for a treaty.
- **Store anchors offline** (protocol 1.2.0, as the reference's `verify_store_anchors` and `check_events_against_anchors` and the TypeScript SDK's `anchors.ts`):
  - `StoreAnchor`, `OfflineVerifier.ParseAnchors` (an array of anchors, or a page of the anchors route; an anchor that is not well formed is refused), `AnchorCanonical` and `AnchorDigest`;
  - `OfflineVerifier.VerifyStoreAnchors` (`AnchorVerifyOptions`): each signature (`anchor_invalid_signature`; under a key history, `na_key_not_valid_at_time`), the sovereign (`anchor_sovereign_mismatch`), the anchor chain (`anchor_sequence_gap`, `anchor_chain_break`) and heads that only move forward (`anchor_not_increasing`);
  - `OfflineVerifier.CheckEventsAgainstAnchors` (`AnchorCheckOptions`): a run of entries tied to the anchors at its start (`export_not_linked_to_anchors`) and its end (`export_ends_before_anchor`), each anchor inside it naming its entry's digest (`anchor_mismatch`, `anchor_entry_missing`), summed up in `EvidenceStoreVerification.Anchors`;
  - `OfflineVerifier.StartingCheckpoint`: the retention checkpoint that explains where a run starts.
- **Conformance:** the `audit` suite's 2 `verify_anchors` vectors run.
- **Several NA instances, retries and operator signers.** `ClientOptions.BaseUrls` lists NA instances sharing one database, used without a load balancer. An idempotent request (a GET, a verification) moves to the next instance on a connection failure or a 502, 503 or 504. Any other request moves only when it never reached an instance. `ClientOptions.Retry` retries idempotent requests with backoff after a connection failure or a 429, 502, 503 or 504; it is off by default. `ClientOptions.Signer` takes an `ISigner` for keys this SDK does not hold, such as one in an HSM. `SeedSigner` signs with a seed held in memory, such as one read from Key Vault at start. `ClientOptions.Headers` adds headers to every request, and `ClientOptions.HttpClient` takes an `HttpClient`, such as a typed client from `IHttpClientFactory`, which the client does not dispose. This matches the TypeScript SDK with one difference: a request that times out, even while connecting, counts as possibly sent, so a state-creating request is not moved to another instance after a timeout.
- **Health and the NA key history.** `client.Health` reads `/healthz`, `/readyz` (a not-ready NA is returned with its failing checks) and `/health`, and probes each configured endpoint (`Endpoints()`). `client.NA.KeyHistory()` returns `/na/key-history`, and `client.NA.VerifiedKeyHistory(rootPublicKey)` verifies it from the root key you hold, throwing `KeyHistoryException` when it does not verify.
- **What a refusal carries.** `GenesisMeshException.RetryAfter` is the wait the NA named (`Retry-After`), and `Details` is the error's `details`. `NetworkException.ConnectFailed` is true when the request did not reach the NA. `Auth.BuildAdminHeadersAsync` signs with an `ISigner`.
- **The records of changes outside the governed path, verified offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies observations, break-glass records, judgements, quarantine entries and registry records in an export, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes:
  - each signature: the observer's on an observation and the executor's on a break-glass record, a registered key of the record's sovereign with the role its kind needs; the NA's on the others, under its keys or the key its history held when the entry was stored;
  - a record is stored exactly as signed: a field outside its signature makes it invalid (`payload_invalid`), and its envelope names what it says (`envelope_mismatch`);
  - each judgement against its subject as stored (`judgement_subject_mismatch`, `judgement_subject_missing`), one per subject (`duplicate_judgement`), and a record it matches used once (`match_reused`) and held before it (`judgement_match_missing`);
  - the positions of each resource's observations (`observation_chain_break`), continued from a retention checkpoint's `observation_heads`;
  - a quarantine entry's record against its digest (`quarantine_digest_mismatch`);
  - execution evidence never rests on a judgement or a change record (`evidence_cites_judgement`).

  The result counts them (`Observations`, `BreakGlass`, `Judgements`, `Quarantined`). Remediation, review, key succession, archive and grant entries are verified too, from 1.9.0.
- **Conformance:** the shared `out_of_band` suite's 37 `verify_export` vectors run.
- **XML documentation for every public type and member.** The 202 members that had none, most of them response properties, now say what each field means and which request fields the NA ignores. IntelliSense shows it, and the build fails on a public member without a doc comment.

### Changed

- **`ExecutorKeyState.ExecutorSovereignId`** is null in the answer to a retirement, which does not send it (new in 1.9.0).
- **The record outbox sends a grant's uses in their chain's order**, not the order they were added, so uses kept in another order (two acts at once, offline) do not stop each flush at the later one.
- **A grant use behind one the NA refused before it took its place in the chain is a dead letter** (`evidence_predecessor_dead_lettered`): such a use, refused without a quarantine entry or as a conflict or a chain mismatch, leaves a gap that never closes. The use behind it stayed pending (`grant_use_chain_gap`) and ended every flush that ignored backoff.
- **`OfflineVerifier.VerifyCapabilityGrant` refuses a grant the reference's model does not take** (`malformed`): a field it requires missing or of another type, a validity longer than 24 hours or starting more than 24 hours after its issue, an envelope parameter it cannot read. Such a grant was checked on its signature alone.
- **A failed read of the NA's key** from `/sovereign.json` throws the exception for that answer's status with the code `na_public_key_unavailable`. Before, an answer without the key threw `InvalidOperationException`, and an error answer threw its own code.
- **Verification routes and `/disclosure/prove` may be sent again**, on another instance or as a retry: they change no NA state, though each attempt writes an audit event. Requests that create state are never sent twice.
- **A `BaseUrl` that is not an absolute http(s) URL is refused when the client is built**, with `ArgumentException`. Before, a missing or relative one was accepted and the first call failed.
- **The constructors of the exception types name their parameters `status`, `code` and `detail`**, like `GenesisMeshException`'s, where they were `s`, `c` and `d`. Only a call that names its arguments changes.

### Fixed

- **Typed records passed back keep the form the NA signed.** `BoundaryDecision.DenialReason`, `MembershipAttestation.SubjectPublicKey`, `ConsensusVote.Reason` and `ContextDigest`, and `ConsensusProof.CascadeAssessmentDigest` were dropped when null, but the NA signs them as null. So such a record passed back was refused: a decision by `Boundary.Verify`, or an attestation's digest no longer matched a decision's attestation binding. They are now written as received.
- **A failed read of the NA's key is no longer reused.** Requests that need the NA's key at the same time share one lookup. Before, a failed lookup whose waiting requests had all been cancelled stayed cached, and the next admin request failed with the old error even after the NA had recovered; a request that resumed on another thread as the lookup failed could join it again on its failover attempt; and a lookup cancelled through the caller's own `HttpClient` (`CancelPendingRequests`) stayed joined. A lookup that has failed or been cancelled is now never joined, and a cancellation the caller's token did not ask for is a `NetworkException`, as a timeout is. Each waiting request now gets an exception of its own, its inner exception included.

### Differs from TypeScript

- `RecordArchive` takes the manifest as received (a `JsonElement`: `AuditPackResponse.Manifest`, or `manifest.json` read back), so it reaches the NA in the form the NA signed; TypeScript takes the typed manifest.
- `Anchors` refuses a page out of range (`Limit` 1 to 1000, `AfterAnchor` 0 or more) and `AuditPack` a store position below 1 before anything is sent; TypeScript sends them and the NA refuses them.
- A value of a changes file is trimmed as the reference (Python) trims it, U+001C to U+001F and U+0085 included and U+FEFF kept; TypeScript trims JavaScript's white space.
- A CSV file whose first line is blank has no columns, as the reference's `csv.DictReader` reads it; TypeScript skips the line.
- A time that falls outside the years 1 to 9999 once in UTC is refused (`FormatException`); TypeScript prints it out of range, and the reference raises `OverflowError`.
- A quote inside a CSV field (not at its start) is text, as the reference's `csv` module reads it; TypeScript opens a quoted field at any quote, so `r1, "rotate"` gives `rotate` there and `"rotate"` here. TypeScript also reads a lone CR as a line end.
- A placement time is read as the references read it, a fraction past six digits (.NET's `"o"` format) cut to microseconds; TypeScript throws for one.
- `NAKeyHistory` takes its successions as digests (`SuccessionDigests`); TypeScript's takes the records.
- A report writes a number the reference writes in exponent form (`1e+16`) as it does; TypeScript writes its digits.
- The reason of a denied judgement in a report, and an ID a judgement or a remediation state is looked up by, is Python's `str()` of it: `1.0` stays `1.0`, a list or object is Python's `repr()` (`{'why': ['x']}`); TypeScript writes `1`, `[object Object]` or the items joined by commas.
- A manifest whose signature carries a field other than `key_id` and `sig` is not in the NA's canonical form (`manifest_invalid`), as the reference reads it; TypeScript accepts it.
- The record outbox's grant uses: as above; TypeScript keeps the order they were added and retries a use behind such a gap.
- `RequestTimeToken` takes a request already signed (`GrantRecords.SignTimeTokenRequest`); TypeScript also signs one when given the bearer key's signer.
- A time token is a `JsonElement`, kept as the NA signed it, so its signature verifies and a use carries it unchanged.
- `AllGrants` and `AllGrantUses` follow the pages; TypeScript reads one page at a time.
- A limit outside 1 to 500 is refused before anything is sent.
- A use without a resource signed by a key whose resource prefix is empty fails as `grant_use_out_of_scope`, as the reference judges it; TypeScript lets it through.
- A failed action's use is persisted with the state (TypeScript's holder moves its chain without persisting it).
- On its first change the holder takes up a later use of its grant the record outbox holds (kept before a stop left the state unpersisted), so no place is signed twice.
- `Sync` still uploads when the NA refuses a time token for a reason other than the grant's end (`GrantSyncResult.TimeTokenRefusal`, such as `time_token_request_stale`); `Act` then falls back, and the NA decides. TypeScript throws, and its sync uploads nothing.
- Under an empty resource prefix, a request without a resource is refused (`grant_use_out_of_scope`), as the NA refuses such a use; TypeScript lets it run.
- The action is a `Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>>`; the result holds either `UnderGrant` or `Fallback`, whose `Outcome` is the governed action's `GovernedOutcome<T>`.
- `NAKeys` is a key history; a list of keys is `NAKeyHistory.ValidAlways(keys)`.
- `GrantRules.Parameter` returns the problem (`invalid envelope parameter: ...`) instead of throwing it, so the envelope rules call nothing that throws.
- A use's `time_token` is kept as received (`JsonElement`), so its signature verifies as the NA signed it.
- `GrantRules.UseProblem` throws `ArgumentException` for a time in its `UseCheck` that is not a timestamp. TypeScript skips the rule that time is for, so an unreadable revocation time lets the use through.
- `GrantUseInput` takes the previous use or its digest, not both (`ArgumentException`).
- **`DecideReview`** refuses a waiver's end given for another verdict, or a waiver without one, before sending anything.
- **`CancelByRotatingForward`:**
  - the denied version defaults to the change's (`ChangeVersionId`), and another one is refused before anything runs (`remediation_subject_mismatch`), as the NA would refuse the record;
  - a rotation whose evidence the NA refused for good (a dead letter) is not followed by the disable;
  - its cancellation token stops the steps up to the disable; once the denied version is disabled, the record is kept whatever the token says;
  - a rotation naming another resource, or an action other than `rotate` or `update`, is refused before anything runs;
  - `RemediationNotRecordedException` carries the rotation's evidence, value and outbox entry, the record, and whether the disable ran, instead of the rotation's whole result.
- **`UrgentAction`** refuses an empty emergency capability or no emergency policies before anything runs (`ArgumentException`).
- **`UrgentActionResult<T>.Outcome`** is a `GovernedOutcome<T>`, as the break-glass overload of `GovernedAction` returns.
- `NextState` returns the NA's refusal (`RemediationTransition.Refusal`) instead of throwing it.
- `RemediationRules.Check` and `RecordProblem` take the key as `ListExecutorKeys` returns it (`ExecutorKeyRecord`).
- As the reference, where TypeScript differs:
  - a due time refuses a date that does not exist, and `Rfc3339` returns null for a time outside the years 1 to 9999 in UTC;
  - `DueAfterDenial` refuses a decision whose grace runs past the year 9999, whenever the change was due before (times in UTC, as the NA writes them);
  - `PolicySectionIssues` refuses a `stands_for` with a `*` anywhere, or blank as Python's `str.strip()` reads it (U+001C to U+001F included, which .NET does not count as white space);
  - a guidance entry's grace may be written as `600.0`.
- `ReviewApprovalProblem` reads a review's recorded times (`decided_at`, `requested_at`) first, as the review's model does, and takes each only as a UTC timestamp in the form the NA writes (`Z`, `+00:00` or `-00:00`): `the review's decided_at is not a UTC timestamp` otherwise. TypeScript skips the 300-second bound for a time it cannot read. The reference's model also reads lenient spellings the NA never writes (a space, a lower-case `t` or `z`, `+0000`); this SDK does not.
- `VerifyStoreAnchors` needs the NA's keys or key history, or `SkipSignatures` to check no signature; TypeScript skips the signatures when its keys are null.
- `StartingCheckpoint` gives the checkpoint as received, the JSON `EvidenceVerifyOptions.Checkpoint` takes.
- `CheckEventsAgainstAnchors` refuses an anchor that is not well formed, as `VerifyStoreAnchors` does; TypeScript checks none there.
- `ParseAnchors` reads JSON (an array, or a page of the anchors route); TypeScript's `parseAnchors` checks items already parsed.

### Differs from the reference

- A CSV file the reference's `csv` module raises `csv.Error` for (a carriage return inside a line, outside quotes; a field longer than 131072 characters) is refused (`FormatException`).
- `ParseRfc3339` refuses a time ending in a line feed, which the reference's `$` accepts (`ParseChanges` trims values first, so a changes file reads the same).
- A manifest or held anchor written in a form the reference's models coerce (`6.0` or `"6"` for an integer, `true` for 1, `+00:00` for `Z`) is refused by its shape or signature, as TypeScript refuses it; the reference reads the coerced value and also names `non_canonical_form`.
- A decision entry whose `decision` is not an object fails the entry's checks; the reference raises (`AttributeError`) while rendering the report.
- `ReportHeaderOf` keeps a manifest time it does not read (one with more than six fraction digits) as given; the reference truncates it to microseconds. Such a manifest is not in canonical form, and both refuse the pack.

## [1.8.1] - 2026-10-24

### Added

- **The vouching horizon (Genesis Mesh 1.8.0).** Judged for the present, a
  record signed by an NA key that has ended vouches only until that key's end,
  the 300-second hand-over and a grace of at most 24 hours. Judged at its own
  time, it is unchanged.
  - New members:
    - `NAKey.End`;
    - `NAKeyHistory.SuccessionVouchingGrace` (24 hours);
    - a new `NAKeyHistory` constructor that takes the grace;
    - `NAKeyHistory.VouchingGrace`, `WithVouchingGrace` (only shortens),
      `VouchesUntil`, `RecordOutlivesKey` and `KeysVouchingAt`.
  - `OfflineVerifier.VerifyCapabilityGrant(grantJson, history, now)`: the
    grant check judged for the present, refusing a grant past its key's
    horizon as `outlives_key`.
  - `OfflineVerifier.NARecordRefusal(model, recordJson, history, now)`: the
    reference's verdict on an attestation, treaty, revocation feed or grant.
    It returns `record_malformed` for a record that its kind's time rules
    refuse, such as a grant that starts more than 24 hours after its issue.
  - `VerifyKeyHistoryResponse` takes the NA's `vouching_grace_seconds` only
    to shorten the grace.
  - The shared `vouching` conformance suite (32 vectors) runs on every test
    run.

### Upgrading

- **Upgrade the Network Authority to 1.8.0 first** and let it re-issue its
  records (`reissue_pending: false` on its `/readyz`) before judging records
  for the present. Every existing verifier keeps judging at the record's own
  time.

## [1.8.0] - 2026-10-23

### Security

- **An attestation ID can no longer reach another admin route.**
  `Attestation.Revoke` put the ID into the request path as given, so an ID
  such as `x/../../recognition-policy?` signed and sent
  `POST /admin/recognition-policy`. An ID that is not one path segment
  (empty, `.` or `..`, or holding `/`, `\`, `?`, `#`, `%` or a control
  character) now throws before anything is signed or sent.
- **`Auth.SerializerOptions` is read-only.** The options this SDK reads the
  NA's responses with were public and mutable, so any code in the process (a
  host application, a plugin, a test helper) could set
  `PropertyNameCaseInsensitive = true` and undo the 1.3.1 defence: a
  `Boundary.Verify` response of
  `{"valid":false,"accepted":false,"Valid":true,"Accepted":true}` then read
  as valid and accepted. The options are now frozen when the SDK loads.

### Added

- **`ForbiddenException` (403), `ConflictException` (409),
  `PayloadTooLargeException` (413) and `ClientErrorException` (any other
  4xx).** All derive from `GenesisMeshException`.
- **A `net10.0` build.** The package targets `net8.0` and `net10.0`, and CI
  tests both. .NET 8 support ends on 2026-11-10; applications on .NET 10 now
  get a build compiled and tested for it.

### Deprecated

- **`ErrorParser`, `StrictJson` and `Canonical.PythonFloatRepr` are
  obsolete** and become internal in 2.0. They are how the SDK reads error
  responses and JSON and writes numbers, not API for callers: catch the
  `GenesisMeshException` subclasses and `StrictJsonException` the client and
  the verifiers throw, and use `Canonical.Of` or `Canonical.FromJson` for the
  canonical form. They work as before until then; code that uses them now
  gets warning CS0618 (an error under `TreatWarningsAsErrors`).

### Fixed

- **A counter-offer can be accepted.** `Agreement.Accept` sends
  `{"offer": ...}`, which the Network Authority refuses for a counter-offer
  (422 `accept_rejected`), so the SDK had no way to accept one. The new
  `Agreement.AcceptCounter(counter, originalOffer)` sends
  `{"counter": ..., "original_offer": ...}`, the form the NA accepts.
- **Admin requests work under every culture.** The SDK wrote the signed
  `X-Admin-Timestamp` in the current culture: under a culture with another
  calendar (`th-TH`, `ar-SA`) the year was 2569 or 1448, and under one with
  another time separator (`fi-FI`) the time read `16.27.41`, so the NA refused
  every admin request. The timestamp is written in the invariant culture.
- **Cancelling a call throws `OperationCanceledException`.** When the
  `CancellationToken` passed to a client method was cancelled, the SDK
  reported it as a `NetworkException`, so
  `catch (OperationCanceledException)` never saw the caller's own
  cancellation. A timeout (`ClientOptions.Timeout`) is still a
  `NetworkException`.
- **A data usage intent and policy can be passed back to `DataUsage.Verify`
  as returned.** The typed `DataAccessIntent` and `DataLicensePolicy` left out
  `estimated_volume_bytes` and `max_volume_bytes_per_session` when they were
  null, but the Network Authority signs them as null: it refused the intent
  ("Invalid intent signature", `intent_exceeds_license`). Both fields are now
  written as null.
- **A 4xx is no longer a `ServerException`.** The NA's 403 (such as
  `insufficient_operator_tier`), 409 (such as `boundary_policy_required`),
  413 and every other 4xx without its own type were thrown as
  `ServerException`, documented as "HTTP 5xx", so a policy that retries
  server errors retried requests the NA had refused. `ServerException` is now
  thrown for 5xx only.
- **A model name the field registry lacks is refused.** The verifiers name
  the record models they check by string; a name the embedded registry did
  not have (a typo, or a model the core renamed) read as a model with no
  fields, so the `unknown_field` and `non_canonical_form` checks were
  silently off for that verifier. Such a name now throws
  `ArgumentException`, and a test checks every model named in the source
  against the registry.

### Upgrading

- **Cancellation.** Code that caught `NetworkException` to handle its own
  cancellation must catch `OperationCanceledException` (or
  `TaskCanceledException`) instead.
- **HTTP 4xx exceptions.** A 403, 409 or 413 now throws `ForbiddenException`,
  `ConflictException` or `PayloadTooLargeException`, and any other 4xx
  without its own type (such as 405) throws `ClientErrorException`, where
  1.7.0 threw `ServerException`. `catch (GenesisMeshException)` catches them
  as before; a `catch (ServerException)` meant for these statuses must catch
  the new types (or `GenesisMeshException`). A non-error status this SDK does
  not know (such as an unfollowed redirect) throws `GenesisMeshException`
  itself.
- **`Attestation.Revoke` validates its ID.** It throws
  `ArgumentNullException` for a null ID and `ArgumentException`
  (`ParamName` `attestationId`) for an ID that is not one path segment, where
  it used to send the request. IDs the Network Authority issues are not
  affected.
- Setting any property of `Auth.SerializerOptions` now throws
  `InvalidOperationException`. To serialize with other settings, copy the
  options first: `new JsonSerializerOptions(Auth.SerializerOptions) { ... }`.
- **`StrictFields.UnknownFields`.** Called with a model the registry does
  not have, it throws `ArgumentException`; 1.7.0 returned an empty list,
  which read as "no unknown fields".

## [1.7.0] - 2026-10-15

### Changed

- Released with Genesis Mesh 1.7.0 (the edge agent). Nothing changes in this SDK: 1.7.0 adds no signed record
  kind, so it verifies everything a 1.7.0 Network Authority signs, as 1.6.0 did.

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
