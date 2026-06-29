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
            BaseUrl    = "http://localhost",
            SigningKey = ZeroSeedB64,
            KeyId      = "test-key",
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
        var json  = Encoding.UTF8.GetString(bytes);
        Assert.Equal("{\"a\":2,\"z\":1}", json);
    }

    [Fact]
    public void CanonicalJson_DoesNotEscapeAngleBrackets()
    {
        var dict = new Dictionary<string, object?> { ["detail"] = "A -> B" };
        var bytes = Auth.CanonicalJson(dict);
        var json  = Encoding.UTF8.GetString(bytes);
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
        var h    = Auth.BuildAdminHeaders(new { }, "key-1", seed);
        Assert.Equal("key-1", h.KeyId);
        Assert.False(string.IsNullOrEmpty(h.Signature));
        Assert.False(string.IsNullOrEmpty(h.Timestamp));
        Assert.False(string.IsNullOrEmpty(h.Nonce));
    }

    [Fact]
    public void BuildAdminHeaders_TimestampEndsWithZ()
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var h    = Auth.BuildAdminHeaders(new { }, "k", seed);
        Assert.EndsWith("Z", h.Timestamp);
    }
}

// ── Error parsing ─────────────────────────────────────────────────────────────

public class ErrorParserTests
{
    [Fact]
    public void Parse_400_ReturnsBadRequest()
    {
        var ex = ErrorParser.Parse(400, "{\"error\":{\"message\":\"bad\",\"code\":\"BAD\"}}");
        Assert.IsType<BadRequestException>(ex);
        Assert.Equal("bad", ex.Detail);
        Assert.Equal("BAD", ex.Code);
    }

    [Fact]
    public void Parse_401_ReturnsUnauthorized()
    {
        var ex = ErrorParser.Parse(401, "{\"error\":{\"message\":\"auth failed\",\"code\":\"admin_auth_failed\"}}");
        Assert.IsType<UnauthorizedException>(ex);
    }

    [Fact]
    public void Parse_422_ReturnsValidation()
    {
        var ex = ErrorParser.Parse(422, "{\"error\":{\"message\":\"invalid\",\"code\":\"VALIDATION\"}}");
        Assert.IsType<ValidationException>(ex);
    }

    [Fact]
    public void Parse_500_ReturnsServer()
    {
        var ex = ErrorParser.Parse(500, "Internal error");
        Assert.IsType<ServerException>(ex);
    }
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
            BaseUrl     = "http://localhost",
            SigningKey   = TestHelpers.ZeroSeedB64,
            KeyId        = "test",
            HttpHandler  = handler,
        });
        var rec = await client.Agreement.Offer(new CapabilityOffer
        {
            ResponderSovereignId = "NA-B",
            Capabilities = ["read"],
            Roles        = ["role:client"],
            ValidFrom    = "2026-01-01T00:00:00.000Z",
            ValidUntil   = "2027-01-01T00:00:00.000Z",
            ExpiresAt    = "2026-01-08T00:00:00.000Z",
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
            BaseUrl     = "http://localhost",
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
            BaseUrl     = "http://localhost",
            SigningKey   = TestHelpers.ZeroSeedB64,
            KeyId        = "test",
            HttpHandler  = handler,
        });
        var ev = await client.Evidence.Build(new TrustDecision
        {
            SourceSovereignId = "ALPHA",
            TargetSovereignId = "BETA",
            Verdict           = "allow",
            Reason            = "passed",
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
                new BoundaryDecision { DecisionId = "dec-1", Allowed = true },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl     = "http://localhost",
            SigningKey   = TestHelpers.ZeroSeedB64,
            KeyId        = "test",
            HttpHandler  = handler,
        });
        var dec = await client.Boundary.Decide(
            new Dictionary<string, object?> { ["requested_capability"] = "read:data" });
        Assert.True(dec.Allowed);
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
            BaseUrl     = "http://localhost",
            SigningKey   = TestHelpers.ZeroSeedB64,
            KeyId        = "test",
            HttpHandler  = handler,
        });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            client.Evidence.Build(new TrustDecision { Verdict = "trusted" }));
        Assert.Equal(422, ex.Status);
    }
}
