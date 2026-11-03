using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using GenesisMesh.Tests;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The holder of a pre-issued grant (1.9.0), as the TypeScript SDK's <c>grant-holder.test.ts</c> checks it: acting within
/// the grant with the NA stopped, the checks before a change, time tokens, the revocation feed, the fallback to
/// break-glass, quarantined uses and the budget, persisting and resuming, and one change at a time.
/// </summary>
public class GrantHolderTests
{
    private static (string Seed, string Public, Key Key) NewKey()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)), key);
    }

    private static readonly (string Seed, string Public, Key Key) NAKey = NewKey();
    private static readonly (string Seed, string Public, Key Key) BearerKey = NewKey();
    private static readonly SeedSigner NASigner = new(NAKey.Seed, "na-key");
    private static readonly SeedSigner Bearer = new(BearerKey.Seed, "device-key");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static string At(double seconds) => Canonical.Timestamp(Start.AddSeconds(seconds));

    private static JsonElement NASigned(JsonObject record)
    {
        record.Remove("signature");
        var unsigned = JsonSerializer.SerializeToElement(record);
        record["signature"] = JsonNode.Parse(OutOfBandRecords.Sign(unsigned, NASigner, CancellationToken.None).GetAwaiter().GetResult().GetRawText());
        return JsonSerializer.SerializeToElement(record);
    }

    private static HttpResponseMessage Reply(int status, JsonNode value) =>
        new((HttpStatusCode)status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Refusal(int status, string code, JsonNode? details = null) =>
        Reply(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "refused", ["details"] = details } });

    /// <summary>
    /// A Network Authority in memory: it issues time tokens for requests the bearer key signs, admits uses and
    /// break-glass records, and serves its revocation feed. <see cref="Offline"/> makes every request fail as a stopped
    /// NA does.
    /// </summary>
    private sealed class FakeNA
    {
        public bool Offline;
        public DateTimeOffset Clock = Start.AddSeconds(600);
        public List<JsonNode> Uses { get; } = [];
        public List<JsonNode> BreakGlass { get; } = [];
        public string? TokenRefusal;
        public int TokenRefusalStatus = 409;
        /// <summary>Refuse the next use for good: the NA quarantines it.</summary>
        public string? QuarantineNext;
        public List<string> RevokedGrants = [];
        public long FeedSequence = 1;
        public List<string> Requests { get; } = [];

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add($"{request.Method.Method} {path}");
            if (Offline) throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
            var body = request.Content is null ? null : JsonNode.Parse(request.Content.ReadAsStringAsync().Result);
            switch (path)
            {
                case "/grants/time-tokens":
                    {
                        var asked = body!["request"]!;
                        if (!OfflineVerifier.VerifyTimeTokenRequest(asked.ToJsonString(), BearerKey.Public))
                            return Refusal(422, "time_token_request_invalid_signature");
                        if (TokenRefusal is { } refused) return Refusal(TokenRefusalStatus, refused);
                        var token = new JsonObject
                        {
                            ["time_token_id"] = $"token-{asked["use_sequence"]}-{Clock.ToUnixTimeSeconds()}",
                            ["grant_id"] = asked["grant_id"]!.GetValue<string>(),
                            ["use_sequence"] = asked["use_sequence"]!.GetValue<int>(),
                        };
                        if (asked["use_head_digest"] is { } head) token["use_head_digest"] = head.GetValue<string>();
                        token["issued_at"] = Canonical.Timestamp(Clock);
                        token["issuer_sovereign_id"] = "sovereign-c";
                        token["issued_by"] = "na-key";
                        return Reply(201, new JsonObject { ["time_token"] = JsonNode.Parse(NASigned(token).GetRawText()) });
                    }
                case "/evidence/grant-executions":
                    if (QuarantineNext is { } code)
                    {
                        QuarantineNext = null;
                        return Refusal(422, code, new JsonObject { ["quarantine_id"] = "q-1" });
                    }
                    Uses.Add(body!["evidence"]!.DeepClone());
                    return Reply(201, new JsonObject
                    {
                        ["status"] = "recorded",
                        ["entry"] = new JsonObject(),
                        ["entry_digest"] = "d",
                        ["payload"] = body["evidence"]!.DeepClone(),
                        ["resource_sequence"] = Uses.Count,
                    });
                case "/evidence/break-glass":
                    BreakGlass.Add(body!["record"]!.DeepClone());
                    return Reply(201, new JsonObject { ["status"] = "recorded", ["entry"] = new JsonObject(), ["entry_digest"] = "d", ["payload"] = body["record"]!.DeepClone() });
                case "/sovereign-revocation-feed":
                    {
                        var feed = new JsonObject
                        {
                            ["feed_id"] = $"feed-{FeedSequence}",
                            ["issuer_sovereign_id"] = "sovereign-c",
                            ["sequence"] = FeedSequence,
                            ["issued_at"] = Canonical.Timestamp(Clock),
                            ["revoked_attestation_ids"] = new JsonArray(),
                            ["revocation_reasons"] = new JsonObject(),
                            ["issued_by"] = "na-key",
                            ["revoked_grant_ids"] = new JsonArray([.. RevokedGrants.Select(g => (JsonNode)g)]),
                        };
                        return Reply(200, SignedFeed(feed));
                    }
                default:
                    return Refusal(404, "not_found");
            }
        }
    }

    private static JsonObject SignedFeed(JsonObject feed)
    {
        feed.Remove("signatures");
        var body = OfflineVerifier.ReceivedCanonical("SovereignRevocationFeed", JsonSerializer.SerializeToElement(feed));
        feed["signatures"] = new JsonArray(new JsonObject
        {
            ["key_id"] = "na-key",
            ["sig"] = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(NAKey.Key, Encoding.UTF8.GetBytes(body))),
        });
        return feed;
    }

    private sealed class Setup : IDisposable
    {
        public required FakeNA NA { get; init; }
        public required CapabilityGrant Grant { get; init; }
        public required JsonElement TimeToken { get; init; }
        public required GrantHolder Holder { get; init; }
        public required GenesisMeshClient Client { get; init; }
        public required MemoryOutbox<RecordOutboxEntry> Outbox { get; init; }
        public required Action<double> Tick { get; init; }

        public static GrantActRequest Rotate(int lifetime = 30, bool fallback = true) => new()
        {
            Capability = "secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = lifetime },
            ResourceId = "kv:site-7/api-key",
            ResourceAction = ResourceActions.Rotate,
            Fallback = fallback
                ? new GrantFallback
                {
                    Justification = "the device is cut off and the key must rotate",
                    Verify = new GovernedVerification { OperatorPublicKeys = [NAKey.Public], ExpectedPolicies = [] },
                }
                : null,
        };

        public void Dispose() => Client.Dispose();
    }

    private static JsonObject GrantRecord(Action<JsonObject>? change = null)
    {
        var grant = new JsonObject
        {
            ["grant_id"] = "grant-1",
            ["bearer_key_id"] = "device-key",
            ["bearer_public_key"] = BearerKey.Public,
            ["bearer_sovereign_id"] = "site-7",
            ["capabilities"] = new JsonArray("secret.rotate"),
            ["basis_kind"] = "attestation",
            ["basis_id"] = "attestation-1",
            ["basis_digest"] = new string('e', 64),
            ["requester_sovereign_id"] = "vendor",
            ["provider_sovereign_id"] = "sovereign-c",
            ["valid_from"] = At(0),
            ["valid_until"] = At(4 * 3600),
            ["max_uses"] = 3,
            ["max_uses_per_time_token"] = 2,
            ["parameter_envelope"] = new JsonObject
            {
                ["lifetime_days"] = new JsonObject
                {
                    ["required"] = true,
                    ["one_of"] = null,
                    ["subset_of"] = null,
                    ["number_range"] = new JsonObject
                    {
                        ["number_type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = 60,
                        ["exclusive_minimum"] = false,
                        ["exclusive_maximum"] = false,
                    },
                },
            },
            ["basis_checks"] = new JsonArray(),
            ["policy_binding"] = new JsonObject
            {
                ["policies"] = new JsonArray(),
                ["policy_set_digest"] = new string('f', 64),
                ["gate_outcomes"] = new JsonArray(),
                ["registry_gate_types"] = new JsonArray(),
            },
            ["issued_at"] = At(0),
            ["issuer_sovereign_id"] = "sovereign-c",
            ["issued_by"] = "na-key",
        };
        change?.Invoke(grant);
        return grant;
    }

    private static Setup Make(Action<JsonObject>? change = null, Func<GrantHolderState, CancellationToken, Task>? persist = null, string? resourcePrefix = null)
    {
        var na = new FakeNA();
        var grant = NASigned(GrantRecord(change)).Deserialize<CapabilityGrant>()!;
        var token = NASigned(new JsonObject
        {
            ["time_token_id"] = "token-0",
            ["grant_id"] = "grant-1",
            ["use_sequence"] = 0,
            ["issued_at"] = At(0),
            ["issuer_sovereign_id"] = "sovereign-c",
            ["issued_by"] = "na-key",
        });
        var outbox = new MemoryOutbox<RecordOutboxEntry>();
        var client = Client(na, outbox);
        var clock = Start.AddSeconds(600);
        var holder = new GrantHolder(new GrantHolderOptions
        {
            Client = client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]),
            Grant = grant,
            TimeToken = token,
            Now = () => clock,
            Persist = persist,
            ResourcePrefix = resourcePrefix,
        });
        return new Setup
        {
            NA = na,
            Grant = grant,
            TimeToken = token,
            Holder = holder,
            Client = client,
            Outbox = outbox,
            Tick = seconds => { clock = clock.AddSeconds(seconds); na.Clock = clock; },
        };
    }

    private static GenesisMeshClient Client(FakeNA na, IOutbox<RecordOutboxEntry>? outbox) => new(new ClientOptions
    {
        BaseUrl = "http://na",
        Audience = "TEST",
        SigningKey = TestHelpers.ZeroSeedB64,
        KeyId = "ops",
        Retry = new RetrySettings { Attempts = 0, BaseDelay = TimeSpan.Zero },
        RecordOutbox = outbox,
        HttpHandler = new FuncHandler(na.Answer),
    });

    private static Func<GrantAuthority, CancellationToken, Task<ActionReport<string>?>> Report(Dictionary<string, object?>? parameters = null, string? value = null) =>
        (_, _) => Task.FromResult<ActionReport<string>?>(new ActionReport<string> { Value = value, ExecutionParameters = parameters });

    private static async Task<GrantUseResult<string>> Use(Setup x, GrantActRequest? request = null) =>
        (await x.Holder.Act(request ?? Setup.Rotate(), Report())).UnderGrant!;

    private static async Task<GrantFallbackResult<string>> Fallback(Setup x, GrantActRequest? request = null) =>
        (await x.Holder.Act(request ?? Setup.Rotate(), Report())).Fallback!;

    // ── Acting within the grant ────────────────────────────────────────────

    [Fact]
    public async Task ActsWithTheNAStopped_KeepsEachUse_AndUploadsThemInOrderOnReconnect()
    {
        using var x = Make();
        x.NA.Offline = true;
        GrantAuthority? seen = null;
        var first = (await x.Holder.Act(Setup.Rotate(), (authority, _) =>
        {
            seen = authority;
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string> { Value = "rotated", ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v8" } });
        })).UnderGrant!;
        Assert.Equal((GrantPaths.Grant, "grant-1", 1), (seen!.Path, seen.Grant!.GrantId, seen.UseSequence));
        Assert.Equal(("rotated", OutboxStates.Pending, RecordOutboxKinds.GrantExecution), (first.Value, first.Queued!.State, first.Queued.Kind));
        Assert.True(OfflineVerifier.VerifyGrantUse(JsonSerializer.Serialize(first.Use, Auth.SerializerOptions), BearerKey.Public));
        Assert.Equal((1, "site-7", x.TimeToken.GetRawText()), (first.Use.UseSequence, first.Use.ExecutorSovereignId, first.Use.TimeToken.GetRawText()));
        x.Tick(60);
        var second = await Use(x, Setup.Rotate(45));
        Assert.Equal((2, GrantRules.UseDigest(first.Use)), (second.Use.UseSequence, second.Use.PrevUseDigest));
        Assert.Equal((2, GrantRules.UseDigest(second.Use)), (x.Holder.State.Uses, x.Holder.State.HeadDigest));
        Assert.Empty(x.NA.Uses);

        x.NA.Offline = false;
        var synced = await x.Holder.Sync();
        Assert.True(synced.Reachable);
        Assert.Null(synced.Unusable);
        Assert.Equal((2, GrantRules.UseDigest(second.Use)), (synced.TimeToken!.Value.GetProperty("use_sequence").GetInt32(), synced.TimeToken.Value.GetProperty("use_head_digest").GetString()));
        Assert.Equal([first.Use.UseId, second.Use.UseId], synced.Flushed!.Admitted.Select(e => e.Id));
        Assert.Equal([first.Use.UseId, second.Use.UseId], x.NA.Uses.Select(u => u["use_id"]!.GetValue<string>()));
        Assert.Empty(await x.Outbox.List());
        // The next use cites the fresh token.
        var third = await Use(x);
        Assert.Equal(2, third.Use.TimeToken.GetProperty("use_sequence").GetInt32());
        Assert.Equal(3L, third.Submission!.ResourceSequence);
    }

    [Fact]
    public async Task ARequestOutsideTheGrantIsRefused_RunningNothing()
    {
        using var x = Make();
        var ran = false;
        Task<ActionReport<string>?> Action(GrantAuthority _, CancellationToken __) { ran = true; return Task.FromResult<ActionReport<string>?>(null); }
        Assert.Equal("grant_capability_mismatch", (await Assert.ThrowsAsync<GrantRefusedException>(() =>
            x.Holder.Act(new GrantActRequest { Capability = "secret.delete", RequestParameters = Setup.Rotate().RequestParameters, ResourceId = "kv:site-7/api-key", ResourceAction = "rotate" }, Action))).Code);
        Assert.Equal("grant_envelope_mismatch", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(Setup.Rotate(400), Action))).Code);
        Assert.Equal("grant_envelope_mismatch", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(new GrantActRequest
        {
            Capability = "secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30, ["extra"] = 1 },
        }, Action))).Code);
        Assert.Contains("go together", (await Assert.ThrowsAsync<ArgumentException>(() => x.Holder.Act(new GrantActRequest
        {
            Capability = "secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["lifetime_days"] = 30 },
            ResourceId = "r",
        }, Action))).Message, StringComparison.Ordinal);
        Assert.False(ran);
        Assert.Equal(0, x.Holder.State.Uses);
    }

    [Fact]
    public async Task AFailedActionIsRecordedAsAFailureUse_AndRethrown()
    {
        using var x = Make();
        x.NA.Offline = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => x.Holder.Act<string>(Setup.Rotate(), (_, _) => throw new InvalidOperationException("vault down")));
        var entry = Assert.Single(await x.Outbox.List());
        Assert.Equal(("failure", "action failed", 1), (entry.Record.GetProperty("outcome").GetString(), entry.Record.GetProperty("outcome_detail").GetString(),
            entry.Record.GetProperty("use_sequence").GetInt32()));
        Assert.Equal(1, x.Holder.State.Uses);
    }

    [Fact]
    public async Task TheOutcomeIsRecordedWithoutMetadataTheSecretGuardRefuses()
    {
        using var x = Make();
        x.NA.Offline = true;
        var result = (await x.Holder.Act(Setup.Rotate(), Report(new Dictionary<string, object?> { ["version_id"] = "v1", ["client_secret"] = "no" }))).UnderGrant!;
        Assert.Equal(["client_secret"], result.Dropped);
        Assert.Equal("""{"version_id":"v1"}""", result.Use.ExecutionParameters.GetRawText());
    }

    [Fact]
    public async Task RequestParametersCarryingSecretMaterialAreRefusedBeforeActing()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln";
        using var x = Make(g => g["parameter_envelope"] = new JsonObject
        {
            ["token"] = new JsonObject { ["required"] = true, ["one_of"] = new JsonArray(jwt), ["subset_of"] = null, ["number_range"] = null },
        });
        var ran = false;
        var e = await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(new GrantActRequest
        {
            Capability = "secret.rotate",
            RequestParameters = new Dictionary<string, object?> { ["token"] = jwt },
        }, (_, _) => { ran = true; return Task.FromResult<ActionReport<string>?>(null); }));
        Assert.Equal("grant_use_secret_material", e.Code);
        Assert.False(ran);
    }

    [Fact]
    public async Task AUseIsNeverDatedBeforeItsTimeTokenOrThePreviousUse()
    {
        using var x = Make();
        x.Tick(-900); // the holder's clock is 5 minutes behind the token
        x.NA.Offline = true;
        var result = await Use(x);
        Assert.Equal(x.TimeToken.GetProperty("issued_at").GetString(), result.Use.ExecutedAt);
        var next = await Use(x);
        Assert.Equal(result.Use.ExecutedAt, next.Use.ExecutedAt);
    }

    // ── Time tokens and the fallback ───────────────────────────────────────

    [Fact]
    public async Task AFreshTimeTokenIsAskedForWhenItsUsesAreSpent()
    {
        using var x = Make();
        await Use(x);
        await Use(x);
        // Two uses cite token 0; the third needs a new one, which the NA gives.
        var third = await Use(x);
        Assert.Equal(2, third.Use.TimeToken.GetProperty("use_sequence").GetInt32());
        Assert.Single(x.NA.Requests, r => r == "POST /grants/time-tokens");
    }

    [Fact]
    public async Task ASpentTimeTokenWithTheNADownFallsBackToBreakGlass_NamingTheCapability()
    {
        using var x = Make(g => g["max_uses_per_time_token"] = 1);
        x.NA.Offline = true;
        await Use(x);
        GrantAuthority? seen = null;
        var result = (await x.Holder.Act(Setup.Rotate(), (authority, _) =>
        {
            seen = authority;
            return Task.FromResult<ActionReport<string>?>(new ActionReport<string> { ExecutionParameters = new Dictionary<string, object?> { ["version_id"] = "v2" } });
        })).Fallback!;
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.TimeTokenExhausted, GrantPaths.BreakGlass), (result.Path, result.Unusable, seen!.Path));
        var record = result.Outcome.BreakGlass!.Record;
        Assert.Equal(("secret.rotate", "attestation-1", "site-7", "the device is cut off and the key must rotate"),
            (record.Capability, record.AttestationId, record.ExecutorSovereignId, record.Justification));
        Assert.Equal(1, x.Holder.State.Uses);
    }

    [Fact]
    public async Task PastTheGrantsEndOrItsBudgetItFallsBackToBreakGlass()
    {
        using var expired = Make();
        expired.NA.Offline = true;
        expired.Tick(4 * 3600);
        var late = await Fallback(expired);
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.Expired), (late.Path, late.Unusable));

        using var spent = Make(g => g["max_uses"] = 1);
        spent.NA.Offline = true;
        await Use(spent);
        var over = await Fallback(spent);
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.BudgetExhausted), (over.Path, over.Unusable));
        Assert.Equal([RecordOutboxKinds.GrantExecution, RecordOutboxKinds.BreakGlass], (await spent.Outbox.List()).Select(e => e.Kind));
    }

    [Fact]
    public async Task WithoutAFallbackItThrowsGrantUnusable_AndBeforeTheGrantStartsItRefuses()
    {
        using var early = Make(g => { g["valid_from"] = At(3600); g["valid_until"] = At(7200); });
        Assert.Equal("grant_use_outside_validity", (await Assert.ThrowsAsync<GrantRefusedException>(() => early.Holder.Act(Setup.Rotate(fallback: false), Report()))).Code);
        using var spent = Make(g => g["max_uses"] = 1);
        spent.NA.Offline = true;
        await Use(spent);
        Assert.Equal(GrantFallbackCodes.BudgetExhausted,
            (await Assert.ThrowsAsync<GrantUnusableException>(() => spent.Holder.Act(Setup.Rotate(fallback: false), Report()))).Code);
    }

    [Fact]
    public async Task ARevokedGrantIsRefusedOnceItsFeedListsIt_AndAnNAThatAnswersIsNeverBrokenThrough()
    {
        using var x = Make();
        x.NA.RevokedGrants = ["grant-1"];
        Assert.True(await x.Holder.RefreshRevocationFeed());
        // The NA answers, but not with a decision this holder accepts: nothing runs.
        var ran = false;
        await Assert.ThrowsAnyAsync<GenesisMeshException>(() => x.Holder.Act(Setup.Rotate(), (_, _) => { ran = true; return Task.FromResult<ActionReport<string>?>(null); }));
        Assert.False(ran);
        x.NA.Offline = true;
        var result = await Fallback(x);
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.Revoked), (result.Path, result.Unusable));
    }

    [Fact]
    public async Task TheNewestVerifiedFeedIsKept_AndOneTheNADidNotSignIsRefused()
    {
        using var x = Make();
        x.NA.FeedSequence = 5;
        await x.Holder.RefreshRevocationFeed();
        x.NA.FeedSequence = 4;
        x.NA.RevokedGrants = ["grant-1"];
        Assert.False(await x.Holder.RefreshRevocationFeed());
        Assert.Equal(5, x.Holder.State.RevocationFeed!.Sequence);
        var forged = x.Holder.State.RevocationFeed!;
        forged.Sequence = 9;
        forged.RevokedGrantIds = [];
        Assert.Equal("feed_invalid_signature", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.UpdateRevocationFeed(forged))).Code);
    }

    [Fact]
    public async Task TheNAsAnswerThatTheGrantEndedIsKept_AndFallenBackOnLater()
    {
        using var x = Make();
        x.NA.TokenRefusal = "grant_expired";
        var synced = await x.Holder.Sync();
        Assert.True(synced.Reachable);
        Assert.Equal("grant_expired", synced.Unusable);
        Assert.Equal("grant_expired", x.Holder.State.Unusable);
        x.NA.Offline = true;
        var result = await Fallback(x);
        Assert.Equal((GrantPaths.BreakGlass, GrantFallbackCodes.Expired), (result.Path, result.Unusable));
    }

    [Fact]
    public async Task AnNAAnsweringAnotherEndThanAFallbackCoversIsRefused()
    {
        using var x = Make();
        x.NA.TokenRefusal = "grant_bearer_key_retired";
        Assert.Equal("grant_bearer_key_retired", (await x.Holder.Sync()).Unusable);
        Assert.Equal("grant_bearer_key_retired", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(Setup.Rotate(), Report()))).Code);
    }

    [Fact]
    public async Task AnUnreachableNAIsReportedBySync_WithoutThrowing()
    {
        using var x = Make();
        x.NA.Offline = true;
        var synced = await x.Holder.Sync();
        Assert.Equal((false, (JsonElement?)null, (string?)null, (RecordFlushResult?)null), (synced.Reachable, synced.TimeToken, synced.Unusable, synced.Flushed));
    }

    // ── Persisting and resuming ────────────────────────────────────────────

    [Fact]
    public async Task ItsStateIsPersistedAfterEveryChange_AndResumedFrom()
    {
        var saved = new List<GrantHolderState>();
        using var x = Make(persist: (s, _) => { saved.Add(s); return Task.CompletedTask; });
        x.NA.Offline = true;
        var first = await Use(x);
        Assert.Equal((1, GrantRules.UseDigest(first.Use)), (saved[^1].Uses, saved[^1].HeadDigest));
        // The same JSON as the TypeScript SDK's state, nulls written.
        var json = JsonNode.Parse(JsonSerializer.Serialize(saved[^1], Auth.SerializerOptions))!.AsObject();
        Assert.Equal(["grant", "time_token", "uses", "head_digest", "head_executed_at", "revocation_feed", "unusable"], json.Select(p => p.Key));
        var restored = JsonSerializer.Deserialize<GrantHolderState>(json.ToJsonString(), Auth.SerializerOptions)!;
        var resumed = new GrantHolder(new GrantHolderOptions
        {
            Client = x.Client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]),
            State = restored,
            Now = () => Start.AddSeconds(700),
        });
        var second = (await resumed.Act(Setup.Rotate(), Report())).UnderGrant!;
        Assert.Equal((2, GrantRules.UseDigest(first.Use)), (second.Use.UseSequence, second.Use.PrevUseDigest));
    }

    [Fact]
    public async Task AStateThatCannotBePersistedIsReported_TheUseKept()
    {
        using var x = Make(persist: (_, _) => throw new IOException("disk full"));
        x.NA.Offline = true;
        var e = await Assert.ThrowsAsync<GrantStateNotKeptException>(() => x.Holder.Act(Setup.Rotate(), Report(value: "rotated")));
        Assert.Equal(("grant_state_not_kept", "rotated", 1), (e.Code, e.Value, e.Use.UseSequence));
        Assert.IsType<IOException>(e.InnerException);
        Assert.Single(await x.Outbox.List());
        Assert.Equal(1, x.Holder.State.Uses);
    }

    [Fact]
    public async Task AGrantTokenOrSignerThatIsNotTheNAsOrTheBearersIsRefused()
    {
        using var x = Make();
        GrantHolderOptions Options(ISigner signer, NAKeyHistory? keys = null, JsonElement? token = null, GenesisMeshClient? client = null) => new()
        {
            Client = client ?? x.Client,
            Signer = signer,
            NAKeys = keys ?? NAKeyHistory.ValidAlways([NAKey.Public]),
            Grant = x.Grant,
            TimeToken = token ?? x.TimeToken,
            Now = () => Start.AddSeconds(600),
        };
        Assert.Equal("grant_bearer_mismatch", Assert.Throws<GrantRefusedException>(() => new GrantHolder(Options(new SeedSigner(BearerKey.Seed, "other")))).Code);
        Assert.Contains("not signed by the NA", Assert.Throws<GrantRefusedException>(() => new GrantHolder(Options(Bearer, NAKeyHistory.ValidAlways([BearerKey.Public])))).Message,
            StringComparison.Ordinal);
        var ahead = JsonNode.Parse(x.TimeToken.GetRawText())!.AsObject();
        ahead["use_sequence"] = 1;
        ahead["use_head_digest"] = new string('a', 64);
        Assert.Equal("grant_time_token_invalid", Assert.Throws<GrantRefusedException>(() => new GrantHolder(Options(Bearer, token: NASigned(ahead)))).Code);
        var impostor = new GrantHolder(Options(new SeedSigner(NewKey().Seed, "device-key")));
        Assert.Equal("grant_bearer_mismatch", (await Assert.ThrowsAsync<GrantRefusedException>(() => impostor.Act(Setup.Rotate(), Report()))).Code);
        using var plain = Client(x.NA, null);
        Assert.Equal("record_outbox_required", Assert.Throws<OutboxException>(() => new GrantHolder(Options(Bearer, client: plain))).Code);
        Assert.Throws<ArgumentException>(() => new GrantHolder(new GrantHolderOptions { Client = x.Client, Signer = Bearer, NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]) }));
    }

    // ── Quarantined uses and the budget ────────────────────────────────────

    [Fact]
    public async Task AUseTheNAQuarantinesKeepsItsPlace_TheNextFollowsIt()
    {
        using var x = Make();
        x.NA.QuarantineNext = "grant_use_outside_validity";
        var first = await Use(x);
        Assert.Equal((OutboxStates.DeadLetter, "grant_use_outside_validity"), (first.Queued!.State, first.Queued.LastError!.Code));
        var second = await Use(x);
        Assert.Equal((2, GrantRules.UseDigest(first.Use), "recorded"), (second.Use.UseSequence, second.Use.PrevUseDigest, second.Submission!.Status));
        Assert.Equal(2, x.Holder.State.Uses);
    }

    [Fact]
    public async Task AQuarantinedUseStopsCountingAgainstTheBudget_AsTheNACountsAdmittedUses()
    {
        using var x = Make(); // max_uses 3
        x.NA.Offline = true;
        var first = await Use(x);
        await Use(x);
        x.NA.Offline = false;
        x.NA.QuarantineNext = "grant_use_outside_validity";
        var synced = await x.Holder.Sync();
        var dead = Assert.Single(synced.Flushed!.DeadLettered);
        Assert.Equal((first.Use.UseId, "grant_use_outside_validity", "q-1"), (dead.Id, dead.LastError!.Code, dead.LastError.QuarantineId));
        // Uses 2, 3 and 4 are the three the NA admits: use 1 keeps its place in the chain, not the budget.
        await Use(x);
        var fourth = await Use(x);
        Assert.Equal((4, "recorded"), (fourth.Use.UseSequence, fourth.Submission!.Status));
        Assert.Equal(3, x.NA.Uses.Count);
        x.NA.Offline = true;
        Assert.Equal(GrantFallbackCodes.BudgetExhausted, (await Fallback(x)).Unusable);
    }

    [Fact]
    public async Task AUseQuarantinedAsAConflictStillCounts_ItDidNotContinueTheChain()
    {
        using var x = Make(g => g["max_uses"] = 1);
        x.NA.QuarantineNext = "grant_use_conflict";
        var first = await Use(x);
        Assert.Equal((OutboxStates.DeadLetter, "q-1"), (first.Queued!.State, first.Queued.LastError!.QuarantineId));
        x.NA.Offline = true;
        Assert.Equal(GrantFallbackCodes.BudgetExhausted, (await Fallback(x)).Unusable);
    }

    [Fact]
    public async Task AUseQuarantinedAsOverBudgetStillCounts_SoAShortCountStopsSigning()
    {
        // Budget 2 so the holder signs use 2 at all; the NA, whose view diverged, admits use 1 and quarantines use 2.
        using var x = Make(g => g["max_uses"] = 2);
        await Use(x);
        x.NA.QuarantineNext = "grant_budget_exhausted";
        var second = await Use(x);
        Assert.Equal((OutboxStates.DeadLetter, "grant_budget_exhausted", "q-1"), (second.Queued!.State, second.Queued.LastError!.Code, second.Queued.LastError.QuarantineId));
        x.NA.Offline = true;
        Assert.Equal(GrantFallbackCodes.BudgetExhausted, (await Fallback(x)).Unusable);
        Assert.Single(x.NA.Uses);
    }

    [Fact]
    public async Task AUseOutsideTheBearerKeysResourcePrefixOrWithoutAResourceIsRefusedOffline()
    {
        using var x = Make(resourcePrefix: "kv:site-7/");
        var rotate = Setup.Rotate();
        Assert.Equal("grant_use_out_of_scope", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(new GrantActRequest
        {
            Capability = rotate.Capability,
            RequestParameters = rotate.RequestParameters,
            ResourceId = "kv:site-8/api-key",
            ResourceAction = "rotate",
        }, Report()))).Code);
        Assert.Equal("grant_use_out_of_scope", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(new GrantActRequest
        {
            Capability = rotate.Capability,
            RequestParameters = rotate.RequestParameters,
        }, Report()))).Code);
        Assert.Equal(GrantPaths.Grant, (await x.Holder.Act(rotate, Report())).Path);
    }

    [Fact]
    public async Task TwoUsesRunOneAfterTheOther_NeverAtOnePlaceInTheChain()
    {
        using var x = Make();
        x.NA.Offline = true;
        var results = await Task.WhenAll(x.Holder.Act(Setup.Rotate(), Report()), x.Holder.Act(Setup.Rotate(), Report()));
        Assert.Equal([1, 2], results.Select(r => r.UnderGrant!.Use.UseSequence).Order());
    }

    [Fact]
    public async Task AUseThatCannotBeKeptIsReported_WithTheSignedUse_AndTheChainStaysPut()
    {
        using var x = Make();
        x.NA.Offline = true;
        var failing = new FailingOutbox();
        using var client = Client(x.NA, failing);
        var holder = new GrantHolder(new GrantHolderOptions
        {
            Client = client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]),
            Grant = x.Grant,
            TimeToken = x.TimeToken,
            Now = () => Start.AddSeconds(600),
        });
        var e = await Assert.ThrowsAsync<EvidenceNotKeptException>(() => holder.Act(Setup.Rotate(), Report(value: "rotated")));
        Assert.Equal(("rotated", 1), (e.Value, e.GrantUse!.UseSequence));
        Assert.Equal(0, holder.State.Uses);
        // The outbox back: the next use takes that place.
        failing.Fails = false;
        Assert.Equal(1, (await holder.Act(Setup.Rotate(), Report())).UnderGrant!.Use.UseSequence);
    }

    /// <summary>A record outbox that cannot store while <see cref="Fails"/>.</summary>
    private sealed class FailingOutbox : IOutbox<RecordOutboxEntry>
    {
        private readonly MemoryOutbox<RecordOutboxEntry> _inner = new();
        public bool Fails = true;
        public Task<IReadOnlyList<RecordOutboxEntry>> List(CancellationToken ct = default) => _inner.List(ct);
        public Task Add(RecordOutboxEntry entry, CancellationToken ct = default) => Fails ? throw new IOException("disk full") : _inner.Add(entry, ct);
        public Task Update(RecordOutboxEntry entry, CancellationToken ct = default) => Fails ? throw new IOException("disk full") : _inner.Update(entry, ct);
        public Task Remove(string id, CancellationToken ct = default) => _inner.Remove(id, ct);
    }

    // ── The independent review's cases ─────────────────────────────────────

    [Fact]
    public async Task AFailedActionsUseIsPersisted_AndResumingSignsTheNextPlace()
    {
        var saved = new List<GrantHolderState>();
        using var x = Make(persist: (s, _) => { saved.Add(s); return Task.CompletedTask; });
        x.NA.Offline = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => x.Holder.Act<string>(Setup.Rotate(), (_, _) => throw new InvalidOperationException("vault down")));
        Assert.Equal(1, saved[^1].Uses);
        var resumed = new GrantHolder(new GrantHolderOptions
        {
            Client = x.Client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]),
            State = saved[^1],
            Now = () => Start.AddSeconds(700),
        });
        Assert.Equal(2, (await resumed.Act(Setup.Rotate(), Report())).UnderGrant!.Use.UseSequence);
    }

    [Fact]
    public async Task AFailedActionWhoseStateCannotBePersistedKeepsBothErrors()
    {
        using var x = Make(persist: (_, _) => throw new IOException("disk full"));
        x.NA.Offline = true;
        var e = await Assert.ThrowsAsync<GrantStateNotKeptException>(() =>
            x.Holder.Act<string>(Setup.Rotate(), (_, _) => throw new InvalidOperationException("vault down")));
        Assert.Equal(("vault down", GrantUseOutcomes.Failure), (e.ActionError!.Message, e.Use.Outcome));
        Assert.IsType<IOException>(e.InnerException);
    }

    [Fact]
    public async Task ACallCancelledInItsTurnLeavesTheOthersInOrder()
    {
        using var x = Make();
        x.NA.Offline = true;
        var release = new TaskCompletionSource();
        var first = x.Holder.Act(Setup.Rotate(), async (_, _) =>
        {
            await release.Task;
            return new ActionReport<string>();
        });
        using var cancel = new CancellationTokenSource();
        var cancelled = x.Holder.Act(Setup.Rotate(), Report(), cancel.Token);
        var third = x.Holder.Act(Setup.Rotate(), Report());
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Task.Delay(50);
        Assert.False(third.IsCompleted); // still waits for the first
        release.SetResult();
        Assert.Equal((1, 2), ((await first).UnderGrant!.Use.UseSequence, (await third).UnderGrant!.Use.UseSequence));
    }

    [Fact]
    public async Task ItsStateIsReadWhole_WhileItActs()
    {
        using var x = Make(g => { g["max_uses"] = 10000; g["max_uses_per_time_token"] = 1000; });
        x.NA.Offline = true;
        var digests = new Dictionary<int, string>();
        var acting = Task.Run(async () =>
        {
            for (var i = 0; i < 150; i++)
            {
                var use = (await x.Holder.Act(Setup.Rotate(), Report())).UnderGrant!.Use;
                lock (digests) digests[use.UseSequence] = GrantRules.UseDigest(use);
            }
        });
        var snapshots = new List<GrantHolderState>();
        while (!acting.IsCompleted) snapshots.Add(x.Holder.State);
        await acting;
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, s => Assert.Equal(s.Uses == 0 ? null : digests[s.Uses], s.HeadDigest));
    }

    [Fact]
    public async Task ATokenTheNARefusesForAClockOffItsOwnStillLetsSyncUpload()
    {
        using var x = Make();
        x.NA.Offline = true;
        var use = await Use(x);
        x.NA.Offline = false;
        (x.NA.TokenRefusal, x.NA.TokenRefusalStatus) = ("time_token_request_stale", 422);
        var synced = await x.Holder.Sync();
        Assert.True(synced.Reachable);
        Assert.Equal(("time_token_request_stale", (string?)null), (synced.TimeTokenRefusal!.Code, synced.Unusable));
        Assert.Equal([use.Use.UseId], synced.Flushed!.Admitted.Select(e => e.Id));
    }

    [Fact]
    public async Task ASpentTokenTheNAWillNotRenewForAClockOffItsOwnGoesToTheNA()
    {
        using var x = Make(g => g["max_uses_per_time_token"] = 1);
        await Use(x);
        (x.NA.TokenRefusal, x.NA.TokenRefusalStatus) = ("time_token_request_stale", 422);
        // The NA answers: the fallback asks it (this fake NA has no evaluate route, so the governed action is refused).
        var e = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => x.Holder.Act(Setup.Rotate(), Report()));
        Assert.Equal("not_found", e.Code);
        Assert.Contains(x.NA.Requests, r => r.Contains("evaluate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnNAAnsweringTheBearerKeyRetiredRefusesTheFirstChangeAlready()
    {
        using var x = Make(g => g["max_uses_per_time_token"] = 1);
        await Use(x);
        x.NA.TokenRefusal = "grant_bearer_key_retired";
        Assert.Equal("grant_bearer_key_retired", (await Assert.ThrowsAsync<GrantRefusedException>(() => x.Holder.Act(Setup.Rotate(), Report()))).Code);
        Assert.Equal("grant_bearer_key_retired", x.Holder.State.Unusable);
    }

    [Fact]
    public async Task AUseKeptBeforeItsStateWasPersistedIsTakenUp_NoPlaceSignedTwice()
    {
        using var x = Make();
        x.NA.Offline = true;
        var before = x.Holder.State;
        var first = await Use(x);
        // A restart from the state persisted before that use: the record outbox holds it.
        var resumed = new GrantHolder(new GrantHolderOptions
        {
            Client = x.Client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public]),
            State = before,
            Now = () => Start.AddSeconds(700),
        });
        var second = (await resumed.Act(Setup.Rotate(), Report())).UnderGrant!.Use;
        Assert.Equal((2, GrantRules.UseDigest(first.Use)), (second.UseSequence, second.PrevUseDigest));
    }

    [Fact]
    public async Task UnderAKeyHistoryAFeedOfALaterKeyReplacesOneOfAnEarlierKey()
    {
        // The NA's key 1 handed over to key 2 an hour ago; key 1 still vouches (the grace), so its feed counts.
        var next = NewKey();
        var handedOver = DateTimeOffset.UtcNow.AddHours(-1);
        var history = new NAKeyHistory("sovereign-c", null,
        [
            new NAKey(1, "na-key", NAKey.Public, null, handedOver, null, false),
            new NAKey(2, "na-key-2", next.Public, handedOver, null, null, false),
        ]);
        using var x = Make(g => { g["issued_at"] = Canonical.Timestamp(handedOver.AddMinutes(-30)); g["valid_from"] = Canonical.Timestamp(handedOver.AddMinutes(-30)); g["valid_until"] = Canonical.Timestamp(handedOver.AddHours(3)); });
        JsonObject Feed(long sequence, DateTimeOffset at, Key key)
        {
            var feed = new JsonObject
            {
                ["feed_id"] = $"feed-{sequence}",
                ["issuer_sovereign_id"] = "sovereign-c",
                ["sequence"] = sequence,
                ["issued_at"] = Canonical.Timestamp(at),
                ["revoked_attestation_ids"] = new JsonArray(),
                ["revocation_reasons"] = new JsonObject(),
                ["issued_by"] = "na-key",
                ["revoked_grant_ids"] = new JsonArray(),
            };
            var body = OfflineVerifier.ReceivedCanonical("SovereignRevocationFeed", JsonSerializer.SerializeToElement(feed));
            feed["signatures"] = new JsonArray(new JsonObject
            {
                ["key_id"] = "na-key",
                ["sig"] = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, Encoding.UTF8.GetBytes(body))),
            });
            return feed;
        }
        var older = Feed(9, handedOver.AddMinutes(-10), NAKey.Key).Deserialize<SovereignRevocationFeed>()!;
        var newer = Feed(1, handedOver.AddMinutes(10), next.Key).Deserialize<SovereignRevocationFeed>()!;
        var holder = new GrantHolder(new GrantHolderOptions
        {
            Client = x.Client,
            Signer = Bearer,
            NAKeys = history,
            Grant = x.Grant,
            TimeToken = x.TimeToken,
        });
        Assert.True(await holder.UpdateRevocationFeed(older));
        Assert.True(await holder.UpdateRevocationFeed(newer));
        Assert.Equal(1, holder.State.RevocationFeed!.Sequence);
        // Keys valid at every time count as one: the higher sequence stays.
        var listed = new GrantHolder(new GrantHolderOptions
        {
            Client = x.Client,
            Signer = Bearer,
            NAKeys = NAKeyHistory.ValidAlways([NAKey.Public, next.Public]),
            Grant = x.Grant,
            TimeToken = x.TimeToken,
        });
        Assert.True(await listed.UpdateRevocationFeed(older));
        Assert.False(await listed.UpdateRevocationFeed(newer));
    }
}
