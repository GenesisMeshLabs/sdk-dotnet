### Added

- **`Governance.CancelByRotatingForward`,** as TypeScript's `cancelByRotatingForward`: a denied change to a versioned resource is cancelled by a governed forward rotation, then the denied version is disabled, and both are recorded. The state, the plan and the denied version are checked before anything runs, and the record before the disable. Types: `RotateForwardOptions<T>`, `RotatedVersion<T>`, `RotateForwardResult<T>` and `RemediationNotRecordedException` (`remediation_not_recorded`, with the rotation's evidence and the signed record).
- **`Governance.UrgentAction`,** as TypeScript's `urgentAction`: the ordinary capability first, then the emergency one with the justification on a DENY, then break-glass naming the emergency capability when the NA cannot be reached. Types: `EmergencyOptions`, `UrgentActionResult<T>` and `UrgentPaths`.
- **`TransitionRefusedException`:** a state that takes no remediation record, refused before anything runs, with the NA's code.

### Differs from TypeScript

- **`CancelByRotatingForward`:**
  - the denied version defaults to the change's (`ChangeVersionId`), and another one is refused before anything runs (`remediation_subject_mismatch`), as the NA would refuse the record;
  - a rotation whose evidence the NA refused for good (a dead letter) is not followed by the disable;
  - its cancellation token stops the steps up to the disable; once the denied version is disabled, the record is kept whatever the token says;
  - a rotation naming another resource, or an action other than `rotate` or `update`, is refused before anything runs;
  - `RemediationNotRecordedException` carries the rotation's evidence, value and outbox entry, the record, and whether the disable ran, instead of the rotation's whole result.
- **`UrgentAction`** refuses an empty emergency capability or no emergency policies before anything runs (`ArgumentException`).
- **`UrgentActionResult<T>.Outcome`** is a `GovernedOutcome<T>`, as the break-glass overload of `GovernedAction` returns.
