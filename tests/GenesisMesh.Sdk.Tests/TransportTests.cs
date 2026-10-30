using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Failover across several NA instances, retries, operator signers and the parts of a response a caller acts on
/// (<c>Retry-After</c>, the error's details), as the TypeScript SDK does them.
/// </summary>
public class TransportTests
{
    private const string A = "http://na-a:8443";
    private const string B = "http://na-b:8443";

    /// <summary>How an instance answers: down (connection refused), reset (a failure after connecting) or a status.</summary>
    private abstract record Behaviour;
    private sealed record Down : Behaviour;
    private sealed record Reset : Behaviour;
    private sealed record Status(int Code) : Behaviour;

    /// <summary>A handler routing by host, recording every URL it is asked for.</summary>
    private sealed class Cluster(Dictionary<string, Behaviour> state, string body = """{"ok":true}""") : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly Dictionary<string, Behaviour> State = state;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Calls.Add(url);
            Requests.Add(req);
            var behaviour = State.FirstOrDefault(p => url.StartsWith(p.Key, StringComparison.Ordinal)).Value ?? new Down();
            return behaviour switch
            {
                Down => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"),
                Reset => throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset"),
                Status { Code: var code } => Task.FromResult(Answer(code, url)),
                _ => throw new InvalidOperationException(),
            };
        }

        private HttpResponseMessage Answer(int code, string url)
        {
            // A healthy instance answers the key lookup with 200, whatever status its other routes give.
            if (code < 300 && url.EndsWith("/sovereign.json", StringComparison.Ordinal)) code = 200;
            var json = code >= 300
                ? """{"error":{"code":"service_not_ready","message":"Service is not ready","details":{"instance":"host:1","ha_mode":"on","rate_limiter":"database","database":{"backend":"postgres","writable":false,"expected_schema_version":13,"error":"OperationalError"},"signing_key":{"key_id":"na","provider":"azure-keyvault","fingerprint":"ab"}},"request_id":"r"}}"""
                : url.EndsWith("/sovereign.json", StringComparison.Ordinal) ? """{"network_authority":{"public_key":"NA-PUBLIC-KEY"}}""" : body;
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static GenesisMeshClient Client(HttpMessageHandler handler, Action<ClientOptions>? configure = null)
    {
        var options = new ClientOptions
        {
            BaseUrls = [A, B],
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            HttpHandler = handler,
        };
        configure?.Invoke(options);
        return new GenesisMeshClient(options);
    }

    private static Dictionary<string, object?> Attestation() => new() { ["subject_id"] = "s", ["roles"] = new[] { "role:client" } };

    private static Dictionary<string, object?> Body() => new() { ["decision"] = new { } };

    // ── Failover (v0.60) ──────────────────────────────────────────────────────

    [Fact]
    public void BaseUrls_StartWithTheFirst_WithoutTrailingSlashes()
    {
        using var transport = new Transport(new ClientOptions { BaseUrls = [A, B + "/"], Audience = "TEST" });
        Assert.Equal([A, B], transport.BaseUrls);
        Assert.Equal(A, transport.BaseUrl);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("empty")]
    [InlineData("ftp")]
    [InlineData("relative")]
    public void BaseUrls_AreRefused_WhenAmbiguousEmptyOrNotHttp(string kind)
    {
        var options = kind switch
        {
            "both" => new ClientOptions { BaseUrl = A, BaseUrls = [B] },
            "empty" => new ClientOptions { BaseUrls = [] },
            "ftp" => new ClientOptions { BaseUrls = ["ftp://x"] },
            _ => new ClientOptions { BaseUrls = ["na-a:8443"] },
        };
        Assert.Throws<ArgumentException>(() => new GenesisMeshClient(options));
    }

    [Fact]
    public async Task AnIdempotentRequest_MovesPastADownInstance_AndStaysThere()
    {
        var cluster = new Cluster(new() { [A] = new Down(), [B] = new Status(200) });
        using var gm = Client(cluster);
        await gm.Health.Liveness();
        Assert.Equal([$"{A}/healthz", $"{B}/healthz"], cluster.Calls);
        await gm.Health.Liveness();
        Assert.Equal($"{B}/healthz", cluster.Calls[^1]);
    }

    [Fact]
    public async Task AnIdempotentRequest_MovesPastA503()
    {
        var cluster = new Cluster(new() { [A] = new Status(503), [B] = new Status(200) });
        using var gm = Client(cluster);
        await gm.Boundary.Verify(Body());
        Assert.Equal(2, cluster.Calls.Count);
    }

    [Fact]
    public async Task AStateCreatingRequest_MovesWhenTheConnectionWasRefused()
    {
        var cluster = new Cluster(new() { [A] = new Down(), [B] = new Status(201) });
        using var gm = Client(cluster, o => o.Audience = "TEST");
        await gm.Attestation.Issue(Attestation());
        Assert.Equal([$"{A}/admin/attestations", $"{B}/admin/attestations"], cluster.Calls);
    }

    [Fact]
    public async Task AStateCreatingRequest_ThatMayHaveArrived_IsNotSentAgain_ButTheNextOneStartsElsewhere()
    {
        var cluster = new Cluster(new() { [A] = new Reset(), [B] = new Status(201) });
        using var gm = Client(cluster, o => o.Audience = "TEST");
        var ex = await Assert.ThrowsAsync<NetworkException>(() => gm.Attestation.Issue(Attestation()));
        Assert.False(ex.ConnectFailed);
        Assert.Single(cluster.Calls);
        await gm.Attestation.Issue(Attestation());
        Assert.StartsWith(B, cluster.Calls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStateCreatingRequest_IsNotSentAgain_AfterA503()
    {
        var cluster = new Cluster(new() { [A] = new Status(503), [B] = new Status(201) });
        using var gm = Client(cluster, o => o.Audience = "TEST");
        var ex = await Assert.ThrowsAsync<ServerException>(() => gm.Attestation.Issue(Attestation()));
        Assert.Equal(503, ex.Status);
        Assert.Single(cluster.Calls);
    }

    [Fact]
    public async Task EveryInstanceDown_IsANetworkError_AfterOneTryEach()
    {
        var cluster = new Cluster(new() { [A] = new Down(), [B] = new Down() });
        using var gm = Client(cluster);
        var ex = await Assert.ThrowsAsync<NetworkException>(() => gm.Health.Liveness());
        Assert.True(ex.ConnectFailed);
        Assert.Equal(2, cluster.Calls.Count);
    }

    [Fact]
    public async Task OneEndpoint_WithoutRetries_IsOneAttempt()
    {
        var cluster = new Cluster(new() { [A] = new Down() });
        using var gm = Client(cluster, o => { o.BaseUrls = null; o.BaseUrl = A; });
        await Assert.ThrowsAsync<NetworkException>(() => gm.Health.Liveness());
        Assert.Single(cluster.Calls);
    }

    [Fact]
    public async Task Failover_CombinesWithRetries()
    {
        var calls = 0;
        using var handler = new FuncHandler(_ =>
        {
            calls++;
            if (calls <= 2) throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset");
            return Json(200, """{"status":"ok"}""");
        });
        using var gm = Client(handler, o => o.Retry = new RetryOptions { Attempts = 1, BaseDelay = TimeSpan.Zero });
        Assert.Equal("ok", (await gm.Health.Liveness()).Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TheKeyLookup_MovesPastADownInstance_AndTheRequestFollows()
    {
        var cluster = new Cluster(new() { [A] = new Down(), [B] = new Status(201) });
        using var gm = Client(cluster);
        await gm.Attestation.Issue(Attestation());
        Assert.Equal([$"{A}/sovereign.json", $"{B}/sovereign.json", $"{B}/admin/attestations"], cluster.Calls);
    }

    [Fact]
    public async Task TheKeyLookup_MovesPastAnInstanceAnswering503()
    {
        var cluster = new Cluster(new() { [A] = new Status(503), [B] = new Status(200) });
        using var transport = new Transport(new ClientOptions
        {
            BaseUrls = [A, B],
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            HttpHandler = cluster,
        });
        Assert.True((await transport.AdminGetAsync<JsonElement>("/admin/evidence", null, default)).GetProperty("ok").GetBoolean());
        Assert.Equal([$"{A}/sovereign.json", $"{B}/sovereign.json", $"{B}/admin/evidence"], cluster.Calls);
    }

    // ── The NA key lookup ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, """{"network_authority":{}}""", typeof(GenesisMeshException))]
    [InlineData(200, "not json", typeof(GenesisMeshException))]
    [InlineData(404, """{"error":{"code":"not_found","message":"x"}}""", typeof(NotFoundException))]
    public async Task AKeyLookupWithoutAKey_IsNaPublicKeyUnavailable_WithItsStatus(int status, string body, Type type)
    {
        using var handler = new FuncHandler(_ => Json(status, body));
        using var gm = Client(handler, o => { o.BaseUrls = null; o.BaseUrl = A; });
        var ex = await Assert.ThrowsAnyAsync<GenesisMeshException>(() => gm.Attestation.Issue(Attestation()));
        Assert.IsType(type, ex);
        Assert.Equal("na_public_key_unavailable", ex.Code);
        Assert.Equal(status, ex.Status);
    }

    [Fact]
    public async Task AFailedKeyLookup_IsTriedAgainByTheNextAdminRequest()
    {
        var lookups = 0;
        using var handler = new FuncHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath != "/sovereign.json") return Json(201, """{"ok":true}""");
            return ++lookups == 1 ? Json(503, "{}") : Json(200, """{"network_authority":{"public_key":"NA-KEY"}}""");
        });
        using var gm = Client(handler, o => { o.BaseUrls = null; o.BaseUrl = A; });
        await Assert.ThrowsAsync<ServerException>(() => gm.Attestation.Issue(Attestation()));
        await gm.Attestation.Issue(Attestation());
        await gm.Attestation.Issue(Attestation());
        Assert.Equal(2, lookups);
    }

    // ── Retries ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task AnAdminGet_IsRetriedWithAFreshNonce(int status)
    {
        var nonces = new List<string>();
        using var handler = new FuncHandler(req =>
        {
            nonces.Add(req.Headers.GetValues("X-Admin-Nonce").Single());
            return nonces.Count == 1 ? Json(status, "{}") : Json(200, """{"ok":true}""");
        });
        using var transport = SingleTransport(handler, new RetryOptions { Attempts = 1, BaseDelay = TimeSpan.Zero });
        await transport.AdminGetAsync<JsonElement>("/admin/test", null, default);
        Assert.Equal(2, nonces.Count);
        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public async Task NetworkFailures_AreRetried_UpToTheBound()
    {
        var calls = 0;
        using var handler = new FuncHandler(_ =>
        {
            calls++;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        });
        using var transport = SingleTransport(handler, new RetryOptions { Attempts = 2, BaseDelay = TimeSpan.Zero });
        await Assert.ThrowsAsync<NetworkException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NothingIsRetried_ByDefault_AndStateCreatingPostsNever()
    {
        foreach (var retry in new RetryOptions?[] { null, new RetryOptions { Attempts = 2, BaseDelay = TimeSpan.Zero } })
        {
            var calls = 0;
            using var handler = new FuncHandler(_ => { calls++; return Json(503, "{}"); });
            using var transport = SingleTransport(handler, retry);
            await Assert.ThrowsAsync<ServerException>(() => transport.AdminPostAsync<JsonElement>("/admin/attestations", new { }));
            Assert.Equal(1, calls);
        }
        var gets = 0;
        using var plain = new FuncHandler(_ => { gets++; return Json(503, "{}"); });
        using var noRetry = SingleTransport(plain, null);
        await Assert.ThrowsAsync<ServerException>(() => noRetry.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(1, gets);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task ARefusal_IsNotRetried(int status)
    {
        var calls = 0;
        using var handler = new FuncHandler(_ => { calls++; return Json(status, "{}"); });
        using var transport = SingleTransport(handler, new RetryOptions { Attempts = 2, BaseDelay = TimeSpan.Zero });
        await Assert.ThrowsAnyAsync<GenesisMeshException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AVerification_IsRetried()
    {
        var calls = 0;
        using var handler = new FuncHandler(_ => ++calls == 1 ? Json(503, "{}") : Json(200, """{"accepted":true}"""));
        using var gm = Client(handler, o =>
        {
            o.BaseUrls = null;
            o.BaseUrl = A;
            o.Retry = new RetryOptions { Attempts = 1, BaseDelay = TimeSpan.Zero };
        });
        Assert.True((await gm.Boundary.Verify(Body())).Accepted);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ADroppedResponseBody_IsANetworkError_RetriedOnlyWhenIdempotent()
    {
        var calls = 0;
        using var handler = new FuncHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
        });
        using var transport = SingleTransport(handler, new RetryOptions { Attempts = 1, BaseDelay = TimeSpan.Zero });
        var ex = await Assert.ThrowsAsync<NetworkException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.False(ex.ConnectFailed);
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<NetworkException>(() => transport.AdminPostAsync<JsonElement>("/admin/attestations", new { }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ADroppedResponseBody_DoesNotMoveAStateCreatingRequest_ToAnotherInstance()
    {
        var calls = new List<string>();
        using var handler = new FuncHandler(req =>
        {
            calls.Add(req.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StreamContent(new BrokenStream()) };
        });
        using var gm = Client(handler, o => o.Audience = "TEST");
        var ex = await Assert.ThrowsAsync<NetworkException>(() => gm.Attestation.Issue(Attestation()));
        Assert.False(ex.ConnectFailed);
        Assert.Equal(["na-a"], calls);
    }

    [Fact]
    public async Task AKeyLookupAnswering503_MovesAStateCreatingRequest_ToTheNextInstance()
    {
        var cluster = new Cluster(new() { [A] = new Status(503), [B] = new Status(201) });
        using var gm = Client(cluster);
        await gm.Attestation.Issue(Attestation());
        Assert.Equal([$"{A}/sovereign.json", $"{B}/sovereign.json", $"{B}/admin/attestations"], cluster.Calls);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(11, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 61_000)]
    public void InvalidRetryOptions_AreRefused(int attempts, int delayMs) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = A,
            Retry = new RetryOptions { Attempts = attempts, BaseDelay = TimeSpan.FromMilliseconds(delayMs) },
        }));

    [Fact]
    public async Task TheBackoff_Doubles()
    {
        var times = new List<DateTimeOffset>();
        using var handler = new FuncHandler(_ =>
        {
            times.Add(DateTimeOffset.UtcNow);
            return Json(503, "{}");
        });
        using var transport = SingleTransport(handler, new RetryOptions { Attempts = 2, BaseDelay = TimeSpan.FromMilliseconds(40) });
        await Assert.ThrowsAsync<ServerException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(3, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(35), "first wait");
        Assert.True(times[2] - times[1] >= TimeSpan.FromMilliseconds(75), "second wait, doubled");
    }

    // ── What a refusal carries ────────────────────────────────────────────────

    [Fact]
    public async Task ARefusal_CarriesItsRetryAfterAndDetails()
    {
        using var handler = new FuncHandler(_ =>
        {
            var response = Json(429, """{"error":{"code":"rate_limited","message":"slow down","details":{"limit":300}}}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var transport = SingleTransport(handler, null);
        var ex = await Assert.ThrowsAsync<RateLimitException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal(300, ex.Details.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ARefusalWithoutAWaitOrDetails_HasNone()
    {
        using var handler = new FuncHandler(_ => Json(409, """{"error":{"code":"evidence_conflict","message":"x"}}"""));
        using var transport = SingleTransport(handler, null);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Null(ex.RetryAfter);
        Assert.Equal(JsonValueKind.Undefined, ex.Details.ValueKind);
    }

    [Fact]
    public async Task ARetryAfterDate_IsReadAsTheWaitUntilThen()
    {
        using var handler = new FuncHandler(_ =>
        {
            var response = Json(503, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60));
            return response;
        });
        using var transport = SingleTransport(handler, null);
        var ex = await Assert.ThrowsAsync<ServerException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task AStatusOutsideTheErrorRanges_IsTheBaseException()
    {
        using var handler = new FuncHandler(_ => Json(302, "{}"));
        using var transport = SingleTransport(handler, null);
        var ex = await Assert.ThrowsAsync<GenesisMeshException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(302, ex.Status);
    }

    [Fact]
    public async Task ANullBody_IsAnEmptyResponse()
    {
        using var handler = new FuncHandler(_ => Json(200, "null"));
        using var transport = SingleTransport(handler, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PublicGetAsync<HealthStatus>("/health"));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, null, true)]
    [InlineData(HttpRequestError.Unknown, SocketError.ConnectionRefused, true)]
    [InlineData(HttpRequestError.Unknown, SocketError.ConnectionReset, false)]
    [InlineData(HttpRequestError.Unknown, null, false)]
    public async Task ConnectFailed_IsTrueOnlyWhenNoConnectionWasMade(HttpRequestError error, SocketError? socket, bool connectFailed)
    {
        using var handler = new FuncHandler(_ => throw new HttpRequestException(error, "failed",
            socket is { } code ? new SocketException((int)code) : null));
        using var transport = SingleTransport(handler, null);
        var ex = await Assert.ThrowsAsync<NetworkException>(() => transport.PublicGetAsync<JsonElement>("/test"));
        Assert.Equal(connectFailed, ex.ConnectFailed);
    }

    [Fact]
    public void RetryAfter_ReadsADelayOrADate()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(5), Transport.RetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)), now));
        Assert.Equal(TimeSpan.FromSeconds(90), Transport.RetryAfter(new RetryConditionHeaderValue(now.AddSeconds(89.2)), now));
        Assert.Equal(TimeSpan.Zero, Transport.RetryAfter(new RetryConditionHeaderValue(now.AddSeconds(-10)), now));
        Assert.Null(Transport.RetryAfter(null, now));
    }

    // ── Signers ───────────────────────────────────────────────────────────────

    private sealed class CountingSigner(string keyId) : ISigner
    {
        public int Calls;
        public bool Fail;
        public int Length = 64;
        public string KeyId { get; } = keyId;

        public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("signing unavailable");
            var signature = await new SeedSigner(TestHelpers.ZeroSeedB64, "x").SignAsync(message, cancellationToken);
            return signature[..Length];
        }
    }

    [Fact]
    public async Task ASigner_IsPreferredOverASeed_SignsEveryRequest_AndAFailureSendsNothing()
    {
        var signer = new CountingSigner("HSM");
        var keyIds = new List<string>();
        using var handler = new FuncHandler(req =>
        {
            keyIds.Add(req.Headers.GetValues("X-Admin-Key-Id").Single());
            return Json(200, "{}");
        });
        using var transport = new Transport(new ClientOptions
        {
            BaseUrl = A,
            Audience = "TEST",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "seed-key",
            Signer = signer,
            HttpHandler = handler,
        });
        await transport.AdminGetAsync<JsonElement>("/a", null, default);
        await transport.AdminGetAsync<JsonElement>("/b", null, default);
        Assert.Equal(["HSM", "HSM"], keyIds);
        Assert.Equal(2, signer.Calls);
        signer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.AdminGetAsync<JsonElement>("/c", null, default));
        Assert.Equal(2, keyIds.Count);
    }

    /// <summary>A signer whose key ID changes each time it is read, as during a key rotation.</summary>
    private sealed class RotatingSigner : ISigner
    {
        private int _reads;
        public string KeyId => "k" + Interlocked.Increment(ref _reads);
        public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            new SeedSigner(TestHelpers.ZeroSeedB64, "x").SignAsync(message, cancellationToken);
    }

    [Fact]
    public async Task TheHeaderNamesTheKeyIdThatWasSigned()
    {
        var request = new AdminRequest("GET", "/a", "TEST");
        var headers = await Auth.BuildAdminHeadersAsync(request, new RotatingSigner());
        var payload = Auth.AdminSigningPayload(request, headers.KeyId, headers.Timestamp, headers.Nonce);
        using var key = Key.Import(SignatureAlgorithm.Ed25519, Auth.LoadSeed(TestHelpers.ZeroSeedB64), KeyBlobFormat.RawPrivateKey);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(key.PublicKey, payload, Convert.FromBase64String(headers.Signature)));
    }

    [Fact]
    public async Task ASignatureThatIsNot64Bytes_IsRefused()
    {
        var signer = new CountingSigner("HSM") { Length = 32 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Auth.BuildAdminHeadersAsync(new AdminRequest("GET", "/a", "TEST"), signer));
        Assert.Contains("64 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeedSigner_SignsAsTheSeedHeadersDo()
    {
        var request = new AdminRequest("POST", "/admin/attestations", "TEST", new Dictionary<string, object?> { ["x"] = 1 });
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var expected = Auth.BuildAdminHeaders(request, "ops", seed, "2026-10-30T12:00:00.000Z", "n-1");
        var actual = await Auth.BuildAdminHeadersAsync(request, new SeedSigner(TestHelpers.ZeroSeedB64, "ops"), "2026-10-30T12:00:00.000Z", "n-1");
        Assert.Equal(expected.Signature, actual.Signature);
        Assert.Equal(("ops", "2026-10-30T12:00:00.000Z", "n-1"), (actual.KeyId, actual.Timestamp, actual.Nonce));
    }

    [Fact]
    public void ASeedSigner_NeedsAKeyIdAndASeed()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SeedSigner(TestHelpers.ZeroSeedB64, ""));
        Assert.Throws<ArgumentException>(() => new SeedSigner("not base64!", "ops"));
    }

    // ── Admin GET with a query, and extra headers ─────────────────────────────

    [Fact]
    public async Task AnAdminGet_SignsItsQueryAndPath()
    {
        HttpRequestMessage? sent = null;
        using var handler = new FuncHandler(req => { sent = req; return Json(200, "{}"); });
        using var transport = new Transport(new ClientOptions
        {
            BaseUrl = A,
            Audience = "NA-KEY",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            HttpHandler = handler,
        });
        var query = new Dictionary<string, string> { ["resource_id"] = "app/1 2", ["limit"] = "10" };
        await transport.AdminGetAsync<JsonElement>("/admin/evidence", query, default);
        Assert.Equal("/admin/evidence?resource_id=app%2F1%202&limit=10", sent!.RequestUri!.PathAndQuery);

        string Header(string name) => sent.Headers.GetValues(name).Single();
        var payload = Auth.AdminSigningPayload(
            new AdminRequest("GET", "/admin/evidence", "NA-KEY", null,
                new Dictionary<string, IReadOnlyList<string>> { ["resource_id"] = ["app/1 2"], ["limit"] = ["10"] }),
            "ops", Header("X-Admin-Timestamp"), Header("X-Admin-Nonce"));
        using var key = Key.Import(SignatureAlgorithm.Ed25519, Auth.LoadSeed(TestHelpers.ZeroSeedB64), KeyBlobFormat.RawPrivateKey);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(key.PublicKey, payload, Convert.FromBase64String(Header("X-Admin-Signature"))));
    }

    [Fact]
    public async Task ExtraHeaders_GoOnEveryRequest_TheKeyLookupToo()
    {
        var seen = new List<string>();
        using var handler = new FuncHandler(req =>
        {
            seen.Add(req.RequestUri!.AbsolutePath + " " + req.Headers.GetValues("X-Gateway").Single());
            return req.RequestUri.AbsolutePath == "/sovereign.json"
                ? Json(200, """{"network_authority":{"public_key":"NA-KEY"}}""")
                : Json(201, """{"ok":true}""");
        });
        using var gm = Client(handler, o =>
        {
            o.BaseUrls = null;
            o.BaseUrl = A;
            o.Headers = new Dictionary<string, string> { ["X-Gateway"] = "g-1" };
        });
        await gm.Attestation.Issue(Attestation());
        Assert.Equal(["/sovereign.json g-1", "/admin/attestations g-1"], seen);
    }

    [Fact]
    public async Task TextResponses_ComeBackAsSent_AndRefusalsStillThrow()
    {
        var answers = new Queue<HttpResponseMessage>([Json(200, "line\n"), Json(200, "{\"a\":1}\n"), Json(503, "bad")]);
        using var handler = new FuncHandler(_ => answers.Dequeue());
        using var transport = new Transport(new ClientOptions { BaseUrl = A, Audience = "TEST", SigningKey = TestHelpers.ZeroSeedB64, HttpHandler = handler });
        Assert.Equal("line\n", await transport.AdminGetTextAsync("/admin/evidence/export", null, default));
        Assert.Equal("{\"a\":1}\n", await transport.PublicGetTextAsync("/na/key-history", default));
        var ex = await Assert.ThrowsAsync<ServerException>(() => transport.AdminGetTextAsync("/admin/evidence/export", null, default));
        Assert.Equal(503, ex.Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Transport SingleTransport(HttpMessageHandler handler, RetryOptions? retry) =>
        new(new ClientOptions
        {
            BaseUrl = A,
            Audience = "TEST",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Retry = retry,
            HttpHandler = handler,
        });

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A response body whose connection drops while it is read.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
