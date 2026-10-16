using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

// ── Helpers ───────────────────────────────────────────────────────────────────

internal static class TestHelpers
{
    // 32 zero bytes — deterministic test seed
    public const string ZeroSeedB64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static GenesisMeshClient ClientWithHandler(HttpMessageHandler handler) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = ZeroSeedB64,
            KeyId = "test-key",
        });

    public static HttpMessageHandler RespondWith(int status, object body)
    {
        var json = JsonSerializer.Serialize(body, Auth.SerializerOptions);
        return new FuncHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        });
    }
}

internal sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        Task.FromResult(fn(req));
}

// ── Auth ──────────────────────────────────────────────────────────────────────

public class AuthTests
{
    [Fact]
    public void CanonicalJson_SortsKeys()
    {
        var dict = new Dictionary<string, object?> { ["z"] = 1, ["a"] = 2 };
        var bytes = Auth.CanonicalJson(dict);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Equal("{\"a\":2,\"z\":1}", json);
    }

    [Fact]
    public void CanonicalJson_DoesNotEscapeAngleBrackets()
    {
        var dict = new Dictionary<string, object?> { ["detail"] = "A -> B" };
        var bytes = Auth.CanonicalJson(dict);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("->", json);
        Assert.DoesNotContain("\\u003e", json);
    }

    [Fact]
    public void CanonicalJson_NestedObjectsSorted()
    {
        var body = new Dictionary<string, object?>
        {
            ["z"] = new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2 },
            ["a"] = "x",
        };
        var json = Encoding.UTF8.GetString(Auth.CanonicalJson(body));
        Assert.Equal("{\"a\":\"x\",\"z\":{\"a\":2,\"b\":1}}", json);
    }

    [Fact]
    public void LoadSeed_ValidBase64_Returns32Bytes()
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        Assert.Equal(32, seed.Length);
    }

    [Fact]
    public void LoadSeed_InvalidBase64_Throws()
    {
        Assert.Throws<ArgumentException>(() => Auth.LoadSeed("not-valid-base64!!!"));
    }

    [Fact]
    public void BuildAdminHeaders_ReturnsAllFourHeaders()
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var h = Auth.BuildAdminHeaders(new AdminRequest("POST", "/admin/invite", "TEST", new { }), "key-1", seed);
        Assert.Equal("key-1", h.KeyId);
        Assert.False(string.IsNullOrEmpty(h.Signature));
        Assert.False(string.IsNullOrEmpty(h.Timestamp));
        Assert.False(string.IsNullOrEmpty(h.Nonce));
    }

    [Fact]
    public void BuildAdminHeaders_TimestampEndsWithZ()
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var h = Auth.BuildAdminHeaders(new AdminRequest("POST", "/admin/invite", "TEST", new { }), "k", seed);
        Assert.EndsWith("Z", h.Timestamp);
    }

    // The timestamp was formatted in the current culture: under a culture with another calendar (th-TH counts
    // Buddhist years, 2569 for 2026) or another time separator (fi-FI writes 12.30.00), the NA refused every admin
    // request. It is written in the invariant culture now.
    [Theory]
    [InlineData("th-TH")]
    [InlineData("fi-FI")]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void BuildAdminHeaders_TimestampIsUtcIsoInEveryCulture(string culture)
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var before = DateTime.UtcNow;
        var saved = CultureInfo.CurrentCulture;
        string timestamp;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            timestamp = Auth.BuildAdminHeaders(new AdminRequest("POST", "/admin/invite", "TEST", new { }), "k", seed).Timestamp;
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", timestamp);
        var parsed = DateTime.ParseExact(timestamp, "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.InRange(parsed, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }
}

// ── Error parsing ─────────────────────────────────────────────────────────────

public class ErrorParserTests
{
    [Fact]
    public void Parse_400_ReturnsBadRequest()
    {
        var ex = ErrorMapping.Parse(400, "{\"error\":{\"message\":\"bad\",\"code\":\"BAD\"}}");
        Assert.IsType<BadRequestException>(ex);
        Assert.Equal("bad", ex.Detail);
        Assert.Equal("BAD", ex.Code);
    }

