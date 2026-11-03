### Added

- **Anchors, audit packs and archives** (NA 1.2.0 and 1.5.0, as the TypeScript SDK's `anchors`, `allAnchors`, `anchor`, `auditPack`, `archives` and `recordArchive`):
  - `EvidenceStoreClient.Anchors` (`AnchorPageParams`, `AnchorPage`) and `AllAnchors`, which follows the pages and refuses a cursor that does not advance; `Anchor` (`AnchorResult`), retried and failed over;
  - `EvidenceStoreClient.AuditPack` (`AuditPackParams`: store positions, and `Since` and `Until` sent in UTC), never replayed;
  - `EvidenceStoreClient.Archives` (`ArchiveList`) and `RecordArchive` (`ArchiveResult`), which sends the manifest as received and is retried; its location is 1 to 1024 characters counted in code points, as the NA counts them.
- **Contract:** the five responses are recorded and checked at every depth, and the live contract check anchors the store, verifies an audit pack offline from the anchors it holds and records its archive.

### Differs from TypeScript

- `RecordArchive` takes the manifest as received (a `JsonElement`: `AuditPackResponse.Manifest`, or `manifest.json` read back), so it reaches the NA in the form the NA signed; TypeScript takes the typed manifest.
- `Anchors` refuses a page out of range (`Limit` 1 to 1000, `AfterAnchor` 0 or more) and `AuditPack` a store position below 1 before anything is sent; TypeScript sends them and the NA refuses them.
