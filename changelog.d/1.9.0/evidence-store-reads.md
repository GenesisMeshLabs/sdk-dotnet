### Added

- **Reading and administering the evidence store.** `client.EvidenceStore` reads the store's status (`Status`, with its anchoring and record counts), the NA's verification of every entry (`Verify`), one resource's history and one vendor's decisions with the NA's verification (`ResourceHistory`, `VendorHistory`), and the export, as text, parsed, or page after page from a cursor (`ExportText`, `Export`, `ExportAll`). It lists executor keys, retired ones included (`ListExecutorKeys`), retires one (`RetireExecutorKey`, privileged), applies retention (`ApplyRetention`, privileged, with `throughSequence`), and reads the latest retention checkpoint (`LatestCheckpoint`). The typed results model every field the NA sends, nested ones included, and a `RetentionCheckpoint` written back keeps its signed form.
- **`OfflineVerifier.ParseExportLines`** reads `gm.evidence.event` JSON Lines: strict JSON, schema version 1, and exactly the reference's envelope, as every implementation reads it.
- **The partner lifecycle** now shows the NA verifying the resource's history and the partner's decisions, the standard key refused key retirement and retention, and, at offboarding, a retired executor key that signs no new record while the store still verifies.

### Changed

- **`ExecutorKeyState.ExecutorSovereignId`** is null in the answer to a retirement, which does not send it (new in 1.9.0).
