using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Sdk.Tests;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Pre-issued grants against a running NA with its grants on (1.9.0): an envelope a policy could deny is not granted;
/// a grant is issued over an attestation and verifies offline with its first time token; a use signed with the bearer
/// key is admitted at its resource position, once; a fresh time token names the chain's head; a second use, checked
/// offline as the NA checks it, goes through the record outbox; the chain reads newest first; a revoked grant gets no
/// time token and its later use is quarantined. Each typed result is compared with the JSON the NA sent, at every depth.
/// </summary>
[Trait("Category", "Live")]
public class GrantsLiveTests
{
    private sealed class Recorder : DelegatingHandler
    {
        public JsonNode? Json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            try { Json = JsonNode.Parse(text); } catch (JsonException) { Json = null; }
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static (string Seed, string PublicKey) NewKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    private static Dictionary<string, object?> D(params (string, object?)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task AGrantIsIssuedUsedAndRevoked()
    {
        using var na = await LiveNa.Start("--out-of-band", "on", "--grants", "on");
        if (na is null) return;
        var naKey = na.NaPublicKey;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        using var ops = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = na.BaseUrl,
            SigningKey = (string)na.Inputs["seed"]!,
            KeyId = "ops",
            HttpHandler = recorder,
        });
        void Check(string name, object typed) => Check2(name, recorder.Json, typed.GetType());
        static void Check2(string name, JsonNode? raw, Type type)
        {
            var problems = ContractTypesTests.Unmodeled(raw ?? throw new InvalidOperationException(name + ": no JSON response"), type, name);
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }

        // The bearer key: a registered executor key of the device that holds the grant.
        var (bearerSeed, bearerKey) = NewKey();
        await ops.EvidenceStore.RegisterExecutorKey(new RegisterExecutorKeyRequest
        {
            KeyId = "device-key",
            PublicKey = bearerKey,
            ExecutorSovereignId = "device-a",
        });
        var bearer = new SeedSigner(bearerSeed, "device-key");

        // What the grant rests on: an attestation naming the capability, and a policy bounding its parameter.
        var attestation = await ops.Attestation.Issue(D(("subject_id", "vendor-grant"), ("roles", new[] { "role:client" }),
            ("claims", D(("capabilities", new[] { "secret.rotate" }))), ("validity_hours", 24)));
        var now = DateTimeOffset.UtcNow;
        var policy = await ops.Policy.Publish(new BoundaryPolicyIntent
        {
            PolicyId = "grant-limits",
            ValidFrom = Canonical.Timestamp(now.AddDays(-1)),
            ValidUntil = Canonical.Timestamp(now.AddDays(1)),
            Selector = new PolicySelector { Capabilities = ["secret.rotate"] },
            Gates =
            [
                new GateSpec
                {
                    GateId = "lifetime", GateType = "max_value.v1", Order = 0,
                    Config = new Dictionary<string, object?> { ["path"] = "request_parameters.lifetime_days", ["max"] = 90 },
                },
            ],
        });
        Assert.True((await ops.Policy.Activate(policy.PolicyId, policy.Version)).Active);

        IssueGrantRequest Request(long maximum) => new()
        {
            BearerKeyId = "device-key",
            Capabilities = ["secret.rotate"],
            AttestationId = attestation.AttestationId,
            ValidUntil = DateTimeOffset.UtcNow.AddHours(1),
            MaxUses = 3,
            MaxUsesPerTimeToken = 2,
            ParameterEnvelope = new Dictionary<string, EnvelopeParameter>
            {
                ["lifetime_days"] = new(true, null, null, new NumericRange("integer", GrantNumber.Of(1), GrantNumber.Of(maximum), false, false)),
            },
        };

        // An envelope the policy could deny is not granted.
        Assert.Equal("grant_gate_may_deny", (await Assert.ThrowsAsync<ValidationException>(() => ops.Grants.IssueGrant(Request(400)))).Code);

