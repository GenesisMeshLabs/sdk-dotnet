### Fixed

- Client responses are read with keys matched in their own case only. A key
  in another case named the same property, so
  `{"authorized":false,"Authorized":true}` read as an authorized decision,
  where every other implementation reads `Authorized` as a key it does not
  know. This held for every typed result: decisions, agreements, evidence,
  attestations, policies and intents.

### Upgrading

- `Auth.SerializerOptions` no longer sets `PropertyNameCaseInsensitive`.
  Code that reads its own JSON with these options needs the exact names the
  models declare (the Network Authority's snake_case names).
