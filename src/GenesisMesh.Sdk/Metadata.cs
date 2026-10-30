using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>
/// The NA's guard on the metadata a signed record carries (the reference's <c>check_metadata_only</c>): execution
/// parameters, an outcome detail, or a boundary evaluation's context. A guard, not a guarantee: send identifiers,
/// versions and timestamps (<c>secret_version</c>, <c>vault_uri</c>, <c>rotated_at</c>), never secret values.
/// </summary>
public static class EvidenceMetadata
{
    /// <summary>The most bytes the NA accepts for the execution parameters and the outcome detail together.</summary>
    public const int MaxMetadataBytes = 16 * 1024;

    private static readonly HashSet<string> SecretKeys =
    [
        "value", "secret", "secretvalue", "password", "passwd", "passphrase", "token",
        "accesstoken", "refreshtoken", "bearer", "privatekey", "keymaterial",
        "credential", "credentials", "clientsecret", "apikey", "pem", "connectionstring",
    ];

    // As Python's re.match with `$`: also matches before a final newline.
    private static readonly Regex LongOpaque = new(@"^[A-Za-z0-9+/=_\-]{120,}$", RegexOptions.CultureInvariant);
    private static readonly Regex Jwt = new(@"^eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Why the NA would refuse <paramref name="executionParameters"/> and <paramref name="outcomeDetail"/>
    /// (<c>evidence_secret_material</c>), or null: over <see cref="MaxMetadataBytes"/>, or holding secret material
    /// (<see cref="SecretMaterial(JsonElement)"/>).
    /// </summary>
    /// <param name="executionParameters">The metadata object; undefined counts as <c>{}</c>.</param>
    /// <param name="outcomeDetail">The outcome detail, if any.</param>
    public static string? CheckMetadataOnly(JsonElement executionParameters, string? outcomeDetail = null)
    {
        var parameters = executionParameters.ValueKind == JsonValueKind.Undefined ? "{}" : Unescaped(Canonical.Of(executionParameters));
        var detail = outcomeDetail is null ? "null" : Unescaped(JsonString(outcomeDetail));
        var size = Encoding.UTF8.GetByteCount("{\"execution_parameters\":" + parameters + ",\"outcome_detail\":" + detail + "}");
        if (size > MaxMetadataBytes)
            return $"metadata is {size} bytes, over the {MaxMetadataBytes}-byte limit";
        if (executionParameters.ValueKind != JsonValueKind.Undefined && SecretMaterial(executionParameters) is { } found)
            return found;
        return string.IsNullOrEmpty(outcomeDetail) ? null : SecretMaterial("outcome_detail", outcomeDetail);
    }

    /// <summary>
    /// The first field of <paramref name="value"/> named like a secret (<c>password</c>, <c>client_secret</c>,
    /// <c>accessToken</c>, ...), or holding a PEM block, a long opaque key or a JWT; null when there is none.
    /// </summary>
    public static string? SecretMaterial(JsonElement value) => Find(value, "");

    private static string? SecretMaterial(string field, string value) =>
        value.Contains("-----BEGIN", StringComparison.Ordinal) ? $"field '{field}' contains a PEM block"
        : LongOpaque.IsMatch(value) || Jwt.IsMatch(value) ? $"field '{field}' looks like key or token material"
        : null;

    private static string? Find(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject())
                {
                    if (SecretKeys.Contains(Normalise(p.Name)))
                        return $"field '{path}{p.Name}' is not allowed in evidence metadata";
                    if (Find(p.Value, $"{path}{p.Name}.") is { } found) return found;
                }
                return null;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (Find(item, $"{path}{i.ToString(CultureInfo.InvariantCulture)}.") is { } found) return found;
                    i++;
                }
                return null;
            case JsonValueKind.String:
                return SecretMaterial(path.TrimEnd('.'), value.GetString()!);
            default:
                return null;
        }
    }

    private static string Normalise(string key) =>
        key.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal);

    private static string JsonString(string value)
    {
        var sb = new StringBuilder();
        Canonical.WriteString(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// Canonical JSON with non-ASCII characters written as themselves (<c>ensure_ascii=False</c>), as the NA sizes
    /// metadata: only escapes of characters from U+007F on are undone; other escapes stay.
    /// </summary>
    private static string Unescaped(string canonical)
    {
        var sb = new StringBuilder(canonical.Length);
        for (var i = 0; i < canonical.Length; i++)
        {
            var c = canonical[i];
            if (c == '\\' && i + 1 < canonical.Length)
            {
                if (canonical[i + 1] == 'u' && i + 5 < canonical.Length
                    && int.TryParse(canonical.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                    && code >= 0x7f)
                {
                    sb.Append((char)code);
                    i += 5;
                    continue;
                }
                sb.Append(c).Append(canonical[i + 1]);
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
