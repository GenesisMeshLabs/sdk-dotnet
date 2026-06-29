using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenesisMesh;

/// <summary>Base error for all NA API errors.</summary>
public class GenesisMeshException : Exception
{
    public int    Status  { get; }
    public string Code    { get; }
    public string Detail  { get; }

    public GenesisMeshException(int status, string code, string detail)
        : base($"genesismesh: HTTP {status} {code}: {detail}")
    {
        Status = status;
        Code   = code;
        Detail = detail;
    }
}

/// <summary>Wraps connection-level failures (timeout, DNS, etc.).</summary>
public sealed class NetworkException : Exception
{
    public NetworkException(Exception inner)
        : base($"genesismesh: network error: {inner.Message}", inner) { }
}

/// <summary>HTTP 400 — request body failed validation.</summary>
public sealed class BadRequestException     : GenesisMeshException { public BadRequestException(int s, string c, string d)     : base(s, c, d) { } }

/// <summary>HTTP 401 — admin signature or key not accepted.</summary>
public sealed class UnauthorizedException   : GenesisMeshException { public UnauthorizedException(int s, string c, string d)   : base(s, c, d) { } }

/// <summary>HTTP 404 — resource not found.</summary>
public sealed class NotFoundException       : GenesisMeshException { public NotFoundException(int s, string c, string d)       : base(s, c, d) { } }

/// <summary>HTTP 422 — semantic validation failed.</summary>
public sealed class ValidationException     : GenesisMeshException { public ValidationException(int s, string c, string d)     : base(s, c, d) { } }

/// <summary>HTTP 429 — rate limit exceeded.</summary>
public sealed class RateLimitException      : GenesisMeshException { public RateLimitException(int s, string c, string d)      : base(s, c, d) { } }

/// <summary>HTTP 5xx — server-side error.</summary>
public sealed class ServerException         : GenesisMeshException { public ServerException(int s, string c, string d)         : base(s, c, d) { } }

public static class ErrorParser
{
    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")]
        public ErrorBody? Error { get; set; }
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("message")]   public string? Message   { get; set; }
        [JsonPropertyName("code")]      public string? Code      { get; set; }
        [JsonPropertyName("request_id")]public string? RequestId { get; set; }
    }

    public static GenesisMeshException Parse(int status, string body)
    {
        string code    = "";
        string message = body;
        try
        {
            var env = JsonSerializer.Deserialize<ErrorEnvelope>(body);
            code    = env?.Error?.Code    ?? "";
            message = env?.Error?.Message ?? body;
        }
        catch { /* keep defaults */ }

        return status switch
        {
            400 => new BadRequestException(status, code, message),
            401 => new UnauthorizedException(status, code, message),
            404 => new NotFoundException(status, code, message),
            422 => new ValidationException(status, code, message),
            429 => new RateLimitException(status, code, message),
            _   => new ServerException(status, code, message),
        };
    }
}
