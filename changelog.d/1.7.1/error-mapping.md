### Added

- **`ForbiddenException` (403), `ConflictException` (409),
  `PayloadTooLargeException` (413) and `ClientErrorException` (any other
  4xx).** All derive from `GenesisMeshException`.

### Fixed

- **A 4xx is no longer a `ServerException`.** The NA's 403 (such as
  `insufficient_operator_tier`), 409 (such as `boundary_policy_required`),
  413 and every other 4xx without its own type were thrown as
  `ServerException`, documented as "HTTP 5xx", so a policy that retries
  server errors retried requests the NA had refused. `ServerException` is now
  thrown for 5xx only.

### Upgrading

- **HTTP 4xx exceptions.** A 403, 409 or 413 now throws `ForbiddenException`,
  `ConflictException` or `PayloadTooLargeException`, and any other 4xx
  without its own type (such as 405) throws `ClientErrorException`, where
  1.7.0 threw `ServerException`. `catch (GenesisMeshException)` catches them
  as before; a `catch (ServerException)` meant for these statuses must catch
  the new types (or `GenesisMeshException`). A non-error status this SDK does
  not know (such as an unfollowed redirect) throws `GenesisMeshException`
  itself.
