# sdk-dotnet

.NET SDK for the Genesis Mesh Network Authority HTTP API.

**Targets: .NET 8 and .NET 10 (`net8.0`, `net10.0`). One dependency: [`NSec.Cryptography`](https://nsec.rocks/) for Ed25519.**

[![NuGet](https://img.shields.io/nuget/v/genesismesh-sdk-dotnet)](https://www.nuget.org/packages/genesismesh-sdk-dotnet)
[![CI](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml)

## Scope

This SDK (1.6) covers the Network Authority's trust API: the sub-clients
below (agreements, boundary decisions, trust evidence, attestations,
selective disclosure, consensus and data usage), offline verification of
their signed records, and signed admin calls to any other route. It has no
governed lifecycle API: no evidence store client or evidence outbox, none
of the 1.3.0 changes outside the controlled path (observations, break-glass
records, the record outbox), and none of the 1.4.0 remediation and review
APIs (remediation records, the due queue, reviews). Those live in the
[TypeScript SDK](https://github.com/GenesisMeshLabs/sdk-typescript) and the
[Rust SDK](https://github.com/GenesisMeshLabs/sdk-rust). The embedded field
registry lists their records, but this SDK does not verify evidence exports.
A boundary policy with the 1.4.0 sections (`remediation`, `review_required`,
`emergency`) verifies here like any other.

From 1.5 it reads the NA's key history (see
[NA key history](#na-key-history)), so decisions and agreements signed before
and after a key succession keep verifying. Audit packs, archives and their
verification are not part of this SDK: they live in the core
(`genesis-mesh audit pack`, `genesis-mesh audit verify`) and in the Rust
SDK's independent verifier `genesis-mesh-verify`.

From 1.6 it checks a pre-issued grant's NA signature (see
[Pre-issued grants](#pre-issued-grants)). Requesting grants, holding time
tokens, acting within a grant offline, recording its uses and falling back to
break-glass live in the
[TypeScript SDK](https://github.com/GenesisMeshLabs/sdk-typescript) and the
[Rust SDK](https://github.com/GenesisMeshLabs/sdk-rust), as does verifying
grants, their uses and revocations in an evidence export.

## Install

```sh
dotnet add package genesismesh-sdk-dotnet
```

## Quick start

```csharp
using GenesisMesh;

var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl    = "http://127.0.0.1:9443",
    SigningKey = Environment.GetEnvironmentVariable("OPERATOR_KEY"), // base64 32-byte Ed25519 seed
    KeyId      = "operator-local",  // must match the NA's registered key
});

var decision = await client.Boundary.Decide(new Dictionary<string, object?>
{
    ["requesting_agent_id"] = "agent-a",
    ["capability"]          = "transactions.read",
});
```

## Local Network Authority

To develop against a governed Network Authority on your machine (policies
required, a privileged key for setup and a standard key for your controller),
see [Develop Against a Local Network Authority](https://docs.genesismesh.org/sdk/local-network-authority.html).
It needs `genesis-mesh` 1.1.0 or later from PyPI.

## Sub-clients

### Agreement

```csharp
// Create a capability offer (admin — requires signing key)
var offer = await client.Agreement.Offer(new CapabilityOffer
{
    OfferorSovereignId   = "ALPHA-NA",
    ResponderSovereignId = "BETA-NA",
    Capabilities         = ["read:data", "write:log"],
    Roles                = ["role:client"],
    ValidFrom            = "2026-01-01T00:00:00.000Z",
    ValidUntil           = "2027-01-01T00:00:00.000Z",
    ExpiresAt            = "2026-01-08T00:00:00.000Z",
});

// Accept an offer (requires the NA to hold a recognition treaty for the responder)
var agreement = await client.Agreement.Accept(offer);

// Verify agreement signatures (no auth required)
var result = await client.Agreement.Verify(new Dictionary<string, object?> { ["agreement"] = offer });
```

### Boundary

```csharp
// Issue a boundary decision (admin)
var decision = await client.Boundary.Decide(new Dictionary<string, object?>
{
    ["agreement"]            = agreement,
    ["requested_capability"] = "read:data",
});

// Verify a boundary decision (no auth required)
var v = await client.Boundary.Verify(new Dictionary<string, object?> { ["decision"] = decision });
```

### Evidence

```csharp
// Build signed trust evidence (admin)
// verdict: "allow" | "block" | "escalate" | "warn"
var evidence = await client.Evidence.Build(new TrustDecision
{
    SourceSovereignId = "ALPHA",
    TargetSovereignId = "BETA",
    Verdict           = "allow",
    Reason            = "long-standing member",
});
```

### Attestation

```csharp
// Issue a membership attestation (admin)
// roles must use a recognised prefix: role:anchor | role:bridge | role:client | role:operator | role:service:<name>
var att = await client.Attestation.Issue(new Dictionary<string, object?>
{
    ["subject_id"]     = "node-xyz",
    ["roles"]          = new[] { "role:client" },
    ["validity_hours"] = 8760,
});

// Revoke an attestation (admin)
await client.Attestation.Revoke(att.AttestationId, new Dictionary<string, object?> { ["reason"] = "key compromised" });
```

### Disclosure

```csharp
// Commit to a capability set (admin)
var commitment = await client.Disclosure.Commit(new Dictionary<string, object?>
{
    ["capabilities"] = new[] { "read:data", "write:log" },
});

// Generate a Merkle membership proof (no auth required)
var proof = await client.Disclosure.Prove(new Dictionary<string, object?>
{
    ["commitment_id"] = commitment.CommitmentId,
    ["capability"]    = "read:data",
});

// Verify the proof (no auth required)
var dv = await client.Disclosure.Verify(new Dictionary<string, object?> { ["proof"] = proof });
```

### Consensus

Validators vote on a justification proof: the `justification_proof` returned
with a decision by `POST /admin/boundary/evaluate` (see Raw admin calls). Pass
votes and proofs back unchanged; every field is covered by a signature.

```csharp
// Cast a validator vote, signed by the NA as validator (admin)
var vote = await client.Consensus.Vote(new Dictionary<string, object?>
{
    ["justification_proof"] = justificationProof,
    ["vote"]                = true,
    ["reason"]              = "evidence satisfactory",
});

// Assemble a K-of-N consensus proof (admin)
var cp = await client.Consensus.Proof(new Dictionary<string, object?>
{
    ["justification_proof"]     = justificationProof,
    ["votes"]                   = new[] { vote },
    ["required_threshold"]      = 1,
    ["validator_sovereign_ids"] = new[] { vote.ValidatorSovereignId },
});

// Verify against the validators' keys (no auth required)
var cv = await client.Consensus.Verify(new Dictionary<string, object?>
{
    ["proof"]                 = cp,
    ["validator_public_keys"] = new Dictionary<string, string> { [vote.ValidatorSovereignId] = validatorPublicKey },
});
// cv.Valid, cv.Reason ("valid", "threshold_not_met", "invalid_vote_signature", ...)
```

### DataUsage

```csharp
// Create a data license policy (admin)
var policy = await client.DataUsage.CreatePolicy(new Dictionary<string, object?>
{
    ["licensee_sovereign_id"] = "BETA",
    ["allowed_source_ids"]    = new[] { "src-a" },
    ["allowed_access_types"]  = new[] { "read", "aggregate" },
    ["valid_from"]            = "2026-01-01T00:00:00Z",
    ["valid_until"]           = "2026-12-31T00:00:00Z",
});

// Create a data access intent (admin)
// source_type: "personal" | "proprietary" | "public" | "synthetic"
var intent = await client.DataUsage.CreateIntent(new Dictionary<string, object?>
{
    ["sources"] = new object[]
    {
        new { source_id = "src-a", source_type = "public", owner_sovereign_id = "MY-NA" },
    },
    ["access_types"] = new[] { "read" },
});

// Get the active policy (no auth required)
var activePol = await client.DataUsage.GetPolicy();

// Verify intent against policy (no auth required)
var dv2 = await client.DataUsage.Verify(new Dictionary<string, object?>
{
    ["intent"] = intent,
    ["policy"] = policy,
});
```

## Offline verification

From 0.61.0 the SDK verifies Network Authority records locally, using
canonical JSON and Ed25519 only, without calling the NA. The reason codes are
the Python reference's. Pass the JSON text as received.

```csharp
var a = OfflineVerifier.VerifyAgreement(agreementJson, new[] { offererKey }, new[] { responderKey });
// a.Accepted, a.Reason: "accepted", "invalid_offerer_signature", "graph_digest_mismatch", ...

var d = OfflineVerifier.VerifyBoundaryDecision(decisionJson, new DecisionVerifyOptions
{
    OperatorPublicKeys = new[] { naPublicKey },
    ExpectedPolicies = new[] { policyJson },   // the decision must bind exactly these versions
    ExpectedAttestation = attestationJson,     // optional
});
// d.Accepted (verified), d.Authorized (ALLOW or DENY), d.Reason

bool ok = OfflineVerifier.VerifyDataLicensePolicySignature(policyJson, new[] { licensorKey });
var v = OfflineVerifier.VerifyDataAccessIntent(intentJson, policyJson, new[] { agentKey });
// v.Valid, v.ViolationReason, v.Violations
```

An accepted DENY has `Accepted` true and `Authorized` false: the decision
verified, and it denies. Revocation is not checked offline. `Canonical.FromJson`
and the `OfflineVerifier` digest helpers reproduce the reference forms. The
verifiers pass the shared `interop` conformance vectors
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/interop.json`), and the
core's cross-language scenario runs them against a live NA.

### NA key history

From 1.5.0 a Network Authority can move to a new signing key (a key
succession, signed by its offline root key and by the outgoing key). Pass the
NA's key history and a record must carry the signature of the key valid at
the record's own time: a decision's `decision_made_at`, an agreement's
`agreed_terms.valid_from` (`NAKeyHistory.RecordTimeFields` lists the time of
each NA-signed record). A succeeded key is still accepted for 300 seconds
after the next key takes effect (`NAKeyHistory.SuccessionHandover`). A
decision may not stay valid more than `NAKeyHistory.MaxDecisionLifetime`
(300 seconds) past the end of the key that signed it, and an agreement
vouches only while its key is still accepted. Without a history nothing
changes: a list of keys is valid at every time.

```csharp
// The body of GET /na/key-history, checked from the root key you hold out of band.
var h = OfflineVerifier.VerifyKeyHistoryResponse(body, rootPublicKey);
// h.Verified; otherwise h.Reason ("genesis_root_mismatch", "succession_chain_break", ...)
// and h.KeySequence, the refused record's position.

var d = OfflineVerifier.VerifyBoundaryDecision(decisionJson, new DecisionVerifyOptions
{
    KeyHistory = h.History,   // instead of OperatorPublicKeys
    AtRecordTime = true,      // optional: an archived decision, judged as of decision_made_at
});
// d.Reason "na_key_not_valid_at_time": signed by a key of the history, but not the one valid then;
// "decision_outlives_key": valid longer than its key may vouch for.

var a = OfflineVerifier.VerifyAgreementWithKeyHistory(agreementJson, h.History!,
    NAKeyHistory.ValidAlways(new[] { responderKey }));   // optional now: the time the key must still be accepted
```

`OfflineVerifier.VerifyKeyHistory(genesisJson, successionsJson, rootPublicKey)`
takes the genesis block and the succession records directly. The refusal
reasons and their order are the reference's: `genesis_invalid`,
`genesis_root_mismatch`, `unknown_field`, `non_canonical_form`,
`genesis_invalid_signature`, then for each succession `payload_invalid`,
`unknown_field` or `invalid_signature`, `non_canonical_form` and the
`succession_*` reasons. A succession carries three signatures: the root
key's, the new key's (proof that it is held) and the outgoing key's. The SDK runs the key
history vectors of the shared `audit` suite
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/audit.json`).

### Pre-issued grants

From 1.6.0 the NA can issue a `CapabilityGrant` before the bearer acts, so
the bearer can act within it while the NA is unreachable. Grants are off
unless the NA runs with `EVIDENCE_GRANTS=on`.
`OfflineVerifier.VerifyCapabilityGrant` checks that a grant is the NA's:
signed by the key of its key history valid at the grant's `issued_at`, with
every signed field known and in canonical form.

```csharp
var g = OfflineVerifier.VerifyCapabilityGrant(grantJson, h.History!); // or NAKeyHistory.ValidAlways(new[] { naKey })
// g.Valid; otherwise g.Reason: "missing_signature", "invalid_signature", "unknown_field",
// "non_canonical_form" or "na_key_not_valid_at_time".
```

It does not check the grant's validity, budget, parameter envelope or
revocation. The NA's revocation feed lists revoked grants in
`revoked_grant_ids` while grants are on (left out while they are off); the
embedded field registry knows both forms. The SDK runs the signed-form and
signature vectors of the shared `grants` suite
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/grants.json`).

## Raw admin calls

For NA routes not yet covered by a sub-client (e.g. `/admin/recognition-treaties`), use `Auth.BuildAdminHeaders` directly:

```csharp
using System.Text;
using System.Text.Json;
using GenesisMesh;

var seed    = Auth.LoadSeed(Environment.GetEnvironmentVariable("OPERATOR_KEY")!);
var body    = new { subject_sovereign_id = "BETA-NA", validity_hours = 24 };
// The signature binds method, path, query, the NA's public key and the body.
var request = new AdminRequest("POST", "/admin/recognition-treaties", "<NA public key from /sovereign.json>", body);
var headers = Auth.BuildAdminHeaders(request, "operator-local", seed);

using var http = new HttpClient();
using var req  = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/admin/recognition-treaties")
{
    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
};
req.Headers.Add("X-Admin-Key-Id",    headers.KeyId);
req.Headers.Add("X-Admin-Signature", headers.Signature);
req.Headers.Add("X-Admin-Timestamp", headers.Timestamp);
req.Headers.Add("X-Admin-Nonce",     headers.Nonce);
```

## Error handling

```csharp
using GenesisMesh;

try
{
    var offer = await client.Agreement.Offer(new CapabilityOffer());
}
catch (UnauthorizedException ex)
{
    // bad signing key or stale timestamp
    Console.WriteLine($"Auth failed: {ex.Detail} [{ex.Code}]");
}
catch (ValidationException ex)
{
    Console.WriteLine($"Validation: {ex.Detail} [{ex.Code}]");
}
catch (RateLimitException)
{
    // back off and retry
}
catch (NetworkException ex)
{
    Console.WriteLine($"Connection error: {ex.InnerException?.Message}");
}
catch (GenesisMeshException ex)
{
    Console.WriteLine($"Error HTTP {ex.Status}: {ex.Detail}");
}
```

## Admin authentication

Admin routes are authenticated with four HTTP headers built from an Ed25519 operator key:

| Header | Description |
|---|---|
| `X-Admin-Key-Id` | Key identifier registered with the NA |
| `X-Admin-Signature` | Ed25519 over the canonical admin payload: `{v: 2, method, path, query, audience, body, key_id, timestamp, nonce}` |
| `X-Admin-Timestamp` | ISO 8601 UTC timestamp (must be within NA's nonce window) |
| `X-Admin-Nonce` | UUID v4 replay-protection token (single use) |

Signature version 2 (1.0.2) binds each signature to the HTTP method, the request
path, the query parameters and the target NA's public key (`Audience`). The
client reads it (`network_authority.public_key`) once from the NA's public
`/sovereign.json`, or
uses `ClientOptions.Audience`. `Auth.AdminSigningPayload` returns the signed bytes.


The SDK handles all of this automatically when `SigningKey` is provided.

## Build and test

```sh
dotnet build
dotnet test
```

## License

MIT
