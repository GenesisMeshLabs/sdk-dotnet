using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace GenesisMesh;

internal sealed class Transport : IDisposable
{
    /// <summary>Statuses a retry may outlast: the NA is busy or an instance is down.</summary>
    private static readonly HashSet<int> RetryableStatus = [429, 502, 503, 504];

    /// <summary>Statuses that move the next attempt to another instance.</summary>
    private static readonly HashSet<int> FailoverStatus = [502, 503, 504];

    private const string LookupUnavailable = "na_public_key_unavailable";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string[] _baseUrls;
    private int _active;
    private readonly ISigner? _signer;
    private readonly int _retryAttempts;
    private readonly TimeSpan _retryBaseDelay;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly object _audienceGate = new();
    private Task<string>? _audienceLookup;
    private volatile string? _audience;

    internal Transport(ClientOptions opts)
    {
        if (opts.BaseUrls is not null)
        {
            if (!string.IsNullOrEmpty(opts.BaseUrl))
                throw new ArgumentException("genesismesh: set BaseUrl or BaseUrls, not both", nameof(opts));
            if (opts.BaseUrls.Count == 0 || opts.BaseUrls.Any(u => !IsHttpUrl(u)))
                throw new ArgumentException("genesismesh: BaseUrls must give at least one http(s) URL", nameof(opts));
            _baseUrls = opts.BaseUrls.Select(u => u.TrimEnd('/')).ToArray();
        }
        else
        {
            if (!IsHttpUrl(opts.BaseUrl))
                throw new ArgumentException("genesismesh: BaseUrl must be an absolute http(s) URL, or set BaseUrls", nameof(opts));
            _baseUrls = [opts.BaseUrl.TrimEnd('/')];
        }

        var retry = opts.Retry ?? new RetrySettings();
        if (retry.Attempts is < 0 or > 10 || retry.BaseDelay < TimeSpan.Zero || retry.BaseDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(opts), "genesismesh: Retry needs 0 to 10 attempts and a BaseDelay of 0 to 60 s");
        _retryAttempts = retry.Attempts;
        _retryBaseDelay = retry.BaseDelay;

        var timeout = opts.Timeout > TimeSpan.Zero ? opts.Timeout : TimeSpan.FromSeconds(10);
        _ownsHttp = opts.HttpClient is null;
        _http = opts.HttpClient
            ?? (opts.HttpHandler is not null
                ? new HttpClient(opts.HttpHandler, disposeHandler: false) { Timeout = timeout }
                : new HttpClient { Timeout = timeout });
        _headers = opts.Headers?.ToArray() ?? [];

        _signer = opts.Signer
            ?? (string.IsNullOrEmpty(opts.SigningKey) ? null : new SeedSigner(Auth.LoadSeed(opts.SigningKey), opts.KeyId ?? ""));
        _audience = string.IsNullOrEmpty(opts.Audience) ? null : opts.Audience;
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Every configured NA endpoint, in preference order.</summary>
    internal IReadOnlyList<string> BaseUrls => _baseUrls;

    /// <summary>The endpoint requests go to now.</summary>
    internal string BaseUrl => Endpoint(Volatile.Read(ref _active));

    private string Endpoint(int position) => _baseUrls[(uint)position % (uint)_baseUrls.Length];

    /// <summary>
    /// Move past the endpoint at <paramref name="used"/>, the position a failed attempt was sent to. Attempts that
    /// failed at the same endpoint at the same time move once together; one that failed at an endpoint already left
    /// behind moves nothing.
    /// </summary>
    private void MoveFrom(int used)
    {
        if (_baseUrls.Length > 1) Interlocked.CompareExchange(ref _active, used + 1, used);
    }

    /// <summary>
    /// <paramref name="value"/> as one segment of a request path (1.8.0). A caller's ID goes into the path as given,
    /// so an ID that is empty, a dot segment, or holds '/', '\', '?', '#', '%' or a control character would sign and
    /// call another route: it is refused before anything is signed or sent.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not one path segment.</exception>
    internal static string PathSegment(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length == 0 || value is "." or ".."
            || value.Any(c => c is '/' or '\\' or '?' or '#' or '%' || char.IsControl(c)))
            throw new ArgumentException(
                $"genesismesh: {paramName} must be one path segment: not empty, \".\" or \"..\", and without '/', '\\', '?', '#', '%' or control characters",
                paramName);
        return value;
    }

