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
    public async Task Vote_PostsToCorrectPath_AndReturnsConsensusVote()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/consensus/vote", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new ConsensusVote
                {
                    VoteId               = "vote-1",
                    ProofId              = "jp-42",
                    ValidatorSovereignId = "ALPHA",
                    Vote                 = true,
                    VotedAt              = "2026-06-30T00:00:00Z",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var vote = await AdminClient(handler).Consensus.Vote(
            new Dictionary<string, object?> { ["justification_proof"] = new { proof_id = "jp-42" }, ["vote"] = true });
        Assert.Equal("vote-1", vote.VoteId);
        Assert.Equal("jp-42", vote.ProofId);
        Assert.True(vote.Vote);
    }

    [Fact]
    public async Task Vote_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(
                new ConsensusVote { VoteId = "vote-1", ProofId = "jp-1", Vote = true },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Consensus.Vote(
            new Dictionary<string, object?> { ["justification_proof"] = new { proof_id = "jp-1" }, ["vote"] = true });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Proof_PostsToCorrectPath_AndReturnsConsensusProof()
    {
        var votes = new[]
        {
            new ConsensusVote { VoteId = "v1", ProofId = "proof-1", Vote = true },
            new ConsensusVote { VoteId = "v2", ProofId = "proof-1", Vote = true },
        };
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/consensus/proof", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new ConsensusProof { ConsensusId = "con-1", ProofId = "proof-1", RequiredThreshold = 2, Votes = votes },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var proof = await AdminClient(handler).Consensus.Proof(
            new Dictionary<string, object?>
            {
                ["justification_proof"]     = new { proof_id = "proof-1" },
                ["votes"]                   = votes,
                ["required_threshold"]      = 2,
                ["validator_sovereign_ids"] = new[] { "A", "B" },
            });
        Assert.Equal("proof-1", proof.ProofId);
        Assert.Equal(2, proof.RequiredThreshold);
        Assert.Equal(2, proof.Votes.Count);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/consensus/verify", req.RequestUri!.PathAndQuery);
            var json = "{\"valid\":true,\"reason\":\"valid\",\"consensus_id\":\"con-1\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Consensus.Verify(
            new Dictionary<string, object?> { ["proof"] = new { } });
        Assert.True(result.Valid);
        Assert.Equal("valid", result.Reason);
        Assert.Equal("con-1", result.ConsensusId);
    }

    /// <summary>
    /// A real Python-signed ConsensusProof deserializes into the typed models and serializes
    /// back to the same canonical JSON: no field is lost or renamed, so votes and proofs can be
    /// passed back to the NA and their signatures still verify.
    /// </summary>
    [Fact]
    public void Types_RoundTrip_A_Python_Signed_Proof()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "consensus.json");
        using var suite = JsonDocument.Parse(File.ReadAllText(path));
        var original = suite.RootElement.GetProperty("vectors")[0].GetProperty("input").GetProperty("proof");
        var proof = original.Deserialize<ConsensusProof>()!;
        Assert.NotEmpty(proof.Votes);
        Assert.All(proof.Votes, v =>
        {
            Assert.NotNull(v.ContextDigest);
            Assert.Equal(JsonValueKind.Object, v.Signature.ValueKind);
        });
        Assert.Equal(Canonical.Of(original), Canonical.FromJson(JsonSerializer.Serialize(proof)));
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
                new Dictionary<string, object?> { ["vote"] = "invalid" }));
        Assert.Equal(422, ex.Status);
    }
}
