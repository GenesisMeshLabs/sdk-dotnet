### Added

- **Remediation and reviews, offline** (protocol 1.4.0), as in the TypeScript SDK:
  - `RemediationRules`, the NA's rules as pure functions with the reference's codes: the state table (`NextState`, `JudgementState`), plans (`Guidance`, `Plan`, `DecisionPlan`, `FactsOf`), due times (`DueAt`, `DueAfterDenial`, `DueAfterWaiver`), `StepsProblem`, the checks of a remediation record (`Check`, `FormProblem`, `RecordProblem`, `SecretProblem`), of a review (`ReviewApprovalProblem`, `ReviewHolderProblem`, `ReviewRequestProblem`, `ReviewRecordProblem`, `AdminApprovalValid`, `Rfc3339`), and of a policy's 1.4.0 sections (`PolicySectionIssues`, `EmergencyMap`).
  - `RemediationRecord`, `ReviewRecord`, `AdminApproval` and `PolicyGateEvaluation`, with their values (`RemediationStates`, `RemediationEvents`, `RemediationSteps` and the others).
  - `ExecutionRecorder.SignRemediation`, which refuses what the NA would refuse before signing (`remediation_malformed`, `remediation_secret_material`).
  - `OfflineVerifier.VerifyRemediationRecord` and `VerifyReviewRecord`; `VerifyOutOfBandRecord` and `OutOfBandCanonical` take both records too.
  - The remediation and review calls, the cancel pair and urgent changes come later in 1.9.0, and the export's remediation and review entries (with TypeScript's `rotationProblem`) after them.
- **The `remediation` conformance suite** (1.4.0): every kind but `verify_export` runs (312 vectors).

### Differs from TypeScript

- `NextState` returns the NA's refusal (`RemediationTransition.Refusal`) instead of throwing it.
- `RemediationRules.Check` and `RecordProblem` take the key as `ListExecutorKeys` returns it (`ExecutorKeyRecord`).
- As the reference, where TypeScript differs:
  - a due time refuses a date that does not exist, and `Rfc3339` returns null for a time outside the years 1 to 9999 in UTC;
  - `PolicySectionIssues` refuses a `stands_for` with a `*` anywhere, or a blank one;
  - a guidance entry's grace may be written as `600.0`.
- `ReviewApprovalProblem` refuses a review whose recorded time it cannot read; TypeScript skips the 300-second bound then.
