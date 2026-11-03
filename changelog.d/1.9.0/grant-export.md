### Added

- **Grants, their uses and revocations in an export** (protocol 1.6.0): `OfflineVerifier.VerifyEvidenceEvents` verifies the `grant`, `grant_execution` and `grant_revocation` entries, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes, and counts them (`Grants`, `GrantExecutions`, `GrantRevocations`):
  - a grant and its revocation are signed by the NA; a revocation is of a grant the run holds (`grant_missing`), naming its digest (`grant_mismatch`), once (`duplicate_grant_revocation`);
  - a use is signed by its grant's bearer key, a registered executor key of its sovereign, within the key's resource prefix (`grant_use_out_of_scope`; a use without a resource is outside any prefix, the empty one included); it continues its grant's use chain, whose head may be a quarantined use that continued it (`grant_use_chain_break`), names a grant the run holds and its digest (`grant_missing`, `grant_bearer_mismatch`, `grant_mismatch`), keeps the use rules against what the run holds (`GrantRules.UseProblem`), and takes the next place of its resource's chain (`resource_chain_break`; one without a resource has none: `envelope_mismatch`). Its envelope's `exec_sequence_no` names its place by value, as TypeScript's strict equality compares it;
  - no use shares an execution record's or a break-glass record's ID (`duplicate_record`), and a judgement may match a use;
  - an integer written with a fraction or an exponent (`1.0`, `1e0`) is read as the integer, as TypeScript reads it: a failure of the entry, never an exception.
- **Conformance:** the `grants` suite's 26 `verify_export` vectors run, so every vector of it an SDK can run does, and 168 variants of its store the Python reference verified, some under an NA key history or after a retention checkpoint (`testdata/grants/export_cases.json`). The live tests verify the NA's whole export, grants included, offline.

### Differs from TypeScript

- A use without a resource signed by a key whose resource prefix is empty fails as `grant_use_out_of_scope`, as the reference judges it; TypeScript lets it through.
