## What changed

<!-- What this pull request changes and why, in 2-4 sentences. Title it with a
conventional prefix (feat:, fix:, docs:, test:, ci:, chore:). -->

## How it was tested

<!-- Commands you ran and what they showed: dotnet build -c Release (warnings
are errors), dotnet test --filter "Category!=Live" on net8.0 and net10.0, and
the live contract test if you changed a client method (see CONTRIBUTING.md). -->

## Bug fix

Bug fix: the test that fails without this change is ___

<!-- Name the test and say how you saw it fail without the fix. A pull request
titled "fix: ..." that changes source files must add or modify a C# test file
(tests/**/*.cs, not test data), or carry the label no-test-needed, which only the Maintainer adds. Write "not a bug
fix" otherwise. -->

## Changelog

- [ ] A fragment `changelog.d/<version>/<short-name>.md` describes the change
      for users (see `changelog.d/README.md`), or the change needs none
      (CI, tests, repository docs).
- [ ] `CHANGELOG.md` is not edited (only a `release/<version>` pull request
      folds the fragments into it).

## Breaking change

- [ ] No public API or behaviour changes for callers.
- [ ] Callers see a change: the fragment has an `### Upgrading` section that
      says what changes for them and what to do.

## Flagged paths

These need the Maintainer's review and merge. Tick any this pull request touches:

- [ ] Contract: the public API (`GenesisMeshClient.cs`, `Models.cs`,
      `Errors.cs`, `Clients/`) or the wire format of a request or response
- [ ] Vectors and registry: `tests/GenesisMesh.Sdk.Tests/testdata/`,
      `canonical_registry.json`
- [ ] Signing and verification: `Auth.cs`, `Transport.cs`, `StrictJson.cs`,
      `StrictFields.cs`, `Canonical.cs`, `OfflineVerifier.cs`,
      `KeyHistory.cs`, `Grants.cs`
- [ ] Workflows: `.github/`
- [ ] `SECURITY.md`
- [ ] Packaging and release: `*.csproj`, `GenesisMesh.sln`, `VERSION`,
      `scripts/check-version.ps1`
- [ ] Ratchets: `ArchitectureTests.cs` (the layer table and its allowlist,
      which may only shrink), `CompatibilitySuppressions.xml` (an accepted API
      break: label `breaking` and an `### Upgrading` note), the coverage
      floors in `ci.yml`, `.editorconfig`

## Related issues

<!-- Closes #, Fixes #, or "None". -->
