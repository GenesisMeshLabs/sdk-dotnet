# sdk-dotnet

.NET SDK for the Genesis Mesh Network Authority HTTP API.

**Target: .NET 8+. One dependency: [`NSec.Cryptography`](https://nsec.rocks/) for Ed25519.**

[![NuGet](https://img.shields.io/nuget/v/genesismesh-sdk-dotnet)](https://www.nuget.org/packages/genesismesh-sdk-dotnet)
[![CI](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml)

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

```csharp
// Cast a validator vote (admin)
var vote = await client.Consensus.Vote(new Dictionary<string, object?>
{
    ["proposal_id"] = "prop-1",
    ["decision"]    = "approve",
    ["reason"]      = "evidence satisfactory",
});

// Assemble a consensus proof (admin)
var cp = await client.Consensus.Proof(new Dictionary<string, object?>
{
    ["proposal_id"] = "prop-1",
    ["threshold"]   = 1,
});

// Verify the consensus proof (no auth required)
var cv = await client.Consensus.Verify(new Dictionary<string, object?> { ["proof"] = cp });
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

## Raw admin calls

For NA routes not yet covered by a sub-client (e.g. `/admin/recognition-treaties`), use `Auth.BuildAdminHeaders` directly:

```csharp
using System.Text;
using System.Text.Json;
using GenesisMesh;

var seed    = Auth.LoadSeed(Environment.GetEnvironmentVariable("OPERATOR_KEY")!);
var body    = new { subject_sovereign_id = "BETA-NA", validity_hours = 24 };
var headers = Auth.BuildAdminHeaders(body, "operator-local", seed);

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
| `X-Admin-Signature` | Ed25519 signature over `canonicalJSON({body, key_id, nonce, timestamp})` |
| `X-Admin-Timestamp` | ISO 8601 UTC timestamp (must be within NA's nonce window) |
| `X-Admin-Nonce` | UUID v4 replay-protection token (single use) |

The SDK handles all of this automatically when `SigningKey` is provided.

## Build and test

```sh
dotnet build
dotnet test
```

## License

MIT
