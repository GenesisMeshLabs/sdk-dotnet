using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class ConsensusTests
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
    public async Task Vote_PostsToCorrectPath_AndReturnsConsensusVote()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/consensus/vote", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new ConsensusVote
                {
                    VoteId     = "vote-1",
                    ProposalId = "prop-42",
                    Decision   = "approve",
                    CastAt     = "2026-06-30T00:00:00.000Z",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var vote = await AdminClient(handler).Consensus.Vote(
            new Dictionary<string, object?> { ["proposal_id"] = "prop-42", ["decision"] = "approve" });
        Assert.Equal("vote-1", vote.VoteId);
        Assert.Equal("prop-42", vote.ProposalId);
        Assert.Equal("approve", vote.Decision);
    }

    [Fact]
    public async Task Vote_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new ConsensusVote { VoteId = "vote-1", ProposalId = "prop-1", Decision = "approve" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Consensus.Vote(
            new Dictionary<string, object?> { ["proposal_id"] = "prop-1", ["decision"] = "approve" });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Proof_PostsToCorrectPath_AndReturnsConsensusProof()
    {
        var votes = new[]
        {
            new ConsensusVote { VoteId = "v1", ProposalId = "prop-1", Decision = "approve" },
            new ConsensusVote { VoteId = "v2", ProposalId = "prop-1", Decision = "approve" },
        };
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/consensus/proof", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new ConsensusProof { ProofId = "proof-1", ProposalId = "prop-1", Threshold = 2, Votes = votes },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var proof = await AdminClient(handler).Consensus.Proof(
            new Dictionary<string, object?>
            {
                ["proposal_id"] = "prop-1",
                ["votes"]       = votes,
                ["threshold"]   = 2,
            });
        Assert.Equal("proof-1", proof.ProofId);
        Assert.Equal(2, proof.Threshold);
        Assert.Equal(2, proof.Votes.Count);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/consensus/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Consensus.Verify(
            new Dictionary<string, object?> { ["proof"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task Vote_422Response_ThrowsValidationException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"bad decision\",\"code\":\"VALIDATION\"}}",
                    Encoding.UTF8, "application/json"),
            });
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            AdminClient(handler).Consensus.Vote(
                new Dictionary<string, object?> { ["proposal_id"] = "p", ["decision"] = "invalid" }));
        Assert.Equal(422, ex.Status);
    }
}
