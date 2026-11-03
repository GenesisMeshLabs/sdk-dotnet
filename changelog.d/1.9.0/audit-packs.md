### Added

- **Audit packs offline** (protocol 1.5.0, as the reference's `verify_audit_pack` and the TypeScript SDK's `audit-pack.ts`):
  - `OfflineVerifier.VerifyAuditPack` (`VerifyAuditPackOptions`, `PackVerification`): a pack's manifest, the NA key history from the root key, the anchors held, the cited entries, the entries with the keys the pack's registry records place, the policies, attestations, treaties, feeds and agreements the records rest on, the report, the period asked for and the completeness check, each failure named by the reference's reason code, its file and its entry;
  - `ReadAuditPackDirectory`, `AuditPackFiles` (`AuditPackResponse`: the manifest written as the NA writes it), `PackJsonBytes`, `PackEntryDigests` and `PackReasons`; `AuditPackManifest`, `PackFile` and the file names (`PackFiles`, `OptionalPackFiles`);
  - `OfflineVerifier.RenderReport` (`ReportHeaderOf`) and `ReportCell`: `report.md` byte for byte as the reference renders it;
  - `OfflineVerifier.RegistryKeys`: executor and observer keys from the registry records the NA signed, placed by store position or by time;
  - `OfflineVerifier.ReceivedSignatureReason` and `VerifyArchiveRecord` (`ArchiveRecord`).
- **Conformance:** the `audit` suite's 22 `pack`, 1 `registry_keys` and 1 `report_cell` vectors run: every vector of the suite runs.

### Differs from TypeScript

- A report writes a number the reference writes in exponent form (`1e+16`) as it does; TypeScript writes its digits.
- The reason of a denied judgement in a report, and an ID a judgement or a remediation state is looked up by, is Python's `str()` of it: `1.0` stays `1.0`, a list or object is Python's `repr()` (`{'why': ['x']}`); TypeScript writes `1`, `[object Object]` or the items joined by commas.
- A manifest whose signature carries a field other than `key_id` and `sig` is not in the NA's canonical form (`manifest_invalid`), as the reference reads it; TypeScript accepts it.

### Differs from the reference

- A manifest or held anchor written in a form the reference's models coerce (`6.0` or `"6"` for an integer, `true` for 1, `+00:00` for `Z`) is refused by its shape or signature, as TypeScript refuses it; the reference reads the coerced value and also names `non_canonical_form`.
- A decision entry whose `decision` is not an object fails the entry's checks; the reference raises (`AttributeError`) while rendering the report.
- `ReportHeaderOf` keeps a manifest time it does not read (one with more than six fraction digits) as given; the reference truncates it to microseconds. Such a manifest is not in canonical form, and both refuse the pack.
