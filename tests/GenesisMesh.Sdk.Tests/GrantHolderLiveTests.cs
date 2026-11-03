using System.Text.Json;
using GenesisMesh;
using GenesisMesh.Sdk.Tests;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// The holder of a pre-issued grant against a running NA with its grants on (1.9.0), as the TypeScript SDK's end-to-end
/// test runs it: with the NA out of reach, two rotations within the grant, each kept in the record outbox; back, a
/// fresh time token and every use admitted in order; a stricter policy revokes the grant, so an NA that answers denies
/// (never broken through) and one out of reach leaves the change to break-glass; a short grant used past its end falls
/// back to break-glass too. The NA is "out of reach" through the device client's handler, which refuses to connect.
/// </summary>
[Trait("Category", "Live")]
public class GrantHolderLiveTests
{
    /// <summary>A handler that, while <see cref="Down"/>, fails every request as an NA out of reach does.</summary>
    private sealed class Switch : DelegatingHandler
    {
        public volatile bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Down ? throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused") : base.SendAsync(request, ct);
    }

    private static (string Seed, string PublicKey) NewKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    private static Dictionary<string, object?> D(params (string, object?)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task AHolderActsWithinAGrantOfflineAndFallsBackPastIt()
    {
        using var na = await LiveNa.Start("--out-of-band", "on", "--grants", "on");
        if (na is null) return;
        var naKey = na.NaPublicKey;
        var seed = (string)na.Inputs["seed"]!;
        using var ops = new GenesisMeshClient(new ClientOptions { BaseUrl = na.BaseUrl, SigningKey = seed, KeyId = "ops" });
        const string capability = "secret.rotate";
        const string resource = "kv:grant-site/api-key";

        var (bearerSeed, bearerKey) = NewKey();
        await ops.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest { KeyId = "device-key", PublicKey = bearerKey, ExecutorSovereignId = "site-a" });
        var bearer = new SeedSigner(bearerSeed, "device-key");
        var attestation = await ops.Attestation.Issue(D(("subject_id", "vendor-holder"), ("roles", new[] { "role:client" }),
            ("claims", D(("capabilities", new[] { capability }))), ("validity_hours", 24)));
        var now = DateTimeOffset.UtcNow;
        BoundaryPolicyIntent Intent(int max) => new()
        {
            PolicyId = "holder-limits",
            ValidFrom = Canonical.Timestamp(now.AddDays(-1)),
            ValidUntil = Canonical.Timestamp(now.AddDays(1)),
            Selector = new PolicySelector { Capabilities = [capability] },
            Gates =
            [
                new GateSpec
                {
                    GateId = "lifetime", GateType = "max_value.v1", Order = 0,
                    Config = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = max },
                },
            ],
        };
        var policy = await ops.Policy.Publish(Intent(90));
        await ops.Policy.Activate(policy.PolicyId, policy.Version);
        IssueGrantRequest Request(DateTimeOffset until, int maxUses, long maximum) => new()
        {
            BearerKeyId = "device-key",
            Capabilities = [capability],
            AttestationId = attestation.AttestationId,
            ValidUntil = until,
            MaxUses = maxUses,
            MaxUsesPerTimeToken = 2,
            ParameterEnvelope = new Dictionary<string, EnvelopeParameter>
            {
                ["lifetime_days"] = new(true, null, null, new NumericRange("integer", GrantNumber.Of(1), GrantNumber.Of(maximum), false, false)),
            },
        };
        var issued = await ops.Grants.IssueGrant(Request(DateTimeOffset.UtcNow.AddMinutes(30), 3, 60));

