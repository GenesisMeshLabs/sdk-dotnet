using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class DataUsageTests
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
    public async Task CreatePolicy_PostsToCorrectPath_AndReturnsDataLicensePolicy()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/data-usage/policy", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new DataLicensePolicy
                {
                    PolicyId        = "pol-1",
                    AllowedPurposes = ["analytics", "training"],
                    IssuedAt        = "2026-06-30T00:00:00.000Z",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var pol = await AdminClient(handler).DataUsage.CreatePolicy(
            new Dictionary<string, object?>
            {
                ["allowed_purposes"] = new[] { "analytics", "training" },
            });
        Assert.Equal("pol-1", pol.PolicyId);
        Assert.Contains("analytics", pol.AllowedPurposes!);
    }

    [Fact]
    public async Task CreatePolicy_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new DataLicensePolicy { PolicyId = "pol-1" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).DataUsage.CreatePolicy(
            new Dictionary<string, object?> { ["allowed_purposes"] = new[] { "analytics" } });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task CreateIntent_PostsToCorrectPath_AndReturnsDataAccessIntent()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/data-usage/intent", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new DataAccessIntent { IntentId = "int-1", AgentSovereignId = "NA-A" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var intent = await AdminClient(handler).DataUsage.CreateIntent(
            new Dictionary<string, object?>
            {
                ["agent_sovereign_id"] = "NA-A",
                ["access_types"]       = new[] { "read" },
                ["sources"]            = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["source_id"]          = "src-1",
                        ["source_type"]        = "public",
                        ["owner_sovereign_id"] = "NA-B",
                    },
                },
            });
        Assert.Equal("int-1", intent.IntentId);
        Assert.Equal("NA-A", intent.AgentSovereignId);
    }

    [Fact]
    public async Task GetPolicy_UsesGetMethod_OnPublicRoute()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("/data-usage/policy", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new DataLicensePolicy { PolicyId = "pol-active", AllowedPurposes = ["analytics"] },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var pol = await PublicClient(handler).DataUsage.GetPolicy();
        Assert.Equal("pol-active", pol.PolicyId);
        Assert.Contains("analytics", pol.AllowedPurposes!);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/data-usage/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).DataUsage.Verify(
            new Dictionary<string, object?> { ["intent"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task CreateIntent_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"invalid source_type\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).DataUsage.CreateIntent(
                new Dictionary<string, object?> { ["access_types"] = new[] { "read" } }));
        Assert.Equal(422, ex.Status);
    }
}
