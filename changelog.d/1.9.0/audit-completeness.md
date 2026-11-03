### Added

- **The completeness check of audit packs** (protocol 1.5.0, as the reference's `genesis_mesh.workflows.audit_pack` and the TypeScript SDK's `audit-pack.ts`):
  - `OfflineVerifier.ParseRfc3339`: an RFC 3339 `date-time` in UTC as the reference prints it; a date without a time, a space for `T`, a missing offset, a leap second or an out-of-range field is refused;
  - `OfflineVerifier.ParseChanges`: changes from a log outside Genesis Mesh, as CSV (a header row naming `ChangeColumns`) or JSON (a list, or `{"changes": [...]}`), each a `Change`;
  - `OfflineVerifier.CheckCompleteness` (`CompletenessOptions`): matches the changes against a pack's records, by source event and then per resource and action for the most pairs and the least total distance within the tolerance, an observation and the record it matched being one change. `CompletenessReport` lists the pairs, the changes inside the period no record matches, those outside it, duplicates, and the records no change matched.
- **Conformance:** the `audit` suite's 13 `changes_file` and 5 `completeness` vectors run.

### Differs from TypeScript

- A value of a changes file is trimmed as the reference (Python) trims it, U+001C to U+001F and U+0085 included and U+FEFF kept; TypeScript trims JavaScript's white space.
- A CSV file whose first line is blank has no columns, as the reference's `csv.DictReader` reads it; TypeScript skips the line.
- A time that falls outside the years 1 to 9999 once in UTC is refused (`FormatException`); TypeScript prints it out of range, and the reference raises `OverflowError`.
