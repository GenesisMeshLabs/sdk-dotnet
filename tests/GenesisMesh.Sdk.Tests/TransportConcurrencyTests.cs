using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// One client shared by concurrent requests, as an application uses it: failover moves once for requests that failed
/// together, a request goes where its key lookup ended, and a failed lookup is shared rather than repeated. Where an
/// outcome depends on when a host fails, it fails when the test or the last request to reach it lets it, not on a
/// clock, so a slow runner cannot change what is tested.
/// </summary>
public class TransportConcurrencyTests
{
    private const string A = "http://na-a:8443";
    private const string B = "http://na-b:8443";

    /// <summary>
    /// A handler where each host is down, hangs until the request times out, times out once <see cref="Release"/> is
    /// set, or answers. With <paramref name="together"/>, a host that is down refuses no request before that many have
    /// reached it.
    /// </summary>
    private sealed class Hosts(Func<string, string> behaviour, int together = 0) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _down;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!;
            Calls.Enqueue(url.Host + url.AbsolutePath);
            await Task.Yield();
            switch (behaviour(url.Host))
            {
                case "down":
                    if (together > 0)
                    {
                        if (Interlocked.Increment(ref _down) == together) Release.TrySetResult();
                        await Release.Task.WaitAsync(ct);
                    }
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");
                case "hang":
                    await Task.Delay(Timeout.Infinite, ct);
                    throw new InvalidOperationException();
                case "timeout":
                    // A TaskCanceledException, as the client's timeout gives; HttpClient passes it on unchanged.
                    await Release.Task.WaitAsync(ct);
                    throw new TaskCanceledException("The request timed out.");
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

    /// <summary>The client's key, signing only once <paramref name="release"/> completes, as a remote signer may wait.</summary>
    private sealed class HeldSigner(Task release) : ISigner
    {
        private readonly SeedSigner _key = new(TestHelpers.ZeroSeedB64, "ops");

        public string KeyId => _key.KeyId;

        public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken);
            return await _key.SignAsync(message, cancellationToken);
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
        // The first instance refuses the twenty only once all of them have reached it, however late a request starts.
        var hosts = new Hosts(host => host == "na-a" ? "down" : "up", together: 20);
        using var gm = Client(hosts, audience: "NA-KEY");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => gm.Health.Liveness())))
            .WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => gm.Attestation.Issue(Attestation()))))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(20, hosts.Calls.Count(c => c.StartsWith("na-a", StringComparison.Ordinal)));
        Assert.Equal(30, hosts.Calls.Count(c => c.StartsWith("na-b", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RequestsWaitingForTheKeyLookup_GoWhereTheLookupEnded()
    {
        var hosts = new Hosts(host => host == "na-a" ? "timeout" : "up");
        using var gm = Client(hosts);
        // Started together, all five wait for the first lookup, which times out only once they all do.
        var calls = Enumerable.Range(0, 5).Select(_ => gm.Attestation.Issue(Attestation())).ToList();
        hosts.Release.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));
        var sent = hosts.Calls.ToList();
        Assert.Equal(1, sent.Count(c => c == "na-a/sovereign.json"));
        Assert.Equal(1, sent.Count(c => c == "na-b/sovereign.json"));
        Assert.DoesNotContain("na-a/admin/attestations", sent);
        Assert.Equal(5, sent.Count(c => c == "na-b/admin/attestations"));
    }

    [Fact]
    public async Task ARequestWaitingForItsSignature_GoesWhereTheClientMovedMeanwhile()
    {
        var hosts = new Hosts(host => host == "na-a" ? "down" : "up");
        var signed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gm = new GenesisMeshClient(new ClientOptions
        {
            BaseUrls = [A, B],
            Signer = new HeldSigner(signed.Task),
            Audience = "NA-KEY",
            HttpHandler = hosts,
        });
        var issue = gm.Attestation.Issue(Attestation());
        // Another request finds the first instance down and moves the client on while this one is being signed.
        await gm.Health.Liveness().WaitAsync(TimeSpan.FromSeconds(30));
        signed.SetResult();
        await issue.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["na-a/healthz", "na-b/healthz", "na-b/admin/attestations"], hosts.Calls.ToList());
    }

    [Fact]
    public async Task AFailedKeyLookup_IsSharedByTheRequestsWaitingForIt()
    {
        // Every instance hangs until the 300 ms timeout: a slow runner makes the test slower, not its outcome different.
        var hosts = new Hosts(_ => "hang");
        using var gm = Client(hosts, timeoutMs: 300);
        // Started together, all eight wait for the first lookup; each would make its own if the lookup were not shared.
        var calls = Enumerable.Range(0, 8).Select(_ => gm.Attestation.Issue(Attestation())).ToList();
        foreach (var call in calls) await Assert.ThrowsAsync<NetworkException>(() => call.WaitAsync(TimeSpan.FromSeconds(30)));
        // One lookup at each instance, in turn; a request that starts or resumes after the second failed may start a third
        // or a fourth.
        var lookups = hosts.Calls.Where(c => c.EndsWith("/sovereign.json", StringComparison.Ordinal)).ToList();
        Assert.InRange(lookups.Count, 2, 4);
        Assert.Equal(["na-a/sovereign.json", "na-b/sovereign.json"], lookups.Take(2));
        Assert.DoesNotContain(hosts.Calls, c => c.EndsWith("/admin/attestations", StringComparison.Ordinal));
    }
}
