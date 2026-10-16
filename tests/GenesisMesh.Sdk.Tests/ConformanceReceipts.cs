using System.Text;
using System.Text.Json;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// What a runner of a shared conformance suite ran: by kind, the vectors it runs and the vectors it deliberately
/// does not run (a receipt, schema <c>gm.contract.receipt/1</c> of the core's contract bundle).
/// </summary>
/// <param name="Suite">The suite's name, as its file names it.</param>
/// <param name="File">The suite's copy, by path in this repository.</param>
/// <param name="Runner">The test that runs the vectors, as <c>file:method</c>.</param>
/// <param name="Slug">The runner's part of the receipt's file name.</param>
/// <param name="Run">Vectors run, by kind (<c>*</c> for vectors without one).</param>
/// <param name="NotRun">Vectors not run, by kind.</param>
internal sealed record ConformanceReceipt(
    string Suite, string File, string Runner, string Slug,
    IReadOnlyDictionary<string, int> Run, IReadOnlyDictionary<string, int> NotRun);

/// <summary>
/// Conformance receipts. Each runner of a shared suite lists the kinds it runs and the kinds it does not run; its
/// <c>EveryKindIsRunOrListedAsNotRun</c> fact checks the lists against the suite file and against the vectors the
/// runner's theory selects, then records the receipt. When <c>GM_CONFORMANCE_RECEIPTS</c> names a directory, the
/// receipt is written to <c>&lt;dir&gt;/&lt;suite&gt;.&lt;runner&gt;.json</c>, for the core's
/// <c>gm_contract.py receipt-check</c>; when it is unset, nothing is written.
/// <para>
/// Both target frameworks run the same tests and write the same receipt to the same file, so a receipt counts
/// each vector once.
/// </para>
/// </summary>
internal static class ConformanceReceipts
{
    public const string Variable = "GM_CONFORMANCE_RECEIPTS";
    public const string Schema = "gm.contract.receipt/1";

    /// <summary>The kind of a vector without one.</summary>
    public const string NoKind = "*";

    /// <summary>Where the suites are copied in this repository.</summary>
    public const string RepoDirectory = "tests/GenesisMesh.Sdk.Tests/testdata/conformance";

    public static string SuitePath(string suite) =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "conformance", suite + ".json");

    public static string KindOf(JsonElement vector) =>
        vector.TryGetProperty("kind", out var kind) ? kind.GetString()! : NoKind;

    /// <summary>
    /// The receipt of <paramref name="runner"/>'s <paramref name="method"/>, a theory over
    /// <paramref name="selected"/> (its member data: one vector ID per row). Every kind of the suite is in
    /// <paramref name="kindsRun"/> or <paramref name="kindsNotRun"/>, every kind run is in the suite, and the theory
    /// selects exactly the vectors of the kinds run.
    /// </summary>
    public static ConformanceReceipt For(
        string suite, Type runner, string method, IEnumerable<object[]> selected,
        IReadOnlyCollection<string> kindsRun, IReadOnlyCollection<string>? kindsNotRun = null)
    {
        kindsNotRun ??= [];
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(SuitePath(suite)));
        Assert.Equal(suite, doc.RootElement.GetProperty("suite").GetString());
        var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => (Id: v.GetProperty("id").GetString()!, Kind: KindOf(v))).ToList();
        var kinds = vectors.Select(v => v.Kind).ToHashSet();

        Assert.Empty(kindsRun.Intersect(kindsNotRun));
        Assert.All(kinds, k => Assert.True(kindsRun.Contains(k) || kindsNotRun.Contains(k),
            $"{suite}: kind {k} is neither run nor listed as not run"));
        Assert.All(kindsRun, k => Assert.Contains(k, kinds));

        var ids = selected.Select(row => Assert.IsType<string>(Assert.Single(row))).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(
            vectors.Where(v => kindsRun.Contains(v.Kind)).Select(v => v.Id).Order(StringComparer.Ordinal),
            ids.Order(StringComparer.Ordinal));

        return new ConformanceReceipt(
            suite, $"{RepoDirectory}/{suite}.json", $"tests/GenesisMesh.Sdk.Tests/{runner.Name}.cs:{method}", runner.Name,
            Count(vectors.Where(v => kindsRun.Contains(v.Kind)).Select(v => v.Kind)),
            Count(vectors.Where(v => !kindsRun.Contains(v.Kind)).Select(v => v.Kind)));
    }

    private static SortedDictionary<string, int> Count(IEnumerable<string> kinds)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in kinds) counts[kind] = counts.GetValueOrDefault(kind) + 1;
        return counts;
    }

    /// <summary>Writes the receipt where <c>GM_CONFORMANCE_RECEIPTS</c> says; nothing when it is unset.</summary>
    public static string? Record(ConformanceReceipt receipt) =>
        WriteTo(Environment.GetEnvironmentVariable(Variable), receipt);

    /// <summary>
    /// Writes the receipt to <c>&lt;directory&gt;/&lt;suite&gt;.&lt;slug&gt;.json</c> and returns its path; nothing
    /// (null) without a directory. The file is replaced whole, so a reader never sees half a receipt.
    /// </summary>
    public static string? WriteTo(string? directory, ConformanceReceipt receipt)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{receipt.Suite}.{receipt.Slug}.json");
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        System.IO.File.WriteAllBytes(temp, Json(receipt));
        try
        {
            // The other target framework may be replacing the same file.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    System.IO.File.Move(temp, path, overwrite: true);
                    return path;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            System.IO.File.Delete(temp);
        }
    }

    private static byte[] Json(ConformanceReceipt receipt)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("suite", receipt.Suite);
            w.WriteString("file", receipt.File);
            w.WriteString("runner", receipt.Runner);
            WriteCounts(w, "run", receipt.Run);
            WriteCounts(w, "not_run", receipt.NotRun);
            w.WriteEndObject();
        }
        buffer.Write(Encoding.UTF8.GetBytes("\n"));
        return buffer.ToArray();
    }

    private static void WriteCounts(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, int> counts)
    {
        w.WriteStartObject(name);
        foreach (var (kind, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal)) w.WriteNumber(kind, count);
        w.WriteEndObject();
    }
}
