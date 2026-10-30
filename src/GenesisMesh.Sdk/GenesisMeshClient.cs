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
    public AgreementClient Agreement { get; }

    /// <summary>Membership attestations — issue, revoke, recognition policy.</summary>
    public AttestationClient Attestation { get; }

    /// <summary>Capability boundary decisions — evaluate, decide, verify.</summary>
    public BoundaryClient Boundary { get; }

    /// <summary>Consensus voting and proofs — vote, proof, verify.</summary>
    public ConsensusClient Consensus { get; }

    /// <summary>Data usage licensing — policy, intent, verify.</summary>
    public DataUsageClient DataUsage { get; }

    /// <summary>Selective capability disclosure — commit, nullifier, prove, verify.</summary>
    public DisclosureClient Disclosure { get; }

    /// <summary>Trust evidence — build and verify.</summary>
    public EvidenceClient Evidence { get; }

    /// <summary>
    /// The evidence store (1.9.0): submit signed execution evidence, read where a resource's chain ends, search what is
    /// stored and register executor keys.
    /// </summary>
    public EvidenceStoreClient EvidenceStore { get; }

    /// <summary>The boundary policy lifecycle: validate, publish, activate, deactivate, list and verify (v0.58).</summary>
    public PolicyClient Policy { get; }

    /// <summary>Liveness, readiness and health of the NA, and of each configured endpoint (v0.60).</summary>
    public HealthClient Health { get; }

    /// <summary>The NA's own keys: its key history, fetched and verified (v1.5.0).</summary>
    public NetworkAuthorityClient NA { get; }

    /// <summary>A client of the NA at <see cref="ClientOptions.BaseUrl"/>.</summary>
    /// <param name="opts">The NA's URL and, for admin routes, the operator key.</param>
    public GenesisMeshClient(ClientOptions opts)
    {
        _transport = new Transport(opts);
        Agreement = new AgreementClient(_transport);
        Attestation = new AttestationClient(_transport);
        Boundary = new BoundaryClient(_transport);
        Consensus = new ConsensusClient(_transport);
        DataUsage = new DataUsageClient(_transport);
        Disclosure = new DisclosureClient(_transport);
        Evidence = new EvidenceClient(_transport);
        EvidenceStore = new EvidenceStoreClient(_transport);
        Policy = new PolicyClient(_transport);
        Health = new HealthClient(_transport);
        NA = new NetworkAuthorityClient(_transport);
    }

    /// <summary>Releases the HTTP client. A <see cref="ClientOptions.HttpHandler"/> stays the caller's to dispose.</summary>
    public void Dispose() => _transport.Dispose();
}
