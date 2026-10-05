using System.Net;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using NSec.Cryptography;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Admin signature version 2: the shared reference vectors
/// (genesismesh/conformance/vectors/admin_auth.json, copied unchanged), the
/// request binding, and the one-time sovereign ID lookup.
/// </summary>
public class AdminAuthTests
{
    private static readonly string VectorsPath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", "admin_auth.json");

    public static IEnumerable<object[]> VectorIds()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
            yield return new object[] { v.GetProperty("id").GetString()! };
    }

    [Theory]
    [MemberData(nameof(VectorIds))]
    public void ReproducesTheReferencePayloadAndSignature(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var v = doc.RootElement.GetProperty("vectors").EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);
        var input = v.GetProperty("input");
        var query = input.GetProperty("query").EnumerateObject().ToDictionary(
            p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(x => x.GetString()!).ToList());
        var request = new AdminRequest(
            input.GetProperty("method").GetString()!, input.GetProperty("path").GetString()!,
            input.GetProperty("audience").GetString()!, input.GetProperty("body").Clone(), query);
        var keyId = input.GetProperty("key_id").GetString()!;
        var ts = input.GetProperty("timestamp").GetString()!;
        var nonce = input.GetProperty("nonce").GetString()!;

        var payload = Encoding.UTF8.GetString(Auth.AdminSigningPayload(request, keyId, ts, nonce));
        Assert.Equal(v.GetProperty("expected").GetProperty("payload").GetString(), payload);

        var seedA = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var headers = Auth.BuildAdminHeaders(request, keyId, seedA, ts, nonce);
        Assert.Equal(v.GetProperty("expected").GetProperty("signature_b64").GetString(), headers.Signature);
    }

    [Fact]
    public void TheSignatureBindsMethodPathQueryAndAudience()
    {
        var seed = Auth.LoadSeed(TestHelpers.ZeroSeedB64);
        var request = new AdminRequest("POST", "/admin/invite", "TEST", new Dictionary<string, object?>());
        var h = Auth.BuildAdminHeaders(request, "k", seed);
        var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        var sig = Convert.FromBase64String(h.Signature);
        foreach (var other in new[]
        {
            request with { Method = "PUT" },
            request with { Path = "/admin/revoke" },
            request with { Audience = "OTHER" },
            request with { Query = new Dictionary<string, IReadOnlyList<string>> { ["limit"] = new[] { "1" } } },
        })
        {
            var payload = Auth.AdminSigningPayload(other, "k", h.Timestamp, h.Nonce);
            Assert.False(SignatureAlgorithm.Ed25519.Verify(key.PublicKey, payload, sig));
        }
        Assert.Throws<ArgumentException>(() =>
            Auth.AdminSigningPayload(request with { Path = "admin/invite" }, "k", "t", "n"));
        var decoded = Encoding.UTF8.GetString(
            Auth.AdminSigningPayload(request with { Path = "/admin/attestations/a?b/revoke" }, "k", "t", "n"));
        Assert.Contains("\"path\":\"/admin/attestations/a?b/revoke\"", decoded);
    }

    [Fact]
    public async Task TheClientReadsTheNaPublicKeyOnceAndSignsWithIt()
    {
        var paths = new List<string>();
        var handler = new FuncHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            var body = req.RequestUri!.AbsolutePath == "/sovereign.json"
                ? "{\"network_authority\":{\"public_key\":\"NA-PUBLIC-KEY\"}}"
                : "{\"commitment_id\":\"c1\"}";
            return new HttpResponseMessage(req.RequestUri!.AbsolutePath == "/sovereign.json" ? HttpStatusCode.OK : HttpStatusCode.Created)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
        var client = new GenesisMeshClient(new ClientOptions
        {
            BaseUrl = "http://localhost", SigningKey = TestHelpers.ZeroSeedB64, KeyId = "k", HttpHandler = handler,
        });
        await client.Disclosure.Commit(new Dictionary<string, object?> { ["capability"] = "read" });
        await client.Disclosure.Commit(new Dictionary<string, object?> { ["capability"] = "read" });
        Assert.Equal(new[] { "/sovereign.json", "/admin/disclosure/commit", "/admin/disclosure/commit" }, paths);
    }
}
