using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>Base error for all NA API errors.</summary>
public class GenesisMeshException : Exception
{
    /// <summary>The HTTP status of the NA's response.</summary>
    public int Status { get; }

    /// <summary>The NA's error code, such as <c>admin_auth_failed</c>; empty when the response named none.</summary>
    public string Code { get; }

    /// <summary>The NA's error message, or the response body when it is not an NA error.</summary>
    public string Detail { get; }

    /// <summary>
    /// The NA's <c>details</c> of the error, such as the failing checks of a not-ready NA; undefined
    /// (<see cref="JsonValueKind.Undefined"/>) when it sent none.
    /// </summary>
    public JsonElement Details { get; internal set; }

    /// <summary>
    /// How long the NA asked the caller to wait before trying again (<c>Retry-After</c>), such as with a 429 or 503;
    /// null when it named no wait.
    /// </summary>
    public TimeSpan? RetryAfter { get; internal set; }

    /// <summary>An error the NA answered with <paramref name="status"/>, <paramref name="code"/> and <paramref name="detail"/>.</summary>
    /// <param name="status">The HTTP status of the response.</param>
    /// <param name="code">The NA's error code.</param>
    /// <param name="detail">The NA's error message.</param>
    public GenesisMeshException(int status, string code, string detail)
        : this(status, code, detail, $"genesismesh: HTTP {status} {code}: {detail}")
    {
    }

    /// <summary>An error with its own <paramref name="message"/>, for a refusal that is not an HTTP answer.</summary>
    /// <param name="status">The HTTP status, or 0 when the refusal is the SDK's own.</param>
    /// <param name="code">The reason code.</param>
    /// <param name="detail">What was refused.</param>
    /// <param name="message">The exception message.</param>
    protected GenesisMeshException(int status, string code, string detail, string message)
        : base(message)
    {
        Status = status;
        Code = code;
        Detail = detail;
    }
}

/// <summary>
/// Wraps connection-level failures (timeout, DNS, etc.). Cancellation through the caller's own
/// <see cref="CancellationToken"/> is not one (1.8.0): it surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class NetworkException : Exception
{
    /// <summary>A failure to reach the NA, caused by <paramref name="inner"/>.</summary>
    /// <param name="inner">The timeout or connection error.</param>
    public NetworkException(Exception inner)
        : base($"genesismesh: network error: {inner.Message}", inner) { }

    internal NetworkException(string message, Exception? inner, bool connectFailed)
        : base($"genesismesh: network error: {message}", inner)
    {
        ConnectFailed = connectFailed;
    }

    /// <summary>
    /// True when the request did not reach the NA: the connection could not be made, or the request was not sent.
    /// Such a request can be sent again without being applied twice.
    /// </summary>
    public bool ConnectFailed { get; }
}

/// <summary>
/// Thrown before anything is signed or sent when metadata would carry secret material or exceed the NA's size limit
/// (<see cref="EvidenceMetadata"/>, code <c>evidence_secret_material</c>). The NA refuses such evidence with the same
/// code; for a boundary evaluation's context the guard is this SDK's own, as in the TypeScript SDK, since the NA does
/// not check contexts.
/// </summary>
public sealed class SecretMaterialException : GenesisMeshException
{
    /// <summary>Metadata refused for <paramref name="reason"/>.</summary>
    /// <param name="reason">Which field holds what, or the size over the limit.</param>
    public SecretMaterialException(string reason)
        : base(0, "evidence_secret_material", reason, "genesismesh: " + reason)
    {
    }
}

