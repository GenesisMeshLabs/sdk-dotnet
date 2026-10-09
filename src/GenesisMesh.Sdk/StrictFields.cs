using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// Strict verification (v1.2.0): every field of a signed record is known.
/// <para>
/// A verifier that copies every received field into the signed form accepts a field it does not
/// understand whenever the signer covered it, so a field added in a later release could change what
/// a record means. The registry (<c>canonical_registry.json</c>, generated from the Python reference
/// and shipped in the shared conformance suite <c>canonical</c>) lists every field of every record
/// this SDK verifies; anything else is refused as <c>unknown_field</c>. See the core's reference
/// page "Canonical Form of Signed Records".
/// </para>
/// <para>
/// After copying a new suite to <c>tests/.../testdata/conformance/canonical.json</c>, write its
/// <c>registry</c> member to <c>src/GenesisMesh.Sdk/canonical_registry.json</c>; the conformance
/// test fails while the two differ.
/// </para>
/// </summary>
public static class StrictFields
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
    {
        using var stream = typeof(StrictFields).Assembly.GetManifestResourceStream("GenesisMesh.canonical_registry.json")
            ?? throw new InvalidOperationException("the canonical registry resource is missing");
        return JsonDocument.Parse(stream);
    });

    /// <summary>The embedded field registry of signed records.</summary>
    public static JsonElement Registry => Document.Value.RootElement;

    /// <summary>
    /// Dotted paths, sorted, of the fields in <paramref name="recordJson"/> that <paramref name="model"/>
    /// does not define, at any depth (<c>policy_binding.policies.0.extra</c>). Free-form fields are not
    /// inspected; values of the wrong type are left to validation.
    /// </summary>
    public static IReadOnlyList<string> UnknownFields(string model, string recordJson)
    {
        using var doc = JsonDocument.Parse(recordJson);
        return UnknownFields(model, doc.RootElement);
    }

    /// <inheritdoc cref="UnknownFields(string, string)"/>
    public static IReadOnlyList<string> UnknownFields(string model, JsonElement record)
    {
        var found = new List<string>();
        Collect(model, record, "", found);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>True when this SDK knows the evidence entry kind.</summary>
    public static bool IsKnownEntryKind(string kind) =>
        Registry.GetProperty("entry_kinds").EnumerateArray().Any(k => k.GetString() == kind);

    private static void Collect(string model, JsonElement data, string path, List<string> found)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !Registry.GetProperty("models").TryGetProperty(model, out var spec)) return;
        var fields = spec.GetProperty("fields");
        foreach (var property in data.EnumerateObject())
        {
            if (!fields.TryGetProperty(property.Name, out var kind))
            {
                found.Add(path + property.Name);
                continue;
            }
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null || kind.ValueKind != JsonValueKind.Object) continue;
            if (kind.TryGetProperty("object", out var nested))
            {
                Collect(nested.GetString()!, value, $"{path}{property.Name}.", found);
            }
            else if (kind.TryGetProperty("list", out nested) && value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    Collect(nested.GetString()!, item, $"{path}{property.Name}.{i++}.", found);
            }
            else if (kind.TryGetProperty("map", out nested) && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in value.EnumerateObject())
                    Collect(nested.GetString()!, item.Value, $"{path}{property.Name}.{item.Name}.", found);
            }
        }
    }
}
