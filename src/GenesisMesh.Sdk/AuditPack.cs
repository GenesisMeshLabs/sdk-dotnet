using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GenesisMesh;

/// <summary>One change from a log outside Genesis Mesh: a cloud audit log, a ticketing export (v1.5.0).</summary>
public sealed record Change
{
    /// <summary>Its position in the log, from 1.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>The changed resource.</summary>
    [JsonPropertyName("resource_id")]
    public required string ResourceId { get; init; }

    /// <summary>The action, such as <c>rotate</c>.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>When it was made: RFC 3339 with an offset (<see cref="OfflineVerifier.ParseChanges"/> writes it in UTC).</summary>
    [JsonPropertyName("changed_at")]
    public required string ChangedAt { get; init; }

    /// <summary>The log it comes from; given with <see cref="SourceEventId"/> or not at all.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Its event's ID in that log.</summary>
    [JsonPropertyName("source_event_id")]
    public string? SourceEventId { get; init; }

    /// <summary>Who made it, as the log names them.</summary>
    [JsonPropertyName("actor")]
    public string? Actor { get; init; }
}

/// <summary>A change matched to the record of it (<see cref="CompletenessReport.Pairs"/>).</summary>
public sealed record CompletenessPair
{
    /// <summary>The change's <see cref="Change.Index"/>.</summary>
    [JsonPropertyName("change")]
    public required int Change { get; init; }

    /// <summary>The record's entry kind: <c>execution</c>, <c>grant_execution</c>, <c>break_glass</c> or <c>observation</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The record's ID.</summary>
    [JsonPropertyName("record_id")]
    public required string RecordId { get; init; }

    /// <summary>The record's store position.</summary>
    [JsonPropertyName("store_sequence")]
    public required long StoreSequence { get; init; }

    /// <summary>
    /// How: <c>source_event_id</c> (the record names that very event) or <c>resource_action_time</c> (the same resource
    /// and action, within the tolerance).
    /// </summary>
    [JsonPropertyName("matched_by")]
    public required string MatchedBy { get; init; }
}

/// <summary>A record no change matched (<see cref="CompletenessReport.UnmatchedRecords"/>).</summary>
public sealed record CompletenessRecord
{
    /// <summary>The record's entry kind.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The record's ID.</summary>
    [JsonPropertyName("record_id")]
    public required string RecordId { get; init; }

    /// <summary>The record's store position.</summary>
    [JsonPropertyName("store_sequence")]
    public required long StoreSequence { get; init; }

    /// <summary>The changed resource.</summary>
    [JsonPropertyName("resource_id")]
    public required string ResourceId { get; init; }

    /// <summary>The action.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

/// <summary>The period a completeness check covers, in UTC as the reference prints a time.</summary>
public sealed record CompletenessPeriod
{
    /// <summary>Its start.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Its end.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }
}

/// <summary>What <see cref="OfflineVerifier.CheckCompleteness"/> found (v1.5.0).</summary>
public sealed record CompletenessReport
{
    /// <summary>The changes given.</summary>
    [JsonPropertyName("changes")]
    public required int Changes { get; init; }

    /// <summary>The tolerance, in whole seconds.</summary>
    [JsonPropertyName("tolerance_seconds")]
    public required long ToleranceSeconds { get; init; }

    /// <summary>The period the records cover.</summary>
    [JsonPropertyName("period")]
    public required CompletenessPeriod Period { get; init; }

    /// <summary>Each change matched to a record, by change.</summary>
    [JsonPropertyName("pairs")]
    public required IReadOnlyList<CompletenessPair> Pairs { get; init; }

    /// <summary>Changes dated inside the period (with the tolerance either side) that no record matches: the store does not record them.</summary>
    [JsonPropertyName("unmatched_changes")]
    public required IReadOnlyList<Change> UnmatchedChanges { get; init; }

