using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    // 1.7.1: the ID went into the path as given, so "x/../../recognition-policy?" signed and sent a request to
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
}
