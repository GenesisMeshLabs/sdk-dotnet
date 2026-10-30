# Architecture

The SDK is one assembly, `GenesisMesh.Sdk` (namespace `GenesisMesh`, package
`genesismesh-sdk-dotnet`), with one runtime dependency, `NSec.Cryptography`
(Ed25519). It has two halves:

- an **offline verifier** for records the Network Authority (NA) signs, which
  never touches the network; and
- a **thin HTTP client** for the NA's trust API, one method per route.

The rules below are checked by `tests/GenesisMesh.Sdk.Tests/ArchitectureTests.cs`.
When you add a source file, add it to the module table there and here.

## Modules

| Module | Files (`src/GenesisMesh.Sdk/`) | What it holds |
|---|---|---|
| json | `StrictJson.cs` | The strict JSON reader every input goes through: refuses duplicate keys, integers outside 64 bits, `-0`, non-finite numbers, lone surrogates, nesting deeper than 64 and text that is not UTF-8, each by a named reason (`StrictJsonException.Reason`). |
| canonical | `Canonical.cs` | Canonical JSON, byte-identical to the Python reference's `json.dumps(sort_keys=True, separators=(",", ":"))`: keys in code-point order, ASCII-only escapes, integers as written, other numbers in Python's float repr. |
| registry | `StrictFields.cs`, `canonical_registry.json` | The field registry of signed records (embedded, generated from the core's `field_registry` suite): known fields per model, canonical timestamps, the signed form of each model. An unknown model name throws. |
| verify | `OfflineVerifier.cs`, `KeyHistory.cs`, `Grants.cs`, `Vouching.cs`, `Metadata.cs` | Offline verification: agreements, boundary decisions, policies, data access intents, the NA key history, capability grants and the vouching horizon (1.8.0). One `partial class OfflineVerifier`; each result record sits in the file of its verifier. `EvidenceMetadata` (1.9.0) is the NA's guard on metadata: size and secret material. |
| models | `Models.cs` | The request and response types of the HTTP API, with snake_case `[JsonPropertyName]` names. Data only. |
| errors | `Errors.cs` | `GenesisMeshException` and its subclasses, and the HTTP status to exception mapping (`ErrorMapping`). |
| auth | `Auth.cs`, `Signer.cs` | Admin request signing (signature version 2): the signed payload, Ed25519 signing, the `X-Admin-*` headers, the read-only `SerializerOptions` every body is written and read with, and the operator signers (`ISigner`, `SeedSigner`). |
| transport | `Transport.cs`, `ClientOptions.cs` | The only code that sends HTTP: the `HttpClient`, the NA's audience key from `/sovereign.json`, admin and public calls, failover across `BaseUrls`, retries, strict response reading, error mapping and cancellation; and the options it is built from (`ClientOptions`, `RetrySettings`). |
| clients | `Clients/*.cs` | One sub-client per NA area (`Agreement`, `Attestation`, `Boundary`, `Consensus`, `DataUsage`, `Disclosure`, `Evidence`, `Health`, `NA`, `Policy`). A method names its route and calls the transport; one that verifies what the NA served calls the offline verifier. |
| client | `GenesisMeshClient.cs` | The entry point: builds the transport and the sub-clients. |

## Layers

A module may use the modules in its row and nothing else. The arrow points
down only: the client half depends on the offline half's primitives, never
the other way.

| Module | May depend on |
|---|---|
| json | (nothing) |
| errors | (nothing) |
| models | (nothing) |
| canonical | json |
| registry | canonical, json |
| auth | canonical, json |
| verify | registry, canonical, json, models |
| transport | auth, errors, models, canonical, json |
| clients | transport, models, errors, verify |
| client | every module |

Two further rules:

- Only transport and client name `System.Net` types (`HttpClient`,
  `HttpMessageHandler`, ...).
- The offline modules (json, canonical, registry, verify) reference no type of
  auth, errors, transport, clients or client, and nothing in `System.Net`. The
  test checks this in the compiled code too, so a reference that the source
  does not spell out (through `var`, say) still fails.

The test's allowlist of exceptions is empty (1.9.0: `ClientOptions` moved
into the transport module, which reads it). It may only stay empty: the test
fails when a new edge appears. Clients may use the offline verifier (1.9.0)
to verify what the NA served, as `NA.VerifiedKeyHistory` does; the verifier
still uses no client.

## How a call flows

1. `new GenesisMeshClient(options)` builds one `Transport` (one `HttpClient`,
   the endpoints, the signer: `ClientOptions.Signer`, or a `SeedSigner` of the
   seed from `Auth.LoadSeed`) and the ten sub-clients.
2. A sub-client method, such as `Boundary.Decide(body)`, calls
   `Transport.AdminPostAsync<BoundaryDecision>("/admin/boundary/decide", body)`.
3. For an admin route, the transport reads the NA's public key once from
   `/sovereign.json` (or `ClientOptions.Audience`), and
   `Auth.BuildAdminHeadersAsync` has the signer sign method, path, query,
   audience and body over their canonical form, again for every attempt.
4. The request is sent to the active endpoint. An idempotent request moves to
   the next endpoint on a connection failure or a 502, 503 or 504; any other
   request only when it never reached an instance. Retries
   (`ClientOptions.Retry`) repeat idempotent requests with backoff. Requests
   that need the NA's key at the same time share one lookup; a failed lookup is
   dropped as it fails.
   Cancellation by the caller's token surfaces as `OperationCanceledException`;
   a timeout or connection failure as `NetworkException`.
