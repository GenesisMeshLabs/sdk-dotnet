using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class DisclosureTests
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
    public async Task Commit_PostsToCorrectPath_AndReturnsCapabilityCommitment()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/disclosure/commit", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new CapabilityCommitment
                {
                    CommitmentId = "cmt-1",
                    MerkleRoot   = "abc123",
                    IssuedAt     = "2026-06-30T00:00:00.000Z",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var cmt = await AdminClient(handler).Disclosure.Commit(
            new Dictionary<string, object?> { ["capabilities"] = new[] { "read:data", "write:logs" } });
        Assert.Equal("cmt-1", cmt.CommitmentId);
        Assert.Equal("abc123", cmt.MerkleRoot);
    }

    [Fact]
    public async Task Commit_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new CapabilityCommitment { CommitmentId = "cmt-1", MerkleRoot = "x" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Disclosure.Commit(
            new Dictionary<string, object?> { ["capabilities"] = new[] { "read:data" } });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Nullifier_PostsToCorrectPath_AndReturnsDictionary()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/disclosure/nullifier", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["nullifier"] = "nul-xyz", ["commitment_id"] = "cmt-1" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await AdminClient(handler).Disclosure.Nullifier(
            new Dictionary<string, object?> { ["commitment_id"] = "cmt-1" });
        Assert.True(result.ContainsKey("nullifier"));
    }

    [Fact]
    public async Task Prove_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/disclosure/prove", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new CapabilityMembershipProof
                {
                    CommitmentId = "cmt-1",
                    Capability   = "read:data",
                    LeafHash     = "hash-abc",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var proof = await PublicClient(handler).Disclosure.Prove(
            new Dictionary<string, object?> { ["commitment_id"] = "cmt-1", ["capability"] = "read:data" });
        Assert.Equal("cmt-1", proof.CommitmentId);
        Assert.Equal("read:data", proof.Capability);
        Assert.Equal("hash-abc", proof.LeafHash);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/disclosure/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Disclosure.Verify(
            new Dictionary<string, object?> { ["proof"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task Commit_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"capabilities required\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).Disclosure.Commit(
                new Dictionary<string, object?> { ["capabilities"] = Array.Empty<string>() }));
        Assert.Equal(422, ex.Status);
    }
}
