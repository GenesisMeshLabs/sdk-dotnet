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
outbox, break-glass when the NA cannot be reached, and reconciliation (see
[Governed actions](#governed-actions)), and verifies an export offline (see
[Verifying an export](#verifying-an-export)).
It signs observations and break-glass records, the changes made outside the
governed path, submits and judges them, keeps them in a record outbox until the
NA admits them, and reads each resource's changes (see
[Changes outside the governed path](#changes-outside-the-governed-path)).
From 1.9 it also carries the NA's 1.4.0 remediation and reviews: the rules,
the due queue, remediation records and reviews, cancelling a denied change by
rotating forward and urgent changes (see
[Remediation and reviews](#remediation-and-reviews)), and verifies their
entries in an export. Not yet here: verifying the store's 1.5.0 and later
entry kinds in an export (key successions, archives, grants) and store
anchors. Those live in the
[TypeScript SDK](https://github.com/GenesisMeshLabs/sdk-typescript) and the
[Rust SDK](https://github.com/GenesisMeshLabs/sdk-rust). The embedded field
registry lists their records.
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

From 1.6 it checks a pre-issued grant's NA signature, and from 1.9 it holds
the grants' records and rules offline (the parameter envelope, a use's place
in its chain and the rules it keeps, and uses and time token requests signed
with the bearer key), calls the NA's grant routes (issuing, reading and
revoking grants, time tokens and uses), and holds a grant: a `GrantHolder` acts
within it while the NA is out of reach and falls back to break-glass past it
(see [Pre-issued grants](#pre-issued-grants)). Verifying grants, their uses
and revocations in an evidence export lives in the
[TypeScript SDK](https://github.com/GenesisMeshLabs/sdk-typescript) and the
[Rust SDK](https://github.com/GenesisMeshLabs/sdk-rust).

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
| `HttpClient` | An `HttpClient` to send with, such as a typed client from `IHttpClientFactory`; its own `Timeout` applies. The client does not dispose it. A resilience handler in its pipeline must not retry POSTs: a repeated admin request carries the same signature and nonce, and the NA refuses it as a replay after the first may have been applied. Nor should a handler read or buffer the response body: an answer whose body cannot be read would look like no answer, and break the glass of a governed action. |
| `Timeout` | Each attempt's timeout (each request, and each read of the NA's key); 10 s by default. A call that fails over or retries can take longer. |
| `Outbox` | Durable storage for signed execution records the NA has not admitted yet: a `FileOutbox`, or your own `IOutbox<OutboxEntry>` (see [Governed actions](#governed-actions)). Give each process its own. |
| `RecordOutbox` | Durable storage for signed observations and break-glass records the NA has not admitted yet: a `FileRecordOutbox` in a directory of its own, or your own `IOutbox<RecordOutboxEntry>` (see [The record outbox](#the-record-outbox)). Give each process its own. |

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

#### Changes outside the governed path

From 1.9 the store also keeps changes made outside the governed path, when the
NA runs with its out-of-band records on (`EVIDENCE_OUT_OF_BAND=on`; otherwise
404 `out_of_band_disabled`):

- an **observation**: a change seen at its source, such as an edit in the
  cloud console or a reconciliation finding, signed by an observer key;
- a **break-glass record**: a change a controller made while it could not get
  a decision, signed by its executor key with a justification.

The NA judges each one after the fact, once, in a judgement it signs: how the
change was governed and whether the policies then allowed it.

```csharp
// The platform team registers an observer key, as an executor key with the observer role.
await admin.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
{
    KeyId = "cloud-observer", PublicKey = observerPublicKey, ExecutorSovereignId = "partner-portal",
    Role = "observer", ResourcePrefix = "partner:",
});

var observer = new ObservationRecorder("partner-portal", new SeedSigner(observerSeed, "cloud-observer"));
var observation = await observer.Record(new ObservationInput
{
    ResourceId = "partner:app-1/secret",
    Action = ResourceActions.Rotate,
    Capability = "partner-secret.rotate",
    ChangedAt = changedAt,                       // or ChangedNotBefore and ChangedNotAfter
    Actor = "principal-7f3a",                    // as the source reported it; a pseudonym, never a credential
    Source = "cloud-activity-log",
    SourceEventId = activityLogEventId,
    VersionId = "v2",
});
var submitted = await client.EvidenceStore.SubmitObservation(observation);  // "recorded", "duplicate" or "quarantined"
var judgement = submitted.Judgement?.Payload;                                // judged at admission, by default

// A reconciliation finding, observed once per scan (null for a resource in sync).
if (ObservationRecorder.FromFinding(drifted, new FindingObservationOptions
{
    Capability = "partner-secret.rotate", PreviousScanAt = lastScan, ScannedAt = thisScan,
}) is { } seen)
    await client.EvidenceStore.SubmitObservation(await observer.Record(seen));

// A controller that acted while the NA could not be reached.
var glass = await executor.SignBreakGlass(new BreakGlassInput
{
    ResourceId = "partner:app-1/secret",
    ResourceAction = ResourceActions.Rotate,
    Capability = "partner-secret.rotate",
    Justification = "NA unreachable during an incident; rotating the leaked secret",
    EvaluationRequest = failedRequest,           // only its digest is recorded
    EvaluationFailure = EvaluationFailures.NetworkError,
    Outcome = ExecutionOutcomes.Success,
});
await client.EvidenceStore.SubmitBreakGlass(glass);

await foreach (var change in client.EvidenceStore.AllResourceChanges("partner:app-1/secret"))
    Show(change.Kind, change.GovernedBy, change.State, change.Verdict);
```

- `Record` and `SignBreakGlass` refuse what the NA would refuse before
  anything is signed (`OutOfBandRecordException`), in the NA's order:
  - `*_malformed`: a field missing, empty, too long (counted in characters, as
    the reference counts them) or not one the NA takes; for an observation,
    no change time or window, both, or a window that ends before it starts;
  - `*_secret_material`: secret material in the metadata, the parameters, the
    attributes, the outcome detail, the justification or the source's own
    strings, or more than 16 KiB of them (`EvidenceMetadata.ObservationProblem`,
    `BreakGlassProblem`, `MetadataProblem`).

  A value the NA could not read throws `StrictJsonException`.
- An observation's ID is derived from the observer, the source and the source
  event (`ObservationRecorder.ObservationId`), so one source event is one
  observation: recorded again, it is a `duplicate`, and both submissions are
  safe to retry. `SubmitObservations` sends up to 100 at once and answers each
  by its index. A record refused there is `refused` with its code, and the
  others are still admitted.
- The NA keeps an authentic record it refuses for good (a retired key, or one
  outside its key's role or prefix) as a quarantine entry, and one outside its
  time bounds as `quarantined`.
- `JudgeObservation` and `JudgeBreakGlass` judge a record that was not judged
  at admission (standard tier); a second call returns the same judgement
  (`existing`). `ResourceChanges` reads a page of a resource's changes (read
  tier); `AllResourceChanges` follows the pages.
- Operator-key holders: `OperatorHolders` reads them (read tier). A key is its
  own holder until a holder change names one. `ProposeHolder` proposes one,
  and `ApproveHolder`, with a privileged key of another named holder, records
  it (both privileged; the same holder gets 409
  `holder_change_needs_second_holder`).

#### The record outbox

A record outbox keeps signed observations and break-glass records (from 1.9.0
also remediation records and grant uses) until the NA admits them, as the
outbox keeps execution records:

```csharp
using var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl = "https://na.example",
    RecordOutbox = new FileRecordOutbox("/var/lib/portal/records"),  // not the outbox's directory
});

var delivery = await client.EvidenceStore.EnqueueRecord(observation);  // or another signed record
// delivery.Submission when the NA admitted it; otherwise delivery.Queued, pending or a dead letter

var flushed = await client.EvidenceStore.FlushRecords();  // at startup and on a timer
// flushed.Admitted (of which flushed.Quarantined), flushed.Pending, flushed.DeadLettered
```

- `EnqueueRecord` stores the record, then submits it. A failed submission does
  not throw: a transient failure leaves the record pending, with a backoff,
  and a refusal no retry overcomes (`EvidenceOutbox.RecordPermanentRefusals`)
  keeps it as a dead letter with the NA's code and, for a record the NA kept,
  its quarantine entry (`LastError.QuarantineId`). An observer key the NA does
  not know yet is retried. While another call is submitting the same record,
  `EnqueueRecord` returns it as `Queued` without sending it, or, until that
  call has stored it, waits for that call.
- `FlushRecords` sends break-glass records first, so an observation of the same
  change finds its break-glass record, then grant uses (a grant's in their
  chain's order), then observations in batches of up to 100, then remediation
  records, each in the order added. A batch the NA refuses whole, such as one
  holding a record it cannot read, is split until the record it refuses is
  tried alone. A batch it throttles (429) halves the client's batches from
  then on. A transient answer that names a wait (`Retry-After`, such as a 429
  or a 503) holds every record back that long, at least 5 seconds and at most
  15 minutes; `EnqueueRecord` still submits its own record. A transient
  failure ends the run; the next one goes on from there. A record another call
  is submitting is left to it, and so is one another call stored a new state
  for since the run read the outbox.
- A record is removed only once the NA says it holds it: `recorded`,
  `duplicate` or `quarantined`. An empty answer, such as a proxy's 204, or a
  success with another status, leaves the record pending
  (`response_body_unreadable`), and so does a batch answer that does not give
  each observation of the batch one result, by its index. The NA answers a
  resubmission of a record it holds with a `duplicate`.
- `FileRecordOutbox` writes the format of the TypeScript and Rust SDKs
  (`gm.evidence.record-outbox.v1`), and keeps each record as it was read, so a
  controller can move between them, whichever SDK kept a record.
  `EvidenceOutbox.RecordEntry` makes an entry
  for your own `IOutbox<RecordOutboxEntry>`, and
  `MemoryOutbox<RecordOutboxEntry>` is for tests.

#### Remediation and reviews

From 1.9 the SDK has the NA's rules for what a judged DENY leads to (1.4.0 of
the protocol), as pure functions in `RemediationRules`. They are offline and
take the reference's codes:
- the NA states what is to be done and by when;
- the controller does it and records it in a remediation record it signs;
- a second holder may review the change, in a review record the NA signs.

```csharp
// What a denied change leads to, from the judgement's policies and the record of the change.
var plan = RemediationRules.Plan(judgement.Verdict, policies, gateEvaluations,
    RemediationRules.FactsOf(ChangeKinds.Observation, observationPayload));
var due = RemediationRules.DueAt(judgement.JudgedAt, plan.GraceSeconds);

// Once the plan is carried out, the record of what was done, signed by the executor key.
var rotated = plan.Steps == RemediationSteps.RotateForwardThenDisable;   // rotated under a governed decision, then disabled
var record = await executor.SignRemediation(new RemediationInput
{
    SubjectKind = RemediationSubjectKinds.Judgement, SubjectId = judgement.JudgementId,
    ChangeKind = ChangeKinds.Observation, ChangeId = observation.ObservationId, ResourceId = observation.ResourceId,
    Steps = plan.Steps,
    RotationEvidenceId = rotated ? rotation.EvidenceId : null,
    VersionId = rotated ? newVersionId : null,
    DisabledVersionId = plan.Steps == RemediationSteps.None ? null : deniedVersionId,
});
var mismatch = RemediationRules.StepsProblem(plan.Mode, plan.Steps, record);   // null: it carries out the plan

// A review the NA signed, and whether its embedded requests prove it.
var verified = OfflineVerifier.VerifyReviewRecord(reviewJson, naKeyHistory);
var problem = RemediationRules.ReviewApprovalProblem(review, naPublicKeys);
```

- **The state table:** `NextState` gives the state after an event, or the
  NA's refusal (`review_not_pending`, `review_already_pending`,
  `remediation_not_due` and the others). A pending review is a flag beside
  the state; it stops no clock. `JudgementState` says where a judged change
  enters the table.
- **Plans:**
  - `Guidance` takes the guidance of each failing enforce-mode gate, then a
    policy's `"*"`, then the NA's defaults. `cancel` wins over `alert_only`,
    and the shortest grace of the winning mode applies.
  - `Plan` turns a `cancel` into steps: a created resource is disabled; a
    rotated or updated one with a version is rotated forward, then the denied
    version disabled. Otherwise the plan falls back to an alert
    (`RemediationFallbacks`). An indeterminate judgement always alerts.
  - `DecisionPlan` does the same for a decision a review denied.
- **Due times:** `DueAt`, `DueAfterDenial` (a review never extends a
  remediation) and `DueAfterWaiver`, printed as the reference prints a time.
- **Remediation records:** `ExecutionRecorder.SignRemediation` refuses what
  the NA would refuse before anything is signed (`OutOfBandRecordException`):
  - `remediation_malformed`: a field missing, empty or too long, or steps that
    do not fit the outcome and versions. An alert changes nothing; a forward
    rotation names its evidence, the version it issued and another one it
    disabled.
  - `remediation_secret_material`: secret material in a version or the detail.

  `RecordProblem` checks a received record as the NA admits one: its exact
  form, then its signature against the registered key as `ListExecutorKeys`
  returns it, the key's role, retirement and prefix, and secret material.
  `OfflineVerifier.VerifyRemediationRecord` checks a record's form and
  signature alone.
- **Reviews:**
  - `OfflineVerifier.VerifyReviewRecord` checks a review's form and the NA's
    signature, under its key history at `decided_at`.
  - `ReviewApprovalProblem` checks the operators' signed requests the review
    embeds, offline: the decider signed exactly the verdict, waiver end and
    note it records, for this NA, within 300 seconds of the decision, and so
    did a requester for the subject and justification. It reads the review's
    recorded times first, each a UTC timestamp in the form the NA writes
    (`Z`, `+00:00` or `-00:00`).
  - `ReviewHolderProblem` and `ReviewRequestProblem` say whether a second
    holder decided it, and whether the change's actor asked for it.
  - `Rfc3339` is the one grammar of the timestamps a review embeds.
- **Policies:** `PolicySectionIssues` checks a policy's 1.4.0 sections
  (remediation guidance, the emergency capability) as the NA does.
  `EmergencyMap` maps each emergency capability to the one it stands for.
- **Where this SDK differs:**
  - `Rfc3339` returns null for a time that falls outside the years 1 to 9999
    in UTC. TypeScript prints one, and the reference cannot convert one to
    UTC.
  - A due time refuses a date that does not exist, such as February 29 of a
    common year, as the reference does. TypeScript's date reading moves it to
    the next month.
  - `ReviewApprovalProblem` refuses a review whose recorded time is not a
    UTC timestamp; TypeScript skips the 300-second bound for a time it cannot
    read. The reference's model also reads spellings the NA never writes (a
    space, a lower-case `t`, `+0000`); this SDK does not.
  - `PolicySectionIssues` refuses a `stands_for` with a `*` anywhere, or a
    blank one, as the reference does; TypeScript checks only for a final
    `.*`.
  - A guidance entry's grace may be written as `600.0`, as the reference
    reads it; an entry its model would not take gives no guidance.

With the NA's remediation records on (`EVIDENCE_REMEDIATION=on`, which needs
its out-of-band records; otherwise 404 `remediation_disabled`, or
`out_of_band_disabled` with those off too), the store has the calls:

```csharp
// The due queue: the open changes, oldest first. Only a due or overdue one takes a remediation record;
// a waived one, or one only under review, does not yet.
await foreach (var due in client.EvidenceStore.AllRemediations())
{
    if (due.State is not (RemediationStates.RemediationDue or RemediationStates.Overdue)) continue;
    if (due.NotifiedAt is null) await client.EvidenceStore.MarkNotified(due.SubjectId, "email");   // before the due time
    if (due.Steps != RemediationSteps.Disable) continue;                // none alerts; a forward rotation also names its rotation
    // ... disable the denied version, then record it:
    var record = await executor.SignRemediation(RemediationInput.For(due, due.Steps) with { DisabledVersionId = due.ChangeVersionId });
    await client.EvidenceStore.EnqueueRecord(record);                   // or SubmitRemediation, without a record outbox
}

// A second holder's review: one holder asks, another decides.
var requested = await requester.EvidenceStore.RequestReview(new ReviewRequest
{
    SubjectKind = RemediationSubjectKinds.Judgement, SubjectId = judgementId, Justification = "Planned with the vendor",
});
var decided = await decider.EvidenceStore.DecideReview(requested.ReviewId!, new ReviewDecision { Verdict = ReviewVerdicts.Deny });
var proved = RemediationRules.ReviewApprovalProblem(decided.Review.Payload, [naPublicKey]) is null;
```

- **The due queue:** a remediation record is taken only from a change that is
  `remediation_due` or `overdue`: one sent for any other state is refused for
  good (`remediation_not_due`). `Remediations` reads a page (read tier) and
  `AllRemediations` follows the pages. Without a filter they list the open
  changes: due, overdue and waived, and those with a pending review that are
  not remediated. A filter takes one of `RemediationStates.Kept`, or
  `review_pending`. `Remediation` reads one subject's state with its reviews
  and stored records.
- **Notices:** `MarkNotified` (standard tier) records an audit event, and the
  first notice's time and key. It is not retried: each call is another
  notice.
- **Reviews:**
  - `RequestReview` (standard tier) needs a named holder for the client's
    operator key, and the requester may not be the change's actor. The NA
    embeds the signed request in the review.
  - `DecideReview` (privileged tier) is for another holder than the
    requester's and the actor's. A `ReviewDecision.WaiveUntil` is given exactly
    for a waiver.
  - `Review` reads a review, with its record once decided.
  - Neither a request nor a decision is retried.
- **Remediation records:**
  - `SubmitRemediation` is public (the executor key's signature authenticates
    it) and safe to retry.
  - The NA admits a forward rotation's record once it holds the rotation's
    execution evidence. `EnqueueRecord` keeps the record in the record outbox,
    so flush the evidence outbox first.
  - `RemediationInput.For` takes the subject, change and resource from a
    state; a decision never executed names itself as its change.

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

#### Break-glass when the NA cannot be reached

With `BreakGlassOptions`, a governed action still runs when the NA cannot be
reached, as the TypeScript SDK's `governedAction` with `breakGlass` does. The
action gets a null decision, and a break-glass record of it, signed by the
executor key with your justification, goes to the record outbox. The NA judges
it once it is back, as the evaluation would have gone:

```csharp
using var client = new GenesisMeshClient(new ClientOptions
{
    BaseUrl = naUrl,
    Signer = new SeedSigner(operatorSeed, "portal-controller"),
    Outbox = new FileOutbox("/var/lib/portal/outbox"),
    RecordOutbox = new FileRecordOutbox("/var/lib/portal/records"),   // required for break-glass
});

var outcome = await Governance.GovernedAction(client, executor, request,
    new BreakGlassOptions { Justification = "INC-4711: the leaked key must be rotated now" },
    async (decision, ct) =>
    {
        // decision is null under break-glass: the NA could not be asked
        var credential = await RotateInGraph(appId, ct);
        return new ActionReport<string>
        {
            Value = credential.SecretText,
            ExecutionParameters = new Dictionary<string, object?> { ["key_id"] = credential.KeyId },
        };
    });
if (outcome.BrokeGlass)
    Alert(outcome.BreakGlass.Failure, outcome.BreakGlass.Queued);   // network_error, timeout, server_error or rate_limited
else if (!outcome.Governed.Authorized)
    Log(outcome.Governed.Summary.EnforcedFailures);
```

- **Only on an outage.** The glass breaks when the evaluation gets no answer,
  times out, or is answered with a 5xx or 429 whose body arrived, whatever it
  holds, so a proxy's error page breaks it too (`Governance.EvaluationFailure`
  names which). A denial, a decision that fails verification, a refusal, a
  success that is not strict JSON, an answer whose body is cut off or stalls,
  whatever its status (the NA may have decided), and the NA's
  `admin_auth_throttled` and `evidence_store_unavailable` never break it;
  cancelling never does either. The lookup of the NA's key before the
  evaluation counts by its status, even when its body cannot be read: a 5xx or
  429 breaks the glass, a 403 or 404 does not, and a success is no answer, as
  it carries no decision.
- **Checked first.** It needs a record outbox that can be read
  (`record_outbox_required`, `outbox_file_unreadable`), a resource, an attestation basis (the NA could not judge an agreement it does
  not see) and a requested capability. The context and justification are
  checked before anything is evaluated: no secret material, a justification of
  1 to 1024 characters, and 2048 bytes of the metadata limit left for the
  action's report (`break_glass_malformed`, `break_glass_secret_material`).
- **Always recorded once it ran.** A report the secret guard refuses is
  recorded without the refused fields (`BreakGlass.Dropped`), or with the
  outcome alone when the rest is still refused; an outcome detail is cut to
  1024 characters. When the action throws, a `failure` record is kept and the
  error rethrown. A record the outbox cannot keep throws
  `EvidenceNotKeptException` with the action's value and the signed record
  (`BreakGlassRecord`): keep or submit it once you can, and do not run the
  action again.
- `BreakGlass.Submission` is set when the NA was back in time to admit the
  record. Otherwise `BreakGlass.Queued` is its record outbox entry: pending,
  which `FlushRecords` submits, break-glass records first, or a dead letter
  when the NA refused it for good, which no flush sends again.
- **Under the NA's decision**, the record is kept as without break-glass. With
  an outbox (`ClientOptions.Outbox`) a refused report throws
  `MetadataRefusedException`; without one the record is submitted at once, so
  an exception from signing or submitting it (`SecretMaterialException`,
  `StrictJsonException`, `NetworkException` or the NA's refusal) comes after
  the action ran: do not run it again.

#### Cancelling a denied change, and urgent changes

From 1.9 two helpers build on governed actions for the NA's remediation
records (1.4.0 of the protocol), as TypeScript's do.

`CancelByRotatingForward` cancels a denied change to a versioned resource
others read, when the NA's plan is `rotate_forward_then_disable`. A credential
is never rolled back:

```csharp
var result = await Governance.CancelByRotatingForward(client, executor, new RotateForwardOptions<string>
{
    Remediation = due,                                   // from the due queue: remediation_due or overdue
    Rotation = new GovernedActionRequest
    {
        RequestedCapability = "partner-secret.rotate", AttestationId = attestation.AttestationId,
        Verify = new GovernedVerification { OperatorPublicKeys = [naKey], ExpectedPolicies = policies, ExpectedAttestation = attestation },
    },
    Rotate = async (decision, ct) => new RotatedVersion<string> { VersionId = await IssueNewVersion(ct) },  // readers move to it
    Disable = (version, ct) => DisableVersion(version, ct),
});
// result.Rotation: the governed rotation; result.Remediation: the record naming it and the disabled version.
```

1. A governed rotation issues a new version under a decision of its own. Its
   execution evidence names the version (`execution_parameters.version_id`),
   so observers match it instead of judging it again. A DENY returns without
   changing anything.
2. `Disable` then disables the denied version: the readers already have the
   new one.
3. The remediation record names the rotation's evidence, the new version and
   the disabled one. It is kept in the record outbox, or submitted without
   one. The NA admits it once it holds the rotation's evidence, so flush the
   evidence outbox first.

The state, the plan and the denied version are checked before anything runs:
- a state that takes no record throws `TransitionRefusedException`
  (`remediation_not_due`, `remediation_already_recorded`);
- a plan that is no forward rotation is `remediation_steps_mismatch`.

The denied version is the one the change made (`ChangeVersionId`, the
default): the NA refuses a record that disables another. The record is checked
before the denied version is disabled, and a rotation whose evidence the NA
refused for good is never followed by the disable, so a disable is never left
without a record the NA will take.

Once the rotation's evidence is in the NA or the evidence outbox,
`RemediationNotRecordedException` says what to do next:
- `Disabled` false: rotate again when the rotation cannot be cited (its
  evidence refused for good, or the denied version issued again), or disable
  and record when the disable failed;
- `Disabled` true: submit its `Record` once the cause is fixed, or sign it
  again when there is none.

A rotation the NA refuses only at a later flush (its evidence a dead letter
then) leaves the record pending for good, with the denied version disabled:
remove the record from the record outbox and cancel again, with a `Disable`
that accepts a version already disabled. Read the state right before
cancelling: for a decision the NA plans from its latest execution.

A failure of the rotation's own governed action after it ran throws as
`GovernedAction` does, before anything is disabled.

`UrgentAction` makes a change that is never blocked and always governed and
judged:

```csharp
var urgent = await Governance.UrgentAction(client, executor, request, new EmergencyOptions
{
    Capability = "partner-secret.emergency-rotate",      // the one capability an emergency policy selects
    Justification = "incident 42: rotate the leaked key now",
    ExpectedPolicies = emergencyPolicies,
}, (decision, ct) => Rotate(decision, ct));
// urgent.Path: ordinary, emergency or break_glass; urgent.Outcome: the change.
```

- **The ordinary capability** is evaluated first; an ALLOW runs the action
  under it.
- **On a DENY,** the emergency capability is requested, with the
  justification in its justification parameter. The NA decides under the
  emergency policy, and reviews the change after it.
- **When the ordinary evaluation gets no answer,** times out or is answered
  with a 5xx or a 429, the emergency capability is evaluated next. When that
  fails so too, the action runs under break-glass naming the emergency
  capability, and the NA judges it once it is back.

Break-glass needs a record outbox, an attestation basis and a resource: all of
it is checked before anything is evaluated.

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

#### Reading and verifying attestations

From 1.9 the client reads attestations, the recognition policy and the NA's
revocation feed, and asks the NA to verify an attestation:

```csharp
var record = await client.Attestation.Get(att.AttestationId);    // record.Status is what holds now
var list = await admin.Attestation.List(new ListAttestationsParams { SubjectId = "node-xyz" });
// list.Attestations, for a signed request; unsigned, list.Count only

var policy = await client.Attestation.GetPolicy();                 // the NA's active recognition policy
var check = await client.Attestation.Verify(new VerifyAttestationRequest
{
    Attestation = record.Attestation,
    RecognitionPolicy = policy,                                    // null for the NA's active policy
});
// check.Accepted, check.Reason: invalid_signature, unknown_issuer, role_not_allowed, locally_revoked, ...

var feed = await client.Attestation.RevocationFeed();              // the NA's signed feed of revoked attestations
// feed.RevokedAttestationIds, feed.RevocationReasons
```

- `Get`, `GetPolicy`, `Verify` and `RevocationFeed` need no key. The signed
  body of an attestation keeps the status it was issued with:
  `AttestationRecord.Status` is what holds now, with `RevokedAt` and, from NA
  1.8.0, the copy it re-issues or that re-issued it (`ReissuedFrom`,
  `SupersededBy`).
- `List` signs the request when the client has a key (read tier or above) and
  gets the attestations. Without a key it gets only the count, and the NA
  refuses a filter by subject or issuer (401 `admin_auth_failed`): it would
  tell who is a member. A signing key needs its `KeyId`, or `List` throws.
  Neither leaves in copies another one re-issued.
- A `RecognizedIssuer` accepts `active` attestations unless told otherwise, as
  the NA reads an issuer that names no statuses.
- A typed attestation or policy passed back keeps what was signed and stored,
  so `Verify` reads it as the NA does.

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

// 1.9.0: observations, break-glass records, judgements, quarantine and registry records.
bool observed = OfflineVerifier.VerifyOutOfBandRecord(observationJson, new[] { observerPublicKey });
bool judged = OfflineVerifier.VerifyOutOfBandRecord(judgementJson, history);   // the key valid at judged_at
string signedForm = OfflineVerifier.OutOfBandCanonical(recordJson);            // OutOfBandDigest: its SHA-256

// 1.9.0: an NA record's signature alone, under the NA's keys or its key history.
bool decided = OfflineVerifier.VerifyDecisionSignature(decisionJson, new[] { naPublicKey });
bool published = OfflineVerifier.VerifyPolicySignature(policyJson, history);
bool issued = OfflineVerifier.VerifyAttestationSignature(attestationJson, history, DateTimeOffset.UtcNow);
bool recognised = OfflineVerifier.VerifyTreatySignature(treatyJson, history);
bool revoking = OfflineVerifier.VerifyRevocationFeedSignature(feedJson, history);
string? signer = OfflineVerifier.RevocationFeedSigner(feedJson, history);  // the key valid at issued_at that signed it
```

The signature verifiers are the TypeScript SDK's `verifyDecisionSignature`,
`verifyPolicySignature`, `verifyAttestationSignature`,
`verifyTreatySignature`, `verifyRevocationFeedSignature` and
`revocationFeedSigner`:
- **What they check:** a signature over the record as received. The record
  must have no field this SDK does not know and be in canonical form as this
  SDK checks it: its timestamps as the reference writes them, and every field
  the reference always writes. The TypeScript SDK checks unknown fields only,
  and not for a treaty.
- **With a key history:** the key valid at the record's time
  (`NAKeyHistory.RecordTimeFields`; a revocation feed's `issued_at`).
- **With a time to judge at:** an attestation, a treaty or a revocation feed
  must still vouch then ([the vouching horizon](#the-vouching-horizon-180)).
- **For the reason:** `VerifyBoundaryDecision` also checks a decision's
  expiry and bindings, and `NARecordRefusal` gives the reference's reason
  for an attestation, a treaty or a feed.
- **What no SDK checks:** the reference also holds the whole record to its
  model, and to what the model writes back. An NA-signed record that only
  that refuses still verifies here, for example one with:
  - an invalid `status`;
  - an integer written as `1.0` or `"1"` (TypeScript refuses `1.0`, since it
    writes the number back as `1`);
  - a timestamp written as a number;
  - a revocation feed's attestation IDs out of order.
- **Typed records:** a record this SDK read into its model, written back
  with `Auth.SerializerOptions`, keeps the form the NA signed.

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
  - every signature:
    - the NA's on decisions, justification proofs, retention checkpoints,
      judgements, quarantine, registry and review records, under its keys or
      the key valid at the time (with a key history);
    - the executor's on execution evidence, break-glass and remediation
      records, and the observer's on observations, each a registered key of
      the record's sovereign with the role its kind needs;
  - each execution record against its decision (`evidence_decision_denied`,
    `evidence_outside_decision_window`, `evidence_capability_mismatch`), and
    never on a judgement or a change record (`evidence_cites_judgement`);
  - the decision, resource and observation chains (`observation_chain_break`);
  - each judgement against its subject as stored, once per subject
    (`judgement_subject_mismatch`, `judgement_subject_missing`,
    `duplicate_judgement`), and each matched record used once and held before
    it (`match_reused`, `judgement_match_missing`);
  - a quarantine entry's record against its digest
    (`quarantine_digest_mismatch`), and each envelope against its record
    (`envelope_mismatch`);
  - each remediation record once per judgement or decision
    (`duplicate_remediation`), of the change, resource and version its
    subject names (`remediation_subject_mismatch`), held after its subject
    (`remediation_subject_missing`), and its forward rotation: held before it
    (`remediation_rotation_missing`), of the same resource
    (`remediation_rotation_mismatch`), by the record's own executor
    (`remediation_rotation_other_executor`), after the change
    (`remediation_rotation_before_change`) and cited once
    (`remediation_rotation_reused`);
  - each review: proved by the requests it embeds
    (`review_approval_invalid`), signed with the public key and holder the
    holder registry recorded for each operator key, when the run holds a
    holder record for that key (`review_approval_key_mismatch`), decided by a second holder
    (`review_same_holder`), of its subject as stored
    (`review_subject_mismatch`, `review_subject_missing`), and no waiver
    after a remediation (`review_waiver_after_remediation`).

  A bad entry is a failure, never an exception.
- It verifies the kinds a governed store holds (decisions, justification
  proofs, execution evidence, retention checkpoints), the records of changes
  outside the governed path (observations, break-glass records, judgements,
  quarantine and registry records) and remediation and review records.
  Entries of the 1.5.0 and later kinds (key successions, archives, grants)
  fail as `unknown_entry_kind`; their envelopes still chain. Verify a store
  holding them with the TypeScript or Rust SDK, or `genesis-mesh audit
  verify`. Anchors are not checked, nor where the store placed an executor
  key.
- For an export that starts after a retention, pass `Checkpoint`, the
  NA-signed checkpoint as received: the run continues from its last removed
  entry, and each resource and observation chain from its head.
- It runs the shared `field_registry` suite's `export` vectors and the
  `out_of_band` and `remediation` suites' `verify_export` vectors, and gives
  the reference's own verification of its export.

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
// g.Valid; otherwise g.Reason: "missing_signature", "malformed" (1.9.0), "invalid_signature",
// "unknown_field", "non_canonical_form" or "na_key_not_valid_at_time".
g = OfflineVerifier.VerifyCapabilityGrant(grantJson, h.History!, DateTimeOffset.UtcNow);
// 1.8.0: also "outlives_key" once the grant's key has passed its vouching horizon.
```

From 1.9.0 a grant must also be one the reference's model takes
(`malformed` otherwise): a validity of at most 24 hours, starting within 24
hours of its issue, each envelope parameter readable. A list of NA keys may
stand for the key history (`VerifyCapabilityGrant(grantJson, [naKey])`).

The NA's revocation feed lists revoked grants in `revoked_grant_ids` while
grants are on (left out while they are off); the embedded field registry knows
both forms.

From 1.9.0, the grants' records and rules, as the TypeScript SDK's
`grants.ts`:

- **Records:** `CapabilityGrant`, `TimeToken`, `TimeTokenRequest`,
  `GrantExecutionEvidence` (a use) and `GrantRevocationRecord`; the limits
  (`GrantLimits`) and the codes the NA refuses a use with, in its order
  (`GrantUseRejectionCodes`).
- **Numbers:** `GrantNumber` reads a number as every implementation does: an
  integral number is an integer (`5.0` is `5`), integers fit 64 bits, and
  comparisons are exact. Scalars compare type-strictly (`true` is not `1`).
- **The envelope:** `GrantRules.Parameter` reads an envelope parameter
  (`one_of`, `subset_of` or `number_range`) or names why the NA would refuse
  it; `EnvelopeProblem` says why a use's `request_parameters` fall outside a
  grant's closed envelope.
- **Uses:** `GrantRules.UseProblem` gives the first rule a use breaks, in the
  reference's order (capability, envelope, budget, the time token, order,
  validity, upload time, revocation); `ContinuesChain`, `NextPreviousUse`,
  `UseSecretProblem`, and the digests uses chain by (`GrantDigest`,
  `UseDigest`, `RecordDigest`).
- **Verifiers:** `OfflineVerifier.VerifyTimeToken` and
  `VerifyGrantRevocation` (NA keys or a key history), `VerifyGrantUse` and
  `VerifyTimeTokenRequest` (the bearer key), and `TokenSignatureValid`.
- **Signing:** `GrantRecords.SignUse` and `SignTimeTokenRequest` sign with the
  bearer key, refused first as the NA would refuse them
  (`GrantRecordException`: `grant_use_malformed`,
  `grant_use_secret_material`, `time_token_request_malformed`).

```csharp
var use = await GrantRecords.SignUse(new GrantUseInput
{
    Grant = grant,
    UseSequence = 1,                    // the first use; later ones give Previous
    Capability = "secret.rotate",
    RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 },
    TimeToken = token,                  // the latest time token, as received
    Outcome = GrantUseOutcomes.Success,
}, bearerSigner);
var problem = GrantRules.UseProblem(use, new UseCheck { Grant = grant, TokenKeys = [naKey] });
```

From 1.9.0 the client also calls the NA's grant routes (`client.Grants`):

```csharp
var issued = await ops.Grants.IssueGrant(new IssueGrantRequest
{
    BearerKeyId = "device-key",                 // a registered executor key
    Capabilities = ["secret.rotate"],
    AttestationId = attestation.AttestationId,  // or an Agreement the NA trusts
    ValidUntil = DateTimeOffset.UtcNow.AddHours(4),
    MaxUses = 3,
    ParameterEnvelope = new Dictionary<string, EnvelopeParameter>
    {
        ["lifetime_days"] = new(true, null, null,
            new NumericRange("integer", GrantNumber.Of(1), GrantNumber.Of(60), false, false)),
    },
});
// issued.Grant; issued.TimeToken, as the NA signed it, for the first use

// The holder signs with the bearer key; its client needs no operator key.
var submitted = await device.Grants.SubmitGrantUse(use);  // or device.EvidenceStore.EnqueueRecord(use)
var request = await GrantRecords.SignTimeTokenRequest(new TimeTokenRequestInput
{
    GrantId = issued.Grant.GrantId,
    UseSequence = 1,                            // the uses made, and the last one's digest
    UseHeadDigest = GrantRules.UseDigest(use),
}, bearerSigner);
var token = await device.Grants.RequestTimeToken(request);  // for the next use
```

- `Grants` and `AllGrants`, `Grant`, and `GrantUses` and `AllGrantUses` read
  grants and a grant's use chain (read tier); `IssueGrant` and `RevokeGrant`
  need the privileged tier.
- The NA refuses a grant an applied policy could deny (`ValidationException`,
  `grant_gate_may_deny`), and a time token for a grant that can no longer be
  used (`ConflictException`: `grant_expired`, `grant_revoked`,
  `grant_budget_exhausted` or `grant_bearer_key_retired`).
- An authentic use it refuses for good is kept as a quarantine entry
  (`Details.quarantine_id`), and keeps its place in the chain unless it was
  refused as a conflict or a chain mismatch. In the record outbox it is a dead
  letter naming that entry. A use behind one the NA refused before it took its
  place can never be placed: the record outbox makes it a dead letter too
  (`evidence_predecessor_dead_lettered`), and sends a grant's uses in their
  chain's order.

#### Holding a grant (1.9.0)

A `GrantHolder` acts within a grant while the NA cannot be reached, as the
TypeScript SDK's does, and keeps each use in the record outbox until the NA is
back:

```csharp
using var device = new GenesisMeshClient(new ClientOptions
{
    BaseUrl = "https://na.example",
    SigningKey = operatorSeed, KeyId = "ops",   // evaluates when the holder falls back
    RecordOutbox = new FileRecordOutbox("/var/lib/site/records"),
});
var holder = new GrantHolder(new GrantHolderOptions
{
    Client = device,
    Signer = bearerSigner,                      // the grant's bearer key
    NAKeys = naKeyHistory,                      // or NAKeyHistory.ValidAlways([naKey])
    Grant = issued.Grant, TimeToken = issued.TimeToken,  // or State, to resume
    Persist = (state, ct) => SaveAsync(state, ct),        // after every change
});

var result = await holder.Act(new GrantActRequest
{
    Capability = "secret.rotate",
    RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 },
    ResourceId = "kv:site-7/api-key", ResourceAction = ResourceActions.Rotate,
    Fallback = new GrantFallback { Justification = "the site is cut off", Verify = verification },
}, async (authority, ct) =>
{
    // authority.Path: "grant", or "decision" / "break_glass" when the grant could not be used
    return new ActionReport<string> { ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v9" } };
});
// result.UnderGrant: the signed use, submitted or queued; result.Fallback: why the grant could not be used, and what ran

var synced = await holder.Sync();  // on reconnect: the revocation feed, a fresh time token, the record outbox
```

- Before each change the holder checks the grant offline, in the NA's order:
  secret material, the bearer key's resource prefix (`ResourcePrefix`), the
  capability, the closed envelope, the budget, the validity, the revocation
  feed it holds and what the NA last answered, and the uses its time token has
  left (asking for a fresh one when the NA answers). A request outside the
  grant throws `GrantRefusedException`, and nothing runs.
- Every use takes the next place in the grant's chain, and is never dated
  before its time token or the previous use. One the NA quarantines after it
  continued the chain keeps its place, and stops counting against the budget,
  which counts admitted uses; one refused as a conflict, a chain mismatch or
  over budget still counts.
- When the grant has ended, its budget or time token is spent, or it is
  revoked, the holder falls back to a governed action with break-glass: an NA
  that answers decides, and a DENY is never broken through; one out of reach
  leaves the change to break-glass, recorded and judged after the fact.
  Without a fallback it throws `GrantUnusableException`.
- Its calls run one at a time (do not call it from inside an action), and its
  state changes whole. `GrantHolderState` is the JSON the TypeScript SDK's
  holder keeps; on its first change the holder takes up a later use of its
  grant the record outbox holds, kept before a stop left the state unpersisted.
- `Sync` reports a time token the NA refuses (`TimeTokenRefusal`, such as a
  clock more than 300 seconds off the NA's) and still uploads the outbox.

The SDK runs the shared `grants` suite's signed-form, signature, envelope,
envelope parameter and use vectors
(`tests/GenesisMesh.Sdk.Tests/testdata/conformance/grants.json`), and 1,510
further cases the Python reference judged
(`tests/GenesisMesh.Sdk.Tests/testdata/grants/rule_cases.json`).

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
SDK's own guard for contexts, and the NA's rule for evidence. An
`OutOfBandRecordException` (1.9.0) is thrown before an observation or a
break-glass record is signed when the NA would refuse it, with the NA's code
(`observation_malformed`, `break_glass_secret_material`, ...). Both have
`Status` 0.

A connection failure or timeout is a `NetworkException`. Its `ConnectFailed`
is true when the request did not reach the NA, so sending it again cannot
apply it twice. From 1.9 its `Code` is `network_error`, or
`response_body_unreadable` when the NA answered but the body was cut off,
stalled past the request timeout, which covers the whole answer, or was larger
than your `HttpClient`'s `MaxResponseContentBufferSize`: the request reached
the NA and may have been applied. A 2xx response that
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
