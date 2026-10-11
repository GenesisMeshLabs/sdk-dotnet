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
            Audience = "TEST",
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
                new { decision_id = "dec-1", authorized = true, denial_reason = (string?)null },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var dec = await AdminClient(handler).Boundary.Decide(
            new Dictionary<string, object?> { ["requested_capability"] = "read:data" });
        Assert.Equal("dec-1", dec.DecisionId);
        Assert.True(dec.Authorized);
        Assert.Null(dec.DenialReason);
#pragma warning disable CS0618 // the obsolete names are filled from the NA's fields
        Assert.True(dec.Allowed);
#pragma warning restore CS0618
    }

    private static FuncHandler RawResponse(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        });

    [Theory]
    [InlineData("{\"decision_id\":\"dec-1\",\"authorized\":false,\"Authorized\":true}")]
    [InlineData("{\"decision_id\":\"dec-1\",\"Authorized\":true,\"authorized\":false}")]
    [InlineData("{\"decision_id\":\"dec-1\",\"AUTHORIZED\":true,\"Decision_Id\":\"dec-2\"}")]
    public async Task Decide_ReadsKeysInTheirOwnCaseOnly(string json)
    {
        // v1.3.1: a key in another case named the same property, so the first decision read as
        // authorized; every other implementation reads it as a key it does not know.
        using var handler = RawResponse(json);
        var dec = await AdminClient(handler).Boundary.Decide(new Dictionary<string, object?>());
        Assert.False(dec.Authorized);
        Assert.Equal("dec-1", dec.DecisionId);
    }

    [Fact]
    public async Task Decide_RefusesADuplicateKey()
    {
        using var handler = RawResponse("{\"decision_id\":\"dec-1\",\"authorized\":false,\"authorized\":true}");
        var e = await Assert.ThrowsAsync<StrictJsonException>(() =>
            AdminClient(handler).Boundary.Decide(new Dictionary<string, object?>()));
        Assert.Equal("duplicate_key", e.Reason);
    }

    [Fact]
    public void SerializerOptions_ReadNestedKeysInTheirOwnCaseOnly()
    {
        var intent = JsonSerializer.Deserialize<DataAccessIntent>(
            "{\"intent_id\":\"i-1\",\"declared_sources\":[{\"source_id\":\"s-1\",\"Source_Id\":\"s-2\"}],"
            + "\"Estimated_Volume_Bytes\":5}", Auth.SerializerOptions)!;
        Assert.Equal("s-1", intent.DeclaredSources.Single().SourceId);
        Assert.Null(intent.EstimatedVolumeBytes);
        var verified = JsonSerializer.Deserialize<VerifyResult>("{\"Valid\":true,\"Accepted\":true}", Auth.SerializerOptions)!;
        Assert.False(verified.Valid);
        Assert.False(verified.Accepted);
    }

    [Fact]
    public async Task Decide_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new { decision_id = "dec-1", authorized = false, denial_reason = "no agreement" },
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
