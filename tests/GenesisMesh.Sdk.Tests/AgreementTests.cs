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
            BaseUrl = "http://localhost",
            SigningKey = TestHelpers.ZeroSeedB64,
            Audience = "TEST",
            KeyId = "test-key",
            HttpHandler = h,
        });

    private static GenesisMeshClient PublicClient(HttpMessageHandler h) =>
        new(new ClientOptions
        {
            BaseUrl = "http://localhost",
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
            Roles = ["role:client"],
            ValidFrom = "2026-01-01T00:00:00.000Z",
            ValidUntil = "2027-01-01T00:00:00.000Z",
            ExpiresAt = "2026-01-08T00:00:00.000Z",
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
            Roles = ["role:client"],
            ValidFrom = "2026-01-01T00:00:00.000Z",
            ValidUntil = "2027-01-01T00:00:00.000Z",
            ExpiresAt = "2026-01-08T00:00:00.000Z",
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
        using var handler = new FuncHandler(req =>
        {
            bodyJson = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var json = JsonSerializer.Serialize(
                new
                {
                    agreement_id = "agr-1",
                    agreed_terms = new { capabilities = new[] { "read" } },
                    established_at = "2026-07-01T12:00:00+00:00",
                },
                Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var offer = new OfferRecord { OfferId = "off-1" };
        var agr = await AdminClient(handler).Agreement.Accept(offer);
        Assert.Equal("agr-1", agr.AgreementId);
        Assert.Equal("2026-07-01T12:00:00+00:00", agr.EstablishedAt);
#pragma warning disable CS0618 // the obsolete names are filled from the NA's fields
        Assert.Equal(new[] { "read" }, agr.Capabilities);
        Assert.Equal(agr.EstablishedAt, agr.CreatedAt);
#pragma warning restore CS0618
        Assert.Contains("\"offer\"", bodyJson);
    }

    [Fact]
    public async Task AcceptCounter_SendsCounterAndOriginalOffer()
    {
        // The NA accepts a counter-offer only as {"counter", "original_offer"}; Accept sends {"offer"},
        // which it refuses for a counter (422 accept_rejected).
        JsonElement? body = null;
        using var handler = new FuncHandler(req =>
        {
            Assert.Equal("/admin/agreements/accept", req.RequestUri!.PathAndQuery);
            body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            var json = JsonSerializer.Serialize(new { agreement_id = "agr-2" }, Auth.SerializerOptions);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
        });
        var offer = new OfferRecord { OfferId = "off-1" };
        var counter = new OfferRecord { OfferId = "off-counter" };
        var agr = await AdminClient(handler).Agreement.AcceptCounter(counter, offer);
        Assert.Equal("agr-2", agr.AgreementId);
        Assert.Equal(new[] { "counter", "original_offer" }, body!.Value.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("off-counter", body.Value.GetProperty("counter").GetProperty("offer_id").GetString());
        Assert.Equal("off-1", body.Value.GetProperty("original_offer").GetProperty("offer_id").GetString());
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
        await Assert.ThrowsAsync<NotFoundException>(() =>
            AdminClient(handler).Agreement.Offer(new CapabilityOffer
            {
                ResponderSovereignId = "NA-B",
                Capabilities = ["read"],
                Roles = ["role:client"],
                ValidFrom = "2026-01-01T00:00:00.000Z",
                ValidUntil = "2027-01-01T00:00:00.000Z",
                ExpiresAt = "2026-01-08T00:00:00.000Z",
            }));
    }
}
