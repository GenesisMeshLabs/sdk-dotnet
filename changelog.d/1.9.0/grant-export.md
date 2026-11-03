### Added

- **Grants, their uses and revocations in an export** (protocol 1.6.0): `OfflineVerifier.VerifyEvidenceEvents` verifies the `grant`, `grant_execution` and `grant_revocation` entries, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes, and counts them (`Grants`, `GrantExecutions`, `GrantRevocations`):
  - a grant and its revocation are signed by the NA; a revocation is of a grant the run holds (`grant_missing`), naming its digest (`grant_mismatch`), once (`duplicate_grant_revocation`);
  - a use is signed by its grant's bearer key, a registered executor key of its sovereign, within the key's resource prefix (`grant_use_out_of_scope`); it continues its grant's use chain, whose head may be a quarantined use that continued it (`grant_use_chain_break`), names a grant the run holds and its digest (`grant_missing`, `grant_bearer_mismatch`, `grant_mismatch`), keeps the use rules against what the run holds (`GrantRules.UseProblem`), and takes the next place of its resource's chain (`resource_chain_break`; one without a resource has none: `envelope_mismatch`);
  - no use shares an execution record's or a break-glass record's ID (`duplicate_record`), and a judgement may match a use.
- **Conformance:** the `grants` suite's 26 `verify_export` vectors run, so every vector of it an SDK can run does, and 73 variants of its store the Python reference verified (`testdata/grants/export_cases.json`). The live tests verify the NA's whole export, grants included, offline.

### Changed

- **An envelope field naming an integer of its record** (a use's `exec_sequence_no`) is compared by value, as TypeScript's strict equality compares it; before, only text fields were.