5. A non-2xx response becomes the typed exception for its status
   (`ErrorMapping`), with the NA's `details` and its `Retry-After` wait. A 2xx body is decoded as strict UTF-8, checked by the
   strict JSON reader, and deserialized with `Auth.SerializerOptions`
   (property names in their own case only).

## How a record is verified offline

1. The caller passes the record as received (JSON text) and the keys, or an
   `NAKeyHistory` (`OfflineVerifier.VerifyKeyHistory`).
2. The strict JSON reader parses it, or refuses it by reason.
3. The signed form comes from the registry: every field but the signature
   (some omitted when null), or a fixed field list, in canonical JSON.
4. The signature is checked with NSec Ed25519; then unknown signed fields
   (`unknown_field`) and non-canonical timestamps (`non_canonical_form`);
   then expiry, the key valid at the record's time and the record's own rules.
5. The result names a reason code: the Python reference's reasons. The
   shared conformance vectors check that the outcomes agree on the cases the
   vectors cover.

## Where a change goes

Each recipe lists the files to touch and the test that fails when one is
missed. Every change that callers see also gets a fragment in
`changelog.d/<version>/` (an `### Upgrading` section if they must act), and
sdk-go gets the same change.

### A new client method (an NA route)

| Touch | Fails if missed |
|---|---|
| `Clients/<Area>Client.cs`: the method, its route and transport call | your unit test in `<Area>Tests.cs` |
| `Models.cs`: the response type (and request type, if typed) | `LiveContractTests` (unmodeled or missing keys) |
| `<Area>Tests.cs`: happy path with `FuncHandler`, the route path, admin without a key, a 4xx | review only |
| `LiveContractTests.cs`: a `Check(...)` call | review only (nothing lists the client methods yet) |
| `testdata/contract/na_responses.json` and `ContractTypesTests.Models`, for a new response type | `ContractTypesTests` |
| `README.md`: the sub-client section | review only |

Adding a method is compatible. Renaming or removing one fails package
validation (see "A breaking change").

### A new verifier or record kind

| Touch | Fails if missed |
|---|---|
| `tests/GenesisMesh.Sdk.Tests/testdata/conformance/`: the core's new or changed suite, copied unchanged | the vector theories |
| `canonical_registry.json`: run `python scripts/sync_canonical_registry.py` after copying `field_registry.json` | `FieldRegistryConformanceTests.EmbedsTheRegistryOfTheSuite` |
| `OfflineVerifier.cs`, or a new file holding `partial class OfflineVerifier` and its result record | the vector theories |
| A new file: its row in the module table of `ArchitectureTests.cs` (and here) | `ArchitectureTests` |
| The suite's test class: a `[Theory]` over the vectors, the kind in `KindsRun` or `KindsNotRun` | `EveryKindIsRunOrListedAsNotRun` (grants, key history) |
| Every model name the verifier uses exists in the registry | `ModelNameTests.EveryModelNamedInTheSourceIsInTheRegistry` |
| `README.md`: "Offline verification" | review only |

### A new error type or status

| Touch | Fails if missed |
|---|---|
| `Errors.cs`: the exception class and its status in `ErrorMapping.Parse` | `ErrorParserTests.Parse_MapsEachStatusToItsType` in `ClientTests.cs` (add the status) |
| `README.md`: "Error handling"; an `### Upgrading` note if a status changes type | review only |

### Vectors changed in the core

Copy the changed suites unchanged into `testdata/conformance/` (and run the
registry sync for `field_registry.json`). The suite's theories fail until the
code matches. The core's `interop.yml` compares these copies with its own.

### A breaking change

Package validation (`EnablePackageValidation`, baseline in
`GenesisMesh.Sdk.csproj`) runs on `dotnet pack` in CI and fails on a removed or
changed public member (`CP0001`, `CP0002`, ...). To make the break on purpose:

1. Run `dotnet pack src/GenesisMesh.Sdk -c Release -p:ApiCompatGenerateSuppressionFile=true`
   and commit `src/GenesisMesh.Sdk/CompatibilitySuppressions.xml`.
2. Add a changelog fragment with an `### Upgrading` section.
3. Ask the Maintainer for the label `breaking`. The `API compatibility` check
   fails a pull request that changes the suppression file without both.

### A release

`VERSION` and `<Version>` in the csproj must match (`scripts/check-version.ps1`,
run by CI); the `release/<version>` pull request folds the fragments
(`python scripts/changelog.py release`). Once the package is on NuGet,
Renovate opens an "Update Genesis Mesh" pull request that sets
`<PackageValidationBaselineVersion>` to it (`renovate.json`); delete
`CompatibilitySuppressions.xml` in that pull request if it exists. Until it
merges, the `API compatibility` check warns on pull requests and fails on main.

## Other checks

- **XML docs:** every public type and member has a doc comment. The compiler's
  CS1591 is on and warnings are errors, so a missing one fails the build.
- **Coverage floor:** CI collects coverage (coverlet) and
  `scripts/check_coverage.py` fails below the floors in `ci.yml`.
- **Property tests:** `PropertyTests.cs` (FsCheck) checks the strict JSON
  reader and the canonical form with generated input.
- **Format and analyzers:** `dotnet format --verify-no-changes` and
  `AnalysisLevel` `10.0-recommended`, warnings as errors in both projects; the
  SDK is pinned to the 10.0.4xx feature band (`global.json`), so a new SDK adds
  no analyzer findings until the pin is moved.
