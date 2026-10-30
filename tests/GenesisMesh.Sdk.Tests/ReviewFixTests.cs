using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// The findings of the review of 1.9.0's transport, policies and evaluation: signed records with null fields passed
/// back, the shared key lookup, the client's construction, probes and the typed policy intent.
/// </summary>
public class ReviewFixTests
{
    private const string A = "http://na-a:8443";
    private const string B = "http://na-b:8443";

    private static string Captured(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "contract", "na_responses.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty(name).GetRawText();
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ── Signed records with a null field, passed back ─────────────────────────

    /// <summary>The captured record with <paramref name="field"/> set to null, as the NA signs it when it is unset.</summary>
    private static string WithNull(string name, string field)
    {
        var node = JsonNode.Parse(Captured(name))!.AsObject();
        node[field] = null;
        return node.ToJsonString();
    }

    [Theory]
    [InlineData("attestation_issue", "subject_public_key", typeof(MembershipAttestation))]
    [InlineData("consensus_vote", "reason", typeof(ConsensusVote))]
    [InlineData("consensus_vote", "context_digest", typeof(ConsensusVote))]
    [InlineData("consensus_proof", "cascade_assessment_digest", typeof(ConsensusProof))]
    [InlineData("boundary_decide", "denial_reason", typeof(BoundaryDecision))]
    public void ANullSignedField_IsWrittenBack(string name, string field, Type model)
    {
        var raw = WithNull(name, field);
        var typed = JsonSerializer.Deserialize(raw, model, Auth.SerializerOptions)!;
        Assert.Equal(Canonical.FromJson(raw), Canonical.FromJson(JsonSerializer.Serialize(typed, model, Auth.SerializerOptions)));
    }

    [Fact]
    public void ATypedAttestation_HasTheDigestOfTheOneTheNASigned()
    {
        var raw = Captured("attestation_issue");
        var typed = JsonSerializer.Deserialize<MembershipAttestation>(raw, Auth.SerializerOptions)!;
        Assert.Equal(OfflineVerifier.AttestationDigest(raw),
            OfflineVerifier.AttestationDigest(JsonSerializer.Serialize(typed, Auth.SerializerOptions)));
    }

    // ── The shared key lookup ─────────────────────────────────────────────────

    [Fact]
    public async Task AFailedLookup_IsDropped_EvenWhenEveryWaiterCancelledFirst()
    {
        var lookups = 0;
        var handler = new AsyncHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath != "/sovereign.json") return Json(201, """{"status":"ok"}""");
            if (Interlocked.Increment(ref lookups) == 1)
            {
                await Task.Delay(300, CancellationToken.None);
                throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
            }
            return Json(200, """{"network_authority":{"public_key":"NA-KEY"}}""");
        });
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = A,
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            HttpHandler = handler,
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                gm.Attestation.Issue(new Dictionary<string, object?> { ["subject_id"] = "s" }, cts.Token));
        await Task.Delay(500);
        await gm.Attestation.Issue(new Dictionary<string, object?> { ["subject_id"] = "s" });
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task EveryWaiterOfAFailedLookup_GetsItsOwnException()
    {
        var gate = new TaskCompletionSource();
        var handler = new AsyncHandler(async (_, _) =>
        {
            await gate.Task;
            return Json(404, """{"error":{"code":"not_found","message":"x"}}""");
        });
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = A,
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            HttpHandler = handler,
        });
        var first = gm.Attestation.Issue(new Dictionary<string, object?> { ["subject_id"] = "s" });
        var second = gm.Attestation.Issue(new Dictionary<string, object?> { ["subject_id"] = "s" });
        gate.SetResult();
        var a = await Assert.ThrowsAsync<NotFoundException>(() => first);
        var b = await Assert.ThrowsAsync<NotFoundException>(() => second);
        Assert.NotSame(a, b);
        Assert.Equal(("na_public_key_unavailable", 404), (b.Code, b.Status));
    }

    [Fact]
    public async Task AStateCreatingRequest_ThatTimedOut_IsNotSentToAnotherInstance()
    {
        var hosts = new List<string>();
        var handler = new AsyncHandler(async (req, ct) =>
        {
            lock (hosts) hosts.Add(req.RequestUri!.Host);
            if (req.RequestUri.Host == "na-a") await Task.Delay(Timeout.Infinite, ct);
            return Json(201, """{"status":"ok"}""");
        });
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrls = [A, B],
            Audience = "NA-KEY",
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Timeout = TimeSpan.FromMilliseconds(200),
            HttpHandler = handler,
        });
        var ex = await Assert.ThrowsAsync<NetworkException>(() =>
            gm.Attestation.Issue(new Dictionary<string, object?> { ["subject_id"] = "s" }));
        Assert.False(ex.ConnectFailed);
        Assert.Equal(["na-a"], hosts);
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("na-a:8443")]
    [InlineData("/relative")]
    [InlineData("ftp://na-a")]
    public void ABaseUrlThatIsNotAnHttpUrl_IsRefusedAtConstruction(string url) =>
        Assert.Throws<ArgumentException>(() => new GenesisMeshClient(new ClientOptions { BaseUrl = url }));

    [Fact]
    public async Task AnHttpClient_IsUsedAsGiven_AndNotDisposed()
    {
        var seen = new List<string>();
        var http = new HttpClient(new FuncHandler(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return Json(200, """{"status":"ok"}""");
        }));
        using (var gm = new GenesisMeshClient(new ClientOptions { BaseUrl = A, HttpClient = http }))
            await gm.Health.Liveness();
        using var after = await http.GetAsync(new Uri(A + "/healthz"));
        Assert.Equal([$"{A}/healthz", $"{A}/healthz"], seen);
        http.Dispose();
    }

    // ── Probes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_IsNotRetried_ButStillMovesToTheNextInstance()
    {
        const string notReady = """{"error":{"code":"service_not_ready","message":"x","details":{"database":{"writable":false}}}}""";
        var calls = new List<string>();
        using var one = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = A,
            Retry = new RetrySettings { Attempts = 3, BaseDelay = TimeSpan.Zero },
            HttpHandler = new FuncHandler(req => { calls.Add(req.RequestUri!.Host); return Json(503, notReady); }),
        });
        Assert.False((await one.Health.Readiness()).Ready);
        Assert.Equal(["na-a"], calls);

        calls.Clear();
        using var two = new GenesisMeshClient(new ClientOptions
        {
            BaseUrls = [A, B],
            Retry = new RetrySettings { Attempts = 3, BaseDelay = TimeSpan.Zero },
            HttpHandler = new FuncHandler(req =>
            {
                calls.Add(req.RequestUri!.Host);
                return req.RequestUri.Host == "na-a" ? Json(503, notReady) : Json(200, Captured("health_readiness"));
            }),
        });
        Assert.True((await two.Health.Readiness()).Ready);
        Assert.Equal(["na-a", "na-b"], calls);
    }

    // ── The typed policy intent ───────────────────────────────────────────────

    [Fact]
    public void TheIntent_SendsReviewRequiredOnlyWhenTrue_AndTypesItsSections()
    {
        var intent = new BoundaryPolicyIntent { PolicyId = "p", ValidFrom = "2026-10-30T00:00:00Z", ValidUntil = "2026-11-30T00:00:00Z" };
        Assert.DoesNotContain("review_required", JsonSerializer.Serialize(intent, Auth.SerializerOptions), StringComparison.Ordinal);
        intent.ReviewRequired = true;
        intent.Remediation = new Dictionary<string, GateRemediation> { ["*"] = new() { Mode = "cancel", GraceSeconds = 300 } };
        intent.Emergency = new EmergencyCapability { StandsFor = "secret.rotate", JustificationParameter = "reason", MaxLifetimeSeconds = 3600 };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(intent, Auth.SerializerOptions));
        Assert.True(doc.RootElement.GetProperty("review_required").GetBoolean());
        Assert.Equal(300, doc.RootElement.GetProperty("remediation").GetProperty("*").GetProperty("grace_seconds").GetInt32());
        Assert.Equal("secret.rotate", doc.RootElement.GetProperty("emergency").GetProperty("stands_for").GetString());
        Assert.Equal(3600, doc.RootElement.GetProperty("emergency").GetProperty("max_lifetime_seconds").GetInt32());
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            fn(request, cancellationToken);
    }
}
