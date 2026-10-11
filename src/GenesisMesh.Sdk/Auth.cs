using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace GenesisMesh;

/// <summary>
/// Admin authentication helpers for the Genesis Mesh Network Authority.
/// </summary>
public static class Auth
{
    /// <summary>
    /// How this SDK writes request bodies and reads the NA's responses. Keys are matched in their own
    /// case only (v1.3.1): before, <c>{"authorized":false,"Authorized":true}</c> read as authorized,
    /// where every other implementation reads <c>Authorized</c> as a key it does not know.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Canonical JSON of a .NET value, byte-identical to Python's
    /// json.dumps(value, sort_keys=True, separators=(",",":")) of what the NA parses
    /// (see <see cref="Canonical"/>). Used for admin request signatures.
    /// </summary>
    public static byte[] CanonicalJson(object? value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        return Encoding.UTF8.GetBytes(Canonical.Of(doc.RootElement));
    }

    /// <summary>
    /// Decodes a base64-encoded 32-byte Ed25519 seed (standard or no-padding encoding).
    /// </summary>
    public static byte[] LoadSeed(string seedBase64)
    {
        if (!TryDecodeBase64(seedBase64.Trim(), out var seed) || seed is null)
            throw new ArgumentException("Invalid signing key base64.", nameof(seedBase64));
        if (seed.Length != 32)
            throw new ArgumentException($"Signing key must be 32 bytes, got {seed.Length}.", nameof(seedBase64));
        return seed;
    }

    private static bool TryDecodeBase64(string s, out byte[]? result)
    {
        try { result = Convert.FromBase64String(s); return true; }
        catch
        {
            // Try raw (unpadded) base64
            var padded = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            try { result = Convert.FromBase64String(padded); return true; }
            catch { result = null; return false; }
        }
    }

    /// <summary>The admin signature format this SDK produces (Genesis Mesh 1.0.2).</summary>
    public const int AdminSignatureVersion = 2;

    /// <summary>
    /// The canonical bytes an operator signs for <paramref name="request"/>
    /// (signature version 2): {v, method, path, query, audience, body, key_id, timestamp, nonce}.
    /// </summary>
    public static byte[] AdminSigningPayload(AdminRequest request, string keyId, string timestamp, string nonce)
    {
        // A decoded path may itself contain '?' (from %3F); query parameters go in Query.
        if (!request.Path.StartsWith('/'))
            throw new ArgumentException("Admin request path must start with '/'.", nameof(request));

        var query = new Dictionary<string, object?>();
        if (request.Query is not null)
            foreach (var (name, values) in request.Query)
                query[name] = values.ToList();

        var payload = new Dictionary<string, object?>
        {
            ["v"]         = AdminSignatureVersion,
            ["method"]    = request.Method.ToUpperInvariant(),
            ["path"]      = request.Path,
            ["query"]     = query,
            ["audience"]  = request.Audience,
            ["body"]      = request.Body ?? new Dictionary<string, object?>(),
            ["key_id"]    = keyId,
            ["timestamp"] = timestamp,
            ["nonce"]     = nonce,
        };
        return CanonicalJson(payload);
    }

    /// <summary>
    /// Computes the four X-Admin-* request headers for one admin request.
    /// <para>
    /// The signature (version 2) binds the HTTP method, the request path, the
    /// query parameters, the target NA's public key and the body.
    /// <paramref name="timestamp"/> and <paramref name="nonce"/> may be fixed
    /// to reproduce a signature (tests and conformance vectors).
    /// </para>
    /// </summary>
    public static AdminHeaders BuildAdminHeaders(
        AdminRequest request, string keyId, byte[] seed, string? timestamp = null, string? nonce = null)
    {
        timestamp ??= DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
        nonce     ??= Guid.NewGuid().ToString();

        var canonical = AdminSigningPayload(request, keyId, timestamp, nonce);
        var sig       = SignatureAlgorithm.Ed25519.Sign(
            Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey),
            canonical);

        return new AdminHeaders
        {
            KeyId     = keyId,
            Signature = Convert.ToBase64String(sig),
            Timestamp = timestamp,
            Nonce     = nonce,
        };
    }
}

/// <summary>
/// What an admin signature binds (signature version 2, Genesis Mesh 1.0.2): the
/// HTTP method, the path the NA serves (decoded, without the query string),
/// the target NA's public key (network_authority.public_key in its /sovereign.json), the
/// JSON body (null signs {}) and the query parameters as sent.
/// </summary>
public sealed record AdminRequest(
    string Method,
    string Path,
    string Audience,
    object? Body = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Query = null);

/// <summary>The four X-Admin-* headers used to authenticate admin API requests.</summary>
public sealed class AdminHeaders
{
    public string KeyId     { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public string Nonce     { get; init; } = "";
}
