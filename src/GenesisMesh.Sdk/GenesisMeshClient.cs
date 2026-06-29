namespace GenesisMesh;

/// <summary>
/// Entry point for the Genesis Mesh .NET SDK.
/// </summary>
/// <example>
/// <code>
/// var client = new GenesisMeshClient(new ClientOptions
/// {
///     BaseUrl    = "http://127.0.0.1:9443",
///     SigningKey = File.ReadLines("operator.key").First(l => l.Length > 0 &amp;&amp; !l.StartsWith('#')),
///     KeyId      = "operator-local",
/// });
/// </code>
/// </example>
public sealed class GenesisMeshClient : IDisposable
{
    private readonly Transport _transport;

    /// <summary>Agreement lifecycle — offer, counter, accept, verify.</summary>
    public AgreementClient   Agreement   { get; }

    /// <summary>Membership attestations — issue, revoke, recognition policy.</summary>
    public AttestationClient Attestation { get; }

    /// <summary>Capability boundary decisions — decide, verify.</summary>
    public BoundaryClient    Boundary    { get; }

    /// <summary>Consensus voting and proofs — vote, proof, verify.</summary>
    public ConsensusClient   Consensus   { get; }

    /// <summary>Data usage licensing — policy, intent, verify.</summary>
    public DataUsageClient   DataUsage   { get; }

    /// <summary>Selective capability disclosure — commit, nullifier, prove, verify.</summary>
    public DisclosureClient  Disclosure  { get; }

    /// <summary>Trust evidence — build and verify.</summary>
    public EvidenceClient    Evidence    { get; }

    public GenesisMeshClient(ClientOptions opts)
    {
        _transport  = new Transport(opts);
        Agreement   = new AgreementClient(_transport);
        Attestation = new AttestationClient(_transport);
        Boundary    = new BoundaryClient(_transport);
        Consensus   = new ConsensusClient(_transport);
        DataUsage   = new DataUsageClient(_transport);
        Disclosure  = new DisclosureClient(_transport);
        Evidence    = new EvidenceClient(_transport);
    }

    public void Dispose() => _transport.Dispose();
}

/// <summary>Configuration for <see cref="GenesisMeshClient"/>.</summary>
public sealed class ClientOptions
{
    /// <summary>NA base URL, e.g. "http://127.0.0.1:9443".</summary>
    public string BaseUrl    { get; set; } = "";

    /// <summary>Base64-encoded 32-byte Ed25519 seed (required for admin routes).</summary>
    public string? SigningKey { get; set; }

    /// <summary>Key identifier sent in X-Admin-Key-Id (required for admin routes).</summary>
    public string? KeyId      { get; set; }

    /// <summary>HTTP request timeout. Defaults to 10 s.</summary>
    public TimeSpan Timeout   { get; set; }

    /// <summary>Custom HTTP message handler, primarily for unit testing.</summary>
    public HttpMessageHandler? HttpHandler { get; set; }
}
