### Fixed

- **A model name the field registry lacks is refused.** The verifiers name
  the record models they check by string; a name the embedded registry did
  not have (a typo, or a model the core renamed) read as a model with no
  fields, so the `unknown_field` and `non_canonical_form` checks were
  silently off for that verifier. Such a name now throws
  `ArgumentException`, and a test checks every model named in the source
  against the registry.

### Upgrading

- **`StrictFields.UnknownFields`.** Called with a model the registry does
  not have, it throws `ArgumentException`; 1.7.0 returned an empty list,
  which read as "no unknown fields".
