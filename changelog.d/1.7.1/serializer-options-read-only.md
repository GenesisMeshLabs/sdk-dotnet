### Security

- **`Auth.SerializerOptions` is read-only.** The options this SDK reads the
  NA's responses with were public and mutable, so any code in the process (a
  host application, a plugin, a test helper) could set
  `PropertyNameCaseInsensitive = true` and undo the 1.3.1 defence: a
  `Boundary.Verify` response of
  `{"valid":false,"accepted":false,"Valid":true,"Accepted":true}` then read
  as valid and accepted. The options are now frozen when the SDK loads.

### Upgrading

- Setting any property of `Auth.SerializerOptions` now throws
  `InvalidOperationException`. To serialize with other settings, copy the
  options first: `new JsonSerializerOptions(Auth.SerializerOptions) { ... }`.
