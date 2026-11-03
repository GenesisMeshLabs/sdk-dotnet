### Added

- **Pre-issued grants, the NA's calls** (protocol 1.6.0, beta routes), as the TypeScript SDK's `GrantsClient`, with the NA's grants on (`GenesisMeshClient.Grants`):
  - `IssueGrant` (`IssueGrantRequest`), not retried: a second call would issue a second grant;
  - `Grants` (a page) and `AllGrants` (every page), filtered by status or bearer key; `Grant`, one grant with its uses left, policy versions and revocation;
  - `GrantUses` (a page) and `AllGrantUses` (every page): a grant's use chain, newest first, quarantined positions included;
  - `RevokeGrant`;
  - `RequestTimeToken`, for a request signed with the bearer key, returning the token exactly as the NA signed it;
  - `SubmitGrantUse`, safe to retry.
- **Models:** `IssuedGrant`, `GrantSummary`, `GrantDetail`, `GrantPolicyVersion`, `GrantList`, `GrantListParams`, `GrantUseSummary`, `GrantUses`, `GrantUsesParams`, `GrantRevoked` and `GrantStatuses`. `RecordSubmission.ResourceSequence` is the place the NA gave a grant use in its resource's chain.
- **Grant uses in the record outbox:** `EnqueueRecord(GrantExecutionEvidence)` and `EvidenceOutbox.RecordEntry(GrantExecutionEvidence)`.
- **`GrantNumber.Of`:** an envelope's bounds from a `long`, a `BigInteger` (up to 2^64 - 1) or a `double` (an integral one as an integer).

### Differs from TypeScript

- `RequestTimeToken` takes a request already signed (`GrantRecords.SignTimeTokenRequest`); TypeScript also signs one when given the bearer key's signer.
- A time token is a `JsonElement`, kept as the NA signed it, so its signature verifies and a use carries it unchanged.
- `AllGrants` and `AllGrantUses` follow the pages; TypeScript reads one page at a time.
- A limit outside 1 to 500 is refused before anything is sent.
