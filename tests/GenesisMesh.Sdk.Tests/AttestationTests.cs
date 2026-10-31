using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class AttestationTests
{
    private static GenesisMeshClient AdminClient(HttpMessageHandler h) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test-key",
            HttpHandler = h,
        });

    [Fact]
    public async Task Issue_PostsToCorrectPath_AndReturnsMembershipAttestation()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/attestations", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new MembershipAttestation
                {
                    AttestationId = "att-1",
                    Roles = ["role:client"],
                    IssuedAt = "2026-06-30T00:00:00.000Z",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var att = await AdminClient(handler).Attestation.Issue(
            new Dictionary<string, object?>
            {
                ["subject_sovereign_id"] = "NA-B",
                ["roles"] = new[] { "role:client" },
            });
        Assert.Equal("att-1", att.AttestationId);
        Assert.Contains("role:client", att.Roles);
    }

    [Fact]
    public async Task Issue_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new MembershipAttestation { AttestationId = "att-1", Roles = ["role:client"] },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Attestation.Issue(
            new Dictionary<string, object?> { ["subject_sovereign_id"] = "NA-B", ["roles"] = new[] { "role:client" } });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Revoke_IncludesAttestationIdInPath()
    {
        string? capturedPath = null;
        using var handler = new FuncHandler(req =>
        {
            capturedPath = req.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await AdminClient(handler).Attestation.Revoke(
            "att-99",
            new Dictionary<string, object?> { ["reason"] = "expired" });
        Assert.Equal("/admin/attestations/att-99/revoke", capturedPath);
    }

    // 1.8.0: the ID went into the path as given, so "x/../../recognition-policy?" signed and sent a request to
    // POST /admin/recognition-policy (the HTTP client resolves the dot segments). An ID that is not one path
    // segment is refused before anything is signed or sent.
    [Theory]
    [InlineData("x/../../recognition-policy?")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a%2Fb")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    [InlineData("a\u007fb")]
    public async Task Revoke_RefusesAnIdThatIsNotOnePathSegment(string attestationId)
    {
        var sent = 0;
        using var handler = new FuncHandler(_ =>
        {
            sent++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var e = await Assert.ThrowsAsync<ArgumentException>(() =>
            AdminClient(handler).Attestation.Revoke(attestationId, new Dictionary<string, object?> { ["reason"] = "x" }));
        Assert.Equal("attestationId", e.ParamName);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task Revoke_RefusesANullId()
    {
        using var handler = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AdminClient(handler).Attestation.Revoke(null!, new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("att-99")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("att_1.2~x")]
    [InlineData("...")]
    public async Task Revoke_KeepsAnIdThatIsOnePathSegment(string attestationId)
    {
        string? path = null;
        using var handler = new FuncHandler(req =>
        {
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await AdminClient(handler).Attestation.Revoke(attestationId, new Dictionary<string, object?>());
        Assert.Equal($"/admin/attestations/{attestationId}/revoke", path);
    }

    [Fact]
    public async Task Revoke_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"invalid\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).Attestation.Revoke(
                "att-bad",
                new Dictionary<string, object?> { ["reason"] = "test" }));
    }

    [Fact]
    public async Task SavePolicy_PostsToRecognitionPolicyPath()
    {
        string? capturedPath = null;
        using var handler = new FuncHandler(req =>
        {
            capturedPath = req.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        await AdminClient(handler).Attestation.SavePolicy(
            new Dictionary<string, object?>
            {
                ["min_roles"] = new[] { "role:anchor" },
                ["require_signatures"] = true,
            });
        Assert.Equal("/admin/recognition-policy", capturedPath);
    }

    [Fact]
    public async Task Issue_WithoutSigningKey_ThrowsInvalidOperation()
    {
        var client = new GenesisMeshClient(new ClientOptions { BaseUrl = "http://localhost" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Attestation.Issue(
                new Dictionary<string, object?> { ["roles"] = new[] { "role:client" } }));
    }

    // ── Reads (1.9.0) ────────────────────────────────────────────────────────

    private sealed record Sent(string Method, string Path, string? Body, bool Admin);

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status, string code) =>
        Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");

    /// <summary>A client whose NA answers with <paramref name="answer"/>, recording each request; signing when <paramref name="sign"/>.</summary>
    private static GenesisMeshClient Client(List<Sent> sent, Func<HttpRequestMessage, HttpResponseMessage> answer, bool sign = true) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = sign ? TestHelpers.ZeroSeedB64 : null,
            KeyId = sign ? "test-key" : null,
            Audience = "TEST",
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, Uri.UnescapeDataString(req.RequestUri!.PathAndQuery),
                    req.Content?.ReadAsStringAsync().Result, req.Headers.Contains("X-Admin-Key-Id")));
                return answer(req);
            }),
        });

    private const string Attestation = """
        {"attestation_id":"att-1","issuer_sovereign_id":"na-a","subject_id":"vendor-1","subject_public_key":null,
         "roles":["role:client"],"status":"active","issued_at":"2026-10-31T08:00:00Z","valid_from":"2026-10-31T08:00:00Z",
         "expires_at":"2026-11-01T08:00:00Z","issued_by":"na","claims":{"apps":["app-1"]},
         "signatures":[{"key_id":"na","sig":"AA=="}]}
        """;

    private static string Record(string status = "active", string? revokedAt = null) =>
        $$"""{"attestation":{{Attestation}},"status":"{{status}}","revoked_at":{{(revokedAt is null ? "null" : $"\"{revokedAt}\"")}},"revocation_reason":null,"reissued_from":null,"superseded_by":null}""";

    [Fact]
    public async Task Get_ReadsTheStoredAttestation_Unsigned()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, Record("revoked", "2026-10-31T09:00:00Z")));
        var record = await gm.Attestation.Get("att-1");
        Assert.Equal(("revoked", "2026-10-31T09:00:00Z", "att-1", "active"),
            (record.Status, record.RevokedAt, record.Attestation.AttestationId, record.Attestation.Status));
        Assert.Equal(("GET", "/attestations/att-1", false), (sent[0].Method, sent[0].Path, sent[0].Admin));

        await Assert.ThrowsAsync<ArgumentException>(() => gm.Attestation.Get("att-1 "));
        await Assert.ThrowsAsync<ArgumentException>(() => gm.Attestation.Get("../admin"));
        Assert.Single(sent);
        using var none = Client([], _ => Error(404, "attestation_not_found"));
        Assert.Equal("attestation_not_found", (await Assert.ThrowsAsync<NotFoundException>(() => none.Attestation.Get("att-2"))).Code);
    }

    [Fact]
    public async Task List_SignsWithAKey_AndSendsEveryFilter()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, $$"""{"count":1,"attestations":[{{Record()}}]}"""));
        var list = await gm.Attestation.List(new ListAttestationsParams { IssuerSovereignId = "na-a", SubjectId = "vendor-1", Status = "active" });
        Assert.Equal((1, "att-1"), (list.Count, Assert.Single(list.Attestations!).Attestation.AttestationId));
        Assert.Equal(("GET", "/attestations?issuer_sovereign_id=na-a&subject_id=vendor-1&status=active", true),
            (sent[0].Method, sent[0].Path, sent[0].Admin));
        await gm.Attestation.List();
        Assert.Equal(("/attestations", true), (sent[1].Path, sent[1].Admin));
    }

    [Fact]
    public async Task List_RefusesASigningKeyWithoutAKeyId()
    {
        var sent = new List<Sent>();
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            HttpHandler = new FuncHandler(req =>
            {
                sent.Add(new Sent(req.Method.Method, req.RequestUri!.PathAndQuery, null, false));
                return Json(200, """{"count":1}""");
            }),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => gm.Attestation.List());
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Verify_IsSentAgain_AsItChangesNothing()
    {
        var calls = 0;
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost",
            Retry = new RetrySettings { Attempts = 1, BaseDelay = TimeSpan.Zero },
            HttpHandler = new FuncHandler(_ => ++calls == 1
                ? Error(503, "unavailable")
                : Json(200, """{"accepted":true,"reason":"accepted","issuer_sovereign_id":"na-a","attestation_id":"att-1"}""")),
        });
        var attestation = JsonSerializer.Deserialize<MembershipAttestation>(Attestation, Auth.SerializerOptions)!;
        Assert.True((await gm.Attestation.Verify(new VerifyAttestationRequest { Attestation = attestation })).Accepted);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ARecognizedIssuer_AcceptsActiveAttestationsByDefault_AsTheNAReadsOne()
    {
        var issuer = new RecognizedIssuer { SovereignId = "na-a", PublicKeys = ["k"], AllowedRoles = ["role:client"] };
        Assert.Equal(["active"], issuer.AcceptedStatuses);
        Assert.Equal(["active"], JsonSerializer.Deserialize<RecognizedIssuer>("""{"sovereign_id":"na-a","public_keys":["k"]}""",
            Auth.SerializerOptions)!.AcceptedStatuses);
    }

    [Fact]
    public async Task List_WithoutAKey_IsTheCount_AndTheNAsRefusalOfAFilterByMember()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => req.RequestUri!.Query.Contains("subject_id")
            ? Error(401, "admin_auth_failed")
            : Json(200, """{"count":3}"""), sign: false);
        var count = await gm.Attestation.List(new ListAttestationsParams { Status = "revoked" });
        Assert.Equal((3, (List<AttestationRecord>?)null), (count.Count, count.Attestations));
        Assert.Equal(("/attestations?status=revoked", false), (sent[0].Path, sent[0].Admin));
        var refused = await Assert.ThrowsAsync<UnauthorizedException>(() => gm.Attestation.List(new ListAttestationsParams { SubjectId = "vendor-1" }));
        Assert.Equal("admin_auth_failed", refused.Code);
    }

    [Fact]
    public async Task Verify_SendsTheAttestationAsSigned_AndThePolicyWhenGiven()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """{"accepted":false,"reason":"locally_revoked","issuer_sovereign_id":"na-a","attestation_id":"att-1"}"""));
        var attestation = JsonSerializer.Deserialize<MembershipAttestation>(Attestation, Auth.SerializerOptions)!;
        var result = await gm.Attestation.Verify(new VerifyAttestationRequest { Attestation = attestation });
        Assert.Equal((false, "locally_revoked", "na-a", "att-1"), (result.Accepted, result.Reason, result.IssuerSovereignId, result.AttestationId));
        Assert.Equal(("POST", "/attestations/verify", false), (sent[0].Method, sent[0].Path, sent[0].Admin));
        var body = JsonNode.Parse(sent[0].Body!)!.AsObject();
        Assert.False(body.ContainsKey("recognition_policy"));
        // The attestation as its issuer signed it, its null public key included.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Attestation), body["attestation"]));

        var policy = new RecognitionPolicy
        {
            LocalSovereignId = "na-b",
            RecognizedIssuers = [new RecognizedIssuer { SovereignId = "na-a", PublicKeys = ["k"], AllowedRoles = ["role:client"], AcceptedStatuses = ["active"] }],
        };
        await gm.Attestation.Verify(new VerifyAttestationRequest { Attestation = attestation, RecognitionPolicy = policy });
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"local_sovereign_id":"na-b","recognized_issuers":[{"sovereign_id":"na-a","public_keys":["k"],
             "allowed_roles":["role:client"],"accepted_statuses":["active"]}],"revoked_attestation_ids":[]}
            """), JsonNode.Parse(sent[1].Body!)!["recognition_policy"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gm.Attestation.Verify(null!));
    }

    [Fact]
    public async Task GetPolicy_ReadsTheActivePolicy_OrThrowsWhenNoneIsSet()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, """
            {"local_sovereign_id":"na-b","recognized_issuers":[{"sovereign_id":"na-a","public_keys":["k"],
             "allowed_roles":["role:client"],"accepted_statuses":["active","suspended"]}],"revoked_attestation_ids":["att-9"]}
            """));
        var policy = await gm.Attestation.GetPolicy();
        Assert.Equal(("na-b", "na-a", 2, "att-9"), (policy.LocalSovereignId, policy.RecognizedIssuers[0].SovereignId,
            policy.RecognizedIssuers[0].AcceptedStatuses.Count, Assert.Single(policy.RevokedAttestationIds)));
        Assert.Equal(("GET", "/recognition-policy", false), (sent[0].Method, sent[0].Path, sent[0].Admin));
        using var none = Client([], _ => Error(404, "recognition_policy_not_configured"));
        Assert.Equal("recognition_policy_not_configured", (await Assert.ThrowsAsync<NotFoundException>(() => none.Attestation.GetPolicy())).Code);
    }

    private const string Feed = """
        {"feed_id":"f-1","issuer_sovereign_id":"na-a","sequence":2,"issued_at":"2026-10-31T09:00:00Z",
         "revoked_attestation_ids":["att-1","att-0"],"revocation_reasons":{"att-1":"offboarded","att-0":"unspecified"},
         "issued_by":"na","signatures":[{"key_id":"na","sig":"AA=="}]}
        """;

    [Fact]
    public async Task RevocationFeed_ReadsTheSignedFeed_AsTheNASignedIt()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, _ => Json(200, Feed));
        var feed = await gm.Attestation.RevocationFeed();
        Assert.Equal((2L, "offboarded", (List<string>?)null), (feed.Sequence, feed.RevocationReasons["att-1"], feed.RevokedGrantIds));
        Assert.Equal(("GET", "/sovereign-revocation-feed", false), (sent[0].Method, sent[0].Path, sent[0].Admin));
        await gm.Attestation.RevocationFeed("na-a");
        Assert.Equal("/sovereign-revocation-feed?issuer_sovereign_id=na-a", sent[1].Path);
        // Written back as signed: without revoked_grant_ids when absent, with them when present.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Feed), JsonNode.Parse(JsonSerializer.Serialize(feed, Auth.SerializerOptions))));
        var withGrants = JsonNode.Parse(Feed)!.AsObject();
        withGrants["revoked_grant_ids"] = new JsonArray("g-1");
        var typed = JsonSerializer.Deserialize<SovereignRevocationFeed>(withGrants.ToJsonString(), Auth.SerializerOptions)!;
        Assert.True(JsonNode.DeepEquals(withGrants, JsonNode.Parse(JsonSerializer.Serialize(typed, Auth.SerializerOptions))));

        using var other = Client([], _ => Error(400, "feed_issuer_mismatch"));
        Assert.Equal("feed_issuer_mismatch", (await Assert.ThrowsAsync<BadRequestException>(() => other.Attestation.RevocationFeed("na-z"))).Code);
    }

    [Fact]
    public async Task TheReads_NeedNoSigningKey()
    {
        var sent = new List<Sent>();
        using var gm = Client(sent, req => req.RequestUri!.AbsolutePath switch
        {
            "/recognition-policy" => Json(200, """{"local_sovereign_id":"na","recognized_issuers":[],"revoked_attestation_ids":[]}"""),
            "/sovereign-revocation-feed" => Json(200, Feed),
            _ => Json(200, Record()),
        }, sign: false);
        await gm.Attestation.Get("att-1");
        await gm.Attestation.GetPolicy();
        await gm.Attestation.RevocationFeed();
        Assert.All(sent, s => Assert.False(s.Admin));
    }
}
