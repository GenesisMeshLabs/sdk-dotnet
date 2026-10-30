namespace GenesisMesh;

/// <summary>Configuration for <see cref="GenesisMeshClient"/>.</summary>
public sealed class ClientOptions
{
    /// <summary>NA base URL, e.g. "http://127.0.0.1:9443". Set this or <see cref="BaseUrls"/>, not both.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Several NA instances sharing one database (high availability), used without a load balancer; set this or
    /// <see cref="BaseUrl"/>, not both. Requests go to one instance. An idempotent request moves to the next instance
    /// on a connection failure or a 502, 503 or 504. Any other request moves only when it never reached an instance:
    /// the connection could not be made, or the NA's key could not be read first because the instance did not answer
    /// or answered 502, 503 or 504. Otherwise it is not sent again, and the next request starts at the next
    /// instance.
    /// </summary>
    public IReadOnlyList<string>? BaseUrls { get; set; }

    /// <summary>Base64-encoded 32-byte Ed25519 seed (required for admin routes unless <see cref="Signer"/> is set).</summary>
    public string? SigningKey { get; set; }

    /// <summary>Key identifier sent in X-Admin-Key-Id (required for admin routes). Ignored when <see cref="Signer"/> is set.</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// The operator signer for admin routes, such as one backed by a key read from Key Vault or by an HSM. Takes
    /// precedence over <see cref="SigningKey"/> and <see cref="KeyId"/>; its key ID is sent.
    /// </summary>
    public ISigner? Signer { get; set; }

    /// <summary>
    /// The NA's public key, which admin signatures name as their audience
    /// (signature version 2). When unset it is read once from /sovereign.json
    /// (network_authority.public_key).
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// The timeout of each attempt: each request, and each read of the NA's key. A call that fails over or retries can
    /// take longer. Defaults to 10 s.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>
    /// Retries of idempotent requests after a connection failure or a 429, 502, 503 or 504, with exponential backoff.
    /// Off when unset. Requests that create state are never retried.
    /// </summary>
    public RetrySettings? Retry { get; set; }

    /// <summary>Extra headers on every request, such as for an API gateway in front of the NA.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// The handler requests go through, such as a <c>SocketsHttpHandler</c> configured for your network (set its
    /// <c>PooledConnectionLifetime</c> so that a long-lived client follows DNS changes) or one for unit tests. The client
    /// keeps it for its lifetime and does not dispose it.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> requests are sent with, such as a typed client from
    /// <c>IHttpClientFactory</c>. Takes precedence over <see cref="HttpHandler"/> and <see cref="Timeout"/>: its own
    /// <c>Timeout</c> applies to each attempt. The client does not dispose it. A handler in its pipeline must not retry
    /// POSTs: a repeated admin request carries the same signature and nonce, and the NA refuses it as a replay after
    /// the first may have been applied.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// 1.9.0: durable storage for signed execution records the NA has not admitted yet, such as a
    /// <see cref="FileOutbox"/> or a store of your own. With one, <see cref="Governance.GovernedAction"/> keeps every
    /// record until the NA admits it, and <see cref="EvidenceStoreClient.FlushPending"/> submits what is left. Give
    /// each process its own.
    /// </summary>
    public IOutbox<OutboxEntry>? Outbox { get; set; }
}

/// <summary>
/// Retries of idempotent requests (<see cref="ClientOptions.Retry"/>). The wait before retry <c>n</c> (from 0) is
/// <see cref="BaseDelay"/> times 2<sup>n</sup>, at most a minute. These waits do not follow <c>Retry-After</c>: a
/// refusal that names a wait carries it in <see cref="GenesisMeshException.RetryAfter"/>, for the caller to honour.
/// </summary>
public sealed class RetrySettings
{
    /// <summary>Extra attempts after the first, 0 to 10. 0 disables retries.</summary>
    public int Attempts { get; set; }

    /// <summary>The first wait, doubled for each further attempt: 0 to 60 s. Defaults to 200 ms.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}