    /// <summary>Unmatched changes dated outside the period: not missing from it.</summary>
    [JsonPropertyName("outside_period")]
    public required IReadOnlyList<Change> OutsidePeriod { get; init; }

    /// <summary>Changes repeating a (source, source event ID) pair already seen: set aside.</summary>
    [JsonPropertyName("duplicate_changes")]
    public required IReadOnlyList<Change> DuplicateChanges { get; init; }

    /// <summary>Records no change matched (the log may not cover every source).</summary>
    [JsonPropertyName("unmatched_records")]
    public required IReadOnlyList<CompletenessRecord> UnmatchedRecords { get; init; }

    /// <summary>True when no change dated inside the period is missing.</summary>
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
}

/// <summary>Options for <see cref="OfflineVerifier.CheckCompleteness"/>.</summary>
public sealed class CompletenessOptions
{
    /// <summary>The start of the period the records cover.</summary>
    public required DateTimeOffset From { get; init; }

    /// <summary>The end of the period the records cover.</summary>
    public required DateTimeOffset To { get; init; }

    /// <summary>How far apart a change and its record may be dated, and how far outside the period a change still counts as in it: 300 seconds by default.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromSeconds(300);
}

public static partial class OfflineVerifier
{
    // Audit packs (v1.5.0; offline here from 1.9.0): a port of the reference's genesis_mesh/workflows/audit_pack.py and
    // of the TypeScript SDK's audit-pack.ts, with their reason codes. This part: changes from a log outside Genesis
    // Mesh, and the completeness check that matches them against a pack's records.

