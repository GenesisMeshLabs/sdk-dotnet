using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

public class AgreementTests
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
    public async Task Offer_PostsToCorrectPath_AndReturnsOfferRecord()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/agreements/offer", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(
                new OfferRecord { OfferId = "off-42", OffererSovereignId = "NA-A", ResponderSovereignId = "NA-B" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var rec = await AdminClient(handler).Agreement.Offer(new CapabilityOffer
        {
            ResponderSovereignId = "NA-B",
            Capabilities = ["read"],
            Roles        = ["role:client"],
            ValidFrom    = "2026-01-01T00:00:00.000Z",
            ValidUntil   = "2027-01-01T00:00:00.000Z",
            ExpiresAt    = "2026-01-08T00:00:00.000Z",
        });
        Assert.Equal("off-42", rec.OfferId);
        Assert.Equal("NA-B", rec.ResponderSovereignId);
    }

    [Fact]
    public async Task Offer_SendsAdminKeyIdHeader()
    {
        string? capturedKeyId = null;
        using var handler = new FuncHandler(req =>
        {
            capturedKeyId = req.Headers.GetValues("X-Admin-Key-Id").FirstOrDefault();
            var json = JsonSerializer.Serialize(new OfferRecord { OfferId = "off-1" }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        await AdminClient(handler).Agreement.Offer(new CapabilityOffer
        {
            ResponderSovereignId = "NA-B",
            Capabilities = ["read"],
            Roles        = ["role:client"],
            ValidFrom    = "2026-01-01T00:00:00.000Z",
            ValidUntil   = "2027-01-01T00:00:00.000Z",
            ExpiresAt    = "2026-01-08T00:00:00.000Z",
        });
        Assert.Equal("test-key", capturedKeyId);
    }

    [Fact]
    public async Task Counter_PostsToCorrectPath_AndReturnsOfferRecord()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/agreements/counter", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new OfferRecord { OfferId = "off-counter" }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var rec = await AdminClient(handler).Agreement.Counter(
            new Dictionary<string, object?> { ["offer_id"] = "off-1", ["responder_sovereign_id"] = "NA-B" });
        Assert.Equal("off-counter", rec.OfferId);
    }

    [Fact]
    public async Task Accept_WrapsOfferAndReturnsAgreementRecord()
    {
        string? bodyJson = null;
        using var handler = new FuncHandler(async req =>
        {
            bodyJson = await req.Content!.ReadAsStringAsync();
            var json = JsonSerializer.Serialize(
                new AgreementRecord { AgreementId = "agr-1", Status = "active" },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var offer = new OfferRecord { OfferId = "off-1" };
        var agr   = await AdminClient(handler).Agreement.Accept(offer);
        Assert.Equal("agr-1", agr.AgreementId);
        Assert.Equal("active", agr.Status);
        Assert.Contains("\"offer\"", bodyJson);
    }

    [Fact]
    public async Task Verify_UsesPublicRoute_NoSigningKeyRequired()
    {
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/agreements/verify", req.RequestUri!.PathAndQuery);
            var json = JsonSerializer.Serialize(new VerifyResult { Valid = true }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var result = await PublicClient(handler).Agreement.Verify(
            new Dictionary<string, object?> { ["agreement"] = new { } });
        Assert.True(result.Valid);
    }

    [Fact]
    public async Task Offer_404Response_ThrowsGenesisMeshException()
    {
        using var handler = new FuncHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"not found\",\"code\":\"NOT_FOUND\"}}",
                    Encoding.UTF8, "application/json"),
            });
        await Assert.ThrowsAsync<GenesisMeshException>(() =>
            AdminClient(handler).Agreement.Offer(new CapabilityOffer
            {
                ResponderSovereignId = "NA-B",
                Capabilities = ["read"],
                Roles        = ["role:client"],
                ValidFrom    = "2026-01-01T00:00:00.000Z",
                ValidUntil   = "2027-01-01T00:00:00.000Z",
                ExpiresAt    = "2026-01-08T00:00:00.000Z",
            }));
    }
}
