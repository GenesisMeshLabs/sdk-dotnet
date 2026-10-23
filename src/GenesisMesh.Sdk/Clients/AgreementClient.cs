namespace GenesisMesh;

/// <summary>Agreement lifecycle — offer, counter, accept, verify.</summary>
public sealed class AgreementClient
{
    private readonly Transport _t;
    internal AgreementClient(Transport t) => _t = t;

    /// <summary>
    /// Creates and signs a capability offer.
    /// POST /admin/agreements/offer
    /// <para>Returns an <see cref="OfferRecord"/>; pass it to <see cref="Accept"/> to complete the handshake.</para>
    /// </summary>
    public Task<OfferRecord> Offer(CapabilityOffer body, CancellationToken ct = default) =>
        _t.AdminPostAsync<OfferRecord>("/admin/agreements/offer", body, ct);

    /// <summary>
    /// Creates a counter-offer against an existing offer.
    /// POST /admin/agreements/counter
    /// </summary>
    public Task<OfferRecord> Counter(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.AdminPostAsync<OfferRecord>("/admin/agreements/counter", body, ct);

    /// <summary>
    /// Accepts an offer and returns a signed agreement.
    /// POST /admin/agreements/accept
    /// <para>
    /// The full <paramref name="offer"/> object is sent as {"offer": ...} so the NA can verify its own signature.
    /// To accept a counter-offer, use <see cref="AcceptCounter"/>: the NA refuses a counter sent as an offer
    /// (422 accept_rejected).
    /// </para>
    /// </summary>
    public Task<AgreementRecord> Accept(object offer, CancellationToken ct = default) =>
        _t.AdminPostAsync<AgreementRecord>("/admin/agreements/accept",
            new Dictionary<string, object?> { ["offer"] = offer }, ct);

    /// <summary>
    /// Accepts a counter-offer and returns a signed agreement (1.8.0).
    /// POST /admin/agreements/accept
    /// <para>
    /// Sent as {"counter": ..., "original_offer": ...}: the <paramref name="counter"/> returned by
    /// <see cref="Counter"/> and the <paramref name="originalOffer"/> it answers, both as received.
    /// </para>
    /// </summary>
    public Task<AgreementRecord> AcceptCounter(object counter, object originalOffer, CancellationToken ct = default) =>
        _t.AdminPostAsync<AgreementRecord>("/admin/agreements/accept",
            new Dictionary<string, object?> { ["counter"] = counter, ["original_offer"] = originalOffer }, ct);

    /// <summary>
    /// Verifies agreement signatures (public route).
    /// POST /agreements/verify
    /// </summary>
    public Task<VerifyResult> Verify(IDictionary<string, object?> body, CancellationToken ct = default) =>
        _t.PublicPostAsync<VerifyResult>("/agreements/verify", body, ct);
}