    /// <summary>The columns of a CSV changes file.</summary>
    public static readonly IReadOnlyList<string> ChangeColumns = ["source", "source_event_id", "resource_id", "action", "changed_at", "actor"];

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})[Tt]([0-9]{2}):([0-9]{2}):([0-9]{2})(\.[0-9]{1,9})?([Zz]|([+-])([0-9]{2}):([0-9]{2}))\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339DateTime();

    /// <summary>
    /// An RFC 3339 <c>date-time</c> in UTC, as the reference prints one (<c>...Z</c>, six fraction digits when not zero).
    /// <c>T</c> and <c>Z</c> may be lower case; a date without a time, a space for <c>T</c>, a missing offset, a leap
    /// second or an out-of-range field is refused. Fractions beyond microseconds are cut, not rounded.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <exception cref="FormatException">It is not an RFC 3339 date-time, or falls outside the years 1 to 9999 in UTC.</exception>
    public static string ParseRfc3339(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var m = Rfc3339DateTime().Match(value);
        if (!m.Success) throw new FormatException("not an RFC 3339 date-time");
        int N(int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        var (year, month, day, hour, minute, second) = (N(1), N(2), N(3), N(4), N(5), N(6));
        var (offsetHour, offsetMinute) = m.Groups[9].Success ? (N(10), N(11)) : (0, 0);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59 || offsetHour > 23 || offsetMinute > 59)
            throw new FormatException("not an RFC 3339 date-time");
        var fraction = m.Groups[7].Success ? long.Parse(m.Groups[7].Value[1..].PadRight(9, '0')[..6], CultureInfo.InvariantCulture) : 0;
        var offset = (m.Groups[9].Value == "-" ? -1 : 1) * (offsetHour * 60L + offsetMinute) * 60_000_000L;
        var micros = new DateTime(year, month, day, hour, minute, second).Ticks / 10 + fraction - offset;
        if (micros < 0 || micros > DateTime.MaxValue.Ticks / 10) throw new FormatException("not an RFC 3339 date-time");
        return Canonical.Timestamp(new DateTimeOffset(micros * 10, TimeSpan.Zero));
    }

    /// <summary>
    /// Changes from a changes file, as the reference's <c>parse_changes</c> reads one: CSV (<paramref name="csv"/>; a
    /// header row naming <see cref="ChangeColumns"/>) or JSON (a list, or <c>{"changes": [...]}</c>; each field a string
    /// or null). <c>resource_id</c>, <c>action</c> and <c>changed_at</c> (RFC 3339 with an offset,
    /// <see cref="ParseRfc3339"/>) are required; <c>source</c> and <c>source_event_id</c> go together; a value is trimmed
    /// as Python trims it, and an empty one is absent.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="csv">True for CSV, false for JSON.</param>
    /// <exception cref="FormatException">The file, or a change of it (named by its position), is not one.</exception>
    /// <exception cref="StrictJsonException">The JSON is not strict JSON.</exception>
    public static IReadOnlyList<Change> ParseChanges(string text, bool csv = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<Dictionary<string, JsonElement?>?>();
        if (csv)
        {
            // The header is the first row, a blank one included (no columns), as Python's csv.DictReader reads it;
            // blank rows after it are skipped.
            var records = CsvRows(text);
            if (records.Count > 0)
            {
                var header = records[0];
                foreach (var record in records.Skip(1).Where(r => r.Count > 0))
                {
                    var row = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
                    for (var i = 0; i < header.Count; i++)
                        row[header[i]] = i < record.Count ? JsonSerializer.SerializeToElement(record[i]) : null;
                    rows.Add(row);
                }
            }
        }
        else
        {
            using var doc = StrictJsonReader.Parse(text);
            var data = doc.RootElement;
            var listed = data.ValueKind == JsonValueKind.Object
                ? data.TryGetProperty("changes", out var changes) ? changes : default
                : data;
            if (listed.ValueKind != JsonValueKind.Array)
                throw new FormatException("a JSON changes file is a list of changes or {\"changes\": [...]}");
            foreach (var item in listed.EnumerateArray())
                rows.Add(item.ValueKind == JsonValueKind.Object
                    ? item.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => (JsonElement?)g.Last().Value.Clone(), StringComparer.Ordinal)
                    : null);
        }
        var out_ = new List<Change>();
        for (var n = 0; n < rows.Count; n++)
        {
            var index = n + 1;
            var row = rows[n] ?? throw new FormatException($"change {index} is not an object");
            string? Value(string name)
            {
                if (!row.TryGetValue(name, out var raw) || raw is not { } value || value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind != JsonValueKind.String) throw new FormatException($"change {index}: {name} must be a string");
                var trimmed = PythonStrip(value.GetString()!);
                return trimmed.Length == 0 ? null : trimmed;
            }
            var (resource, action, changed) = (Value("resource_id"), Value("action"), Value("changed_at"));
            if (resource is null || action is null || changed is null)
                throw new FormatException($"change {index}: resource_id, action and changed_at are required");
            string at;
            try
            {
                at = ParseRfc3339(changed);
            }
            catch (FormatException)
            {
                throw new FormatException($"change {index}: changed_at is not an RFC 3339 time with an offset (Z for UTC)");
            }
            var (source, eventId) = (Value("source"), Value("source_event_id"));
            if (source is null != eventId is null) throw new FormatException($"change {index}: source and source_event_id go together");
            out_.Add(new Change
            {
                Index = index,
                ResourceId = resource,
                Action = action,
                ChangedAt = at,
                Source = source,
                SourceEventId = eventId,
                Actor = Value("actor"),
            });
        }
        return out_;
    }

    /// <summary>A value trimmed as Python's <c>str.strip()</c> trims it.</summary>
    private static string PythonStrip(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && RemediationRules.PythonSpace(value[start])) start++;
        while (end > start && RemediationRules.PythonSpace(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>RFC 4180 rows of a CSV text, as Python's <c>csv</c> module reads them; an empty line is a row of no fields.</summary>
    private static List<List<string>> CsvRows(string input)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var (quoted, touched) = (false, false);
        void EndRow()
        {
            if (touched || row.Count > 0) row.Add(field.ToString());
            rows.Add(row);
            row = [];
            field.Clear();
            touched = false;
        }
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < input.Length && input[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
                touched = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                touched = true;
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < input.Length && input[i + 1] == '\n') i++;
                EndRow();
            }
            else
            {
                field.Append(ch);
                touched = true;
            }
        }
        // The end of the text ends no row of its own.
        if (touched || row.Count > 0) EndRow();
        return rows;
    }

    /// <summary>A change a pack records: an observation, a break-glass record, execution evidence or a grant use.</summary>
    private sealed class ChangeRecord(string kind, string recordId, long storeSequence, string resourceId, string action,
        long earliest, long latest, string? source, string? sourceEventId)
    {
        public string Kind => kind;
        public string RecordId => recordId;
        public long StoreSequence => storeSequence;
        public string ResourceId => resourceId;
        public string Action => action;
        public long Earliest => earliest;
        public string? Source => source;
        public string? SourceEventId => sourceEventId;
        public string Group { get; set; } = "";

        /// <summary>How far <paramref name="when"/> is from the record's time (or its window), in microseconds.</summary>
        public long Distance(long when) =>
            earliest <= when && when <= latest ? 0 : Math.Min(Math.Abs(when - earliest), Math.Abs(when - latest));
    }

    /// <summary>The records of one change: an observation and the record it matched, represented by the latter.</summary>
    private sealed class ChangeGroup(string group)
    {
        public string Group => group;
        public List<ChangeRecord> Members { get; } = [];

        /// <summary>The execution evidence, grant use or break-glass record when the group holds one, else its observation.</summary>
        public ChangeRecord Representative => Members.MinBy(r => (r.Kind == "observation" ? 1 : 0, r.StoreSequence))!;
    }

    /// <summary>Python's truthiness of a value read from JSON.</summary>
    private static bool Truthy(JsonElement? value) => value is { } v && v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Number => !v.TryGetDouble(out var d) || d != 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => true,
    };

    /// <summary>Python's <c>str()</c> of a value read from JSON, for the IDs a judgement names.</summary>
    private static string PythonStr(JsonElement? value) => value switch
    {
        null => "None",
        { ValueKind: JsonValueKind.Null } => "None",
        { ValueKind: JsonValueKind.True } => "True",
        { ValueKind: JsonValueKind.False } => "False",
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        // An int in decimal (-0 is 0), a float as Python writes it (1e2 is 100.0).
        { ValueKind: JsonValueKind.Number } v => v.GetRawText() is "-0" ? "0" : Canonical.NumberLiteral(v.GetRawText()),
        { } v => PythonRepr(v),
    };

    /// <summary>Python's <c>repr()</c> of a value read from JSON: what <c>str()</c> writes of a list or a dict.</summary>
    private static string PythonRepr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => PythonStringRepr(value.GetString()!),
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(PythonRepr)) + "]",
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().Select(p => PythonStringRepr(p.Name) + ": " + PythonRepr(p.Value))) + "}",
        _ => PythonStr(value),
    };

    /// <summary>
    /// Python's <c>repr()</c> of a string: in single quotes (double when it holds a single quote and no double one),
    /// with a backslash, the quote, tab, line feed and return escaped, and any other character Python does not print
    /// written <c>\xhh</c>, <c>\uhhhh</c> or <c>\Uhhhhhhhh</c>.
    /// </summary>
    private static string PythonStringRepr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        for (var i = 0; i < s.Length; i++)
        {
            int code = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) code = char.ConvertToUtf32(s[i], s[++i]);
            if (code == quote || code == '\\') sb.Append('\\').Append((char)code);
            else if (code == '\t') sb.Append(@"\t");
            else if (code == '\n') sb.Append(@"\n");
            else if (code == '\r') sb.Append(@"\r");
            else if (code is < 0x20 or 0x7f || (code > 0x7f && !PythonPrintable(code)))
                sb.Append(code switch
                {
                    <= 0xff => @"\x" + code.ToString("x2", CultureInfo.InvariantCulture),
                    <= 0xffff => @"\u" + code.ToString("x4", CultureInfo.InvariantCulture),
                    _ => @"\U" + code.ToString("x8", CultureInfo.InvariantCulture),
                });
            else sb.Append(char.ConvertFromUtf32(code));
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Python's <c>str.isprintable()</c> of one character: not a control, format, surrogate, private-use, unassigned or separator one.</summary>
    private static bool PythonPrintable(int code) =>
        code is < 0xd800 or > 0xdfff
        && CharUnicodeInfo.GetUnicodeCategory(code) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);

    private static JsonElement? Member(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;

    private static List<ChangeRecord> ChangeRecords(IReadOnlyList<EvidenceEvent> entries, IReadOnlyList<EvidenceEvent> cited)
    {
        var records = new List<ChangeRecord>();
        var matched = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in cited.Concat(entries))
            if (e.Entry.EntryKind == "judgement" && Truthy(Member(e.Payload, "matched_evidence_id")))
                matched[PythonStr(Member(e.Payload, "subject_id"))] = PythonStr(Member(e.Payload, "matched_evidence_id"));
        foreach (var e in entries)
        {
            var p = e.Payload;
            static long? Time(JsonElement? value) =>
                value is { ValueKind: JsonValueKind.String } v && TryReadTimestamp(v.GetString()!, out var at) ? at : null;
            void Add(string idField, string actionField, long? earliest, long? latest, string? source = null, string? eventId = null)
            {
                if (Str(p, idField) is { } id && Str(p, "resource_id") is { } resource && Str(p, actionField) is { } action
                    && earliest is { } from && latest is { } to)
                    records.Add(new ChangeRecord(e.Entry.EntryKind, id, e.Entry.StoreSequence, resource, action, from, to, source, eventId));
            }
            JsonElement? Or(string first, string second) => Truthy(Member(p, first)) ? Member(p, first) : Member(p, second);
            switch (e.Entry.EntryKind)
            {
                case "observation":
                    Add("observation_id", "action", Time(Or("changed_at", "changed_not_before")), Time(Or("changed_at", "changed_not_after")),
                        Str(p, "source"), Str(p, "source_event_id"));
                    break;
                case "break_glass":
                    Add("break_glass_id", "resource_action", Time(Member(p, "executed_at")), Time(Member(p, "executed_at")));
                    break;
                case "execution" when Truthy(Member(p, "resource_id")):
                    Add("evidence_id", "resource_action", Time(Member(p, "executed_at")), Time(Member(p, "executed_at")));
                    break;
                case "grant_execution" when Truthy(Member(p, "resource_id")):
                    // v1.6.0: a change made under a grant.
                    Add("use_id", "resource_action", Time(Member(p, "executed_at")), Time(Member(p, "executed_at")));
                    break;
            }
        }
        // A governed change seen by an observer is one change: the observation and what it matched.
        foreach (var record in records) record.Group = matched.GetValueOrDefault(record.RecordId, record.RecordId);
        return records;
    }

    /// <summary>
    /// The matching of one (resource, action), as the reference's <c>_match_bucket</c>: the most pairs, then the least
    /// total distance. Changes in time order (then by index) are matched to groups in time order (their representative's
    /// earliest time, then store position) without crossing; at each step the choices are, in this order, pairing,
    /// leaving the change, leaving the group, and the first best is taken, so every verifier finds the same pairs.
    /// </summary>
    private static List<(Change Change, ChangeGroup Group)> MatchBucket(List<(Change Change, long At)> changes, List<ChangeGroup> groups, long tolerance)
    {
        var cs = changes.OrderBy(c => c.At).ThenBy(c => c.Change.Index).ToList();
        var gs = groups.OrderBy(g => g.Representative.Earliest).ThenBy(g => g.Representative.StoreSequence).ToList();
        var (n, m) = (cs.Count, gs.Count);
        var best = new (long Pairs, long Cost)[n + 1, m + 1];
        var choice = new int[n + 1, m + 1];
        static bool Better((long Pairs, long Cost) a, (long Pairs, long Cost) b) => a.Pairs > b.Pairs || (a.Pairs == b.Pairs && a.Cost > b.Cost);
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                var top = (Value: best[i + 1, j], Taken: 1);
                var d = gs[j].Representative.Distance(cs[i].At);
                if (d <= tolerance) top = ((best[i + 1, j + 1].Pairs + 1, best[i + 1, j + 1].Cost - d), 0);
                if (top.Taken == 0 && Better(best[i + 1, j], top.Value)) top = (best[i + 1, j], 1);
                if (Better(best[i, j + 1], top.Value)) top = (best[i, j + 1], 2);
                (best[i, j], choice[i, j]) = top;
            }
        }
        var out_ = new List<(Change, ChangeGroup)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            switch (choice[i, j])
            {
                case 0:
                    out_.Add((cs[i].Change, gs[j]));
                    i++;
                    j++;
                    break;
                case 1:
                    i++;
                    break;
                default:
                    j++;
                    break;
            }
        }
        return out_;
    }

    /// <summary>
    /// Matches changes from a log outside Genesis Mesh against a pack's records, as the reference's
    /// <c>check_completeness</c> does: changes repeating a (source, source event ID) pair are set aside
    /// (<see cref="CompletenessReport.DuplicateChanges"/>); a change with a source event ID matches the record naming that
    /// very event when it agrees on resource and action; the rest are matched per (resource, action) for the most pairs,
    /// then the least total distance within the tolerance (an observation and the execution evidence it was matched to
    /// are one change, represented by the execution evidence). An unmatched change dated inside the period (with the
    /// tolerance either side) is <see cref="CompletenessReport.UnmatchedChanges"/>; one outside it
    /// <see cref="CompletenessReport.OutsidePeriod"/>.
    /// </summary>
    /// <param name="changes">The changes (<see cref="ParseChanges"/>).</param>
    /// <param name="entries">The pack's range of entries.</param>
    /// <param name="cited">The pack's cited entries: their judgements say which observation matched which record.</param>
    /// <param name="options">The period the records cover, and the tolerance.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An argument holds a null, a change's time is not a timestamp, or the tolerance is negative.</exception>
    public static CompletenessReport CheckCompleteness(IReadOnlyList<Change> changes, IReadOnlyList<EvidenceEvent> entries,
        IReadOnlyList<EvidenceEvent> cited, CompletenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(cited);
        ArgumentNullException.ThrowIfNull(options);
        if (changes.Any(c => c is null)) throw new ArgumentException("changes holds a null", nameof(changes));
        if (entries.Concat(cited).Any(e => e?.Entry is null)) throw new ArgumentException("the entries hold a null", nameof(entries));
        if (options.Tolerance < TimeSpan.Zero) throw new ArgumentException("the tolerance is negative", nameof(options));
        var at = new Dictionary<Change, long>(ReferenceEqualityComparer.Instance);
        foreach (var c in changes)
            at[c] = TryReadTimestamp(c.ChangedAt, out var t) ? t : throw new ArgumentException($"change {c.Index}: changed_at is not a timestamp", nameof(changes));
        var tolerance = options.Tolerance.Ticks / 10;
        var records = ChangeRecords(entries, cited);
        var groups = new Dictionary<string, ChangeGroup>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!groups.TryGetValue(record.Group, out var group)) groups[record.Group] = group = new ChangeGroup(record.Group);
            group.Members.Add(record);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<CompletenessPair>();
        var seen = new HashSet<(string, string)>();
        var duplicates = new List<Change>();
        var unique = new List<Change>();
        foreach (var change in changes)
        {
            if (change.Source is { } source && change.SourceEventId is { } eventId && !seen.Add((source, eventId)))
            {
                duplicates.Add(change);
                continue;
            }
            unique.Add(change);
        }
        var byEvent = new Dictionary<(string, string), ChangeRecord>();
        foreach (var r in records)
            if (r.Source is { } source && r.SourceEventId is { } eventId)
                byEvent.TryAdd((source, eventId), r);
        var left = new List<Change>();
        foreach (var change in unique)
        {
            if (change.Source is { } source && change.SourceEventId is { } eventId && byEvent.TryGetValue((source, eventId), out var named)
                && !used.Contains(named.Group) && named.ResourceId == change.ResourceId && named.Action == change.Action)
            {
                used.Add(named.Group);
                pairs.Add(new CompletenessPair
                {
                    Change = change.Index,
                    Kind = named.Kind,
                    RecordId = named.RecordId,
                    StoreSequence = named.StoreSequence,
                    MatchedBy = "source_event_id",
                });
            }
            else
            {
                left.Add(change);
            }
        }
        var buckets = new Dictionary<(string Resource, string Action), (List<(Change, long)> Changes, List<ChangeGroup> Groups)>();
        foreach (var change in left)
        {
            if (!buckets.TryGetValue((change.ResourceId, change.Action), out var bucket))
                buckets[(change.ResourceId, change.Action)] = bucket = ([], []);
            bucket.Changes.Add((change, at[change]));
        }
        foreach (var group in groups.Values)
            if (!used.Contains(group.Group) && buckets.TryGetValue((group.Representative.ResourceId, group.Representative.Action), out var bucket))
                bucket.Groups.Add(group);
        // By index, as the reference keeps them.
        var matchedChanges = new HashSet<int>();
        foreach (var key in buckets.Keys.OrderBy(k => k.Resource, Canonical.CodePointComparer.Instance).ThenBy(k => k.Action, Canonical.CodePointComparer.Instance))
        {
            var bucket = buckets[key];
            foreach (var (change, group) in MatchBucket(bucket.Changes, bucket.Groups, tolerance))
            {
                used.Add(group.Group);
                matchedChanges.Add(change.Index);
                var rep = group.Representative;
                pairs.Add(new CompletenessPair
                {
                    Change = change.Index,
                    Kind = rep.Kind,
                    RecordId = rep.RecordId,
                    StoreSequence = rep.StoreSequence,
                    MatchedBy = "resource_action_time",
                });
            }
        }
        var unmatched = left.Where(c => !matchedChanges.Contains(c.Index)).ToList();
        var (start, end) = (options.From.UtcTicks / 10, options.To.UtcTicks / 10);
        var inside = unmatched.Where(c => start - tolerance <= at[c] && at[c] <= end + tolerance).ToList();
        var outside = unmatched.Where(c => !inside.Contains(c)).ToList();
        Change Reported(Change c) => c with { ChangedAt = Canonical.Timestamp(new DateTimeOffset(at[c] * 10, TimeSpan.Zero)) };
        return new CompletenessReport
        {
            Changes = changes.Count,
            ToleranceSeconds = (long)options.Tolerance.TotalSeconds,
            Period = new CompletenessPeriod
            {
                From = Canonical.Timestamp(new DateTimeOffset(start * 10, TimeSpan.Zero)),
                To = Canonical.Timestamp(new DateTimeOffset(end * 10, TimeSpan.Zero)),
            },
            Pairs = pairs.OrderBy(p => p.Change).ToList(),
            UnmatchedChanges = inside.OrderBy(c => c.Index).Select(Reported).ToList(),
            OutsidePeriod = outside.OrderBy(c => c.Index).Select(Reported).ToList(),
            DuplicateChanges = duplicates.Select(Reported).ToList(),
            UnmatchedRecords = groups.Values.Where(g => !used.Contains(g.Group)).Select(g => g.Representative).OrderBy(r => r.StoreSequence)
                .Select(r => new CompletenessRecord
                {
                    Kind = r.Kind,
                    RecordId = r.RecordId,
                    StoreSequence = r.StoreSequence,
                    ResourceId = r.ResourceId,
                    Action = r.Action,
                })
                .ToList(),
            Complete = inside.Count == 0,
        };
    }
}
