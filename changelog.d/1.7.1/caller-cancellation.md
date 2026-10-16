### Fixed

- **Cancelling a call throws `OperationCanceledException`.** When the
  `CancellationToken` passed to a client method was cancelled, the SDK
  reported it as a `NetworkException`, so
  `catch (OperationCanceledException)` never saw the caller's own
  cancellation. A timeout (`ClientOptions.Timeout`) is still a
  `NetworkException`.

### Upgrading

- **Cancellation.** Code that caught `NetworkException` to handle its own
  cancellation must catch `OperationCanceledException` (or
  `TaskCanceledException`) instead.
