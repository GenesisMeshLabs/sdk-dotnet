using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class BoundaryTests
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
    public async Task Decide_PostsToCorrectPath_AndReturnsBoundaryDecision()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/boundary/decide", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new BoundaryDecision { DecisionId = "dec-1", Allowed = true, Reason = "capability matched" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var dec = await AdminClient(handler).Boundary.Decide(
            new Dictionary<string, object?> { ["requested_capability"] = "read:data" });
        Assert.Equal("dec-1", dec.DecisionId);
        Assert.True(dec.Allowed);
        Assert.Equal("capability matched", dec.Reason);
    }

    [Fact]
    public async Task Decide_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new BoundaryDecision { DecisionId = "dec-1", Allowed = false },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Boundary.Decide(
            new Dictionary<string, object?> { ["requested_capability"] = "read:data" });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/boundary/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Boundary.Verify(
            new Dictionary<string, object?> { ["decision"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task Verify_InvalidSignature_ReturnsNotValid()
    {
        using var handler = new FuncHandler(_ =>
        {
            var json = JsonSerializer.Serialize(
                new VerifyResult { Valid = false, Reason = "signature mismatch" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Boundary.Verify(
            new Dictionary<string, object?> { ["decision"] = new { } });
        Assert.False(result.Valid);
        Assert.Equal("signature mismatch", result.Reason);
    }

    [Fact]
    public async Task Decide_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"missing field\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).Boundary.Decide(
                new Dictionary<string, object?> { ["bad"] = "data" }));
        Assert.Equal(422, ex.Status);
    }
}
