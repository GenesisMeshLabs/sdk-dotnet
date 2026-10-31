### Added

- **The record outbox.** `ClientOptions.RecordOutbox` keeps signed observations and break-glass records until the NA admits them, as `ClientOptions.Outbox` keeps execution records.
  - `EvidenceStore.EnqueueRecord` stores a record and submits it. A transient failure leaves it pending with a backoff, and a final refusal (`EvidenceOutbox.RecordPermanentRefusals`) keeps it as a dead letter, with the NA's quarantine entry when it kept the record.
  - `EvidenceStore.FlushRecords` submits what is pending: break-glass records first, then observations in batches of up to 100. A batch the NA refuses whole is split until the record it refuses is found, and a throttled batch halves the batches that follow and waits for the NA's `Retry-After`.
  - `FileRecordOutbox` stores entries in the TypeScript and Rust SDKs' format (`gm.evidence.record-outbox.v1`), keeping each record as it was read. A grant use or remediation record one of them kept there is submitted too.
  - `RecordOutboxEntry`, `RecordOutboxKinds`, `RecordDelivery`, `RecordFlushResult`, `EvidenceOutbox.RecordEntry` and `EvidenceOutbox.ClassifyRecord`.
