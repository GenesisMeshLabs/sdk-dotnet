# sdk-dotnet

.NET SDK for the Genesis Mesh Network Authority HTTP API.

**Targets: .NET 8 (`net8.0`); from 1.8.0 also .NET 10 (`net10.0`). One dependency: [`NSec.Cryptography`](https://nsec.rocks/) for Ed25519.**

[![NuGet](https://img.shields.io/nuget/v/genesismesh-sdk-dotnet)](https://www.nuget.org/packages/genesismesh-sdk-dotnet)
[![CI](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/GenesisMeshLabs/sdk-dotnet/actions/workflows/ci.yml)

## Scope

This SDK covers the Network Authority's trust API: the sub-clients
below (agreements, boundary decisions and from 1.9 policy-aware evaluation,
boundary policies, trust evidence, attestations, selective disclosure,
consensus and data usage), offline verification of their signed records, and
signed admin calls to any other route. It is growing to the TypeScript SDK's
governed lifecycle (decision 0008 of the core). From 1.9 it records and
submits execution evidence, reads where each resource's chain ends, and reads
and administers the store: its status, the NA's verification of it, resource
and vendor histories, the export, executor keys and retention (see
[EvidenceStore](#evidencestore)). It runs governed actions with the evidence
outbox and reconciliation (see [Governed actions](#governed-actions)), and
verifies an export offline (see [Verifying an export](#verifying-an-export)).
Not yet here: verifying the store's 1.3.0 and later entry kinds offline, store
anchors, none of the 1.3.0 changes outside the controlled path (observations,
break-glass records, the record outbox), and none of the 1.4.0 remediation and
review APIs (remediation records, the due queue, reviews, urgent actions).
Those live in the [TypeScript SDK](https://github.com/GenesisMeshLabs/sdk-typescript)
and the [Rust SDK](https://github.com/GenesisMeshLabs/sdk-rust). The embedded
field registry lists their records.
A boundary policy with the 1.4.0 sections (`remediation`, `review_required`,
`emergency`) verifies here like any other.

From 1.5 it reads the NA's key history (see
[NA key history](#na-key-history)), so decisions and agreements signed before
and after a key succession keep verifying. Audit packs, archives and their
verification are not part of this SDK: they live in the core
(`genesis-mesh audit pack`, `genesis-mesh audit verify`) and in the Rust
SDK's standalone verifier `genesis-mesh-verify`.

From 1.9 a client can serve several NA instances with failover, retry
idempotent requests, and sign with a key it does not hold (see
[Client options](#client-options)). It also reads the NA's health and its key
history (see [Health](#health) and [NA](#na)).

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

A controller asks the NA for a decision with one signed call,
`Boundary.Evaluate`, which applies the active boundary policies. Setup, here
publishing a policy and issuing an attestation for the controller, uses the
privileged key `operator-local`; the controller uses its standard key
`controller`.

```csharp
using GenesisMesh;

// A key file holds comment lines and the base64 Ed25519 seed.
static string Seed(string path) =>
    File.ReadLines(path).Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));

var baseUrl = "http://127.0.0.1:9443";

// Setup: the privileged key. Create one client and reuse it; dispose it when done.
using var admin = new GenesisMeshClient(new ClientOptions
{
    BaseUrl    = baseUrl,
    SigningKey = Seed("local/.genesis-mesh/keys/operator.key"),
    KeyId      = "operator-local",
});
var policy = await admin.Policy.Publish(new BoundaryPolicyIntent
{
    PolicyId   = "app-invoke",
    ValidFrom  = DateTimeOffset.UtcNow.ToString("O"),
    ValidUntil = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
    Selector   = new PolicySelector { Capabilities = ["app.*"] },
    Gates =
    [
        new GateSpec
        {
            GateId   = "max-lifetime",
            GateType = "max_value.v1",
            Order    = 0,
            Config   = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = 90 },
        },
    ],
});
await admin.Policy.Activate(policy.PolicyId, policy.Version);
var attestation = await admin.Attestation.Issue(new Dictionary<string, object?>
{
    ["subject_id"]     = "my-controller",
    ["roles"]          = new[] { "role:client" },
    ["claims"]         = new Dictionary<string, object?> { ["capabilities"] = new[] { "app.invoke" } },
    ["validity_hours"] = 24,
});

// Run time: the controller's standard key evaluates a request under the attestation.
using var controller = new GenesisMeshClient(new ClientOptions
{
    BaseUrl    = baseUrl,
    SigningKey = Seed("local/.genesis-mesh/keys/controller.key"),
    KeyId      = "controller",
});
var evaluation = await controller.Boundary.Evaluate(new EvaluateRequest
{
    AttestationId       = attestation.AttestationId,
    RequestedCapability = "app.invoke",
    Context = new EvaluationContext
    {
        RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 },
    },
});
Console.WriteLine($"authorized: {evaluation.Decision.Authorized} {evaluation.Decision.DenialReason}");
```

The decision is signed by the NA: check it offline with
`OfflineVerifier.VerifyBoundaryDecision` (see
[Offline verification](#offline-verification)) before acting on it. A denial
is a signed decision with `Authorized` false, not an exception.
`Boundary.Evaluate` refuses a context holding secret material
(`SecretMaterialException`) before anything is signed or sent. It is the
SDK's own guard, as in the TypeScript SDK: a field named like a secret
(`password`, `token`, `value`, ...) or a value that looks like a key or token
is refused. Put identifiers, versions and timestamps in it, never secret
values.

## Client options

`ClientOptions` beyond the URL and the key:

| Option | What it does |
|---|---|
| `BaseUrls` | Several NA instances sharing one database, without a load balancer; set this or `BaseUrl`. Requests go to one instance. An idempotent request (a GET, a verification) moves to the next instance on a connection failure or a 502, 503 or 504. Any other request moves only when it never reached an instance: the connection could not be made, or the NA's key could not be read first because the instance did not answer or answered 502, 503 or 504. Otherwise it is not sent again, and the next request starts at the next instance. |
| `Retry` | `new RetrySettings { Attempts = 3 }`: idempotent requests are tried again after a connection failure or a 429, 502, 503 or 504, waiting `BaseDelay` (200 ms) and doubling it each time. Off by default. A request that creates state is never retried. |
| `Signer` | An `ISigner` for admin requests, used instead of `SigningKey` and `KeyId`: implement it for an HSM, or use `SeedSigner` for a key read at start. |
| `Headers` | Extra headers on every request, such as for an API gateway in front of the NA. |
| `HttpHandler` | The handler requests go through, such as a `SocketsHttpHandler` with a `PooledConnectionLifetime`, so that a long-lived client follows DNS changes. The client does not dispose it. |
| `HttpClient` | An `HttpClient` to send with, such as a typed client from `IHttpClientFactory`; its own `Timeout` applies. The client does not dispose it. A resilience handler in its pipeline must not retry POSTs: a repeated admin request carries the same signature and nonce, and the NA refuses it as a replay after the first may have been applied. |
| `Timeout` | Each attempt's timeout (each request, and each read of the NA's key); 10 s by default. A call that fails over or retries can take longer. |
| `Outbox` | Durable storage for signed execution records the NA has not admitted yet: a `FileOutbox`, or your own `IOutbox<OutboxEntry>` (see [Governed actions](#governed-actions)). Give each process its own. |

```csharp
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using GenesisMesh;

// Key Vault has no Ed25519 keys: the operator seed is a secret, read once at start and used in memory.
var vault = new SecretClient(new Uri("https://my-vault.vault.azure.net/"), new DefaultAzureCredential());
var seed = (await vault.GetSecretAsync("gm-controller-key")).Value.Value;

using var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrls = ["https://na-1.internal:9443", "https://na-2.internal:9443"],
    Signer   = new SeedSigner(seed, "controller"),
    Retry    = new RetrySettings { Attempts = 3 },
});
```

A retry does not wait for a `Retry-After` the NA sends; the wait is on the
exception (`RetryAfter`, see [Error handling](#error-handling)) for you to
honour, for example when the NA's admin rate limit answers 429.

`BaseUrl` must be an absolute http(s) URL: a missing or relative one is
refused when the client is built. A request that times out may have reached
the NA, so a state-creating request is not moved to another instance after a
timeout, even one spent connecting (the TypeScript SDK moves a request whose
connection timed out). `Health.Readiness()` is a probe: it fails over but is
not retried.

## Sub-clients

The examples below use `client`, a client with the privileged key (as
`admin` in the quick start), and `naKey`, the NA's public key
(`network_authority.public_key` of its `/sovereign.json`, or
`(await client.NA.KeyHistory()).Keys.Single(k => k.Current).PublicKey`). Counter, Accept, `Attestation.Issue`, `Revoke`, `SavePolicy` and
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

`Accept` sends `{"offer": ...}`. To accept a counter-offer, call
`AcceptCounter(counter, originalOffer)`, which sends
`{"counter": ..., "original_offer": ...}`.

### Boundary

`Evaluate` (`POST /admin/boundary/evaluate`, 1.9.0) applies the active
boundary policies under exactly one basis, `AttestationId` or `Agreement`, as
in the [quick start](#quick-start). `Decide` (`POST /admin/boundary/decide`)
applies only the built-in gates: a Network Authority that requires boundary
policies (`BOUNDARY_POLICY_ENFORCEMENT=required`, which `genesis-mesh init`
sets) refuses it with 409 `boundary_policy_required`.

```csharp
// Verify a boundary decision, here the one from the quick start (no auth required).
var v = await client.Boundary.Verify(new Dictionary<string, object?> { ["decision"] = evaluation.Decision });
// v.Accepted
```

A typed `BoundaryDecision` passed back keeps the form the NA signed. Before
1.9.0 it dropped a null `denial_reason`, and the NA refused it.

### Policy

The boundary policy lifecycle (v0.58). `Publish`, `Activate` and
`Deactivate` need a privileged key.

```csharp
var check = await client.Policy.Validate(intent);         // a dry run: check.Valid, check.Issues
var v1 = await client.Policy.Publish(intent);             // a new inactive version, signed
await client.Policy.Activate(v1.PolicyId, v1.Version);
var active = await client.Policy.Active();                // the active set, its health, the enforcement mode
var all = await client.Policy.List();                     // every version of every policy
var history = await client.Policy.History(v1.PolicyId);   // newest first, with the signed bodies
await client.Policy.Activate(v1.PolicyId, 1);             // activating an older version is the rollback
await client.Policy.Deactivate(v1.PolicyId, v1.Version);
var verified = await client.Policy.Verify(v1);            // public: the policy's signature
```

`Publish` refuses an invalid intent with 400 `boundary_policy_invalid`, its
issues in the exception's `Details`. A gate runs in `enforce` mode unless its
`Mode` is `observe`: an observed failure is reported in the decision but does
not deny, so optional rules can be rolled out before they are enforced.

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

### EvidenceStore

From 1.9 a controller records what it did after acting on an authorized
decision and submits the record to the NA's evidence store. An executor key
signs it: the controller generates the key pair, and the platform team
registers its public key for the controller's sovereign, optionally limited to
a resource prefix. A record holds metadata only: identifiers, settings,
versions and times, never a secret value.

```csharp
// The platform team, with the privileged key: register the controller's executor key.
await admin.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
{
    KeyId = "portal-executor",
    PublicKey = executorPublicKey,
    ExecutorSovereignId = "partner-portal",
    ResourcePrefix = "partner:",
});

// The controller, with a standard key: evaluate, verify, act, then record.
var executor = new ExecutionRecorder("partner-portal", new SeedSigner(executorSeed, "portal-executor"));
var evaluation = await client.Boundary.Evaluate(request);
// ... verify the decision, then act ...
var resource = $"partner:{appId}/secret";
var record = await executor.Record(new RecordExecutionRequest
{
    Decision = evaluation.Decision,
    ExecutedCapability = "partner-secret.rotate",
    Outcome = ExecutionOutcomes.Success,
    ExecutionParameters = new Dictionary<string, object?> { ["app_id"] = appId, ["key_id"] = newKeyId },
    ResourceId = resource,
    ResourceAction = ResourceActions.Rotate,
    PriorResource = await client.EvidenceStore.ResourceHead(resource),   // null for its first record
});
var submission = await client.EvidenceStore.Submit(record);            // "recorded", or "duplicate" on a retry
```

- `Record` builds and signs the record as the Python reference does. Before
  anything is signed it refuses secret material (`SecretMaterialException`)
  and values the NA would refuse as `invalid_json` (`StrictJsonException`).
  Records under one decision chain through `PriorRecord`; records of one
  resource, from any decision, through `PriorResource`
  (`ExecutionRecorder.HeadOf(record)` gives the head a record leaves).
- `Submit` needs no operator key: the executor's signature authenticates the
  record. Resubmitting the same record returns `duplicate`, so it is safe to
  retry. The NA refuses with 409 `evidence_conflict` when the record's
  position is taken, most often because another record was stored on the
  resource after its head was read: read the head again and record anew. It
  refuses other records with 422 and a code, for example
  `evidence_out_of_scope` (the key is limited to other resources) or
  `evidence_decision_denied`. The NA keeps an authentic refused record as a
  quarantine entry.
- Reads need a read-tier key or above: `ResourceHead` (null for a resource
  without history) and `StoredResourceHead`, which refuse an ID they cannot
  send as a path (one holding `%`, `?`, `#` or `\`, an empty segment, or white
  space at either end), `Search` and `Iterate` over the
  stored entries, and `ResourceStates`, each resource's latest change for
  reconciliation against a cloud inventory.

`Governance.GovernedAction` does all of this in one call, and with an
outbox keeps the record until the NA admits it (see
[Governed actions](#governed-actions)).

#### Reading and administering the store

From 1.9 an operator reads the whole store and administers it. Reads need a
read-tier key or above. Retiring a key and applying retention need a
privileged one: a standard key gets 403 `insufficient_operator_tier`.

```csharp
var status = await client.EvidenceStore.Status();     // entries, last position, anchoring, retention
var check = await client.EvidenceStore.Verify();      // the NA verifies every entry, chain and signature
// check.Verified, check.Failures: StoreSequence, Reason, Detail

var history = await client.EvidenceStore.ResourceHistory("partner:org-1/app-orders/secret");
// history.Entries (decisions, proofs, records), history.Verification, history.Truncated
var vendor = await client.EvidenceStore.VendorHistory("partner-org-1");

// An incremental pull for a SIEM pipeline: every entry after the last position kept.
await foreach (var e in client.EvidenceStore.ExportAll(sinceSequence: lastKept))
    Keep(e);                                          // e.Entry.StoreSequence is the next cursor
string page = await client.EvidenceStore.ExportText(new EvidenceExportParams { SinceSequence = lastKept, Limit = 500 });
var events = OfflineVerifier.ParseExportLines(page);  // gm.evidence.event lines, payloads as received

var keys = await client.EvidenceStore.ListExecutorKeys();        // retired keys included
await admin.EvidenceStore.RetireExecutorKey("portal-executor");
var retention = await admin.EvidenceStore.ApplyRetention(olderThanDays: 365);
// retention.RemovedCount, retention.Checkpoint, retention.HeldBack (why it stopped short)
var checkpoint = await client.EvidenceStore.LatestCheckpoint();  // null before the first retention
```

- `Verify`, and the verification each history comes with, is the NA's own. A
  history is not a run of the store, so its store links are checked only
  between adjacent positions. It holds at most the oldest 10,000 records
  (`Truncated`); read the rest through the export. A resource whose records
  retention removed has no history (`resource_not_found`), but `ResourceHead`
  still reads where its chain ends.
- `ExportAll` follows the export's pages (1 to 1000 entries each) and refuses
  an export whose positions do not advance. `ParseExportLines` refuses a line
  that is not strict JSON (`StrictJsonException`), or not an event of schema
  version 1 with exactly the reference's envelope (`JsonException`, with the
  line number). `OfflineVerifier.VerifyEvidenceEvents` verifies the events
  offline (see [Verifying an export](#verifying-an-export)).
- A retired key still verifies the records it signed and signs no new one
  (`evidence_executor_key_retired`). Its ID cannot be registered again, and
  retiring it again is `executor_key_retired` (`ConflictException`).
- `ApplyRetention` removes a prefix of the store behind a checkpoint the NA
  signs, so the rest still verifies. The prefix ends before the first entry
  recorded within the days given, under a decision still valid, or not judged
  yet. From NA 1.5.0 it also ends at `throughSequence`, and at what archive
  records cover while the NA requires archives (its default). `HeldBack` says
  what stopped it short.

### Governed actions

From 1.9 `Governance.GovernedAction` makes one governed change, as the
TypeScript SDK's `governedAction` does: it evaluates the request, verifies the
decision offline, runs your action only on an authorized decision, and records
the outcome on the resource's chain.

```csharp
using var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl = naUrl,
    Signer  = new SeedSigner(operatorSeed, "portal-controller"),   // a standard key evaluates
    Outbox  = new FileOutbox("/var/lib/portal/outbox"),
});
var executor = new ExecutionRecorder("partner-portal", new SeedSigner(executorSeed, "portal-executor"));

var result = await Governance.GovernedAction(client, executor, new GovernedActionRequest
{
    RequestedCapability = "partner-secret.rotate",
    AttestationId = partnerAttestationId,
    Context = new EvaluationContext
    {
        RequestParameters = new Dictionary<string, object?> { ["app_id"] = appId, ["lifetime_days"] = 90 },
    },
    ResourceId = $"partner:{appId}/secret",
    ResourceAction = ResourceActions.Rotate,
    Verify = new GovernedVerification
    {
        OperatorPublicKeys = [naKey],
        ExpectedPolicies = [rotationPolicy],      // the policies the decision must be bound to
        ExpectedAttestation = partnerAttestation,
    },
}, async (decision, ct) =>
{
    var credential = await RotateInGraph(appId, ct);    // your change, only after a verified ALLOW
    return new ActionReport<string>
    {
        Value = credential.SecretText,                   // returned to you, never recorded
        ExecutionParameters = new Dictionary<string, object?> { ["key_id"] = credential.KeyId, ["expires_on"] = credential.End },
    };
});
if (!result.Authorized) Log(result.Summary.EnforcedFailures);
```

- **Before the action.** The decision must verify under the NA's keys (or
  `KeyHistory`), be bound to exactly the expected policies (an empty list for
  a capability no policy selects) and, for an attestation basis, to the
  expected attestation, carry the request's context ID, and still be valid
  after the resource's head was read. Otherwise `DecisionVerificationException`
  names the reason and nothing runs. It is verified as the NA sent it, so a
  field this SDK does not know is refused by name (`unknown_field`). A denial
  returns `Authorized == false` with `Summary` (`SummarizeDecision`): the
  enforced and the observe-mode failures apart, and the policies applied.
- **The record.** It chains from the resource's head: the newest pending
  record in the outbox, otherwise the NA's head; set `PriorResource`, or
  `NewResource` for a resource without history, to skip the read. When the
  action throws, a `failure` record (`action failed`, without the error's
  text) is submitted and the error rethrown. Cancelling stops the steps up to
  and including the action; once it has run, its outcome is recorded.
- **Without an outbox** a failed submission throws after the action ran, and
  secret material in the report throws `SecretMaterialException` with nothing
  signed. Keep the action idempotent, or use an outbox.
- **With an outbox** every record is kept until the NA admits it, and a failed
  submission does not throw: `result.Queued` is the entry, pending (retried)
  or a dead letter (refused for good, with the NA's code; kept, never
  dropped). Secret material in the report is recorded without the refused
  fields and throws `MetadataRefusedException`, with the action's value; a
  record that cannot be kept throws `EvidenceNotKeptException`. Do not run the
  action again after either.

Flush the outbox at startup and on a timer, such as from an Azure Functions
timer trigger:

```csharp
var flushed = await client.EvidenceStore.FlushPending();
// flushed.Admitted, flushed.Pending (in backoff, or behind a pending record), flushed.DeadLettered
```

Records are submitted in the order they were added, each after the records it
chains from, with a backoff from 5 seconds to 15 minutes (or the NA's
`Retry-After`, at most 15 minutes). `EvidenceOutbox` holds the rules: which
refusals are final (`PermanentRefusals`) and how a failure is classified
(`Classify`). `Enqueue` keeps and submits a record you signed yourself.

`FileOutbox` writes one JSON file per entry, in the format of the TypeScript
and Rust SDKs, so a controller can move between them; it creates its directory
`0700` on Linux and macOS (restrict it yourself on Windows). One process uses a
directory: where instances share a file system, such as Azure Functions scaled
out over one `%HOME%`, give each instance its own directory or use a database. For a database,
implement `IOutbox<OutboxEntry>`: `Add`, `Update`, `Remove`, and `List` in the
order added, each durable once it completes. `MemoryOutbox<OutboxEntry>` is
for tests.

Compare the cloud with the governed history with
`Governance.ReconcileResources`, a pure function:

```csharp
var findings = Governance.ReconcileResources(
    inventory.Select(s => new ObservedResource { ResourceId = s.Id, Exists = s.Exists, Version = s.KeyId }),
    await client.EvidenceStore.ResourceStates(),
    new ReconcileOptions { VersionKey = "key_id", CompleteInventory = true, ScopePrefix = "partner:" });
// unmanaged, drifted, present_after_revoke, missing or in_sync, each with a detail
```

Correct each finding through a governed action.

#### A partner controller on a standard key

[`PartnerLifecycleTests`](https://github.com/GenesisMeshLabs/sdk-dotnet/blob/main/tests/GenesisMesh.Sdk.Tests/PartnerLifecycleTests.cs)
runs a partner credential controller's lifecycle against a live NA that
refuses the policy-free decide route (`BOUNDARY_POLICY_ENFORCEMENT=required`).
It is a worked example of the split between the two keys:

- **The platform team sets up, with the privileged key:**
  - the policies: the partner lifecycle (`partner-secret.*`, with the
    application in the partner's claims, an owner and a lifetime limit), an
    emergency policy that requires an incident, and the other controller's;
  - the partner's attestation, its capabilities and applications as claims;
  - the security team's attestation for emergency removal, ahead of any incident;
  - the controller's executor key, limited to `partner:`.
- **The controller does the rest, with a standard key.** It creates, rotates,
  retires and revokes as governed actions, with an outbox. Its key cannot
  publish policies, issue or revoke attestations or register keys
  (`ForbiddenException`).
- **What holds:**
  - A denial runs nothing.
  - An ALLOW bound to other policies or another attestation than expected
    is refused before the action. This is the controller's guard on the
    policies: `required` refuses only the decide route, and a capability no
    policy selects is decided by the attestation's claims alone.
  - A secret in the request is refused before anything is sent; one in the
    report is recorded without it.
  - A record on a stale head is refused (`evidence_conflict`).
  - A record for a resource outside `partner:` is refused for good, and kept
    as a dead letter (`evidence_out_of_scope`). The prefix limits what is
    recorded, not what runs: the decision does not name the resource, so
    check that a resource is yours before acting on it.
  - Once the partner's attestation is revoked, its requests are denied, and a
    record under such a denial is refused (`evidence_decision_denied`).
- **Emergency removal** is evaluated under the security team's attestation and
  the emergency policy; the partner's attestation cannot authorize it. So it
  still works after the partner's attestation is revoked, and needs no
  privileged key.

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
with a decision by `Boundary.Evaluate` (see the
[quick start](#quick-start)). Pass votes and proofs back unchanged; every
field is covered by a signature.

```csharp
var justificationProof = evaluation.JustificationProof;

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

### Health

```csharp
var live  = await client.Health.Liveness();    // GET /healthz: the process answers ("ok")
var ready = await client.Health.Readiness();   // GET /readyz
if (!ready.Ready)
    Console.WriteLine($"not ready: database writable {ready.Database.Writable}, {ready.Database.Error}");
var health = await client.Health.Status();     // GET /health: network, version, policies, evidence store

// With BaseUrls: each instance, asked directly, for monitoring.
foreach (var e in await client.Health.Endpoints())
    Console.WriteLine($"{e.BaseUrl}: reachable {e.Reachable}, ready {e.Ready} {e.Error}");
```

A not-ready NA (503 `service_not_ready`) is returned with `Ready` false and
the failing checks, not thrown. `ready.NAKeyState.State` is the NA key's
state (1.5.0): anything but `signing` is not ready.

### NA

The NA's key history (1.5.0): the signed genesis block, every key succession
and each key's validity.

```csharp
var view = await client.NA.KeyHistory();                        // GET /na/key-history, as served
var history = await client.NA.VerifiedKeyHistory(rootPublicKey); // verified from the root key you hold
```

`VerifiedKeyHistory` ignores the keys the NA lists and rebuilds the history
from the genesis block, with the root key you hold out of band. It throws
`KeyHistoryException` when the history does not verify: `Code` is the first
failure's reason, and `Verification` has the details. Pass the history to the
offline verifiers (see [NA key history](#na-key-history)).

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

// 1.9.0: execution evidence and the justification proof of an evaluation.
bool executed = OfflineVerifier.VerifyExecutionSignature(evidenceJson, executorPublicKey);
bool justified = OfflineVerifier.VerifyJustificationSignature(evaluation.JustificationProof.GetRawText(), history);
string digest = OfflineVerifier.ExecutionDigest(evidenceJson);   // what the next record links to
var events = OfflineVerifier.ParseExportLines(exportText);       // gm.evidence.event JSON Lines
```

An accepted DENY has `Accepted` true and `Authorized` false: the decision
verified, and it denies. Revocation is not checked offline. `Canonical.FromJson`
and the `OfflineVerifier` digest helpers reproduce the reference forms. The
verifiers pass the shared `interop` conformance vectors
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/interop.json`). The core's
cross-language scenario (`interop/`) also has them verify agreements,
decisions, a license policy and an intent that a live NA signs, on every pull
request and push to the core's `main`.

### Verifying an export

From 1.9 an export, or a history, verifies offline, without trusting the NA:

```csharp
var events = new List<EvidenceEvent>();
await foreach (var e in admin.EvidenceStore.ExportAll()) events.Add(e);
var keys = await admin.EvidenceStore.ListExecutorKeys();            // retired keys included
var result = OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
{
    KeyHistory = await admin.NA.VerifiedKeyHistory(rootPublicKey),  // or NAPublicKeys = [naKey]
    ExecutorKeys = keys,
});
// result.Verified, result.Failures: StoreSequence, Reason, Detail

var history = await admin.EvidenceStore.ResourceHistory(resourceId);
var check = OfflineVerifier.VerifyEvidenceEvents(history.Entries, new EvidenceVerifyOptions
{
    NAPublicKeys = [naKey],
    ExecutorKeys = keys,
    Contiguous = false,                                             // a history is no run of the store
});
bool kept = OfflineVerifier.VerifyRetentionCheckpoint(checkpointJson, [naKey]);
```

- It is a port of the reference's `verify_evidence_events`, with its reason
  codes:
  - each envelope and its digests, and the store chain;
  - every signature: the NA's on decisions, justification proofs and
    retention checkpoints, under its keys or the key valid at the time
    (with a key history), and the executor's on execution evidence;
  - each execution record against its decision (`evidence_decision_denied`,
    `evidence_outside_decision_window`, `evidence_capability_mismatch`);
  - the decision and resource chains.

  A bad entry is a failure, never an exception.
- It verifies the kinds a governed store holds: decisions, justification
  proofs, execution evidence and retention checkpoints. Entries of the 1.3.0
  and later kinds fail as `unknown_entry_kind`; their envelopes still chain.
  Verify a store holding them with the TypeScript or Rust SDK, or
  `genesis-mesh audit verify`. Anchors are not checked.
- For an export that starts after a retention, pass `Checkpoint`, the
  NA-signed checkpoint as received: the run continues from its last removed
  entry, and each resource chain from its head.
- It runs the shared `field_registry` suite's `export` vectors, and gives the
  reference's own verification of its export.

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

For NA routes not covered by a sub-client, such as `/admin/recognition-treaties`,
use `Auth.BuildAdminHeaders` directly. This one issues a recognition treaty for
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
`genesismesh: HTTP <status> <code>: <detail>`. From 1.9:

- `RetryAfter` is the wait the NA named (`Retry-After`), such as with a 429; null when it named none.
- `Details` is the NA's `details` of the error, such as the failing checks of a not-ready NA.
- A failure to read the NA's key from `/sovereign.json` is the exception for that answer's status with the code
  `na_public_key_unavailable`. Before 1.9 an answer without the key was an `InvalidOperationException`.

A `SecretMaterialException` (1.9.0, code `evidence_secret_material`) is
thrown before anything is signed or sent when an evaluation context holds
secret material or more than 16 KiB of metadata (`EvidenceMetadata`): the
SDK's own guard for contexts, and the NA's rule for evidence.

A connection failure or timeout is a `NetworkException`. Its `ConnectFailed`
is true when the request did not reach the NA, so sending it again cannot
apply it twice. A 2xx response that
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


The SDK handles all of this automatically when `SigningKey` or `Signer` is provided.
`Auth.BuildAdminHeadersAsync` signs with an `ISigner`.

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