    /// <summary>
    /// <paramref name="value"/> as the last segment of a request path (1.9.0): one <see cref="PathSegment"/> that does
    /// not start or end with white space. The URI drops white space at the end of the path, so the request would sign
    /// one path and send another.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not such a segment.</exception>
    internal static string LastPathSegment(string value, string paramName)
    {
        PathSegment(value, paramName);
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            throw new ArgumentException($"genesismesh: {paramName} must not start or end with white space", paramName);
        return value;
    }

    /// <summary>
    /// A resource ID as the tail of a request path (1.9.0): the NA serves such routes with the whole remaining path,
    /// so the ID's <c>/</c> separate segments, and each segment must pass <see cref="PathSegment"/>. An ID that starts
    /// or ends with white space is refused too: the URI drops white space at the end of the path, so the request would
    /// sign one path and send another.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="resourceId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceId"/> cannot be sent as a path.</exception>
    internal static string ResourcePath(string resourceId, string paramName)
    {
        ArgumentNullException.ThrowIfNull(resourceId, paramName);
        if (resourceId.Length > 0 && (char.IsWhiteSpace(resourceId[0]) || char.IsWhiteSpace(resourceId[^1])))
            throw new ArgumentException($"genesismesh: {paramName} must not start or end with white space", paramName);
        return string.Join('/', resourceId.Split('/').Select(segment => PathSegment(segment, paramName)));
    }