    [Fact]
    public void Parse_401_ReturnsUnauthorized()
    {
        var ex = ErrorMapping.Parse(401, "{\"error\":{\"message\":\"auth failed\",\"code\":\"admin_auth_failed\"}}");
        Assert.IsType<UnauthorizedException>(ex);
    }

    [Fact]
    public void Parse_422_ReturnsValidation()
    {
        var ex = ErrorMapping.Parse(422, "{\"error\":{\"message\":\"invalid\",\"code\":\"VALIDATION\"}}");
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public void Parse_500_ReturnsServer()
    {
        var ex = ErrorMapping.Parse(500, "Internal error");
        Assert.IsType<ServerException>(ex);
    }

    // 1.7.1: a 403, 409, 413 or other 4xx was a ServerException ("HTTP 5xx"), so a retry-on-server-error
    // policy retried a refusal. The codes are ones the core sends (genesis_mesh/na_service/errors.py).
    [Theory]
    [InlineData(403, "insufficient_operator_tier", typeof(ForbiddenException))]
    [InlineData(403, "node_key_revoked", typeof(ForbiddenException))]
    [InlineData(409, "boundary_policy_required", typeof(ConflictException))]
    [InlineData(413, "request_entity_too_large", typeof(PayloadTooLargeException))]
    [InlineData(405, "method_not_allowed", typeof(ClientErrorException))]
    [InlineData(410, "gone", typeof(ClientErrorException))]
    [InlineData(400, "bad_request", typeof(BadRequestException))]
    [InlineData(401, "admin_auth_failed", typeof(UnauthorizedException))]
    [InlineData(404, "not_found", typeof(NotFoundException))]
    [InlineData(422, "request_validation_failed", typeof(ValidationException))]
    [InlineData(429, "rate_limit_exceeded", typeof(RateLimitException))]
    [InlineData(500, "internal_server_error", typeof(ServerException))]
    [InlineData(503, "service_unavailable", typeof(ServerException))]
    [InlineData(302, "", typeof(GenesisMeshException))]
    public void Parse_MapsEachStatusToItsType(int status, string code, Type expected)
    {
        var ex = ErrorMapping.Parse(status, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"m\",\"details\":{{}},\"request_id\":\"r\"}}}}");
        Assert.IsType(expected, ex);
        Assert.Equal(status, ex.Status);
        Assert.Equal(code, ex.Code);
        Assert.Equal("m", ex.Detail);
        Assert.Equal(status >= 500, ex is ServerException);
    }
}

// ── Internals published as API (1.7.1: obsolete, internal in 2.0) ───────────

public class ObsoleteInternalsTests
{
#pragma warning disable CS0618 // what these tests are about
    [Fact]
    public void Internals_AreMarkedObsolete()
    {
        Assert.NotNull(typeof(ErrorParser).GetCustomAttributes(typeof(ObsoleteAttribute), false).SingleOrDefault());
        Assert.NotNull(typeof(StrictJson).GetCustomAttributes(typeof(ObsoleteAttribute), false).SingleOrDefault());
        Assert.NotNull(typeof(Canonical).GetMethod(nameof(Canonical.PythonFloatRepr))!
            .GetCustomAttributes(typeof(ObsoleteAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void Internals_StillWorkUntil2_0()
    {
        Assert.IsType<BadRequestException>(ErrorParser.Parse(400, "{}"));
        Assert.Equal("1e-05", Canonical.PythonFloatRepr(1e-5));
        Assert.Equal("duplicate_key", Assert.Throws<StrictJsonException>(() => StrictJson.Check("{\"a\":1,\"a\":2}")).Reason);
        using var doc = StrictJson.Parse(StrictJson.DecodeUtf8("{\"a\":1}"u8.ToArray()));
        Assert.Equal(64, StrictJson.MaxDepth);
    }
#pragma warning restore CS0618
}

// ── Client — no signing key ───────────────────────────────────────────────────

public class ClientNoKeyTests
{
    [Fact]
    public async Task AdminRoute_WithoutKey_ThrowsInvalidOperation()
    {
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Agreement.Offer(new CapabilityOffer(), CancellationToken.None));
    }
}

// ── Agreement ─────────────────────────────────────────────────────────────────

public class AgreementClientTests
{
    [Fact]
    public async Task Offer_ReturnsOfferRecord()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/agreements/offer", req.RequestUri!.PathAndQuery);
            Assert.NotNull(req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault());
            var json = JsonSerializer.Serialize(new OfferRecord { OfferId = "off-1" }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });

        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test",
            HttpHandler = handler,
        });
        var rec = await client.Agreement.Offer(new CapabilityOffer
        {
            ResponderSovereignId = "NA-B",
            Capabilities = ["read"],
            Roles = ["role:client"],
            ValidFrom = "2026-01-01T00:00:00.000Z",
            ValidUntil = "2027-01-01T00:00:00.000Z",
            ExpiresAt = "2026-01-08T00:00:00.000Z",
        });