/// <summary>HTTP 400 — request body failed validation.</summary>
public sealed class BadRequestException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public BadRequestException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 401 — admin signature or key not accepted.</summary>
public sealed class UnauthorizedException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public UnauthorizedException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>
/// HTTP 403 (1.8.0) — the caller may not do this, such as <c>insufficient_operator_tier</c> or
/// <c>node_key_revoked</c>. Retrying the same request does not help.
/// </summary>
public sealed class ForbiddenException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public ForbiddenException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 404 — resource not found.</summary>
public sealed class NotFoundException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public NotFoundException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>
/// HTTP 409 (1.8.0) — the request conflicts with the NA's state or configuration, such as
/// <c>boundary_policy_required</c>. Retrying the same request does not help.
/// </summary>
public sealed class ConflictException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public ConflictException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 413 (1.8.0) — the request body is larger than the NA accepts (<c>request_entity_too_large</c>).</summary>
public sealed class PayloadTooLargeException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public PayloadTooLargeException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 422 — semantic validation failed.</summary>
public sealed class ValidationException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public ValidationException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 429 — rate limit exceeded.</summary>
public sealed class RateLimitException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public RateLimitException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>
/// Any other HTTP 4xx (1.8.0), such as 405 <c>method_not_allowed</c>: the NA refused the request as sent.
/// Before 1.8.0 these, and 403, 409 and 413, were <see cref="ServerException"/>.
/// </summary>
public sealed class ClientErrorException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public ClientErrorException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>HTTP 5xx only — server-side error (1.8.0: no longer any status below 500).</summary>
public sealed class ServerException : GenesisMeshException
{
    /// <inheritdoc cref="GenesisMeshException(int, string, string)"/>
    public ServerException(int status, string code, string detail) : base(status, code, detail) { }
}

/// <summary>Reads the NA's error response into the typed exception for its HTTP status.</summary>
[Obsolete("ErrorParser is an implementation detail of this SDK and becomes internal in 2.0. Catch the GenesisMeshException subclasses the client throws instead.")]
public static class ErrorParser
{
    /// <summary>The typed exception for an NA error response with <paramref name="status"/> and <paramref name="body"/>.</summary>
    /// <param name="status">The HTTP status of the response.</param>
    /// <param name="body">The response body, the NA's <c>{"error": {"message", "code"}}</c>.</param>
    public static GenesisMeshException Parse(int status, string body) => ErrorMapping.Parse(status, body);
}

/// <summary>The typed exception for an NA error response (<see cref="Transport"/>).</summary>
internal static class ErrorMapping
{
    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")]
        public ErrorBody? Error { get; set; }
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("request_id")] public string? RequestId { get; set; }
        [JsonPropertyName("details")] public JsonElement Details { get; set; }
    }

    internal static GenesisMeshException Parse(int status, string body) => Parse(status, body, null);

    /// <summary>The typed exception for an error response, with the wait it named (<c>Retry-After</c>).</summary>
    internal static GenesisMeshException Parse(int status, string body, TimeSpan? retryAfter)
    {
        string code = "";
        string message = body;
        JsonElement details = default;
        try
        {
            var env = JsonSerializer.Deserialize<ErrorEnvelope>(body);
            code = env?.Error?.Code ?? "";
            message = env?.Error?.Message ?? body;
            if (env?.Error?.Details is { ValueKind: not JsonValueKind.Undefined } d) details = d.Clone();
        }
        catch { /* keep defaults */ }

        var error = ForStatus(status, code, message);
        error.Details = details;
        error.RetryAfter = retryAfter;
        return error;
    }

    /// <summary>A new exception like <paramref name="error"/>, for another caller.</summary>
    internal static GenesisMeshException Copy(GenesisMeshException error)
    {
        var copy = ForStatus(error.Status, error.Code, error.Detail);
        copy.Details = error.Details;
        copy.RetryAfter = error.RetryAfter;
        return copy;
    }

    /// <summary>The exception type for <paramref name="status"/>.</summary>
    internal static GenesisMeshException ForStatus(int status, string code, string message) =>
        status switch
        {
            400 => new BadRequestException(status, code, message),
            401 => new UnauthorizedException(status, code, message),
            403 => new ForbiddenException(status, code, message),
            404 => new NotFoundException(status, code, message),
            409 => new ConflictException(status, code, message),
            413 => new PayloadTooLargeException(status, code, message),
            422 => new ValidationException(status, code, message),
            429 => new RateLimitException(status, code, message),
            >= 400 and < 500 => new ClientErrorException(status, code, message),
            >= 500 => new ServerException(status, code, message),
            // Not an error status this SDK knows (a redirect it did not follow): the base type, with the raw status.
            _ => new GenesisMeshException(status, code, message),
        };
}
