### Added

- **The records of changes outside the governed path, verified offline.** `OfflineVerifier.VerifyEvidenceEvents` verifies observations, break-glass records, judgements, quarantine entries and registry records in an export, as the TypeScript SDK's `verifyEvidenceEvents` does, with the reference's reason codes:
  - each signature: the observer's on an observation and the executor's on a break-glass record, a registered key of the record's sovereign with the role its kind needs; the NA's on the others, under its keys or the key its history held when the entry was stored;
  - a record is stored exactly as signed: a field outside its signature makes it invalid (`payload_invalid`), and its envelope names what it says (`envelope_mismatch`);
  - each judgement against its subject as stored (`judgement_subject_mismatch`, `judgement_subject_missing`), one per subject (`duplicate_judgement`), and a record it matches used once (`match_reused`) and held before it (`judgement_match_missing`);
  - the positions of each resource's observations (`observation_chain_break`), continued from a retention checkpoint's `observation_heads`;
  - a quarantine entry's record against its digest (`quarantine_digest_mismatch`);
  - execution evidence never rests on a judgement or a change record (`evidence_cites_judgement`).

  The result counts them (`Observations`, `BreakGlass`, `Judgements`, `Quarantined`). Remediation, review, key succession, archive and grant entries still fail as `unknown_entry_kind`; a judgement matching a grant use, or execution evidence resting on a remediation record, then fails on what this SDK can see.
- **Conformance:** the shared `out_of_band` suite's 37 `verify_export` vectors run.
