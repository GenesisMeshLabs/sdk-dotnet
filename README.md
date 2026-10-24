# sdk-dotnet

.NET SDK for the Genesis Mesh Network Authority HTTP API.

**Targets: .NET 8 (`net8.0`); from 1.8.0 also .NET 10 (`net10.0`). One dependency: [`NSec.Cryptography`](https://nsec.rocks/) for Ed25519.**

[![NuGet](https://img.shields.io/nuget/v/genesismesh-sdk-dotnet)](https://www.nuget.org/packages/genesismesh-sdk-dotnet)
[![CI](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml)

## Scope

This SDK (1.8) covers the Network Authority's trust API: the sub-clients
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
SDK's standalone verifier `genesis-mesh-verify`.

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

## Local Network Authority

To develop against a governed Network Authority on your machine (policies
required, a privileged key for setup and a standard key for your controller),
see [Develop Against a Local Network Authority](https://docs.genesismesh.org/sdk/local-network-authority.html).
It needs `genesis-mesh` 1.1.0 or later from PyPI. The quick start below uses
the keys and port that guide sets up.

## Quick start

A controller asks the NA for a decision with one signed call:
`POST /admin/boundary/evaluate`, which applies the active boundary policies.
This SDK has no method for that route, so the call is a raw admin call (see
[Raw admin calls](#raw-admin-calls)). Setup, here issuing an attestation for
the controller, uses the privileged key `operator-local`; the controller uses
its standard key `controller`.

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GenesisMesh;

// A key file holds comment lines and the base64 Ed25519 seed.
static string Seed(string path) =>
    File.ReadLines(path).Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));

var baseUrl = "http://127.0.0.1:9443";

// Setup: the privileged key. Create one client and reuse it; dispose it when done.
using var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl    = baseUrl,
    SigningKey = Seed("local/.genesis-mesh/keys/operator.key"),
    KeyId      = "operator-local",
});
var attestation = await client.Attestation.Issue(new Dictionary<string, object?>
{
    ["subject_id"]     = "my-controller",
    ["roles"]          = new[] { "role:client" },
    ["claims"]         = new Dictionary<string, object?> { ["capabilities"] = new[] { "app.invoke" } },
    ["validity_hours"] = 24,
});

// Run time: the controller's standard key evaluates a request under the attestation.
var body = new Dictionary<string, object?>
{
    ["attestation_id"]       = attestation.AttestationId,
    ["requested_capability"] = "app.invoke",
};
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
var naKey = (await http.GetFromJsonAsync<JsonElement>("/sovereign.json"))
    .GetProperty("network_authority").GetProperty("public_key").GetString()!;
var headers = Auth.BuildAdminHeaders(new AdminRequest("POST", "/admin/boundary/evaluate", naKey, body),
    "controller", Auth.LoadSeed(Seed("local/.genesis-mesh/keys/controller.key")));
using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/boundary/evaluate")
{
    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
};
request.Headers.Add("X-Admin-Key-Id",    headers.KeyId);
request.Headers.Add("X-Admin-Signature", headers.Signature);
request.Headers.Add("X-Admin-Timestamp", headers.Timestamp);
request.Headers.Add("X-Admin-Nonce",     headers.Nonce);
using var response = await http.SendAsync(request);
response.EnsureSuccessStatusCode();

using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
var decision = result.RootElement.GetProperty("decision");
var justificationProof = result.RootElement.GetProperty("justification_proof");
Console.WriteLine($"authorized: {decision.GetProperty("authorized").GetBoolean()}");
```

The decision is signed by the NA: check it offline with
`OfflineVerifier.VerifyBoundaryDecision` (see
[Offline verification](#offline-verification)) before acting on it. With no
active policy the decision rests on the attestation alone; publish and
activate policies (`POST /admin/boundary-policies`, then
`/admin/boundary-policies/<id>/activate`, privileged) to add their gates.

## Sub-clients

The examples below use `client` from the quick start, with the privileged
key. Counter, Accept, `Attestation.Issue`, `Revoke`, `SavePolicy` and
`DataUsage.CreatePolicy` need a privileged key; with a standard key the NA
answers 403 `insufficient_operator_tier`.

### Agreement

`Accept` needs a recognition treaty for the responder: issue one first with
`POST /admin/recognition-treaties` (see [Raw admin calls](#raw-admin-calls)).
The NA names itself as the offerer: it ignores `OfferorSovereignId` and
`Roles`.

```csharp
var now = DateTimeOffset.UtcNow;
static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("s") + "Z"; // 2026-01-01T00:00:00Z

// Create a capability offer (admin)
var offer = await client.Agreement.Offer(new CapabilityOffer
{
    ResponderSovereignId = "BETA-NA",
    Capabilities         = ["read:data", "write:log"],
    ValidFrom            = Iso(now),
    ValidUntil           = Iso(now.AddDays(365)),
    ExpiresAt            = Iso(now.AddDays(7)),
});

// Counter it with narrower terms (admin)
var counter = await client.Agreement.Counter(new Dictionary<string, object?>
{
    ["offer"]        = offer,
    ["capabilities"] = new[] { "read:data" },
    ["valid_from"]   = Iso(now),
    ["valid_until"]  = Iso(now.AddDays(180)),
});

// Accept the offer (admin)
var agreement = await client.Agreement.Accept(offer);

// Verify the agreement's two signatures (no auth required)
var check = await client.Agreement.Verify(new Dictionary<string, object?> { ["agreement"] = agreement });
// check.Accepted
```

`Accept` sends `{"offer": ...}`. To accept a counter-offer, send
`{"counter": ..., "original_offer": ...}` to `POST /admin/agreements/accept`
with a raw admin call.

### Boundary

`Decide` (`POST /admin/boundary/decide`) applies only the built-in gates. A
Network Authority that requires boundary policies
(`BOUNDARY_POLICY_ENFORCEMENT=required`, which `genesis-mesh init` sets)
refuses it with 409 `boundary_policy_required`. Call
`POST /admin/boundary/evaluate` instead, as in the [quick start](#quick-start);
this SDK has no method for it.

```csharp
// Verify a boundary decision, here the one from the quick start (no auth required)
var v = await client.Boundary.Verify(new Dictionary<string, object?> { ["decision"] = decision });
// v.Accepted
```

### Evidence

```csharp
// Build signed trust evidence (admin)
// verdict: "allow" | "block" | "escalate" | "warn"; any other value gets 400 invalid_verdict
var evidence = await client.Evidence.Build(new TrustDecision
{
    SourceSovereignId = "ALPHA",
    TargetSovereignId = "BETA",
    Verdict           = "allow",
    Reason            = "long-standing member",
});

// Verify it (no auth required)
var ev = await client.Evidence.Verify(new Dictionary<string, object?> { ["evidence"] = evidence });
```

### Attestation

```csharp
// Issue a membership attestation (admin)
// roles must use a recognised prefix: role:anchor | role:bridge | role:client | role:operator | role:service:<name>;
// a bare name gets 400 invalid_role
var att = await client.Attestation.Issue(new Dictionary<string, object?>
{
    ["subject_id"]     = "node-xyz",
    ["roles"]          = new[] { "role:client" },
    ["validity_hours"] = 8760,
});

// Revoke an attestation (admin)
await client.Attestation.Revoke(att.AttestationId, new Dictionary<string, object?> { ["reason"] = "key compromised" });

// Set the recognition policy: the issuers whose attestations this NA recognises (admin)
await client.Attestation.SavePolicy(new Dictionary<string, object?>
{
    ["recognition_policy"] = new Dictionary<string, object?>
    {
        ["local_sovereign_id"] = "MY-NA",
        ["recognized_issuers"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["sovereign_id"]  = "PARTNER-NA",
                ["public_keys"]   = new[] { "<base64 Ed25519 public key>" },
                ["allowed_roles"] = new[] { "role:client" },
            },
        },
    },
});
```

From 1.8.0, `Revoke` refuses an ID that is not one path segment (empty, `.`,
`..`, or containing `/`, `\`, `?`, `#`, `%` or a control character) before
anything is signed or sent: it throws `ArgumentException`.

### Disclosure

```csharp
// Commit to a capability set under an agreement (admin)
var commitment = await client.Disclosure.Commit(new Dictionary<string, object?>
{
    ["capabilities"] = new[] { "read:data", "write:log" },
    ["agreement"]    = agreement,
});

// Prove that one capability is in the committed set (no auth required)
var proof = await client.Disclosure.Prove(new Dictionary<string, object?>
{
    ["capability"]          = "read:data",
    ["capabilities"]        = new[] { "read:data", "write:log" },
    ["commitment"]          = commitment,
    ["prover_sovereign_id"] = "BETA-NA",
});

// Verify the proof against the commitment (no auth required)
var dv = await client.Disclosure.Verify(new Dictionary<string, object?>
{
    ["proof"]      = proof,
    ["commitment"] = commitment,
});
```

### Consensus

Validators vote on a justification proof: the `justification_proof` returned
with a decision by `POST /admin/boundary/evaluate` (see the
[quick start](#quick-start)). Pass votes and proofs back unchanged; every
field is covered by a signature.

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

// Verify against the validators' keys; here the NA is the only validator (no auth required)
var cv = await client.Consensus.Verify(new Dictionary<string, object?>
{
    ["proof"]                 = cp,
    ["validator_public_keys"] = new Dictionary<string, string> { [vote.ValidatorSovereignId] = naKey },
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
    ["valid_from"]            = Iso(now),
    ["valid_until"]           = Iso(now.AddDays(365)),
});

// Create a data access intent (admin)
// source_type: "personal" | "proprietary" | "public" | "synthetic"; a source without
// source_id, source_type and owner_sovereign_id gets 400 invalid_source
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
the Python reference's. Pass the JSON text as received (for a `JsonElement`,
its `GetRawText()`).

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

bool ok = OfflineVerifier.VerifyDataLicensePolicySignature(licensePolicyJson, new[] { licensorKey });
var v = OfflineVerifier.VerifyDataAccessIntent(intentJson, licensePolicyJson, new[] { agentKey });
// v.Valid, v.ViolationReason, v.Violations
```

An accepted DENY has `Accepted` true and `Authorized` false: the decision
verified, and it denies. Revocation is not checked offline. `Canonical.FromJson`
and the `OfflineVerifier` digest helpers reproduce the reference forms. The
verifiers pass the shared `interop` conformance vectors
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/interop.json`). The core's
cross-language scenario (`interop/`) also has them verify agreements,
decisions, a license policy and an intent that a live NA signs, on every pull
request and push to the core's `main`.

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

#### The vouching horizon (1.8.0)

Judged at its own time, a record names the key that signed it. But whoever
kept a succeeded key could still sign a record dated inside that key's
window. So, judged **for the present**, a record of a key that has ended
vouches only until that key's horizon: its end, the 300-second hand-over and
a grace of at most 24 hours (`NAKeyHistory.SuccessionVouchingGrace`). The NA
re-issues what is still valid under the new key.

```csharp
h.History!.RecordOutlivesKey(publicKey, DateTimeOffset.UtcNow);  // past its horizon?
h.History!.KeysVouchingAt(issuedAt, DateTimeOffset.UtcNow);      // KeysAt without the keys past their horizon
var reason = OfflineVerifier.NARecordRefusal("MembershipAttestation", recordJson, h.History!, DateTimeOffset.UtcNow);
// null when it vouches; otherwise "outlives_key", "record_malformed", "na_key_not_valid_at_time",
// "invalid_signature", "unknown_field" or "non_canonical_form".
```

`VerifyKeyHistoryResponse` takes the NA's `vouching_grace_seconds` only to
shorten the grace; `WithVouchingGrace` does the same for a history you hold.
`NARecordRefusal` covers attestations, treaties, revocation feeds and grants,
and passes the shared `vouching` suite
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/vouching.json`). Upgrade
the NA to 1.8.0 first and let it re-issue (`reissue_pending: false` on its
`/readyz`) before judging records for the present.

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
g = OfflineVerifier.VerifyCapabilityGrant(grantJson, h.History!, DateTimeOffset.UtcNow);
// 1.8.0: also "outlives_key" once the grant's key has passed its vouching horizon.
```

It does not check the grant's validity, budget, parameter envelope or
revocation. The NA's revocation feed lists revoked grants in
`revoked_grant_ids` while grants are on (left out while they are off); the
embedded field registry knows both forms. The SDK runs the signed-form and
signature vectors of the shared `grants` suite
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/grants.json`).

## Raw admin calls

For NA routes not covered by a sub-client, such as `/admin/boundary/evaluate`
(see the [quick start](#quick-start)) or `/admin/recognition-treaties`, use
`Auth.BuildAdminHeaders` directly. This one issues a recognition treaty for
`BETA-NA`, which `Agreement.Accept` needs (privileged):

```csharp
using System.Text;
using System.Text.Json;
using GenesisMesh;

var seed    = Auth.LoadSeed(Seed("local/.genesis-mesh/keys/operator.key"));
var body    = new
{
    subject_sovereign_id = "BETA-NA",
    subject_public_keys  = new[] { "<BETA-NA's base64 Ed25519 public key>" },
    scope                = new { allowed_roles = new[] { "role:client" } },
    validity_hours       = 24,
};
// The signature binds method, path, query, the NA's public key (naKey, from /sovereign.json) and the body.
var request = new AdminRequest("POST", "/admin/recognition-treaties", naKey, body);
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
using var resp = await http.SendAsync(req);   // 201 with the signed treaty
```

## Error handling

The exception types for 403, 409, 413 and other 4xx statuses are from 1.8.0.
In 1.7.0 and earlier, 403, 409, 413 and any other status without its own
type are `ServerException`.

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
catch (ForbiddenException ex)
{
    // 403: e.g. insufficient_operator_tier; retrying does not help
    Console.WriteLine($"Forbidden: {ex.Detail} [{ex.Code}]");
}
catch (BadRequestException ex)
{
    // 400: a missing or malformed field, such as invalid_role or invalid_verdict
    Console.WriteLine($"Bad request: {ex.Detail} [{ex.Code}]");
}
catch (ValidationException ex)
{
    // 422: well-formed but refused, such as accept_rejected
    Console.WriteLine($"Validation: {ex.Detail} [{ex.Code}]");
}
catch (RateLimitException)
{
    // back off and retry
}
catch (ServerException)
{
    // 5xx only: may succeed later
}
catch (NetworkException ex)
{
    Console.WriteLine($"Connection error: {ex.InnerException?.Message}");
}
catch (GenesisMeshException ex)
{
    // every other NA error: ConflictException (409), PayloadTooLargeException (413),
    // ClientErrorException (any other 4xx), ...
    Console.WriteLine($"Error HTTP {ex.Status}: {ex.Detail}");
}
```

| Status | Exception |
|---|---|
| 400 | `BadRequestException` |
| 401 | `UnauthorizedException` |
| 403 | `ForbiddenException` |
| 404 | `NotFoundException` |
| 409 | `ConflictException` |
| 413 | `PayloadTooLargeException` |
| 422 | `ValidationException` |
| 429 | `RateLimitException` |
| other 4xx | `ClientErrorException` |
| 5xx | `ServerException` |

All of them derive from `GenesisMeshException` (`Status`, `Code`, `Detail`).
`Detail` is the NA's message; `Message` is the formatted
`genesismesh: HTTP <status> <code>: <detail>`.
A connection failure or timeout is a `NetworkException`. A 2xx response that
is not strict JSON (duplicate keys, `-0`, lone surrogates, ...) throws
`StrictJsonException`, a `JsonException`, not a `GenesisMeshException`. Cancelling through
your own `CancellationToken` throws `OperationCanceledException`.

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

Contributing: [CONTRIBUTING.md](https://github.com/GenesisMeshLabs/sdk-dotnet/blob/main/CONTRIBUTING.md).
How the SDK is laid out and where a change goes:
[docs/architecture.md](https://github.com/GenesisMeshLabs/sdk-dotnet/blob/main/docs/architecture.md).

## License

MIT