    /// <summary>
    /// The NA's public key for admin signatures, read once from /sovereign.json. Requests that need it at the same
    /// time share one lookup. A lookup that gets no answer throws <see cref="NetworkException"/>; an answer without
    /// the key throws <c>na_public_key_unavailable</c> with the answer's status. A failed lookup is tried again by the
    /// next request.
    /// </summary>
    private async Task<string> AudienceAsync(CancellationToken ct)
    {
        if (_audience is { } known) return known;
        Task<string> lookup;
        lock (_audienceGate)
        {
            if (_audienceLookup is { } running)
            {
                lookup = running;
            }
            else
            {
                lookup = _audienceLookup = LookupAsync();
                // Dropped as it fails, so the next request starts a new lookup even when every request that waited
                // for this one was cancelled first.
                _ = lookup.ContinueWith(failed =>
                {
                    lock (_audienceGate)
                    {
                        if (ReferenceEquals(_audienceLookup, failed)) _audienceLookup = null;
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        try
        {
            return await lookup.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (GenesisMeshException e) when (lookup.IsFaulted)
        {
            // Each waiting request gets an exception of its own: one instance rethrown by several threads at once
            // would share, and garble, one stack trace.
            throw ErrorMapping.Copy(e);
        }
    }

    /// <summary>
    /// One read of the NA's key from the active endpoint, bounded by the request timeout rather than one caller's
    /// cancellation, since several requests wait for it. An endpoint that does not answer, or answers 502, 503 or
    /// 504, is moved past.
    /// </summary>
    private async Task<string> LookupAsync()
    {
        var used = Volatile.Read(ref _active);
        int status;
        byte[] body;
        try
        {
            (status, body) = await GetAtAsync(Endpoint(used), "/sovereign.json", CancellationToken.None).ConfigureAwait(false);
        }
        catch (NetworkException)
        {
            MoveFrom(used);
            throw;
        }
        string? key = null;
        try
        {
            using var doc = StrictJsonReader.Parse(StrictJsonReader.DecodeUtf8(body));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("network_authority", out var authority)
                && authority.ValueKind == JsonValueKind.Object
                && authority.TryGetProperty("public_key", out var publicKey)
                && publicKey.ValueKind == JsonValueKind.String)
                key = publicKey.GetString();
        }
        catch (Exception e) when (e is JsonException or FormatException) { /* no key */ }
        if (status != 200 || string.IsNullOrEmpty(key))
        {
            if (FailoverStatus.Contains(status)) MoveFrom(used);
            throw ErrorMapping.ForStatus(status, LookupUnavailable,
                $"Could not read the NA public key from /sovereign.json (HTTP {status})");
        }
        _audience = key;
        return key;
    }

    /// <summary>A signed admin POST, sent again only when <paramref name="idempotent"/>.</summary>
    internal async Task<T> AdminPostAsync<T>(string path, object? body, bool idempotent, CancellationToken ct) =>
        Parse<T>(await SendAsync(new Spec(HttpMethod.Post, path, null, body, Admin: true, idempotent), ct).ConfigureAwait(false));

    internal Task<T> AdminPostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        AdminPostAsync<T>(path, body, idempotent: false, ct);

    internal async Task AdminPostAsync(string path, object? body, CancellationToken ct = default) =>
        Parse<object?>(await SendAsync(new Spec(HttpMethod.Post, path, null, body, Admin: true, Idempotent: false), ct).ConfigureAwait(false));

    /// <summary>A signed admin GET (the NA verifies the signature over an empty body).</summary>
    internal async Task<T> AdminGetAsync<T>(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct) =>
        Parse<T>(await SendAsync(new Spec(HttpMethod.Get, path, query, null, Admin: true, Idempotent: true), ct).ConfigureAwait(false));

    /// <summary>A signed admin GET whose body is returned as text, such as an NDJSON export.</summary>
    internal async Task<string> AdminGetTextAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct) =>
        Text(await SendAsync(new Spec(HttpMethod.Get, path, query, null, Admin: true, Idempotent: true), ct).ConfigureAwait(false));

    /// <summary>
    /// An unauthenticated POST, sent again only when <paramref name="idempotent"/>: every verification route, and
    /// <c>/disclosure/prove</c>, which reads no NA state, is.
    /// </summary>
    internal async Task<T> PublicPostAsync<T>(string path, object? body, bool idempotent, CancellationToken ct) =>
        Parse<T>(await SendAsync(new Spec(HttpMethod.Post, path, null, body, Admin: false, idempotent), ct).ConfigureAwait(false));

    internal Task<T> PublicGetAsync<T>(string path, CancellationToken ct = default) => PublicGetAsync<T>(path, retry: true, ct);

    /// <summary>An unauthenticated GET with a query (1.9.0).</summary>
    internal async Task<T> PublicGetAsync<T>(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct) =>
        Parse<T>(await SendAsync(new Spec(HttpMethod.Get, path, query, null, Admin: false, Idempotent: true), ct).ConfigureAwait(false));

    /// <summary>True when the client holds a signing key, so it can make admin requests (1.9.0).</summary>
    internal bool CanSign => _signer is not null;

    /// <summary>The key ID admin requests name (<see cref="ClientOptions.KeyId"/>, or the signer's); null without a signer.</summary>
    internal string? SignerKeyId => _signer?.KeyId;

    /// <summary>An unauthenticated GET; without <paramref name="retry"/>, failover only, as a probe needs.</summary>
    internal async Task<T> PublicGetAsync<T>(string path, bool retry, CancellationToken ct) =>
        Parse<T>(await SendAsync(new Spec(HttpMethod.Get, path, null, null, Admin: false, Idempotent: true, NoRetry: !retry), ct)
            .ConfigureAwait(false));

    /// <summary>An unauthenticated GET whose body is returned as text, for a caller that verifies it as received.</summary>
    internal async Task<string> PublicGetTextAsync(string path, CancellationToken ct) =>
        Text(await SendAsync(new Spec(HttpMethod.Get, path, null, null, Admin: false, Idempotent: true), ct).ConfigureAwait(false));

    /// <summary>
    /// An unauthenticated GET to one endpoint, without failover or retries: its status, and the body read as
    /// <typeparamref name="T"/> when the status is 200, or the error's <c>details</c> otherwise (undefined when it
    /// sent none).
    /// </summary>
    /// <exception cref="JsonException">A 200 answer whose body is not strict JSON.</exception>
    internal async Task<(int Status, T? Body, JsonElement Details)> PublicGetAtAsync<T>(string baseUrl, string path, CancellationToken ct)
    {
        var (status, bytes) = await GetAtAsync(baseUrl.TrimEnd('/'), path, ct).ConfigureAwait(false);
        if (status == 200)
        {
            var text = StrictJsonReader.DecodeUtf8(bytes);
            StrictJsonReader.Check(text);
            return (status, JsonSerializer.Deserialize<T>(text, Auth.SerializerOptions), default);
        }
        try
        {
            using var doc = StrictJsonReader.Parse(StrictJsonReader.DecodeUtf8(bytes));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("details", out var details))
                return (status, default, details.Clone());
        }
        catch (JsonException) { /* an answer without details */ }
        return (status, default, default);
    }

    /// <summary><paramref name="value"/> as JSON, as request bodies are written.</summary>
    internal static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Auth.SerializerOptions);

    /// <summary><paramref name="element"/> read as <typeparamref name="T"/>, as responses are read.</summary>
    internal static T? Read<T>(JsonElement element) => element.Deserialize<T>(Auth.SerializerOptions);

    /// <summary>One request: <paramref name="NoRetry"/> sends an idempotent request without <see cref="ClientOptions.Retry"/>, failover kept.</summary>
    private readonly record struct Spec(
        HttpMethod Method, string Path, IReadOnlyDictionary<string, string>? Query, object? Body, bool Admin, bool Idempotent,
        bool NoRetry = false);

    private sealed record Answer(int Status, byte[] Body, TimeSpan? RetryAfter)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    /// <summary>
    /// One request with failover and retries. An idempotent request may try every other endpoint once, plus the
    /// configured retries with backoff. Any other request moves to another endpoint only when it was never sent;
    /// after any other failure it is not sent again, and the next request starts at another endpoint.
    /// </summary>
    private async Task<Answer> SendAsync(Spec spec, CancellationToken ct)
    {
        var failovers = _baseUrls.Length - 1;
        var retries = spec.Idempotent && !spec.NoRetry ? _retryAttempts : 0;
        var failoversLeft = failovers;
        var retriesUsed = 0;
        while (true)
        {
            Answer? answer = null;
            Exception? error = null;
            int? used = null;
            try
            {
                var headers = spec.Admin ? await SignAsync(spec, ct).ConfigureAwait(false) : null;
                // Read after the key lookup, which may have moved past a down instance.
                used = Volatile.Read(ref _active);
                answer = await AttemptAsync(spec, headers, Endpoint(used.Value), ct).ConfigureAwait(false);
            }
            catch (NetworkException e)
            {
                error = e;
            }
            catch (GenesisMeshException e) when (e.Code == LookupUnavailable && RetryableStatus.Contains(e.Status))
            {
                // The NA's key could not be read from a busy or down instance: the request was never sent.
                error = e;
            }
            var status = answer?.Status ?? (error as GenesisMeshException)?.Status;
            var failoverWorthy = error is NetworkException || (status is { } s && FailoverStatus.Contains(s));
            // A failed key lookup has moved past its endpoint already.
            if (failoverWorthy && used is { } position) MoveFrom(position);
            // A request that may have reached an instance is sent elsewhere only when it is idempotent; one that was
            // never sent can always move on.
            var mayMove = spec.Idempotent || (error is NetworkException network ? network.ConnectFailed : error is not null);
            if (failoverWorthy && mayMove && failoversLeft > 0)
            {
                failoversLeft--;
                continue;
            }
            var retryable = error is NetworkException || (status is { } r && RetryableStatus.Contains(r));
            if (retryable && retriesUsed < retries)
            {
                await Task.Delay(Backoff(retriesUsed), ct).ConfigureAwait(false);
                retriesUsed++;
                failoversLeft = failovers;
                continue;
            }
            if (error is not null) ExceptionDispatchInfo.Throw(error);
            return answer!;
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var delay = _retryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delay, 60_000));
    }

    /// <summary>The admin headers of one attempt, signed afresh, after the NA's key is known.</summary>
    private async Task<AdminHeaders> SignAsync(Spec spec, CancellationToken ct)
    {
        if (_signer is null)
            throw new InvalidOperationException($"genesismesh: signing key required for admin route {spec.Path}");
        string audience;
        try
        {
            audience = await AudienceAsync(ct).ConfigureAwait(false);
        }
        catch (NetworkException e)
        {
            // The request itself was not sent, so it may move to another instance, idempotent or not.
            throw new NetworkException($"{spec.Method} {spec.Path} not sent: {e.Message}", e, connectFailed: true);
        }
        var query = spec.Query?.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[p.Value]);
        var signed = new AdminRequest(spec.Method.Method, spec.Path, audience,
            spec.Method == HttpMethod.Get ? null : spec.Body, query);
        return await Auth.BuildAdminHeadersAsync(signed, _signer, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>One request to <paramref name="baseUrl"/>.</summary>
    private async Task<Answer> AttemptAsync(Spec spec, AdminHeaders? admin, string baseUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(spec.Method, baseUrl + PathWithQuery(spec.Path, spec.Query));
        foreach (var (name, value) in _headers) req.Headers.TryAddWithoutValidation(name, value);
        if (admin is not null)
        {
            req.Headers.Add("X-Admin-Key-Id", admin.KeyId);
            req.Headers.Add("X-Admin-Signature", admin.Signature);
            req.Headers.Add("X-Admin-Timestamp", admin.Timestamp);
            req.Headers.Add("X-Admin-Nonce", admin.Nonce);
        }

        if (spec.Body is not null)
        {
            var json = JsonSerializer.Serialize(spec.Body, Auth.SerializerOptions);
            req.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        // The body is read in full before SendAsync returns, so a dropped body is a NetworkException there.
        using var resp = await SendOnceAsync(req, ct).ConfigureAwait(false);
        // The bytes, so a body that is not UTF-8 is refused rather than repaired.
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new Answer((int)resp.StatusCode, bytes, RetryAfter(resp.Headers.RetryAfter));
    }

    /// <summary>An unauthenticated GET to one endpoint: its status and body bytes.</summary>
    private async Task<(int Status, byte[] Body)> GetAtAsync(string baseUrl, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        foreach (var (name, value) in _headers) req.Headers.TryAddWithoutValidation(name, value);
        using var resp = await SendOnceAsync(req, ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 1.8.0: the caller cancelled; not a network error
        }
        catch (HttpRequestException e)
        {
            throw new NetworkException($"{req.Method} {req.RequestUri?.AbsolutePath} failed: {e.Message}", e, ConnectFailed(e));
        }
        catch (TaskCanceledException e)
        {
            throw new NetworkException(e); // the timeout: the request may have reached the NA
        }
    }

    /// <summary>True when no connection was made, so the request was never sent.</summary>
    private static bool ConnectFailed(HttpRequestException e) =>
        e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
        || e.InnerException is SocketException
        {
            SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable or SocketError.TryAgain,
        };

    /// <summary>The wait a <c>Retry-After</c> header names, as a delay or a date; null without one.</summary>
    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset? now = null)
    {
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - (now ?? DateTimeOffset.UtcNow);
            return wait > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Ceiling(wait.TotalSeconds)) : TimeSpan.Zero;
        }
        return null;
    }

    private static string PathWithQuery(string path, IReadOnlyDictionary<string, string>? query) =>
        query is null || query.Count == 0
            ? path
            : path + "?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static T Parse<T>(Answer answer)
    {
        if (!answer.Ok)
            throw ErrorMapping.Parse(answer.Status, Encoding.UTF8.GetString(answer.Body), answer.RetryAfter);
        var raw = StrictJsonReader.DecodeUtf8(answer.Body);

        if (typeof(T) == typeof(object) || string.IsNullOrWhiteSpace(raw))
            return default!;

        // v1.2.0: refuse JSON every implementation would not read alike.
        StrictJsonReader.Check(raw);
        return JsonSerializer.Deserialize<T>(raw, Auth.SerializerOptions)
            ?? throw new InvalidOperationException("genesismesh: empty response");
    }

    private static string Text(Answer answer)
    {
        if (!answer.Ok)
            throw ErrorMapping.Parse(answer.Status, Encoding.UTF8.GetString(answer.Body), answer.RetryAfter);
        return StrictJsonReader.DecodeUtf8(answer.Body);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
