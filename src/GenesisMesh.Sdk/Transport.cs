using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GenesisMesh;

internal sealed class Transport : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;
    private readonly byte[]?    _seed;
    private readonly string     _keyId;

    internal Transport(ClientOptions opts)
    {
        _baseUrl = opts.BaseUrl.TrimEnd('/');
        _keyId   = opts.KeyId ?? "";
        _http    = opts.HttpHandler is not null
            ? new HttpClient(opts.HttpHandler, disposeHandler: false) { Timeout = opts.Timeout > TimeSpan.Zero ? opts.Timeout : TimeSpan.FromSeconds(10) }
            : new HttpClient                                          { Timeout = opts.Timeout > TimeSpan.Zero ? opts.Timeout : TimeSpan.FromSeconds(10) };

        if (!string.IsNullOrEmpty(opts.SigningKey))
            _seed = Auth.LoadSeed(opts.SigningKey);
    }

    internal async Task<T> AdminPostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        if (_seed is null)
            throw new InvalidOperationException($"genesismesh: signing key required for admin route {path}");

        var headers = Auth.BuildAdminHeaders(body, _keyId, _seed);
        return await DoAsync<T>(HttpMethod.Post, path, body, headers, ct).ConfigureAwait(false);
    }

    internal async Task AdminPostAsync(string path, object? body, CancellationToken ct = default)
    {
        if (_seed is null)
            throw new InvalidOperationException($"genesismesh: signing key required for admin route {path}");

        var headers = Auth.BuildAdminHeaders(body, _keyId, _seed);
        await DoAsync<object?>(HttpMethod.Post, path, body, headers, ct).ConfigureAwait(false);
    }

    internal async Task<T> PublicPostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        await DoAsync<T>(HttpMethod.Post, path, body, null, ct).ConfigureAwait(false);

    internal async Task<T> PublicGetAsync<T>(string path, CancellationToken ct = default) =>
        await DoAsync<T>(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);

    private async Task<T> DoAsync<T>(
        HttpMethod     method,
        string         path,
        object?        body,
        AdminHeaders?  adminHeaders,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + path);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Auth.SerializerOptions);
            req.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        if (adminHeaders is not null)
        {
            req.Headers.Add("X-Admin-Key-Id",    adminHeaders.KeyId);
            req.Headers.Add("X-Admin-Signature", adminHeaders.Signature);
            req.Headers.Add("X-Admin-Timestamp", adminHeaders.Timestamp);
            req.Headers.Add("X-Admin-Nonce",     adminHeaders.Nonce);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new NetworkException(ex);
        }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw ErrorParser.Parse((int)resp.StatusCode, raw);

            if (typeof(T) == typeof(object) || string.IsNullOrWhiteSpace(raw))
                return default!;

            return JsonSerializer.Deserialize<T>(raw, Auth.SerializerOptions)
                ?? throw new InvalidOperationException("genesismesh: empty response");
        }
    }

    public void Dispose() => _http.Dispose();
}
