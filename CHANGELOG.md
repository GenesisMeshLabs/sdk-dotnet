# Changelog

All notable changes to `sdk-dotnet` are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions align with the [Genesis Mesh release sequence](https://github.com/GenesisMeshLabs/genesismesh/blob/main/CHANGELOG.md).

---

## [0.55.0] — 2026-06-29

### Added

- `GenesisMeshClient` — unified entry point with 7 domain sub-clients over shared transport
- `AgreementClient` — capability offer, counter, accept, verify
- `BoundaryClient` — boundary decision and verification
- `EvidenceClient` — trust evidence build and verify
- `AttestationClient` — membership attestation issue, revoke, recognition policy
- `DisclosureClient` — selective Merkle capability disclosure, nullifier, prove, verify
- `ConsensusClient` — validator vote, consensus proof assembly and verify
- `DataUsageClient` — data license policy, access intent, get policy, verify
- `Auth` — `CanonicalJson`, `LoadSeed`, `BuildAdminHeaders` (Ed25519 via NSec.Cryptography)
- `Transport` — internal HTTP transport with admin header injection and typed error mapping
- `Errors` — `GenesisMeshException` and typed subclasses for all NA error codes
- `Models` — 16+ protocol record types matching the NA JSON wire format
- 20 unit tests across auth, errors, and all sub-client paths
- NuGet Trusted Publishing workflow (`publish.yml`)
- Smoke test (`sandbox/sdk-smoke-dotnet`) exercising all routes against a live NA

[0.55.0]: https://github.com/GenesisMeshLabs/sdk-dotnet/releases/tag/v0.55.0
