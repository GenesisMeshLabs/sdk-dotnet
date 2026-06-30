using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class EvidenceTests
{
    private static GenesisMeshClient AdminClient(HttpMessageHandler h) =>
        new(new ClientOptions
        {
            BaseUrl     = "http://localhost",
            SigningKey   = TestHelpers.ZeroSeedB64,
            KeyId        = "test-key",
            HttpHandler  = h,
        });

    private static GenesisMeshClient PublicClient(HttpMessageHandler h) =>
        new(new ClientOptions
        {
            BaseUrl     = "http://localhost",
            HttpHandler = h,
        });

    [Fact]
    public async Task Build_PostsToCorrectPath_AndReturnsTrustEvidence()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/trust-evidence", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new TrustEvidence { EvidenceId = "ev-1", Verdict = "allow", IssuedAt = "2026-06-30T00:00:00.000Z" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var ev = await AdminClient(handler).Evidence.Build(new TrustDecision
        {
            SourceSovereignId = "NA-A",
            TargetSovereignId = "NA-B",
            Verdict           = "allow",
            Reason            = "all checks passed",
        });
        Assert.Equal("ev-1", ev.EvidenceId);
        Assert.Equal("allow", ev.Verdict);
    }

    [Fact]
    public async Task Build_WrapsDecisionInDecisionKey()
    {
        string? bodyJson = null;
        using var handler = new FuncHandler(async req =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync();
            var json = JsonSerializer.Serialize(
                new TrustEvidence { EvidenceId = "ev-2", Verdict = "block" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Evidence.Build(new TrustDecision
        {
            Verdict = "block",
            Reason  = "policy violation",
        });
        Assert.NotNull(bodyJson);
        Assert.Contains("\"decision\"", bodyJson);
        Assert.Contains("\"block\"", bodyJson);
    }

    [Fact]
    public async Task Build_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new TrustEvidence { EvidenceId = "ev-1", Verdict = "allow" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Evidence.Build(new TrustDecision { Verdict = "allow", Reason = "ok" });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/trust-evidence/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Evidence.Verify(
            new Dictionary<string, object?> { ["evidence"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task Build_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"bad verdict\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).Evidence.Build(new TrustDecision { Verdict = "trusted", Reason = "test" }));
        Assert.Equal(422, ex.Status);
    }

    [Fact]
    public async Task Build_WithoutSigningKey_ThrowsInvalidOperation()
    {
        var client = new GenesisMeshClient(new ClientOptions { BaseUrl = "http://localhost" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Evidence.Build(new TrustDecision { Verdict = "allow", Reason = "test" }));
    }
}
