### Added

- **The record outbox.** `ClientOptions.RecordOutbox` keeps signed observations and break-glass records until the NA admits them, as `ClientOptions.Outbox` keeps execution records.
  - `EvidenceStore.EnqueueRecord` stores a record and submits it. A transient failure leaves it pending with a backoff, and a final refusal (`EvidenceOutbox.RecordPermanentRefusals`) keeps it as a dead letter, with the NA's quarantine entry when it kept the record.
  - `EvidenceStore.FlushRecords` submits what is pending: break-glass records first, then grant uses, observations in batches of up to 100, then remediation records. A batch the NA refuses whole, or that is too large for it (413), is split until the record it refuses is found. A throttled batch (429) halves the client's batches from then on, and a `Retry-After` holds every record back, 5 seconds to 15 minutes.
  - `FileRecordOutbox` stores entries in the TypeScript and Rust SDKs' format (`gm.evidence.record-outbox.v1`), keeping each record as it was read. A grant use or remediation record one of them kept there is submitted too.
  - `RecordOutboxEntry`, `RecordOutboxKinds`, `RecordDelivery`, `RecordFlushResult`, `EvidenceOutbox.RecordEntry` and `EvidenceOutbox.ClassifyRecord`.
