using System.Collections.Frozen;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// Strict verification (v1.2.0): every signed field of a record is known.
/// <para>
/// Before 1.2.0 this SDK copied every received field into the signed form, so a field a newer
/// signer covered verified here and could change what a record means. The registry
/// (<c>canonical_registry.json</c>, generated from the Python reference and shipped in the shared
/// conformance suite <c>field_registry</c>) lists every field of every record this SDK verifies.
/// Verifiers check the signature over the record as received first; a signed field the registry
/// does not list is then refused as <c>unknown_field</c>, and a record signed over a timestamp the
/// reference does not write as <c>non_canonical_form</c> (v1.2.0). See the core's reference page
/// "Canonical Form of Signed Records".
/// </para>
/// <para>
/// After copying a new suite to <c>tests/GenesisMesh.Sdk.Tests/testdata/conformance/field_registry.json</c>,
/// run <c>python scripts/sync_canonical_registry.py</c>; the conformance test fails while the
/// embedded registry differs from the suite.
/// </para>
/// </summary>
public static class StrictFields
{
    /// <summary>
    /// A structured field: one record (<c>object</c>), a list or a map of records, or a timestamp
    /// (<c>timestamp</c>, v1.2.0, with no model).
    /// </summary>
    internal sealed record Nested(string Shape, string Model);

    /// <summary>
    /// A model's fields (<c>null</c> for a scalar or free-form field) and its canonical rules.
    /// </summary>
    internal sealed record ModelSpec(
        FrozenDictionary<string, Nested?> Fields,
        string? SignatureField,
        IReadOnlyList<string> OmitWhenNone,
        IReadOnlyList<string>? CanonicalFields);

