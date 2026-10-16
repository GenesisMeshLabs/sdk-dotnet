# Contributing to sdk-dotnet

Thank you for your interest in contributing. This document covers how to set up
the development environment, run tests, and submit changes.

---

## Prerequisites

| Tool | Version |
|------|---------|
| .NET SDK | 10.0.4xx, as `global.json` pins it (it builds every target framework) |
| .NET runtime | 8, to run the tests on `net8.0` as well |
| Python | 3.12, only for the live contract test and the scripts in `scripts/` |

---

## Set up

```sh
git clone https://github.com/GenesisMeshLabs/sdk-dotnet.git
cd sdk-dotnet
dotnet restore
```

Verify the build and tests pass before making any changes:

```sh
dotnet format --verify-no-changes             # CI checks the formatting
dotnet build -c Release                       # warnings are errors (recommended analyzers)
dotnet test -c Release --filter "Category!=Live"
dotnet pack src/GenesisMesh.Sdk -c Release    # package validation against the last release
python scripts/changelog.py check
```

A change that removes or changes a public member fails package validation.
If the break is intended, see "A breaking change" in
[docs/architecture.md](docs/architecture.md): a suppression file, an
`### Upgrading` note and the label `breaking`.

`dotnet test` runs the tests once per target framework of the test project.

### Live contract test

`LiveContractTests` calls every client method against a real, disposable
Network Authority (`scripts/contract_na.py`) and compares each typed result
with the JSON the NA sent. It needs a Python with the genesis-mesh core
installed:

```sh
pip install -e ../genesismesh
GM_E2E_PYTHON=python dotnet test -c Release --filter "Category=Live"
```

Without `GM_E2E_PYTHON` the test returns at once. CI sets
`GM_E2E_REQUIRED=1`, and checks the TRX results (`scripts/check_trx.py`), so
there a run that checked nothing fails instead of passing.

---

## Project structure

[docs/architecture.md](docs/architecture.md) maps the modules, the layer
table that `ArchitectureTests` enforces, and where each common change goes,
with the test that fails when a step is missed. Read it before adding or
moving source files.

---

## Making changes

### Branching

Branch from `main`. Use the pattern `{type}/{short-description}`:

```
feat/add-consensus-threshold-param
fix/datasource-required-fields
docs/update-raw-admin-examples
```

### Commit messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(attestation): add optional subject_public_key param
fix(errors): handle nested NA error format correctly
docs(readme): correct evidence verdict values
test(consensus): add negative test for threshold below vote count
```

Scope is the primary area: `auth`, `transport`, `errors`, `models`,
`verify`, `strict`, `agreement`, `boundary`, `evidence`, `attestation`,
`disclosure`, `consensus`, `data-usage`, `sdk`, `ci`, `docs`.

### Code style

- All exported types use C# naming conventions (PascalCase properties with `[JsonPropertyName]` snake_case)
- JSON property names must match the NA wire format exactly
- No `Console.WriteLine` in `src/GenesisMesh.Sdk/` source
- No global state

### Tests

Tests live in `tests/GenesisMesh.Sdk.Tests/`, one file per area
(`AgreementTests.cs`, `BoundaryTests.cs`, `KeyHistoryTests.cs`, ...;
`ClientTests.cs` holds the shared helpers and the auth, error and transport
tests). Every public client method needs:

1. Happy-path — `FuncHandler` returns the correct shape
2. URL — method calls the correct route path
3. Auth — admin methods throw `InvalidOperationException` without a signing key
4. Error — NA 4xx → typed SDK exception with correct `.Code`
5. A `Check(...)` call in `LiveContractTests.cs`

A bug fix comes with the test that fails without it. The `Regression test`
check fails a pull request titled `fix: ...` that changes source files
(deletions included) but adds or modifies no C# test file (`tests/**/*.cs`;
deleted tests, the test project and test data do not count), unless the
Maintainer adds the label `no-test-needed`.

Use `FuncHandler` from the test project (no third-party mocking library):

```csharp
using var handler = new FuncHandler(req =>
{
    var json = JsonSerializer.Serialize(new MyResponseType { ... }, Auth.SerializerOptions);
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
});
var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl     = "http://localhost",
    SigningKey   = TestHelpers.ZeroSeedB64,
    KeyId        = "test",
    HttpHandler  = handler,
});
```

### Changelog

Do not edit `CHANGELOG.md`. Add a fragment
`changelog.d/<version>/<short-name>.md` (see
[changelog.d/README.md](changelog.d/README.md)); a change callers must act on
gets an `### Upgrading` section. Only a `release/<version>` pull request folds
the fragments into `CHANGELOG.md`.

---

## NA protocol constraints

Before adding a new sub-client method, check the constraints table in
[AGENT.md](AGENT.md). Common traps:

- Evidence `verdict` must be `"allow" | "block" | "escalate" | "warn"`
- Roles must use a `role:` prefix (`role:client`, `role:anchor`, etc.)
- `DataSourceDescriptor` requires `source_id`, `source_type`, and `owner_sovereign_id`
- Agreement `accept` requires the NA to hold a prior recognition treaty

---

## Pull requests

- Keep PRs focused — one feature or fix per PR
- Fill in the pull request template: how it was tested, the regression test,
  the changelog fragment, any Upgrading note
- Changes to the paths the template flags (public API, vectors, signing and
  verification, workflows, `SECURITY.md`, packaging) need the Maintainer's
  review and merge (`.github/CODEOWNERS`)

---

## Smoke testing against a live NA

```sh
cd sandbox/sdk-smoke-dotnet
dotnet run   # requires NA on http://127.0.0.1:9443
```

---

## Reporting issues

Use the GitHub issue templates:

- **Bug report** — unexpected behaviour, wrong types, broken build
- **Feature request** — new sub-client method, new NA route support

For security vulnerabilities, follow the process in [SECURITY.md](SECURITY.md).
