using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace GenesisMesh;

/// <summary>
/// Admin authentication helpers for the Genesis Mesh Network Authority.
/// </summary>
public static class Auth
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions _noEscapeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Produces deterministic JSON matching Python's
    /// json.dumps(value, sort_keys=True, separators=(",",":")).
    /// Object keys are sorted; strings are not HTML-escaped (&lt; &gt; &amp; kept as-is).
    /// </summary>
    public static byte[] CanonicalJson(object? value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        return Encoding.UTF8.GetBytes(WriteCanonical(doc.RootElement));
    }

    private static string WriteCanonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => WriteObject(e),
        JsonValueKind.Array  => WriteArray(e),
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()!, _noEscapeOptions),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _                    => "null",
    };

    private static string WriteObject(JsonElement e)
    {
        var parts = e.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{JsonSerializer.Serialize(p.Name, _noEscapeOptions)}:{WriteCanonical(p.Value)}");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string WriteArray(JsonElement e)
    {
        var parts = e.EnumerateArray().Select(WriteCanonical);
        return "[" + string.Join(",", parts) + "]";
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

    /// <summary>
    /// Computes the four X-Admin-* request headers for a signed admin request.
    /// <para>
    /// The signature covers canonicalJSON({body, key_id, nonce, timestamp}).
    /// </para>
    /// </summary>
    public static AdminHeaders BuildAdminHeaders(object? body, string keyId, byte[] seed)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
        var nonce     = Guid.NewGuid().ToString();

        var payload = new Dictionary<string, object?>
        {
            ["body"]      = body,
            ["key_id"]    = keyId,
            ["nonce"]     = nonce,
            ["timestamp"] = timestamp,
        };

        var canonical = CanonicalJson(payload);
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

/// <summary>The four X-Admin-* headers used to authenticate admin API requests.</summary>
public sealed class AdminHeaders
{
    public string KeyId     { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public string Nonce     { get; init; } = "";
}
