# AGENT.md — sdk-dotnet

Guidance for AI coding agents and human contributors working inside the
Genesis Mesh .NET SDK.

This SDK is a standalone NuGet package. It does **not** import from the Python main
repo. It wraps the NA HTTP API surface documented in:

- `genesismesh/docs/sdk/dotnet/` — public reference
- `genesismesh/docs/api/trust-http.md` — NA HTTP routes

It also verifies NA-signed records offline (`OfflineVerifier`), against the
conformance vectors the core generates (`conformance/vectors/`, copied into
`tests/GenesisMesh.Sdk.Tests/testdata/conformance/`).

---

## Repo layout and layers

[docs/architecture.md](docs/architecture.md) is the map: the modules, the
layer table (which module may use which), how a call and an offline
verification flow, and "where a change goes" recipes with the test that
fails when a step is missed. `tests/GenesisMesh.Sdk.Tests/ArchitectureTests.cs`
enforces the layer table; a new source file goes into its module table.

```text
src/GenesisMesh.Sdk/       # the package (one assembly, namespace GenesisMesh)
tests/GenesisMesh.Sdk.Tests/ # tests; testdata/conformance/ holds the core's vectors, copied unchanged
scripts/                   # changelog, version, TRX and registry tools; contract_na.py for the live test
sandbox/sdk-smoke-dotnet/  # smoke program against a running NA (compiled in CI)
changelog.d/               # changelog fragments, one per change
docs/architecture.md       # modules, layers, flows, recipes
```

**Do not mix layers.** Signing belongs in `Auth.cs`, HTTP in `Transport.cs`,
and the offline verifiers (`OfflineVerifier.cs`, `KeyHistory.cs`,
`Grants.cs`) never touch HTTP.

---

## Architectural principles

### 1. Minimal dependencies

The SDK uses only BCL types plus `NSec.Cryptography` (Ed25519). Do not introduce
additional HTTP client libraries, JSON libraries, or utility packages.

### 2. Field names follow the wire format exactly

All `Models.cs` types have `[JsonPropertyName("snake_case")]` attributes matching
the NA's JSON API exactly. C# property names follow PascalCase convention but the
wire representation is snake_case.

### 3. Security-sensitive code stays boring

`Auth.cs` is the most security-critical file. Keep it minimal and explicit:

- Do not add caching or memoization to key operations.
- Do not add fallbacks for unsupported key formats.
- Do not silently swallow signing errors.
- `CanonicalJson` must produce output identical to Python's
  `json.dumps(sort_keys=True, separators=(",",":"))`. It does so through
  `Canonical.Of`, which writes every string itself (ASCII only, as Python's
  `ensure_ascii`); never hand-roll JSON escaping or rely on an encoder's
  defaults (the default encoder HTML-escapes `<`, `>`, `&`, which causes
  `admin_auth_failed`).
- `Auth.SerializerOptions` reads keys in their own case only (1.3.1); never
  make it case-insensitive.

### 4. Errors fail closed

`ErrorMapping.Parse` (`Errors.cs`) must handle the NA's nested error format:
`{ "error": { "message": "...", "code": "..." } }`. If the format changes,
**return an error**, do not silently produce a misleading result.

Each HTTP error status maps to one exception type in `Errors.cs`; a status
without its own type still throws (a `GenesisMeshException` subclass with the
raw status) — never swallow it.

### 5. Admin route invariant

The NA constructs and signs protocol artifacts from declared intent.
The SDK must not pre-build a model client-side and ask the NA to sign it.
Sub-client methods send parameters, not pre-built signed models.

---

## Known constraints (learned from smoke testing)

These are non-obvious and not in the HTTP reference. Tests must cover them.

| Constraint | Detail |
|-----------|--------|
| Evidence verdict | Must be `"allow"` \| `"block"` \| `"escalate"` \| `"warn"`. The NA refuses any other value, such as `"trusted"`, with 400 `invalid_verdict`. |
| Role prefixes | Roles must start with `role:anchor`, `role:bridge`, `role:client`, `role:operator`, or `role:service:<name>`. The NA refuses a bare name with 400 `invalid_role`. |
| Agreement accept | Requires the NA to hold an active recognition treaty for the `responder_sovereign_id`. Issue it via `POST /admin/recognition-treaties` first. |
| `DataSourceDescriptor` | `source_id`, `source_type` (`"personal"` \| `"proprietary"` \| `"public"` \| `"synthetic"`), and `owner_sovereign_id` are all required. The NA refuses a source without them with 400 `invalid_source`. |
| Boundary decide | A Network Authority with `BOUNDARY_POLICY_ENFORCEMENT=required` (what `genesis-mesh init` sets) refuses `POST /admin/boundary/decide` with 409 `boundary_policy_required`; governed callers use `POST /admin/boundary/evaluate`. |
| Operator tier | Counter, Accept, `Attestation.Issue`, `Revoke`, `SavePolicy` and `DataUsage.CreatePolicy` need a privileged key; a standard key gets 403 `insufficient_operator_tier`. |
| HTML-escape bug | C#'s default `JsonSerializer` HTML-escapes `>` as `\u003E`. Python does not. Signed bytes always come from `Canonical.Of` (via `Auth.CanonicalJson`), never from `JsonSerializer` output. |

