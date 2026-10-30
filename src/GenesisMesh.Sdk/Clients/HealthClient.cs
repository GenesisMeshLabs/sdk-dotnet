using System.Text.Json;

namespace GenesisMesh;

/// <summary>Liveness, readiness and health of the Network Authority (v0.60).</summary>
public sealed class HealthClient
{
    private readonly Transport _t;
    internal HealthClient(Transport t) => _t = t;

    /// <summary>
    /// Whether the process is alive, without checking its dependencies.
    /// GET /healthz
    /// </summary>
    public Task<LivenessStatus> Liveness(CancellationToken ct = default) =>
        _t.PublicGetAsync<LivenessStatus>("/healthz", ct);

    /// <summary>
    /// Whether the NA can serve: its database is writable at the expected schema, its key is loaded and signing, and
    /// in HA mode its rate limits are shared. A not-ready NA (503 <c>service_not_ready</c>) is returned with
    /// <see cref="GenesisMesh.Readiness.Ready"/> false and the failing checks, not thrown. With several
    /// <see cref="ClientOptions.BaseUrls"/>, a not-ready instance is passed over for the next. A probe answers at once:
    /// <see cref="ClientOptions.Retry"/> does not apply.
    /// GET /readyz
    /// </summary>
    public async Task<Readiness> Readiness(CancellationToken ct = default)
    {
        try
        {
            return await _t.PublicGetAsync<Readiness>("/readyz", retry: false, ct).ConfigureAwait(false);
        }
        catch (GenesisMeshException e) when (e.Status == 503 && e.Code == "service_not_ready")
        {
            return NotReady(e.Details);
        }
    }

    /// <summary>
    /// The network, its version, the health of the boundary policies and the evidence store's mode.
    /// GET /health
    /// </summary>
    public Task<NAHealth> Status(CancellationToken ct = default) =>
        _t.PublicGetAsync<NAHealth>("/health", ct);

    /// <summary>
    /// The readiness of every configured endpoint, each asked directly, without failover: for monitoring an HA
    /// deployment. An endpoint that does not answer, or answers 200 with a body that is not strict JSON, is reported
    /// unreachable, not thrown.
    /// GET /readyz on each endpoint
    /// </summary>
    public async Task<IReadOnlyList<EndpointReadiness>> Endpoints(CancellationToken ct = default) =>
        await Task.WhenAll(_t.BaseUrls.Select(url => Probe(url, ct))).ConfigureAwait(false);

    private async Task<EndpointReadiness> Probe(string baseUrl, CancellationToken ct)
    {
        try
        {
            var (status, readiness, details) = await _t.PublicGetAtAsync<Readiness>(baseUrl, "/readyz", ct).ConfigureAwait(false);
            if (status == 200)
                return new EndpointReadiness { BaseUrl = baseUrl, Reachable = true, Ready = readiness?.Ready == true, Readiness = readiness };
            return new EndpointReadiness
            {
                BaseUrl = baseUrl,
                Reachable = true,
                Ready = false,
                Readiness = details.ValueKind == JsonValueKind.Object ? NotReady(details) : null,
                Error = $"HTTP {status}",
            };
        }
        catch (Exception e) when (e is NetworkException or JsonException)
        {
            return new EndpointReadiness { BaseUrl = baseUrl, Reachable = false, Ready = false, Error = e.Message };
        }
    }

    /// <summary>The checks of a not-ready answer, as <see cref="GenesisMesh.Readiness"/>.</summary>
    private static Readiness NotReady(JsonElement details)
    {
        var readiness = details.ValueKind == JsonValueKind.Object
            ? Transport.Read<Readiness>(details) ?? new Readiness()
            : new Readiness();
        readiness.Status = "not_ready";
        return readiness;
    }
}
