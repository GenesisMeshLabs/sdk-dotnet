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
    /// Why named values would be refused as secret material, or null (1.9.0): the reference's <c>metadata_problem</c>,
    /// which the NA applies to observations and break-glass records. Their canonical JSON, with non-ASCII characters
    /// escaped as the reference counts them, is at most <see cref="MaxMetadataBytes"/>; then, value by value in the
    /// order given, no field named like a secret and no PEM block, key or token (<see cref="SecretMaterial(JsonElement)"/>).
    /// </summary>
    /// <param name="values">A JSON object: each value by its name.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is not an object.</exception>
    public static string? MetadataProblem(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("genesismesh: the values must be a JSON object", nameof(values));
        return MetadataProblem(values.EnumerateObject().Select(p => (p.Name, p.Value)).ToList());
    }

    /// <summary>
    /// Why the NA would refuse an observation as secret material, or null (1.9.0): <see cref="MetadataProblem(JsonElement)"/>
    /// over its metadata and the source's own strings (<c>actor</c>, <c>source_event_id</c>, <c>version_id</c>; absent
    /// ones left out), as the reference's <c>check_observation</c> gathers them. An actor is a pseudonym, never a
    /// credential.
    /// </summary>
    public static string? ObservationProblem(ObservationRecord observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var values = new List<(string, JsonElement)> { ("metadata", ObjectOrEmpty(observation.Metadata)) };
        if (observation.Actor is not null) values.Add(("actor", Element(observation.Actor)));
        values.Add(("source_event_id", Element(observation.SourceEventId)));
        if (observation.VersionId is not null) values.Add(("version_id", Element(observation.VersionId)));
        return MetadataProblem(values);
    }

    /// <summary>
    /// Why the NA would refuse a break-glass record as secret material, or null (1.9.0):
    /// <see cref="MetadataProblem(JsonElement)"/> over its execution parameters, request parameters, attributes, outcome
    /// detail and justification, as the reference's <c>check_break_glass</c> gathers them.
    /// </summary>
    public static string? BreakGlassProblem(BreakGlassRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return MetadataProblem(
        [
            ("execution_parameters", ObjectOrEmpty(record.ExecutionParameters)),
            ("request_parameters", ObjectOrEmpty(record.RequestParameters)),
            ("attributes", ObjectOrEmpty(record.Attributes)),
            ("outcome_detail", Element(record.OutcomeDetail ?? "")),
            ("justification", Element(record.Justification)),
        ]);
    }

    private static string? MetadataProblem(IReadOnlyList<(string Name, JsonElement Value)> values)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (name, value) in values.OrderBy(v => v.Name, Canonical.CodePointComparer.Instance))
        {
            if (!first) sb.Append(',');
            first = false;
            Canonical.WriteString(sb, name);
            sb.Append(':');
            Canonical.Write(sb, value, null, null);
        }
        var size = sb.Append('}').Length;
        if (size > MaxMetadataBytes)
            return $"metadata is {size} bytes, over the {MaxMetadataBytes}-byte limit";
        foreach (var (name, value) in values)
        {
            var found = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Find(value, name + ".")
                : SecretKeys.Contains(Normalise(name)) ? $"field '{name}' is not allowed in evidence metadata"
                : value.ValueKind == JsonValueKind.String ? SecretMaterial(name, value.GetString()!)
                : null;
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>A record's object field, or <c>{}</c> where it is not set, as the reference fills it.</summary>
    private static JsonElement ObjectOrEmpty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? Element(new Dictionary<string, object?>()) : value;

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);

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