---

## Development environment

**This is a Windows project.** Development is on Windows 11 / PowerShell.
CI runs on Linux. Code must work on both.

---

## Pre-commit equivalent

Run these before every commit:

```sh
dotnet format --verify-no-changes                # formatted as dotnet format writes it
dotnet build -c Release                          # must exit 0 (warnings are errors)
dotnet test -c Release --filter "Category!=Live" # all tests must pass
dotnet pack src/GenesisMesh.Sdk -c Release       # package validation: no public API break
python scripts/changelog.py check                # fragments are valid
```

CI also holds a coverage floor (`scripts/check_coverage.py`) and the layer
table (`ArchitectureTests`); see docs/architecture.md.

The live contract test: `GM_E2E_PYTHON=python dotnet test -c Release --filter "Category=Live"`
with the genesis-mesh core installed (see CONTRIBUTING.md).

---

## Testing requirements

Use `FuncHandler` from the test project. No third-party mocking library.
A bug fix comes with the test that fails without it (the `Regression test`
check enforces this for `fix:` pull requests).

Every public method must have:

- A happy-path test that returns the expected type shape.
- A test that admin methods throw `InvalidOperationException` without a signing key.
- A negative test: the method maps a 4xx response to the correct typed exception.

---

## Coding standards

- C# 12 — use primary constructors, collection expressions, pattern matching.
- No `Console.WriteLine` in `src/GenesisMesh.Sdk/` source. Return exceptions, never log.
- Do not add comments explaining what the code does. Only add comments for
  non-obvious invariants.
- All exported symbols must have XML doc comments: a summary that says what the
  member means on the wire or to the caller, not what its name already says. The
  build fails on a missing one (CS1591 is not suppressed, and warnings are errors).

---

## Release process

This SDK follows the same release process as the main Python repo. Starting
with v0.56.0, the core and all official SDKs use one coordinated version. The
`VERSION` file, project package version, and release tag must match. Run
`./scripts/check-version.ps1` before building or publishing.

Every version shipped must:

1. Pass `dotnet build` and `dotnet test`.
2. Have its changelog fragments folded into `CHANGELOG.md` by the
   `release/<version>` pull request (`python scripts/changelog.py release`).
3. Be committed, tagged `vX.Y.Z`, and pushed (the core's `vX.Y.Z` tag first).
4. The `publish.yml` GitHub Actions workflow triggers automatically on the tag,
   tests, packs and pushes to NuGet.org.
5. Once the package is on NuGet.org, Renovate opens an "Update Genesis Mesh"
   pull request that sets `<PackageValidationBaselineVersion>` in
   `src/GenesisMesh.Sdk/GenesisMesh.Sdk.csproj` to the new version
   (`renovate.json`); delete `src/GenesisMesh.Sdk/CompatibilitySuppressions.xml`
   in that pull request if it exists, and merge it.
   Until then the `API compatibility` check warns on pull requests and
   fails on main, and package validation compares with the older release.

Publishing uses NuGet trusted publishing when it is configured, and the
long-lived `NUGET_API_KEY` secret only as a fallback. To configure it:

1. On nuget.org, signed in as the account that owns `genesismesh-sdk-dotnet`
   (or an organization owning it), open the account's **Trusted Publishing**
   page and add a GitHub Actions policy: repository owner `GenesisMeshLabs`,
   repository `sdk-dotnet`, workflow file `publish.yml`, no environment.
2. In the GitHub repository, add the Actions variable `NUGET_USER`: the
   nuget.org user name (profile name, not the e-mail address) of the account
   that created the policy.
3. After a release publishes with "Publishing with a short-lived trusted
   publishing key" in its log, delete the `NUGET_API_KEY` secret (and revoke
   the key on nuget.org), then remove the fallback from `publish.yml`.

---

## Agent behavior rules

When acting as an AI coding agent in this repository:

1. Read this file before making changes.
2. Keep changes small. One method → one test. One sub-client → one test class.
3. Preserve layer boundaries. No signing logic in sub-clients. No domain
   knowledge in `Transport.cs`.
4. Match the NA's wire format exactly — `[JsonPropertyName]` must be snake_case.
5. Do not introduce runtime dependencies.
6. Do not add methods that the NA HTTP API does not expose.
7. When a NA constraint is discovered (invalid enum value, missing required
   field, prerequisite call), add it to the "Known constraints" table in this
   file AND cover it with a negative test.
8. Confirm before destructive operations. Approval once does not generalize.
9. The conformance vectors define the canonical wire format all SDK
   implementations must match. They are generated in the core
   (`python conformance/generate_vectors.py`) and copied unchanged into
   `tests/GenesisMesh.Sdk.Tests/testdata/conformance/`; after copying
   `field_registry.json`, run `python scripts/sync_canonical_registry.py`.
   The grants and key-history tests fail on a vector kind they neither run
   nor list as not run.
