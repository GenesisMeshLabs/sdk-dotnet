### Added

- **Remediation and review entries, verified offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies an export's remediation and review records, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes:
  - a remediation record is signed by a registered executor key of its sovereign; a review by the NA, under its keys or the key its history held when the entry was stored; each envelope names what its record says (`envelope_mismatch`);
  - one remediation per judgement or decision (`duplicate_remediation`), of the change, resource and version its subject names (`remediation_subject_mismatch`), its subject held before it in a run from its start (`remediation_subject_missing`);
  - the forward rotation it cites: held before it (`remediation_rotation_missing`), of the same resource (`remediation_rotation_mismatch`), by the record's own executor (`remediation_rotation_other_executor`), after the change (`remediation_rotation_before_change`), cited once (`remediation_rotation_reused`);
  - a review is proved by the requests it embeds (`review_approval_invalid`), each signed with the public key and holder the NA-signed holder registry recorded for its operator key, when the run holds a holder record for that key (`review_approval_key_mismatch`), decided by a second holder (`review_same_holder`), of its subject as stored (`review_subject_mismatch`, `review_subject_missing`), and no waiver follows a remediation (`review_waiver_after_remediation`);
  - execution evidence never rests on a remediation or review record (`evidence_cites_judgement`).

  The result counts them (`Remediations`, `Reviews`). Key succession and archive entries still fail as `unknown_entry_kind` (grant entries are verified too, from 1.9.0).
- **`RemediationRules.RotationProblem`** and `RotationFacts`, as TypeScript's `rotationProblem`: whether the forward rotation a remediation record cites can carry it.
- **Conformance:** the shared `remediation` suite's 28 `verify_export` vectors run; all 340 of its vectors now run. 177 further variants of the suite's store, with the answers of the reference's `verify_evidence_events`, run as unit tests (`testdata/remediation/export_cases.json`), some under an NA key history.
