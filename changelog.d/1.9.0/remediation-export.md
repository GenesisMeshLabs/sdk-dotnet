### Added

- **Remediation and review entries, verified offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies an export's remediation and review records, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes:
  - a remediation record is signed by a registered executor key of its sovereign; a review by the NA, under its keys or the key its history held when the entry was stored; each envelope names what its record says (`envelope_mismatch`);
  - one remediation per judgement or decision (`duplicate_remediation`), of the change, resource and version its subject names (`remediation_subject_mismatch`), its subject held before it in a run from its start (`remediation_subject_missing`);
  - the forward rotation it cites: held before it (`remediation_rotation_missing`), of the same resource (`remediation_rotation_mismatch`), by the record's own executor (`remediation_rotation_other_executor`), after the change (`remediation_rotation_before_change`), cited once (`remediation_rotation_reused`);
  - a review is proved by the requests it embeds (`review_approval_invalid`), each signed with the public key and holder the NA-signed holder registry recorded for its operator key (`review_approval_key_mismatch`), decided by a second holder (`review_same_holder`), of its subject as stored (`review_subject_mismatch`, `review_subject_missing`), and no waiver follows a remediation (`review_waiver_after_remediation`);
  - execution evidence never rests on a remediation or review record (`evidence_cites_judgement`).

  The result counts them (`Remediations`, `Reviews`). Key succession, archive and grant entries still fail as `unknown_entry_kind`.
- **`RemediationRules.RotationProblem`** and `RotationFacts`, as TypeScript's `rotationProblem`: whether the forward rotation a remediation record cites can carry it.
- **Conformance:** the shared `remediation` suite's 28 `verify_export` vectors run; all 340 of its vectors now run.

### Fixed

- **`RemediationRules.PolicySectionIssues` reads a blank `stands_for` as the reference does:** Python's `str.strip()` also removes U+001C to U+001F, which .NET does not count as white space, so a `stands_for` of only those characters was accepted.
