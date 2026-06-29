# Contributing to sdk-dotnet

Thank you for your interest in contributing. This document covers how to set up
the development environment, run tests, and submit changes.

---

## Prerequisites

| Tool | Minimum version |
|------|----------------|
| .NET | 8.0 |

---

## Set up

```sh
git clone https://github.com/GenesisMeshLabs/sdk-dotnet.git
cd sdk-dotnet
dotnet restore
```

Verify the build and tests pass before making any changes:

```sh
dotnet build
dotnet test
```

---

## Project structure

Read [AGENT.md](AGENT.md) for the enforced layer rule before adding or changing
source files. The short version:

| File | What goes here |
|------|---------------|
| `src/GenesisMesh.Sdk/Auth.cs` | Crypto only — canonical JSON, Ed25519 signing, admin headers |
| `src/GenesisMesh.Sdk/Transport.cs` | HTTP transport only — admin/public post/get, error mapping |
| `src/GenesisMesh.Sdk/Errors.cs` | Typed exception types only |
| `src/GenesisMesh.Sdk/Models.cs` | Protocol record types only — no methods, no functions |
| `src/GenesisMesh.Sdk/Clients/*.cs` | Sub-client — thin wrapper over transport |

Do not mix layers. A sub-client file must not contain crypto primitives.
The auth module must not make HTTP calls.

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

Scope is the primary area: `auth`, `transport`, `models`, `agreement`,
`boundary`, `evidence`, `attestation`, `disclosure`, `consensus`,
`data-usage`, `sdk`, `ci`, `docs`.

### Code style

- All exported types use C# naming conventions (PascalCase properties with `[JsonPropertyName]` snake_case)
- JSON property names must match the NA wire format exactly
- No `Console.WriteLine` in `src/GenesisMesh.Sdk/` source
- No global state

### Tests

Every public method needs tests in `tests/GenesisMesh.Sdk.Tests/ClientTests.cs`:

1. Happy-path — `FuncHandler` returns the correct shape
2. URL — method calls the correct route path
3. Auth — admin methods throw `InvalidOperationException` without a signing key
4. Error — NA 4xx → typed SDK exception with correct `.Code`

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
- Include a test for every changed behaviour
- Ensure `dotnet build` and `dotnet test` pass locally before opening the PR

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
