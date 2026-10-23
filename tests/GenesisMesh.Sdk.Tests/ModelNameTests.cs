using System.Text.Json;
using System.Text.RegularExpressions;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Tests;

/// <summary>
/// Model names fail closed (1.8.0). The verifiers name the record models they check as string literals; a model the
/// embedded registry lacks used to read as a model with no fields, so a typo or a core rename silently turned the
/// unknown-field and canonical-form checks off for that verifier.
/// </summary>
public class ModelNameTests
{
    private const string Smuggled = "{\"decision_id\":\"d-1\",\"smuggled\":true}";

    [Fact]
    public void AKnownModelReportsAnUnknownField() =>
        Assert.Equal(new[] { "smuggled" }, StrictFields.UnknownFields("BoundaryDecision", Smuggled));

    [Theory]
    [InlineData("BoundaryDecison")]
    [InlineData("boundarydecision")]
    [InlineData("")]
    public void AModelTheRegistryLacksIsRefused(string model)
    {
        Assert.Throws<ArgumentException>(() => StrictFields.UnknownFields(model, Smuggled));
        using var doc = JsonDocument.Parse(Smuggled);
        Assert.Throws<ArgumentException>(() => StrictFields.UnknownFields(model, doc.RootElement));
        Assert.Throws<ArgumentException>(() => StrictFields.NonCanonicalFields(model, doc.RootElement));
        Assert.Throws<ArgumentException>(() => StrictFields.NonCanonicalTimestamps(model, doc.RootElement));
        Assert.Throws<ArgumentException>(() => StrictFields.Model(model));
        // Also when the value is not an object, so a check never depends on the input to fail.
        using var notAnObject = JsonDocument.Parse("[]");
        Assert.Throws<ArgumentException>(() => StrictFields.UnknownFields(model, notAnObject.RootElement));
    }

    /// <summary>A call that names a model by a string literal, as the verifiers do.</summary>
    private static readonly Regex ModelLiteral = new(
        @"\b(?:UnknownFields|NonCanonicalFields|NonCanonicalTimestamps|Model|SignedAsReceived|StrictRefusal|ReceivedCanonical)\(\s*""(?<model>[^""]*)""" +
        @"|\bconst\s+string\s+model\s*=\s*""(?<model>[^""]*)""");

    private static DirectoryInfo SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var src = new DirectoryInfo(Path.Combine(dir.FullName, "src", "GenesisMesh.Sdk"));
            if (src.Exists) return src;
        }
        throw new DirectoryNotFoundException("src/GenesisMesh.Sdk not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryModelNamedInTheSourceIsInTheRegistry()
    {
        var known = StrictFields.ModelNames.ToHashSet(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceDirectory().EnumerateFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            foreach (Match m in ModelLiteral.Matches(File.ReadAllText(file.FullName)))
                named.Add(m.Groups["model"].Value);
        }
        // Guard against a pattern that silently matches nothing: these are named today.
        Assert.Superset(new SortedSet<string>(StringComparer.Ordinal)
        {
            "AgreementRecord", "BoundaryDecision", "BoundaryPolicy", "CapabilityGrant", "DataAccessIntent",
            "DataLicensePolicy", "GenesisBlock", "KeySuccessionRecord", "MembershipAttestation",
        }, named);
        var missing = named.Where(m => !known.Contains(m)).ToList();
        Assert.True(missing.Count == 0, "models named in the source but not in the registry: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryModelGivenARecordTimeIsInTheRegistry()
    {
        var known = StrictFields.ModelNames.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(NAKeyHistory.RecordTimeFields);
        var missing = NAKeyHistory.RecordTimeFields.Keys.Where(m => !known.Contains(m)).ToList();
        Assert.True(missing.Count == 0, "record times for models not in the registry: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryNestedModelInTheRegistryIsInTheRegistry()
    {
        using var doc = JsonDocument.Parse(StrictFields.RegistryJson);
        var models = doc.RootElement.GetProperty("models");
        var missing = new List<string>();
        foreach (var model in models.EnumerateObject())
            foreach (var field in model.Value.GetProperty("fields").EnumerateObject())
                if (field.Value.ValueKind == JsonValueKind.Object)
                    foreach (var shape in field.Value.EnumerateObject())
                        if (!models.TryGetProperty(shape.Value.GetString()!, out _))
                            missing.Add($"{model.Name}.{field.Name}: {shape.Value.GetString()}");
        Assert.Empty(missing);
    }
}
