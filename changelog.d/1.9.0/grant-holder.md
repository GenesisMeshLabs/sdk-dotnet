### Added

- **`GrantHolder`** (protocol 1.6.0), as the TypeScript SDK's: the holder of a pre-issued grant acts within it while the NA cannot be reached (`Act`), keeps each use in the record outbox, and on reconnect reads the revocation feed, asks for a fresh time token and uploads its uses (`Sync`, `RefreshRevocationFeed`, `RefreshTimeToken`, `UpdateRevocationFeed`).
  - Before each change it checks the grant offline in the NA's order, and refuses a request outside it (`GrantRefusedException`): nothing runs.
  - When the grant has ended, its budget or time token is spent, or it is revoked, it falls back to a governed action with break-glass (`GrantFallback`): an NA that answers decides, one out of reach leaves the change to break-glass. Without a fallback: `GrantUnusableException`.
  - A use the NA quarantines after it continued the chain keeps its place and stops counting against the budget (a conflict, a chain mismatch or an over-budget refusal still counts); uses are never dated before their time token or the previous use; calls run one at a time, and the state changes whole.
  - `GrantHolderState` is the JSON the TypeScript SDK's holder keeps (`Persist`, `State`); `GrantStateNotKeptException` when it cannot be persisted.
- **Types:** `GrantHolderOptions`, `GrantActRequest`, `GrantFallback`, `GrantAuthority`, `GrantActResult<T>`, `GrantUseResult<T>`, `GrantFallbackResult<T>`, `GrantSyncResult`, `GrantFallbackCodes`, `GrantPaths`. `EvidenceNotKeptException.GrantUse` is the signed use a holder could not keep.

### Differs from TypeScript

- A failed action's use is persisted with the state (TypeScript's holder moves its chain without persisting it).
- On its first change the holder takes up a later use of its grant the record outbox holds (kept before a stop left the state unpersisted), so no place is signed twice.
- `Sync` still uploads when the NA refuses a time token for a reason other than the grant's end (`GrantSyncResult.TimeTokenRefusal`, such as `time_token_request_stale`); `Act` then falls back, and the NA decides. TypeScript throws, and its sync uploads nothing.
- Under an empty resource prefix, a request without a resource is refused (`grant_use_out_of_scope`), as the NA refuses such a use; TypeScript lets it run.
- The action is a `Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>>`; the result holds either `UnderGrant` or `Fallback`, whose `Outcome` is the governed action's `GovernedOutcome<T>`.
- `NAKeys` is a key history; a list of keys is `NAKeyHistory.ValidAlways(keys)`.
