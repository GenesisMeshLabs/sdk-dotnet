# Changelog

All notable changes to `sdk-dotnet` are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions align with the [Genesis Mesh release sequence](https://github.com/GenesisMeshLabs/genesismesh/blob/main/CHANGELOG.md).

---

## [0.58.1] - 2026-09-29

Coordinated Genesis Mesh v0.58.1 release. No functional changes; the core adds
attestation-backed boundary evaluation, which this SDK does not wrap yet.

## [0.58.0] - 2026-09-29

Coordinated Genesis Mesh v0.58.0 release. No functional changes; the SDK passes its
compatibility tests against the v0.58.0 Network Authority. Version 0.57 was skipped
across the train (see the core `docs/development/versioning.md`).

### Changed

- CI and publishing now fail if this component's version is ahead of the Genesis Mesh core version.

## [0.56.0] - Unreleased

### Changed

- Joined the coordinated Genesis Mesh v0.56.0 release train.
- Added a shared `VERSION` declaration and publishing guard that rejects tags
  which do not match project package metadata.
- Updated the supported security line to `0.56.x`.

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