        var issued = await ops.Grants.IssueGrant(Request(60));
        Check("grant_issue", issued);
        var grantJson = recorder.Json!["grant"]!.ToJsonString();
        var grantId = issued.Grant.GrantId;
        Assert.Equal("accepted", OfflineVerifier.VerifyCapabilityGrant(grantJson, [naKey]).Reason);
        Assert.True(OfflineVerifier.VerifyTimeToken(issued.TimeToken.GetRawText(), [naKey]));
        Assert.Equal(GrantRules.RecordDigest("CapabilityGrant", grantJson), GrantRules.GrantDigest(issued.Grant));
        Assert.Equal(policy.PolicyId, issued.Grant.PolicyBinding.GetProperty("policies")[0].GetProperty("policy_id").GetString());
        Assert.Null(GrantRules.EnvelopeShapeProblem(issued.Grant.ParameterEnvelope));

        var grant = await ops.Grants.Grant(grantId);
        Check("grant_get", grant);
        Assert.Equal((GrantStatuses.Active, 3, 0, "device-a"), (grant.Status, grant.RemainingUses, grant.ChainLength, grant.BearerSovereignId));
        Assert.Equal([(policy.PolicyId, policy.Version)], grant.PolicyVersions.Select(p => (p.PolicyId, p.Version)));
        var listed = await ops.Grants.Grants(new GrantListParams { BearerKeyId = "device-key" });
        Check("grant_list", listed);
        Assert.Equal(grantId, Assert.Single(listed.Grants).GrantId);

