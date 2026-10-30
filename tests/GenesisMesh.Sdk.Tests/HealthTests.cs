using System.Net;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>Liveness, readiness and health (v0.60), and the NA's key history (v1.5.0).</summary>
public class HealthTests
{
    private const string A = "http://na-a:8443";
    private const string B = "http://na-b:8443";

    private const string NotReady =
        """{"error":{"code":"service_not_ready","message":"Service is not ready","details":{"instance":"host:1","ha_mode":"on","rate_limiter":"database","database":{"backend":"postgres","writable":false,"expected_schema_version":13,"error":"OperationalError"},"signing_key":{"key_id":"na","provider":"azure-keyvault","fingerprint":"ab"}},"request_id":"r"}}""";

    private static string Captured(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "contract", "na_responses.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty(name).GetRawText();
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GenesisMeshClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer, params string[] urls) =>
        new(new ClientOptions
        {
            BaseUrl = urls.Length == 0 ? A : "",
            BaseUrls = urls.Length == 0 ? null : urls,
            HttpHandler = new FuncHandler(answer),
        });

    [Fact]
    public async Task Readiness_IsReady_WithItsChecks()
    {
        var paths = new List<string>();
        using var gm = Client(req => { paths.Add(req.RequestUri!.AbsolutePath); return Json(200, Captured("health_readiness")); });
        var r = await gm.Health.Readiness();
        Assert.True(r.Ready);
        Assert.Equal("ready", r.Status);
        Assert.True(r.Database.Writable);
        Assert.Equal(r.Database.ExpectedSchemaVersion, r.Database.SchemaVersion);
        Assert.Equal("file", r.SigningKey.Provider);
        Assert.Equal("signing", r.NAKeyState!.State);
        Assert.Equal(JsonValueKind.Object, r.Reissue.ValueKind);
        Assert.Equal(["/readyz"], paths);
    }

    [Fact]
    public async Task Readiness_OfANotReadyNA_IsReturnedWithTheFailingChecks()
    {
        using var gm = Client(_ => Json(503, NotReady));
        var r = await gm.Health.Readiness();
        Assert.False(r.Ready);
        Assert.Equal("not_ready", r.Status);
        Assert.False(r.Database.Writable);
        Assert.Equal("OperationalError", r.Database.Error);
        Assert.Equal("azure-keyvault", r.SigningKey.Provider);
    }

    [Fact]
    public async Task Readiness_OfANotReadyNAWithoutChecks_IsNotReady()
    {
        using var gm = Client(_ => Json(503, """{"error":{"code":"service_not_ready","message":"x"}}"""));
        var r = await gm.Health.Readiness();
        Assert.False(r.Ready);
        Assert.Equal("", r.Instance);
    }

    [Fact]
    public async Task Endpoints_ReportsAnEmptyReadyAnswer_AsNotReady()
    {
        using var gm = Client(_ => Json(200, "null"), A);
        Assert.True((await gm.Health.Endpoints())[0] is { Reachable: true, Ready: false, Readiness: null });
    }

    [Fact]
    public async Task Readiness_RethrowsOtherFailures()
    {
        using var gm = Client(_ => Json(503, """{"error":{"code":"evidence_store_unavailable","message":"x"}}"""));
        var ex = await Assert.ThrowsAsync<ServerException>(() => gm.Health.Readiness());
        Assert.Equal("evidence_store_unavailable", ex.Code);
    }

