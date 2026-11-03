using System.Text.Json;

namespace GenesisMesh;

/// <summary>What a grant use records (<see cref="GrantRecords.SignUse"/>).</summary>
public sealed record GrantUseInput
{
    /// <summary>The grant used.</summary>
    public required CapabilityGrant Grant { get; init; }

    /// <summary>The use's position in the grant's use chain: 1 for the first use.</summary>
    public required int UseSequence { get; init; }

    /// <summary>The previous use; null for the first use, or give <see cref="PreviousDigest"/>.</summary>
    public GrantExecutionEvidence? Previous { get; init; }

    /// <summary>The previous use's digest, when <see cref="Previous"/> is not held.</summary>
    public string? PreviousDigest { get; init; }

    /// <summary>One of the grant's capabilities.</summary>
    public required string Capability { get; init; }

    /// <summary>The request's parameters: inside the grant's envelope. Default none.</summary>
    public IReadOnlyDictionary<string, object?>? RequestParameters { get; init; }

    /// <summary>The resource: an identifier, never a value.</summary>
    public string? ResourceId { get; init; }

    /// <summary>A value of <see cref="ResourceActions"/>, given with <see cref="ResourceId"/>.</summary>
    public string? ResourceAction { get; init; }

    /// <summary>When the action ran; default now.</summary>
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>The latest time token the holder saw, exactly as the NA signed it.</summary>
    public required JsonElement TimeToken { get; init; }

    /// <summary>A value of <see cref="GrantUseOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Up to 1024 characters; no secret material.</summary>
    public string? OutcomeDetail { get; init; }

    /// <summary>Metadata of the action: identifiers and versions only. Default none.</summary>
    public IReadOnlyDictionary<string, object?>? ExecutionParameters { get; init; }

    /// <summary>The use's ID; default a new UUID.</summary>
    public string? UseId { get; init; }
}

/// <summary>What a time token request asks for (<see cref="GrantRecords.SignTimeTokenRequest"/>).</summary>
public sealed record TimeTokenRequestInput
{
    /// <summary>The grant.</summary>
    public required string GrantId { get; init; }

    /// <summary>The uses the holder has made (0 before the first).</summary>
    public required int UseSequence { get; init; }

    /// <summary>The digest of the last of them; null for none.</summary>
    public string? UseHeadDigest { get; init; }

    /// <summary>When the holder asks; default now. Must be within 300 seconds of the NA's clock.</summary>
    public DateTimeOffset? RequestedAt { get; init; }
}

/// <summary>
/// The holder's side of a pre-issued grant (Genesis Mesh 1.6.0; 1.9.0 in this SDK), as the TypeScript SDK's
/// <c>grantUseRecord</c>, <c>signGrantUse</c> and <c>signTimeTokenRequest</c>: a use and a time token request, signed by
/// the grant's bearer key, each refused before signing as the NA would refuse it.
/// </summary>
public static class GrantRecords
{
    /// <summary>The unsigned use for <paramref name="input"/>, executed by the grant's bearer sovereign.</summary>
    /// <param name="input">What the use records.</param>
    /// <exception cref="StrictJsonException">A parameter holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    public static GrantExecutionEvidence UseRecord(GrantUseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Grant);
        return new GrantExecutionEvidence
        {
            UseId = input.UseId ?? Guid.NewGuid().ToString(),
            GrantId = input.Grant.GrantId,
            GrantDigest = GrantRules.GrantDigest(input.Grant),
            UseSequence = input.UseSequence,
            PrevUseDigest = input.Previous is { } previous ? GrantRules.UseDigest(previous) : input.PreviousDigest,
            ExecutorSovereignId = input.Grant.BearerSovereignId,
            Capability = input.Capability,
            RequestParameters = ExecutionRecorder.Element(input.RequestParameters ?? new Dictionary<string, object?>()),
            ResourceId = input.ResourceId,
            ResourceAction = input.ResourceAction,
            ExecutedAt = Canonical.Timestamp(input.ExecutedAt ?? DateTimeOffset.UtcNow),
            TimeToken = input.TimeToken,
            Outcome = input.Outcome,
            OutcomeDetail = input.OutcomeDetail,
            ExecutionParameters = ExecutionRecorder.Element(input.ExecutionParameters ?? new Dictionary<string, object?>()),
        };
    }

    /// <summary>
    /// Throws what the NA would refuse an unsigned use for, in its order: a value it cannot read, a use its model does
    /// not take, then secret material.
    /// </summary>
    /// <param name="record">The unsigned use.</param>
    /// <exception cref="StrictJsonException">The use holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    /// <exception cref="GrantRecordException">
    /// <c>grant_use_malformed</c>: a field is missing, empty, too long or of another type (a use ID that is not a
    /// lowercase UUID, <c>prev_use_digest</c> on the first use or missing after it, a resource without its action, a
    /// time token that is not one). <c>grant_use_secret_material</c>: a parameter or the outcome detail holds secret
    /// material.
    /// </exception>
    public static void CheckUse(GrantExecutionEvidence record) => Checked(record);

    private static JsonElement Checked(GrantExecutionEvidence record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var unsigned = OutOfBandRecords.Signable(record);
        if (!OfflineVerifier.ValidGrantUse(unsigned))
            throw new GrantRecordException("grant_use_malformed",
                "the use does not match the GrantExecutionEvidence model (a field missing, empty, too long or of another type)");
        if (GrantRules.UseSecretProblem(record) is { } secret) throw new GrantRecordException("grant_use_secret_material", secret);
        return unsigned;
    }

    /// <summary>A use built (<see cref="UseRecord"/>), checked (<see cref="CheckUse"/>) and signed with the bearer key.</summary>
    /// <param name="input">What the use records.</param>
    /// <param name="signer">The grant's bearer key: its <see cref="ISigner.KeyId"/> is the grant's <c>bearer_key_id</c>.</param>
    /// <param name="ct">Cancels the signing.</param>
    /// <exception cref="StrictJsonException">The use holds a value the NA would refuse as <c>invalid_json</c>.</exception>
    /// <exception cref="GrantRecordException">The NA would refuse the use (<see cref="CheckUse"/>).</exception>
    public static async Task<GrantExecutionEvidence> SignUse(GrantUseInput input, ISigner signer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var record = UseRecord(input);
        var unsigned = Checked(record);
        record.Signature = await OutOfBandRecords.Sign(unsigned, signer, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>A time token request, signed with the bearer key (beta).</summary>
    /// <param name="input">What it asks for.</param>
    /// <param name="signer">The grant's bearer key.</param>
    /// <param name="ct">Cancels the signing.</param>
    /// <exception cref="GrantRecordException">
    /// <c>time_token_request_malformed</c>: the NA's model would not take it (a head digest given before the first use,
    /// or missing after it).
    /// </exception>
    public static async Task<TimeTokenRequest> SignTimeTokenRequest(TimeTokenRequestInput input, ISigner signer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(signer);
        var request = new TimeTokenRequest
        {
            GrantId = input.GrantId,
            UseSequence = input.UseSequence,
            UseHeadDigest = input.UseHeadDigest,
            RequestedAt = Canonical.Timestamp(input.RequestedAt ?? DateTimeOffset.UtcNow),
        };
        var unsigned = OutOfBandRecords.Signable(request);
        if (!OfflineVerifier.ValidTimeTokenRequest(unsigned))
            throw new GrantRecordException("time_token_request_malformed", "the request does not match the TimeTokenRequest model");
        request.Signature = await OutOfBandRecords.Sign(unsigned, signer, ct).ConfigureAwait(false);
        return request;
    }
}
