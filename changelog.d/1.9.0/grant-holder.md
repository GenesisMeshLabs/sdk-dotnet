### Added

- **`GrantHolder`** (protocol 1.6.0), as the TypeScript SDK's: the holder of a pre-issued grant acts within it while the NA cannot be reached (`Act`), keeps each use in the record outbox, and on reconnect reads the revocation feed, asks for a fresh time token and uploads its uses (`Sync`, `RefreshRevocationFeed`, `RefreshTimeToken`, `UpdateRevocationFeed`).
  - Before each change it checks the grant offline in the NA's order, and refuses a request outside it (`GrantRefusedException`): nothing runs.
  - When the grant has ended, its budget or time token is spent, or it is revoked, it falls back to a governed action with break-glass (`GrantFallback`): an NA that answers decides, one out of reach leaves the change to break-glass. Without a fallback: `GrantUnusableException`.
  - A use the NA quarantines keeps its place in the chain and stops counting against the budget; uses are never dated before their time token or the previous use; calls run one at a time.
  - `GrantHolderState` is the JSON the TypeScript SDK's holder keeps (`Persist`, `State`); `GrantStateNotKeptException` when it cannot be persisted.
- **Types:** `GrantHolderOptions`, `GrantActRequest`, `GrantFallback`, `GrantAuthority`, `GrantActResult<T>`, `GrantUseResult<T>`, `GrantFallbackResult<T>`, `GrantSyncResult`, `GrantFallbackCodes`, `GrantPaths`. `EvidenceNotKeptException.GrantUse` is the signed use a holder could not keep.

### Differs from TypeScript

- `GrantHolder` is `IDisposable` (the lock its calls take turns on).
- The action is a `Func<GrantAuthority, CancellationToken, Task<ActionReport<T>?>>`; the result holds either `UnderGrant` or `Fallback`, whose `Outcome` is the governed action's `GovernedOutcome<T>`.
- `NAKeys` is a key history; a list of keys is `NAKeyHistory.ValidAlways(keys)`.
