### Changed

- **`Attestation.Get` signs when the client can (1.10.0).** As `List` does, a
  client with a signing key signs the read and gets
  `AttestationRecord.Attestation`. A Network Authority 1.10.0 answers a
  standard-tier key only for an attestation whose subject it is, and with 404
  otherwise. A signing key without `ClientOptions.KeyId` throws
  `InvalidOperationException` before anything is sent.
- **An unsigned read gives the status only.** Without a signing key, an NA
  1.10.0 answers with the status and lineage, and without the attestation.
  `AttestationRecord.Attestation` is nullable, and
  `AttestationRecord.AttestationId` is new: the ID in an unsigned read.

### Upgrading

- **Code that reads `record.Attestation` from an unsigned client** gets null
  from an NA 1.10.0. Sign the read with an operator key, or keep the
  attestation from when it was issued.
