### Added

- **Pre-issued grants, offline** (protocol 1.6.0), as the TypeScript SDK's `grants.ts`:
  - the records `CapabilityGrant`, `TimeToken`, `TimeTokenRequest`, `GrantExecutionEvidence` and `GrantRevocationRecord`, with `GrantLimits`, `GrantRevocationReasons`, `GrantUseOutcomes` and `GrantUseRejectionCodes` (in the NA's order, and the ones it quarantines);
  - `GrantNumber`, the one number rule: an integral number is an integer, integers fit 64 bits, comparisons are exact;
  - `GrantRules`: `Parameter`, `EnvelopeShapeProblem`, `ParameterValueProblem`, `EnvelopeProblem`, `ScalarEquals`, `InRange`, `UseProblem` (with `UseCheck`, `PreviousUse`, `TokenHead`, `UseProblem`), `ContinuesChain` (`UseChainHead`), `NextPreviousUse`, `UseSecretProblem`, `GrantDigest`, `UseDigest` and `RecordDigest`;
  - `OfflineVerifier.VerifyTimeToken`, `VerifyGrantRevocation`, `VerifyGrantUse`, `VerifyTimeTokenRequest` and `TokenSignatureValid`, and `VerifyCapabilityGrant` under a list of NA keys;
  - `GrantRecords.UseRecord`, `CheckUse`, `SignUse` and `SignTimeTokenRequest` (`GrantUseInput`, `TimeTokenRequestInput`), which sign as the reference signs and refuse first what the NA would refuse (`GrantRecordException`).
- **Conformance:** the `grants` suite's `envelope_parameter`, `envelope` and `use` vectors run (53), and 1,510 cases the Python reference judged: envelope parameters, envelope problems, use problems at microsecond boundaries, the five records verified with their digests, and the secret guard.

### Changed

- **`OfflineVerifier.VerifyCapabilityGrant` refuses a grant the reference's model does not take** (`malformed`): a field it requires missing or of another type, a validity longer than 24 hours or starting more than 24 hours after its issue, an envelope parameter it cannot read. Such a grant was checked on its signature alone.

### Differs from TypeScript

- `GrantRules.Parameter` returns the problem (`invalid envelope parameter: ...`) instead of throwing it, so the envelope rules call nothing that throws.
- A use's `time_token` is kept as received (`JsonElement`), so its signature verifies as the NA signed it.
- `GrantRules.UseProblem` throws `ArgumentException` for a time in its `UseCheck` that is not a timestamp. TypeScript skips the rule that time is for, so an unreadable revocation time lets the use through.
- `GrantUseInput` takes the previous use or its digest, not both (`ArgumentException`).
