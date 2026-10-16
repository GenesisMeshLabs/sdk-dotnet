### Deprecated

- **`ErrorParser`, `StrictJson` and `Canonical.PythonFloatRepr` are
  obsolete** and become internal in 2.0. They are how the SDK reads error
  responses and JSON and writes numbers, not API for callers: catch the
  `GenesisMeshException` subclasses and `StrictJsonException` the client and
  the verifiers throw, and use `Canonical.Of` or `Canonical.FromJson` for the
  canonical form. They work as before until then; code that uses them now
  gets warning CS0618 (an error under `TreatWarningsAsErrors`).