        Assert.Equal("off-1", rec.OfferId);
    }

    [Fact]
    public async Task Verify_PublicRoute_ReturnsResult()
    {
        using var handler = new FuncHandler(_ =>
        {
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            HttpHandler = handler,
        });
        var result = await client.Agreement.Verify(
            new Dictionary<string, object?> { ["agreement"] = new { } });
        Assert.True(result.Valid);
    }
}

// ── Evidence ──────────────────────────────────────────────────────────────────

public class EvidenceClientTests
{
    [Fact]
    public async Task Build_ReturnsEvidence()
    {
        using var handler = new FuncHandler(_ =>
        {
            var json = JsonSerializer.Serialize(
                new TrustEvidence { EvidenceId = "ev-1", Verdict = "allow" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test",
            HttpHandler = handler,
        });
        var ev = await client.Evidence.Build(new TrustDecision
        {
            SourceSovereignId = "ALPHA",
            TargetSovereignId = "BETA",
            Verdict = "allow",
            Reason = "passed",
        });
        Assert.Equal("ev-1", ev.EvidenceId);
        Assert.Equal("allow", ev.Verdict);
    }
}

// ── Boundary ──────────────────────────────────────────────────────────────────

public class BoundaryClientTests
{
    [Fact]
    public async Task Decide_AdminRoute_ReturnsDecision()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/boundary/decide", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new { decision_id = "dec-1", authorized = true },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test",
            HttpHandler = handler,
        });
        var dec = await client.Boundary.Decide(
            new Dictionary<string, object?> { ["requested_capability"] = "read:data" });
        Assert.True(dec.Authorized);
    }
}

// ── HTTP error propagation ────────────────────────────────────────────────────

public class HttpErrorTests
{
    [Fact]
    public async Task HttpError_Propagates_AsGenesisMeshException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"bad verdict\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test",
            HttpHandler = handler,
        });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            client.Evidence.Build(new TrustDecision { Verdict = "trusted" }));
        Assert.Equal(422, ex.Status);
    }

    [Fact]
    public async Task Forbidden_IsNotAServerError_AndStillAGenesisMeshException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"insufficient_operator_tier\",\"message\":\"This operation requires the root operator tier.\",\"details\":{},\"request_id\":\"r-1\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test",
            HttpHandler = handler,
        });
        GenesisMeshException ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            client.Boundary.Decide(new Dictionary<string, object?> { ["requested_capability"] = "read" }));
        Assert.Equal(403, ex.Status);
        Assert.Equal("insufficient_operator_tier", ex.Code);
    }
}

// ── Cancellation (1.7.1) ──────────────────────────────────────────────────────

public class CancellationTests
{
    /// <summary>Answers only when the request's token is cancelled (or never).</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static GenesisMeshClient Client(TimeSpan timeout = default) =>
        new(new ClientOptions { BaseUrl = "http://localhost", HttpHandler = new HangingHandler(), Timeout = timeout });

    private static Dictionary<string, object?> Body() => new() { ["decision"] = new { } };

    // Before 1.7.1 the caller's own cancellation was reported as a NetworkException, so
    // catch (OperationCanceledException) never saw it.
    [Fact]
    public async Task CallerCancellation_BeforeTheCall_IsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var client = Client();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Boundary.Verify(Body(), cts.Token));
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task CallerCancellation_DuringTheCall_IsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var client = Client();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Boundary.Verify(Body(), cts.Token));
    }

    [Fact]
    public async Task Timeout_IsStillANetworkError()
    {
        using var client = Client(TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<NetworkException>(() => client.Boundary.Verify(Body(), CancellationToken.None));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }
}
