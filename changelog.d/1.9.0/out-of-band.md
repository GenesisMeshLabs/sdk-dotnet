### Added

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
