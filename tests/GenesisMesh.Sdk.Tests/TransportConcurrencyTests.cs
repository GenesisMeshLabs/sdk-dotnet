using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// One client shared by concurrent requests, as an application uses it: failover moves once for requests that failed
/// together, a request goes where its key lookup ended, and a failed lookup is shared rather than repeated.
/// </summary>
public class TransportConcurrencyTests
{
    private const string A = "http://na-a:8443";
    private const string B = "http://na-b:8443";

    /// <summary>A handler where each host is down, hangs until the request times out, or answers.</summary>
    private sealed class Hosts(Func<string, string> behaviour) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!;
            Calls.Enqueue(url.Host + url.AbsolutePath);
            await Task.Yield();
            switch (behaviour(url.Host))
            {
                case "down":
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
                case "hang":
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException();
                default:
                    var json = url.AbsolutePath == "/sovereign.json"
                        ? """{"network_authority":{"public_key":"NA-KEY"}}"""
                        : """{"status":"ok"}""";
                    return new HttpResponseMessage(url.AbsolutePath == "/sovereign.json" ? HttpStatusCode.OK : HttpStatusCode.Created)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
            }
        }
    }

    private static GenesisMeshClient Client(Hosts hosts, string? audience = null, int timeoutMs = 10_000) =>
        new(new ClientOptions
        {
            BaseUrls = [A, B],
            SigningKey = TestHelpers.ZeroSeedB64,
            KeyId = "ops",
            Audience = audience,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            HttpHandler = hosts,
        });

    private static Dictionary<string, object?> Attestation() => new() { ["subject_id"] = "s", ["roles"] = new[] { "role:client" } };

    [Fact]
    public async Task ConcurrentFailuresAtOneInstance_MoveOnce_AndEveryRequestSucceeds()
    {
        var hosts = new Hosts(host => host == "na-a" ? "down" : "up");
        using var gm = Client(hosts, audience: "NA-KEY");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => gm.Health.Liveness())));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => gm.Attestation.Issue(Attestation()))));
        Assert.Equal(30, hosts.Calls.Count(c => c.StartsWith("na-b", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RequestsWaitingForTheKeyLookup_GoWhereTheLookupEnded()
    {
        var hosts = new Hosts(host => host == "na-a" ? "hang" : "up");
        using var gm = Client(hosts, timeoutMs: 300);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => gm.Attestation.Issue(Attestation()))));
        var calls = hosts.Calls.ToList();
        Assert.Equal(1, calls.Count(c => c == "na-a/sovereign.json"));
        Assert.Equal(1, calls.Count(c => c == "na-b/sovereign.json"));
        Assert.DoesNotContain("na-a/admin/attestations", calls);
        Assert.Equal(5, calls.Count(c => c == "na-b/admin/attestations"));
    }

    [Fact]
    public async Task AFailedKeyLookup_IsSharedByTheRequestsWaitingForIt()
    {
        var hosts = new Hosts(_ => "hang");
        using var gm = Client(hosts, timeoutMs: 300);
        var clock = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => gm.Attestation.Issue(Attestation()))).ToList();
        foreach (var call in calls) await Assert.ThrowsAsync<NetworkException>(() => call);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.InRange(hosts.Calls.Count(c => c.EndsWith("/sovereign.json", StringComparison.Ordinal)), 2, 4);
        Assert.DoesNotContain(hosts.Calls, c => c.EndsWith("/admin/attestations", StringComparison.Ordinal));
    }
}
