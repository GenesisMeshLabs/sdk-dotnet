using System.Text.Json;
using GenesisMesh.Tests;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// The conformance receipts against the suite files, read independently of the runners: every suite copied here
/// has one receipt, every vector of every kind is counted once (run or not run), and the kinds the verifier tier
/// requires are run in full.
/// </summary>
public class ConformanceReceiptTests
{
    /// <summary>
    /// The kinds the verifier tier requires, per suite (the core's <c>contract/bundle.json</c>); null for every kind
    /// except the suite's <c>na_only_kinds</c>.
    /// </summary>
    private static readonly Dictionary<string, string[]?> VerifierRequired = new()
    {
        ["admin_auth"] = null,
        ["canonical"] = null,
        ["consensus"] = null,
        ["interop"] = null,
        ["field_registry"] = ["unknown_fields", "verify_decision", "verify_agreement"],
        ["audit"] = ["canonical_form", "key_history", "key_at", "decision_at_record_time", "record_time"],
        ["grants"] = ["canonical_form", "verify_record"],
    };

    private static ConformanceReceipt[] Receipts() =>
    [
        AdminAuthTests.Receipt(),
        CanonicalSuiteTests.Receipt(),
        ConsensusTests.Receipt(),
        FieldRegistryConformanceTests.Receipt(),
        ConformanceTests.Receipt(),
        KeyHistoryTests.Receipt(),
        GrantsTests.Receipt(),
    ];

    private static int Get(JsonElement counts, string kind) =>
        counts.TryGetProperty(kind, out var n) ? n.GetInt32() : 0;

    [Fact]
    public void EverySuiteHasOneReceiptThatAccountsForEveryVector()
    {
        var dir = Directory.CreateTempSubdirectory("gm-receipts-");
        try
        {
            // Twice, as the two target frameworks write them: the second replaces the first.
            foreach (var receipt in Receipts().Concat(Receipts())) ConformanceReceipts.WriteTo(dir.FullName, receipt);

            var suites = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "testdata", "conformance"), "*.json")
                .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(VerifierRequired.Keys.Order(StringComparer.Ordinal), suites);
            var files = Directory.GetFiles(dir.FullName);
            Assert.Equal(suites.Count, files.Length);

            foreach (var file in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var suite = r.GetProperty("suite").GetString()!;
                Assert.Equal("gm.contract.receipt/1", r.GetProperty("schema").GetString());
                Assert.StartsWith(suite + ".", Path.GetFileName(file), StringComparison.Ordinal);
                Assert.Equal($"tests/GenesisMesh.Sdk.Tests/testdata/conformance/{suite}.json", r.GetProperty("file").GetString());
                Assert.Matches(@"^tests/GenesisMesh\.Sdk\.Tests/\w+\.cs:\w+$", r.GetProperty("runner").GetString());

                using var suiteDoc = JsonDocument.Parse(File.ReadAllText(ConformanceReceipts.SuitePath(suite)));
                var inFile = suiteDoc.RootElement.GetProperty("vectors").EnumerateArray()
                    .GroupBy(ConformanceReceipts.KindOf).ToDictionary(g => g.Key, g => g.Count());
                var run = r.GetProperty("run");
                var notRun = r.GetProperty("not_run");
                var named = run.EnumerateObject().Concat(notRun.EnumerateObject()).ToList();
                Assert.All(named, p => Assert.True(p.Value.GetInt32() > 0 && inFile.ContainsKey(p.Name), $"{suite}: {p.Name}"));
                foreach (var (kind, count) in inFile)
                    Assert.True(Get(run, kind) + Get(notRun, kind) == count, $"{suite}: {kind} is not accounted for");

                var naOnly = suiteDoc.RootElement.TryGetProperty("na_only_kinds", out var na)
                    ? na.EnumerateArray().Select(k => k.GetString()!).ToHashSet() : [];
                var required = VerifierRequired[suite] ?? inFile.Keys.Where(k => !naOnly.Contains(k)).ToArray();
                foreach (var kind in required)
                    Assert.True(Get(run, kind) == inFile.GetValueOrDefault(kind, -1), $"{suite}: required kind {kind} is not run in full");
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingIsWrittenWithoutADirectory(string? directory) =>
        Assert.Null(ConformanceReceipts.WriteTo(directory, AdminAuthTests.Receipt()));
}