        // The device: its client evaluates with the operator key when the holder falls back, and keeps its records.
        var network = new Switch { InnerHandler = new HttpClientHandler() };
        GenesisMeshClient Device() => new(new ClientOptions
        {
            BaseUrl = na.BaseUrl,
            SigningKey = seed,
            KeyId = "ops",
            HttpHandler = network,
            Retry = new RetrySettings { Attempts = 0 },
            RecordOutbox = new MemoryOutbox<RecordOutboxEntry>(),
        });
        using var device = Device();
        var holder = new GrantHolder(new GrantHolderOptions
        {
            Client = device,
            Signer = bearer,
            NAKeys = NAKeyHistory.ValidAlways([naKey]),
            Grant = issued.Grant,
            TimeToken = issued.TimeToken,
        });
        GrantActRequest Rotate(int lifetime, BoundaryPolicy expected) => new()
        {
            Capability = capability,
            RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = lifetime },
            ResourceId = resource,
            ResourceAction = ResourceActions.Rotate,
            Fallback = new GrantFallback
            {
                Justification = "the site is cut off and the key must rotate",
                Verify = new GovernedVerification { OperatorPublicKeys = [naKey], ExpectedPolicies = [expected], ExpectedAttestation = attestation },
            },
        };
        Func<GrantAuthority, CancellationToken, Task<ActionReport<string>?>> Version(string version, List<string> paths) => (authority, _) =>
        {
            paths.Add(authority.Path);
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string> { ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = version } });
        };

        // The NA out of reach: two rotations within the grant, each kept in the record outbox.
        network.Down = true;
        var paths = new List<string>();
        var uses = new List<GrantUseResult<string>>();
        foreach (var version in new[] { "v8", "v9" })
        {
            var result = (await holder.Act(Rotate(30, policy), Version(version, paths))).UnderGrant!;
            Assert.Equal((OutboxStates.Pending, RecordOutboxKinds.GrantExecution), (result.Queued!.State, result.Queued.Kind));
            uses.Add(result);
        }
        Assert.Equal([GrantPaths.Grant, GrantPaths.Grant], paths);
        Assert.True(OfflineVerifier.VerifyGrantUse(JsonSerializer.Serialize(uses[1].Use, Auth.SerializerOptions), bearerKey));

        // Back: a fresh time token naming the chain's head, and every use admitted in order.
        network.Down = false;
        var synced = await holder.Sync();
        Assert.True(synced.Reachable);
        Assert.Null(synced.Unusable);
        Assert.Equal(2, synced.TimeToken!.Value.GetProperty("use_sequence").GetInt32());
        Assert.Equal(uses.Select(u => u.Use.UseId), synced.Flushed!.Admitted.Select(e => e.Id));
        Assert.Empty(synced.Flushed.DeadLettered);
        Assert.Empty(await device.EvidenceStore.RecordOutbox!.List());
        var grant = await ops.Grants.Grant(issued.Grant.GrantId);
        Assert.Equal((2, 1, 2), (grant.Uses, grant.RemainingUses, grant.ChainLength));
        Assert.Equal((2L, uses[1].Use.UseId), ((await ops.EvidenceStore.StoredResourceHead(resource)).ResourceSequence, (await ops.EvidenceStore.StoredResourceHead(resource)).RecordId));

        // A stricter version of the policy replaces the one the grant is bound to: the NA revokes it, and its feed says so.
        var stricter = await ops.Policy.Publish(Intent(20));
        await ops.Policy.Activate(stricter.PolicyId, stricter.Version);
        Assert.Equal(GrantStatuses.Revoked, (await ops.Grants.Grant(issued.Grant.GrantId)).Status);
        Assert.True(await holder.RefreshRevocationFeed());
        Assert.Contains(issued.Grant.GrantId, holder.State.RevocationFeed!.RevokedGrantIds!);

        // Revoked, the NA up: it decides, and its DENY is never broken through.
        paths.Clear();
        var denied = (await holder.Act(Rotate(30, stricter), Version("v10", paths))).Fallback!;
        Assert.Equal((GrantPaths.Decision, GrantFallbackCodes.Revoked, false), (denied.Path, denied.Unusable, denied.Outcome.Governed!.Authorized));
        Assert.Empty(paths);
        // Revoked, the NA out of reach: break-glass naming the capability, recorded and judged afterwards.
        network.Down = true;
        var broke = (await holder.Act(Rotate(10, stricter), Version("v10", paths))).Fallback!;
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.Revoked), (broke.Path, broke.Unusable));
        Assert.Equal((capability, resource), (broke.Outcome.BreakGlass!.Record.Capability, broke.Outcome.BreakGlass.Record.ResourceId));
        Assert.Equal([GrantPaths.BreakGlass], paths);
        network.Down = false;
        Assert.Equal([RecordOutboxKinds.BreakGlass], (await holder.Sync()).Flushed!.Admitted.Select(e => e.Kind));

        // A short grant under the stricter policy, used past its end with the NA out of reach: break-glass again.
        var shortGrant = await ops.Grants.IssueGrant(Request(DateTimeOffset.UtcNow.AddSeconds(3), 1, 20));
        using var second = Device();
        var shortHolder = new GrantHolder(new GrantHolderOptions
        {
            Client = second,
            Signer = bearer,
            NAKeys = NAKeyHistory.ValidAlways([naKey]),
            Grant = shortGrant.Grant,
            TimeToken = shortGrant.TimeToken,
        });
        var ends = OfflineVerifier.ParseTimestamp(shortGrant.Grant.ValidUntil);
        await Task.Delay(ends - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500) is { Ticks: > 0 } wait ? wait : TimeSpan.Zero);
        network.Down = true;
        var expired = (await shortHolder.Act(Rotate(10, stricter), Version("v11", paths))).Fallback!;
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.Expired, OutboxStates.Pending), (expired.Path, expired.Unusable, expired.Outcome.BreakGlass!.Queued!.State));
        network.Down = false;
        Assert.Equal([RecordOutboxKinds.BreakGlass], (await shortHolder.Sync()).Flushed!.Admitted.Select(e => e.Kind));
        var changes = (await ops.EvidenceStore.ResourceChanges(resource)).Changes.Select(c => (c.Kind, c.GovernedBy));
        Assert.Equal([("grant_execution", "grant"), ("grant_execution", "grant"), ("break_glass", "after_the_fact"), ("break_glass", "after_the_fact")], changes);
    }
}