    private static readonly Lazy<string> Raw = new(() =>
    {
        using var stream = typeof(StrictFields).Assembly.GetManifestResourceStream("GenesisMesh.canonical_registry.json")
            ?? throw new InvalidOperationException("the canonical registry resource is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<FrozenDictionary<string, ModelSpec>> Models = new(() => Parse(Raw.Value));

    /// <summary>The embedded registry as JSON, for the conformance tests.</summary>
    internal static string RegistryJson => Raw.Value;

    /// <summary>A model's canonical rules.</summary>
    internal static ModelSpec Model(string model) => Models.Value[model];

    private static FrozenDictionary<string, ModelSpec> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        static IReadOnlyList<string> List(JsonElement e) => e.EnumerateArray().Select(s => s.GetString()!).ToArray();
        var models = new Dictionary<string, ModelSpec>(StringComparer.Ordinal);
        foreach (var model in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var spec = model.Value;
            var fields = new Dictionary<string, Nested?>(StringComparer.Ordinal);
            foreach (var field in spec.GetProperty("fields").EnumerateObject())
            {
                Nested? nested = null;
                if (field.Value.ValueKind == JsonValueKind.Object)
                {
                    var shape = field.Value.EnumerateObject().Single();
                    nested = new Nested(shape.Name, shape.Value.GetString()!);
                }
                else if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() == "timestamp")
                {
                    nested = new Nested("timestamp", "");
                }
                fields[field.Name] = nested;
            }
            models[model.Name] = new ModelSpec(
                fields.ToFrozenDictionary(StringComparer.Ordinal),
                spec.TryGetProperty("signature_field", out var sig) && sig.ValueKind == JsonValueKind.String ? sig.GetString() : null,
                spec.TryGetProperty("omit_when_none", out var omit) ? List(omit) : Array.Empty<string>(),
                spec.TryGetProperty("canonical_fields", out var canonical) ? List(canonical) : null);
        }
        return models.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Dotted paths, sorted, of the signed fields in <paramref name="recordJson"/> that
    /// <paramref name="model"/> does not define, at any depth (<c>policy_binding.policies.0.extra</c>).
    /// Only the signed projection is checked (not the signature, not an agreement's unsigned fields);
    /// free-form fields are not inspected; values of the wrong type are left to validation.
    /// </summary>
    public static IReadOnlyList<string> UnknownFields(string model, string recordJson)
    {
        using var doc = JsonDocument.Parse(recordJson);
        return UnknownFields(model, doc.RootElement);
    }

    /// <inheritdoc cref="UnknownFields(string, string)"/>
    public static IReadOnlyList<string> UnknownFields(string model, JsonElement record) =>
        UnknownFields(model, record, "");

    internal static List<string> UnknownFields(string model, JsonElement record, string prefix)
    {
        var found = new List<string>();
        Collect(model, record, prefix, projection: true, found);
        found.Sort(Canonical.CodePointComparer.Instance);
        return found;
    }

    private static bool OutsideProjection(ModelSpec spec, string key) =>
        key == spec.SignatureField || (spec.CanonicalFields is { } fields && !fields.Contains(key));

    private static void Collect(string model, JsonElement data, string path, bool projection, List<string> found)
    {
        if (data.ValueKind != JsonValueKind.Object || !Models.Value.TryGetValue(model, out var spec)) return;
        foreach (var property in data.EnumerateObject())
        {
            if (projection && OutsideProjection(spec, property.Name)) continue;
            if (!spec.Fields.TryGetValue(property.Name, out var nested))
            {
                found.Add(path + property.Name);
                continue;
            }
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null || nested is null || nested.Shape == "timestamp") continue;
            switch (nested.Shape)
            {
                case "object":
                    Collect(nested.Model, value, $"{path}{property.Name}.", false, found);
                    break;
                case "list" when value.ValueKind == JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                        Collect(nested.Model, item, $"{path}{property.Name}.{i++}.", false, found);
                    break;
                case "map" when value.ValueKind == JsonValueKind.Object:
                    foreach (var item in value.EnumerateObject())
                        Collect(nested.Model, item.Value, $"{path}{property.Name}.{item.Name}.", false, found);
                    break;
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TimestampForm = new(
        @"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{6}))?(Z|[+-](\d{2}):(\d{2}))?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="value"/> is a timestamp in canonical form (v1.2.0): what the
    /// reference writes, <c>YYYY-MM-DDTHH:MM:SS</c>, six digits of microseconds when not all zero, then
    /// <c>Z</c> for UTC or <c>+HH:MM</c> / <c>-HH:MM</c> for another offset (none for a timestamp without
    /// one), naming an instant that exists.
    /// </summary>
    public static bool CanonicalTimestamp(string value)
    {
        var m = TimestampForm.Match(value);
        if (!m.Success || m.Groups[7].Value == "000000" || m.Groups[8].Value is "+00:00" or "-00:00") return false;
        int N(int i) => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (m.Groups[9].Success && (N(9) > 23 || N(10) > 59)) return false;
        var (year, month, day) = (N(1), N(2), N(3));
        if (year < 1 || month is < 1 or > 12 || N(4) > 23 || N(5) > 59 || N(6) > 59) return false;
        return day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    /// <summary>
    /// Dotted paths, sorted, of the timestamps in <paramref name="record"/>'s signed projection that are
    /// not in canonical form (v1.2.0). Values that are not strings are left to validation.
    /// </summary>
    internal static List<string> NonCanonicalTimestamps(string model, JsonElement record)
    {
        var found = new List<string>();
        void Walk(string name, JsonElement data, string path, bool projection)
        {
            if (data.ValueKind != JsonValueKind.Object || !Models.Value.TryGetValue(name, out var spec)) return;
            foreach (var property in data.EnumerateObject())
            {
                if (projection && OutsideProjection(spec, property.Name)) continue;
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Null || !spec.Fields.TryGetValue(property.Name, out var nested)
                    || nested is null) continue;
                switch (nested.Shape)
                {
                    case "timestamp":
                        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
                        if (items.Any(i => i.ValueKind == JsonValueKind.String && !CanonicalTimestamp(i.GetString()!)))
                            found.Add(path + property.Name);
                        break;
                    case "object":
                        Walk(nested.Model, value, $"{path}{property.Name}.", false);
                        break;
                    case "list" when value.ValueKind == JsonValueKind.Array:
                        var i = 0;
                        foreach (var item in value.EnumerateArray()) Walk(nested.Model, item, $"{path}{property.Name}.{i++}.", false);
                        break;
                    case "map" when value.ValueKind == JsonValueKind.Object:
                        foreach (var item in value.EnumerateObject()) Walk(nested.Model, item.Value, $"{path}{property.Name}.{item.Name}.", false);
                        break;
                }
            }
        }
        Walk(model, record, "", true);
        found.Sort(Canonical.CodePointComparer.Instance);
        return found;
    }
}