        // The device's client needs no operator key: the bearer key's signatures authenticate its calls.
        using var device = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = na.BaseUrl,
            HttpHandler = recorder,
            RecordOutbox = new MemoryOutbox<RecordOutboxEntry>(),
        });
        GrantUseInput Use(int sequence, JsonElement token, GrantExecutionEvidence? previous) => new()
        {
            Grant = issued.Grant,
            UseSequence = sequence,
            Previous = previous,
            Capability = "secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 },
            ResourceId = "kv:grant/api-key",
            ResourceAction = ResourceActions.Rotate,
            TimeToken = token,
            Outcome = GrantUseOutcomes.Success,
            ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v" + sequence },
        };

        // The first use, signed with the bearer key: the NA gives it its place in the resource's chain, once.
        var first = await GrantRecords.SignUse(Use(1, issued.TimeToken, null), bearer);
        var submitted = await device.Grants.SubmitGrantUse(first);
        Check("grant_use_submit", submitted);
        Assert.Equal(("recorded", 1L), (submitted.Status, submitted.ResourceSequence));
        // Sent again: a duplicate, whose position is its entry's.
        var duplicate = await device.Grants.SubmitGrantUse(first);
        Assert.Equal(("duplicate", (long?)null, (long?)1), (duplicate.Status, duplicate.ResourceSequence, duplicate.Entry.ResourceSequence));
        // A use of a grant the NA does not hold: refused (422), not a missing route.
        var stray = JsonSerializer.Deserialize<CapabilityGrant>(JsonSerializer.Serialize(issued.Grant))!;
        stray.GrantId = "no-such-grant";
        var unknown = await Assert.ThrowsAsync<ValidationException>(async () =>
            await device.Grants.SubmitGrantUse(await GrantRecords.SignUse(Use(1, issued.TimeToken, null) with { Grant = stray }, bearer)));
        Assert.Equal((422, "grant_not_found"), (unknown.Status, unknown.Code));

        // A fresh time token, naming the chain's head.
        var request = await GrantRecords.SignTimeTokenRequest(
            new TimeTokenRequestInput { GrantId = grantId, UseSequence = 1, UseHeadDigest = GrantRules.UseDigest(first) }, bearer);
        var token = await device.Grants.RequestTimeToken(request);
        Check2("time_token", recorder.Json?["time_token"], typeof(TimeToken));
        Assert.True(OfflineVerifier.VerifyTimeToken(token.GetRawText(), [naKey]));
        Assert.Equal((1, GrantRules.UseDigest(first)), (token.GetProperty("use_sequence").GetInt32(), token.GetProperty("use_head_digest").GetString()));

        // The second use, checked offline as the NA checks it, through the record outbox.
        var second = await GrantRecords.SignUse(Use(2, token, first), bearer);
        Assert.Null(GrantRules.UseProblem(second, new UseCheck
        {
            Grant = issued.Grant,
            Previous = GrantRules.NextPreviousUse(first),
            Admitted = 1,
            TokenHead = new TokenHead(GrantRules.UseDigest(first), first.ExecutedAt),
            TokenKeys = [naKey],
        }));
        var delivered = await device.EvidenceStore.EnqueueRecord(second);
        Assert.Equal(("recorded", 2L), (delivered.Submission!.Status, delivered.Submission.ResourceSequence));

        // The chain, newest first, a page at a time.
        var chain = await ops.Grants.GrantUses(grantId, new GrantUsesParams { Limit = 1 });
        Check("grant_uses", chain);
        Assert.Equal([(second.UseId, "admitted", (long?)2)], chain.Uses.Select(u => (u.UseId, u.State, u.ResourceSequence)));
        Assert.Equal(2, chain.NextBefore);
        var positions = new List<int>();
        await foreach (var use in ops.Grants.AllGrantUses(grantId, 1)) positions.Add(use.UseSequence);
        Assert.Equal([2, 1], positions);

        // Revoked: no time token, and a later use is refused and kept as a quarantine entry in the chain.
        var revoked = await ops.Grants.RevokeGrant(grantId, "the device was lost");
        Check("grant_revoke", revoked);
        Assert.True(OfflineVerifier.VerifyGrantRevocation(recorder.Json!["revocation"]!.ToJsonString(), [naKey]));
        Assert.Equal(("operator", "the device was lost"), (revoked.Revocation.Reason, revoked.Revocation.Detail));
        Assert.Equal("grant_already_revoked", (await Assert.ThrowsAsync<ConflictException>(() => ops.Grants.RevokeGrant(grantId))).Code);
        var ended = await GrantRecords.SignTimeTokenRequest(
            new TimeTokenRequestInput { GrantId = grantId, UseSequence = 2, UseHeadDigest = GrantRules.UseDigest(second) }, bearer);
        Assert.Equal("grant_revoked", (await Assert.ThrowsAsync<ConflictException>(() => device.Grants.RequestTimeToken(ended))).Code);
        var third = await GrantRecords.SignUse(Use(3, token, second), bearer);
        var refused = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => device.Grants.SubmitGrantUse(third));
        Assert.Equal("grant_revoked", refused.Code);
        Assert.Equal(JsonValueKind.String, refused.Details.GetProperty("quarantine_id").ValueKind);
        var after = await ops.Grants.Grant(grantId);
        Assert.Equal((GrantStatuses.Revoked, 2, 3), (after.Status, after.Uses, after.ChainLength));
        Assert.Equal(GrantRevocationReasons.Operator, after.Revocation!.Reason);
        var latest = (await ops.Grants.GrantUses(grantId, new GrantUsesParams { Limit = 1 })).Uses[0];
        Assert.Equal((third.UseId, "quarantined", "grant_revoked"), (latest.UseId, latest.State, latest.RejectionCode));
        Assert.Contains((await ops.Grants.Grants(new GrantListParams { Status = GrantStatuses.Revoked })).Grants, g => g.GrantId == grantId);

        // The whole store verifies offline: the grant, its admitted uses, the quarantined one in its chain, the revocation.
        await ExportVerifiesOffline(ops, naKey, grants: 1, uses: 2, revocations: 1);
    }

    /// <summary>
    /// The NA's whole export verifies offline (1.9.0), as the NA verifies it: every entry, the grants, their uses and
    /// revocations with them, and as many of each kind.
    /// </summary>
    internal static async Task ExportVerifiesOffline(GenesisMeshClient ops, string naKey, long grants, long uses, long revocations)
    {
        var events = new List<EvidenceEvent>();
        await foreach (var e in ops.EvidenceStore.ExportAll()) events.Add(e);
        var server = await ops.EvidenceStore.Verify();
        Assert.True(server.Verified);
        var offline = OfflineVerifier.VerifyEvidenceEvents(events, new EvidenceVerifyOptions
        {
            NAPublicKeys = [naKey],
            ExecutorKeys = await ops.EvidenceStore.ListExecutorKeys(),
        });
        Assert.True(offline.Verified, string.Join("; ", offline.Failures.Select(f => $"{f.StoreSequence} {f.Reason} {f.Detail}")));
        Assert.Equal((server.CheckedEntries, server.Grants, server.GrantExecutions, server.GrantRevocations, server.Quarantined, server.BreakGlass),
            (offline.CheckedEntries, offline.Grants, offline.GrantExecutions, offline.GrantRevocations, offline.Quarantined, offline.BreakGlass));
        Assert.Equal((grants, uses, revocations), (offline.Grants, offline.GrantExecutions, offline.GrantRevocations));
    }
}
