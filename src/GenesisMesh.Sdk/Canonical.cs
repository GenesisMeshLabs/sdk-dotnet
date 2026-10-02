using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// Canonical JSON, byte-identical to Python's
/// <c>json.dumps(value, sort_keys=True, separators=(",", ":"))</c> applied to the value the
/// Network Authority parsed: keys sorted by code point, every non-ASCII character (and DEL)
/// escaped as in <c>ensure_ascii</c>, integer literals kept as written and other numbers in
/// Python's float repr (a received <c>90.0</c> stays <c>90.0</c>; <c>1e-5</c> becomes <c>1e-05</c>).
/// </summary>
public static class Canonical
{
    /// <summary>Canonical form of a JSON document given as text.</summary>
    public static string FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Of(doc.RootElement);
    }

    /// <summary>Canonical form of a parsed JSON element.</summary>
    public static string Of(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(sb, element, null, null);
        return sb.ToString();
    }

    /// <summary>
    /// Canonical form of a JSON object without the <paramref name="exclude"/> keys, and without
    /// the <paramref name="omitWhenNull"/> keys when their value is null or absent.
    /// </summary>
    public static string Of(JsonElement obj, IReadOnlyCollection<string> exclude, IReadOnlyCollection<string>? omitWhenNull = null)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Expected a JSON object.", nameof(obj));
        var sb = new StringBuilder();
        Write(sb, obj, exclude, omitWhenNull);
        return sb.ToString();
    }

    /// <summary>Canonical form of selected top-level keys of an object (missing keys become null).</summary>
    public static string OfKeys(JsonElement obj, IReadOnlyCollection<string> keys)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var key in keys.OrderBy(k => k, CodePointComparer.Instance))
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, key);
            sb.Append(':');
            if (obj.TryGetProperty(key, out var value)) Write(sb, value, null, null);
            else sb.Append("null");
        }
        return sb.Append('}').ToString();
    }

    internal static void Write(StringBuilder sb, JsonElement e, IReadOnlyCollection<string>? exclude, IReadOnlyCollection<string>? omitWhenNull)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, CodePointComparer.Instance))
                {
                    if (exclude is not null && exclude.Contains(p.Name)) continue;
                    if (omitWhenNull is not null && omitWhenNull.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.Null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    Write(sb, p.Value, null, null);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(sb, item, null, null);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(sb, e.GetString()!);
                break;
            case JsonValueKind.Number:
                sb.Append(NumberLiteral(e.GetRawText()));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    /// <summary>Escape a string as <c>json.dumps(..., ensure_ascii=True)</c> does.</summary>
    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s) // UTF-16 code units: astral characters become surrogate pairs, as in Python
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c >= 0x7f)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static bool IsIntegerLiteral(string s)
    {
        var start = s.StartsWith('-') ? 1 : 0;
        if (s.Length == start) return false;
        for (var i = start; i < s.Length; i++)
            if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }

    /// <summary>An integer literal stays as written (Python int, any size); anything else is a Python float.</summary>
    internal static string NumberLiteral(string raw) =>
        IsIntegerLiteral(raw) ? raw : PythonFloatRepr(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));

    /// <summary>Python's <c>repr(float)</c>: shortest round-trip digits; positional for exponents -4..15.</summary>
    public static string PythonFloatRepr(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("Canonical JSON cannot encode NaN or Infinity.");
        // "R" yields the shortest round-trippable decimal; normalise it to (digits, exponent).
        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = r.StartsWith('-');
        if (negative) r = r[1..];
        var exp10 = 0;
        var ePos = r.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0)
        {
            exp10 = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }
        var dot = r.IndexOf('.');
        var intPart = dot >= 0 ? r[..dot] : r;
        var fracPart = dot >= 0 ? r[(dot + 1)..] : "";
        var digits = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - digits.Length;
        int exponent; // exponent of the first significant digit
        if (digits.Length == 0)
        {
            digits = "0";
            exponent = 0;
        }
        else
        {
            exponent = intPart.Length - 1 - leadingZeros + exp10;
        }
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        var sign = negative ? "-" : "";

        if (exponent >= -4 && exponent < 16)
        {
            var point = exponent + 1;
            string text;
            if (point <= 0) text = "0." + new string('0', -point) + digits;
            else if (point >= digits.Length) text = digits + new string('0', point - digits.Length) + ".0";
            else text = digits[..point] + "." + digits[point..];
            return sign + text;
        }
        var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        var expSign = exponent < 0 ? "-" : "+";
        return $"{sign}{mantissa}e{expSign}{Math.Abs(exponent):00}";
    }

    /// <summary>Orders strings by Unicode code point, as Python sorts dictionary keys.</summary>
    internal sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var ex = x.EnumerateRunes();
            var ey = y.EnumerateRunes();
            while (true)
            {
                var hx = ex.MoveNext();
                var hy = ey.MoveNext();
                if (!hx || !hy) return hx == hy ? 0 : (hx ? 1 : -1);
                var c = ex.Current.Value.CompareTo(ey.Current.Value);
                if (c != 0) return c;
            }
        }
    }
}