    [Fact]
    public async Task LivenessAndStatus_ReadTheirRoutes()
    {
        var paths = new List<string>();
        using var gm = Client(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return Json(200, req.RequestUri.AbsolutePath == "/healthz" ? Captured("health_liveness") : Captured("health_status"));
        });
        Assert.Equal("ok", (await gm.Health.Liveness()).Status);
        var status = await gm.Health.Status();
        Assert.Equal(("healthy", "contract-na", "on"), (status.BoundaryPolicies, status.Network, status.EvidenceStore));
        Assert.Equal(["/healthz", "/health"], paths);
    }

    [Fact]
    public async Task Endpoints_ProbesEveryEndpointDirectly()
    {
        using var gm = Client(req => req.RequestUri!.Host == "na-a"
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused")
            : Json(200, Captured("health_readiness")), A, B);
        var report = await gm.Health.Endpoints();
        Assert.Equal([(A, false, false), (B, true, true)], report.Select(e => (e.BaseUrl, e.Reachable, e.Ready)));
        Assert.Contains("connection refused", report[0].Error, StringComparison.Ordinal);
        Assert.True(report[1].Readiness!.Ready);
    }

    [Fact]
    public async Task Endpoints_ReportsAReachableNotReadyEndpoint_WithItsChecks()
    {
        using var gm = Client(req => req.RequestUri!.Host == "na-a" ? Json(503, NotReady) : Json(502, "bad gateway"), A, B);
        var report = await gm.Health.Endpoints();
        Assert.True(report[0] is { Reachable: true, Ready: false, Error: "HTTP 503" });
        Assert.False(report[0].Readiness!.Database.Writable);
        Assert.True(report[1] is { Reachable: true, Ready: false, Readiness: null, Error: "HTTP 502" });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"error":"down"}""")]
    [InlineData("""{"error":{"code":"x"}}""")]
    public async Task Endpoints_ReportsAnAnswerWithoutChecks_WithoutReadiness(string body)
    {
        using var gm = Client(_ => Json(503, body), A);
        Assert.True((await gm.Health.Endpoints())[0] is { Reachable: true, Ready: false, Readiness: null, Error: "HTTP 503" });
    }

    [Fact]
    public async Task Endpoints_ReportsAnEndpointWithABrokenBody_AsUnreachable()
    {
        using var gm = Client(_ => Json(200, "{\"status\":\"ready\",\"status\":\"ready\"}"), A);
        var report = await gm.Health.Endpoints();
        Assert.True(report[0] is { Reachable: false, Ready: false });
    }

    // ── NA key history (v1.5.0) ───────────────────────────────────────────────

    private static string RootKey()
    {
        using var doc = JsonDocument.Parse(Captured("na_key_history"));
        return doc.RootElement.GetProperty("genesis").GetProperty("root_public_key").GetString()!;
    }

    [Fact]
    public async Task KeyHistory_ReturnsWhatTheNAServes()
    {
        using var gm = Client(req =>
        {
            Assert.Equal("/na/key-history", req.RequestUri!.AbsolutePath);
            return Json(200, Captured("na_key_history"));
        });
        var view = await gm.NA.KeyHistory();
        Assert.Equal("contract-na", view.SovereignId);
        Assert.Equal(1, view.SigningKeySequence);
        Assert.True(view.Keys.Single() is { Current: true, KeySequence: 1, ValidFrom: null, SucceededAt: null });
        Assert.Equal(JsonValueKind.Object, view.Genesis.ValueKind);
    }

    [Fact]
    public async Task VerifiedKeyHistory_RebuildsTheHistoryFromTheRootKey()
    {
        using var gm = Client(_ => Json(200, Captured("na_key_history")));
        var history = await gm.NA.VerifiedKeyHistory(RootKey());
        Assert.Equal("contract-na", history.SovereignId);
        Assert.Equal(RootKey(), history.Keys.Single().PublicKey);
    }

    [Fact]
    public async Task VerifiedKeyHistory_RefusesAnotherRootKey()
    {
        using var gm = Client(_ => Json(200, Captured("na_key_history")));
        var other = Convert.ToBase64String(new byte[32]);
        var ex = await Assert.ThrowsAsync<KeyHistoryException>(() => gm.NA.VerifiedKeyHistory(other));
        Assert.Equal("genesis_root_mismatch", ex.Code);
        Assert.False(ex.Verification.Verified);
        Assert.Equal(0, ex.Status);
    }

    [Fact]
    public async Task VerifiedKeyHistory_NeedsARootKey()
    {
        using var gm = Client(_ => Json(200, Captured("na_key_history")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => gm.NA.VerifiedKeyHistory(""));
    }

    [Fact]
    public void KeyHistoryException_NamesTheFirstFailure_OrADefault()
    {
        var failed = new KeyHistoryException(new KeyHistoryVerificationResult(false, "succession_chain_break", 2, null));
        Assert.Contains("succession_chain_break (key 2)", failed.Message, StringComparison.Ordinal);
        var bare = new KeyHistoryException(new KeyHistoryVerificationResult(false, null, null, null));
        Assert.Equal("key_history_invalid", bare.Code);
        Assert.Throws<ArgumentNullException>(() => new KeyHistoryException(null!));
    }
}
