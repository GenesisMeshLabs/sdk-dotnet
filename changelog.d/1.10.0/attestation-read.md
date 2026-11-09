### Added

- **`Attestation.GetStatus` (1.10.0).** An unsigned read of an attestation's
  status and lineage, typed as the new `AttestationStatusRecord`. A Network
  Authority 1.10.0 answers an unsigned `GET /attestations/<id>` with these
  only, and gives the attestation to a signed read.

### Changed

- **`Attestation.Get` signs when the client can (1.10.0).** As `List` does, a
  client with a signing key signs the read and gets
  `AttestationRecord.Attestation`. An NA 1.10.0 answers a standard-tier key
  only for an attestation whose subject it is, and with 404 otherwise. A
  signing key without `ClientOptions.KeyId` throws
  `InvalidOperationException` before anything is sent.
- **`AttestationRecord.Attestation` is nullable**, and
  `AttestationRecord.AttestationId` is new: an unsigned `Get` from an NA
  1.10.0 has the ID and no attestation.

### Upgrading

- **Code that reads `record.Attestation` from an unsigned client** gets null
  from an NA 1.10.0. Read the status with `GetStatus`, sign the read with an
  operator key, or keep the attestation from when it was issued.
