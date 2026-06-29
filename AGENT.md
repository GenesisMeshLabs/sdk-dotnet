# AGENT.md — sdk-dotnet

Guidance for AI coding agents and human contributors working inside the
Genesis Mesh .NET SDK.

This SDK is a standalone NuGet package. It does **not** import from the Python main
repo. It wraps the NA HTTP API surface documented in:

- `genesismesh/docs/sdk/dotnet.md` — public reference
- `genesismesh/docs/api/trust-http.md` — NA HTTP routes

---

## Repo layout

```text
sdk-dotnet/
  src/GenesisMesh.Sdk/
    Auth.cs            # CanonicalJson, LoadSeed, BuildAdminHeaders (Ed25519 via NSec)
    Transport.cs       # HTTP transport — AdminPostAsync, PublicPostAsync, PublicGetAsync
    Errors.cs          # GenesisMeshException + typed subclasses, ErrorParser
    Models.cs          # Protocol record types (JsonPropertyName snake_case, matching NA wire)
    GenesisMeshClient.cs # Client entry point — 7 sub-clients + ClientOptions
    Clients/
      AgreementClient.cs
      AttestationClient.cs
      BoundaryClient.cs
      ConsensusClient.cs
      DataUsageClient.cs
      DisclosureClient.cs
      EvidenceClient.cs
    GenesisMesh.Sdk.csproj
  tests/GenesisMesh.Sdk.Tests/
    ClientTests.cs
    GenesisMesh.Sdk.Tests.csproj
  sandbox/sdk-smoke-dotnet/
    Program.cs
    sdk-smoke-dotnet.csproj
  .github/
    workflows/ci.yml
    workflows/publish.yml
    CODEOWNERS
  GenesisMesh.sln
```

---

## Layer rule

Mirror the Python main repo's enforced layer separation:

```
Auth.cs        = Pure crypto: CanonicalJson, LoadSeed, BuildAdminHeaders.
                 No HTTP. No domain knowledge.
                 Python equivalent: genesis_mesh/crypto/

Transport.cs   = HTTP transport only: AdminPostAsync, PublicPostAsync, PublicGetAsync.
                 No signing logic inline. No domain knowledge.
                 Python equivalent: na_service/ (transport layer)

Errors.cs      = Typed exception types only.
                 No domain logic. No HTTP calls.

Models.cs      = Protocol record types only.
                 No methods beyond property accessors. Pure data.
                 Python equivalent: genesis_mesh/models/

Clients/*.cs   = Sub-client: thin wrapper over transport.
                 One file per domain. Methods call AdminPostAsync/PublicPostAsync/PublicGetAsync.
                 No signing logic inline. No URL construction beyond the path.
                 Python equivalent: na_service/routes/
```

**Do not mix layers.** If a sub-client needs to sign something directly, it
belongs in `Auth.cs`. If a sub-client is doing HTTP retry logic, it belongs in
`Transport.cs`.

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
  `json.dumps(sort_keys=True, separators=(",",":"))`.
- Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — Python does NOT HTML-escape
  `<`, `>`, `&` but C#'s default encoder does. This mismatch causes `admin_auth_failed`.

### 4. Errors fail closed

`ErrorParser.Parse` must handle the NA's nested error format:
`{ "error": { "message": "...", "code": "..." } }`. If the format changes,
**return an error**, do not silently produce a misleading result.

Unknown HTTP status codes fall through to `GenesisMeshException` with the
raw status — never swallow them.

### 5. Admin route invariant

The NA constructs and signs protocol artifacts from declared intent.
The SDK must not pre-build a model client-side and ask the NA to sign it.
Sub-client methods send parameters, not pre-built signed models.

---

## Known constraints (learned from smoke testing)

These are non-obvious and not in the HTTP reference. Tests must cover them.

| Constraint | Detail |
|-----------|--------|
| Evidence verdict | Must be `"allow"` \| `"block"` \| `"escalate"` \| `"warn"`. The value `"trusted"` is invalid. |
| Role prefixes | Roles must start with `role:anchor`, `role:bridge`, `role:client`, `role:operator`, or `role:service:<name>`. Bare names return 422. |
| Agreement accept | Requires the NA to hold an active recognition treaty for the `responder_sovereign_id`. Issue it via `POST /admin/recognition-treaties` first. |
| `DataSourceDescriptor` | `source_id`, `source_type` (`"personal"` \| `"proprietary"` \| `"public"` \| `"synthetic"`), and `owner_sovereign_id` are all required. Missing any returns HTTP 422. |
| HTML-escape bug | C#'s default `JsonSerializer` HTML-escapes `>` as `>`. Python does not. Always use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` in `CanonicalJson` and key serialization. |

---

## Development environment

**This is a Windows project.** Development is on Windows 11 / PowerShell.
CI runs on Linux. Code must work on both.

---

## Pre-commit equivalent

Run these before every commit:

```sh
dotnet build       # must exit 0
dotnet test        # all tests must pass
```

---

## Testing requirements

Use `FuncHandler` from the test project. No third-party mocking library.

Every public method must have:

- A happy-path test that returns the expected type shape.
- A test that admin methods throw `InvalidOperationException` without a signing key.
- A negative test: the method maps a 4xx response to the correct typed exception.

---

## Coding standards

- .NET 8 / C# 12 — use primary constructors, collection expressions, pattern matching.
- No `Console.WriteLine` in `src/GenesisMesh.Sdk/` source. Return exceptions, never log.
- Do not add comments explaining what the code does. Only add comments for
  non-obvious invariants.
- All exported symbols must have XML doc comments.

---

## Release process

This SDK follows the same release process as the main Python repo. Every
version shipped must:

1. Pass `dotnet build` and `dotnet test`.
2. Have a CHANGELOG entry.
3. Be committed, tagged `vX.Y.Z`, and pushed.
4. The `publish.yml` GitHub Actions workflow triggers automatically on the tag and
   publishes to NuGet.org via Trusted Publishing (OIDC — no stored API key needed).

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
