### Added

- **Signature verifiers of NA records**, as the TypeScript SDK's: `OfflineVerifier.VerifyDecisionSignature`, `VerifyPolicySignature`, `VerifyAttestationSignature`, `VerifyTreatySignature` and `VerifyRevocationFeedSignature`, and `RevocationFeedSigner`.
  - Each takes the NA's keys, or its key history: then the key valid at the record's time.
  - With a time to judge at, an attestation, a treaty or a revocation feed must still vouch then (the vouching horizon).
  - A record must have no unknown field and be in canonical form, as the reference requires. The TypeScript SDK checks unknown fields only, and not for a treaty.
