using System.Globalization;
using System.Text.Json;
using GenesisMesh;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// An audit pack's completeness check (1.9.0): RFC 3339 times, changes files (CSV and JSON) and the matching of changes
/// against a pack's records, as the Python reference judged them. Then the arguments.
/// </summary>
public class CompletenessTests
{
    private static readonly Lazy<JsonElement> Data = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "testdata", "audit", "completeness_cases.json"))).RootElement);

    public static IEnumerable<object[]> CaseIds() =>
        Data.Value.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("id").GetString()! });

    private static JsonElement Case(string id) =>
        Data.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);

    private static string Normalized(object value) => Canonical.FromJson(JsonSerializer.Serialize(value));

    [Fact]
    public void HoldsTheCases() => Assert.Equal(182, CaseIds().Count());

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void AnswersAsTheReferenceDid(string id)
    {
        var c = Case(id);
        var expected = c.GetProperty("expected");
        switch (c.GetProperty("kind").GetString())
        {
            case "rfc3339":
                if (expected.TryGetProperty("refused", out _))
                    Assert.Throws<FormatException>(() => OfflineVerifier.ParseRfc3339(c.GetProperty("value").GetString()!));
                else
                    Assert.Equal(expected.GetProperty("value").GetString(), OfflineVerifier.ParseRfc3339(c.GetProperty("value").GetString()!));
                break;
            case "changes_file":
                {
                    var text = c.GetProperty("text").GetString()!;
                    var csv = c.GetProperty("csv").GetBoolean();
                    if (expected.TryGetProperty("refused", out _))
                    {
                        var e = Record.Exception(() => OfflineVerifier.ParseChanges(text, csv));
                        Assert.True(e is FormatException or StrictJsonException, e?.ToString() ?? "parsed");
                    }
                    else
                    {
                        Assert.Equal(Canonical.FromJson(expected.GetProperty("changes").GetRawText()), Normalized(OfflineVerifier.ParseChanges(text, csv)));
                    }
                    break;
                }
            default:
                {
                    var pool = Data.Value.GetProperty("lines");
                    var events = OfflineVerifier.ParseExportLines(string.Join("\n", c.GetProperty("lines").EnumerateArray().Select(i => pool[i.GetInt32()].GetString())));
                    var cited = c.GetProperty("cited").GetInt32();
                    var n = 0;
                    var changes = c.GetProperty("changes").EnumerateArray().Select(row => new Change
                    {
                        Index = ++n,
                        ResourceId = row.GetProperty("resource_id").GetString()!,
                        Action = row.GetProperty("action").GetString()!,
                        ChangedAt = row.GetProperty("changed_at").GetString()!,
                        Source = row.TryGetProperty("source", out var s) ? s.GetString() : null,
                        SourceEventId = row.TryGetProperty("source_event_id", out var e) ? e.GetString() : null,
                        Actor = row.TryGetProperty("actor", out var a) ? a.GetString() : null,
                    }).ToList();
                    var period = c.GetProperty("period");
                    var report = OfflineVerifier.CheckCompleteness(changes, events.Skip(cited).ToList(), events.Take(cited).ToList(), new CompletenessOptions
                    {
                        From = DateTimeOffset.Parse(period[0].GetString()!, CultureInfo.InvariantCulture),
                        To = DateTimeOffset.Parse(period[1].GetString()!, CultureInfo.InvariantCulture),
                        Tolerance = TimeSpan.FromSeconds(c.GetProperty("tolerance_seconds").GetInt32()),
                    });
                    Assert.Equal(Canonical.FromJson(expected.GetProperty("report").GetRawText()), Normalized(report));
                    break;
                }
        }
    }

    [Fact]
    public void TheArgumentsAreChecked()
    {
        var change = new Change { Index = 1, ResourceId = "kv:a", Action = "rotate", ChangedAt = "2026-03-01T12:00:00Z" };
        var options = new CompletenessOptions { From = DateTimeOffset.UnixEpoch, To = DateTimeOffset.UnixEpoch };
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckCompleteness([change with { ChangedAt = "soon" }], [], [], options));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckCompleteness([null!], [], [], options));
        Assert.Throws<ArgumentException>(() => OfflineVerifier.CheckCompleteness([change], [null!], [], options));
        // A negative tolerance pairs nothing by time and narrows the period, as the reference reads it.
        var narrowed = OfflineVerifier.CheckCompleteness([change], [], [], new CompletenessOptions
        {
            From = DateTimeOffset.Parse("2026-03-01T11:00:00Z", CultureInfo.InvariantCulture),
            To = DateTimeOffset.Parse("2026-03-01T12:00:30Z", CultureInfo.InvariantCulture),
            Tolerance = TimeSpan.FromSeconds(-60),
        });
        Assert.Equal((-60L, 0, 1), (narrowed.ToleranceSeconds, narrowed.UnmatchedChanges.Count, narrowed.OutsidePeriod.Count));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.CheckCompleteness(null!, [], [], options));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ParseChanges(null!));
        Assert.Throws<ArgumentNullException>(() => OfflineVerifier.ParseRfc3339(null!));
        // Outside the years 1 to 9999 once in UTC: refused (the reference raises OverflowError).
        Assert.Throws<FormatException>(() => OfflineVerifier.ParseRfc3339("9999-12-31T23:59:59-00:01"));
        Assert.Throws<FormatException>(() => OfflineVerifier.ParseRfc3339("0001-01-01T00:00:00+00:01"));
        Assert.Equal(["source", "source_event_id", "resource_id", "action", "changed_at", "actor"], OfflineVerifier.ChangeColumns);
    }
}
