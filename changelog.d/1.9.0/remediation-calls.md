### Added

- **Remediation and review calls** (protocol 1.4.0), as in the TypeScript SDK, with the NA's remediation records on:
  - `EvidenceStoreClient.SubmitRemediation`, safe to retry;
  - the due queue: `Remediations` (a page) and `AllRemediations` (every page), filtered by a kept state or `review_pending`;
  - `Remediation`, one subject's state with its reviews and stored records;
  - `MarkNotified`, a notice to the change's actor;
  - `RequestReview`, `Review` and `DecideReview`.
- **Models:** `RemediationStatus`, `RemediationDetail`, `RemediationQueuePage`, `RemediationQueueParams`, `ReviewSummary`, `ReviewDetail`, `ReviewRequest`, `ReviewDecision`, `ReviewDecided` and `StoredRecord<TPayload>`. `RecordSubmission.Remediation` is the subject's state once a remediation record is recorded.
- **Remediation records in the record outbox:** `EnqueueRecord(RemediationRecord)` and `EvidenceOutbox.RecordEntry(RemediationRecord)`.
- **`RemediationInput.For`:** the subject, change and resource of a remediation state; `RemediationInput` is a record, so the versions and detail are added with `with`.

### Differs from TypeScript

- **`DecideReview`** refuses a waiver's end given for another verdict, or a waiver without one, before sending anything.
- **`MarkNotified`, `RequestReview` and `DecideReview`** are not retried, as in TypeScript; `SubmitRemediation` is.
