namespace GenesisMesh;

/// <summary>Data usage licensing — policy, intent, verify.</summary>
public sealed class DataUsageClient
{
    private readonly Transport _t;
    internal DataUsageClient(Transport t) => _t = t;

    /// <summary>
    /// Creates and signs a data license policy.
    /// POST /admin/data-usage/policy
    /// </summary>
    public Task<DataLicensePolicy> CreatePolicy(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<DataLicensePolicy>("/admin/data-usage/policy", body, ct);

    /// <summary>
    /// Creates and signs a data access intent.
    /// POST /admin/data-usage/intent
    /// <para>
    /// Each source in sources requires source_id, source_type, and owner_sovereign_id.
    /// source_type must be "personal" | "proprietary" | "public" | "synthetic".
    /// </para>
    /// </summary>
    public Task<DataAccessIntent> CreateIntent(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<DataAccessIntent>("/admin/data-usage/intent", body, ct);

    /// <summary>
    /// Returns the currently active data license policy (public route).
    /// GET /data-usage/policy
    /// </summary>
    public Task<DataLicensePolicy> GetPolicy(CancellationToken ct = default) =>
        _t.PublicGetAsync<DataLicensePolicy>("/data-usage/policy", ct);

    /// <summary>
    /// Verifies an intent against a policy (public route).
    /// POST /data-usage/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/data-usage/verify", body, ct);
}
