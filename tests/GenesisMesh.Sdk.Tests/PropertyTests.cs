using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using GenesisMesh;
using Microsoft.FSharp.Core;
using Xunit;

namespace GenesisMesh.Sdk.Tests;

/// <summary>
/// Property tests (FsCheck) of the strict JSON reader and the canonical form, with generated input: any JSON
/// value, written in any of the ways JSON allows, reads back from its canonical form; a duplicate key is refused
/// wherever it is; number lexemes are accepted or refused by the reference's rules; strings of any UTF-16 code
/// units and any bytes are read exactly or refused by reason. A failure prints the counterexample.
/// <para>
/// The inputs come from a fixed seed, so every run (and CI's coverage) checks the same cases. To explore, set
/// <c>GM_PROPERTY_SEED</c> to another number, or to <c>random</c> for a new seed each run (FsCheck prints the
/// seed of a failure): <c>GM_PROPERTY_SEED=random dotnet test --filter PropertyTests</c>.
/// </para>
/// </summary>
public class PropertyTests
{
    private const int Runs = 300;

    /// <summary>The seed every property starts from unless <c>GM_PROPERTY_SEED</c> names another.</summary>
    private const ulong FixedSeed = 20261016;

    private static Config Settings(int runs)
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(runs);
        var seed = Environment.GetEnvironmentVariable("GM_PROPERTY_SEED");
        if (seed == "random") return config;
        var value = string.IsNullOrEmpty(seed) ? FixedSeed : ulong.Parse(seed, CultureInfo.InvariantCulture);
        return config.WithReplay(FSharpOption<Replay>.Some(new Replay(new Rnd(value), FSharpOption<int>.None)));
    }

    private static void Holds<T>(Arbitrary<T> input, Action<T> property, int runs = Runs) =>
        Check.One(Settings(runs), Prop.ForAll(input, property));

    private static Gen<(TA, TB, TC)> Zip<TA, TB, TC>(Gen<TA> a, Gen<TB> b, Gen<TC> c) =>
        Gen.Zip(Gen.Zip(a, b), c, (ab, x) => (ab.Item1, ab.Item2, x));

    private static Gen<(TA, TB, TC, TD)> Zip<TA, TB, TC, TD>(Gen<TA> a, Gen<TB> b, Gen<TC> c, Gen<TD> d) =>
        Gen.Zip(Zip(a, b, c), d, (abc, x) => (abc.Item1, abc.Item2, abc.Item3, x));

    // ── a JSON value model and generators ───────────────────────────────────

    private abstract record J;
    private sealed record JNull : J;
    private sealed record JBool(bool Value) : J;
    private sealed record JInt(BigInteger Value) : J;
    private sealed record JFloat(double Value) : J;
    private sealed record JString(string Value) : J;
    private sealed record JArray(IReadOnlyList<J> Items) : J;
    private sealed record JObject(IReadOnlyList<(string Key, J Value)> Members) : J;

    private static readonly BigInteger MinInteger = -BigInteger.Pow(2, 63);
    private static readonly BigInteger MaxInteger = BigInteger.Pow(2, 64) - 1;

    /// <summary>A character as UTF-16: ASCII, controls, DEL, the escaped characters, any BMP scalar or an astral one.</summary>
    private static readonly Gen<string> Character = Gen.Frequency(
        (6, Gen.Choose(0x20, 0x7e).Select(c => ((char)c).ToString())),
        (2, Gen.Elements("\"", "\\", "/", "\b", "\f", "\n", "\r", "\t", "\u007f", "\u0000", "\u001f")),
        (2, Gen.Choose(0x80, 0xd7ff).Select(c => ((char)c).ToString())),
        (1, Gen.Choose(0xe000, 0xffff).Select(c => ((char)c).ToString())),
        (2, Gen.Choose(0x10000, 0x10ffff).Select(c => char.ConvertFromUtf32(c))));

    private static readonly Gen<string> Text = Character.ListOf().Select(cs => string.Concat(cs));

    private static readonly Gen<BigInteger> Integer = Gen.OneOf(
        Gen.Choose(-1000, 1000).Select(i => new BigInteger(i)),
        Gen.Two(Gen.Choose(0, 1 << 30)).Select(t => (new BigInteger(t.Item1) << 34) + t.Item2 - (BigInteger.One << 63)),
        Gen.Elements(MinInteger, MaxInteger, MinInteger + 1, MaxInteger - 1, new BigInteger(long.MaxValue)),
        Gen.Two(Gen.Choose(0, 1 << 30)).Select(t => MaxInteger - ((new BigInteger(t.Item1) << 30) + t.Item2)));

    private static readonly Gen<double> Float = Gen.OneOf(
        // Any finite double, from random bits (drawn from a seed, so a failure replays).
        Gen.Choose(0, int.MaxValue)
            .Select(seed => BitConverter.Int64BitsToDouble(new Random(seed).NextInt64(long.MinValue, long.MaxValue)))
            .Where(double.IsFinite),
        Gen.Two(Gen.Choose(-100000, 100000)).Select(t => t.Item1 / (double)(t.Item2 == 0 ? 1 : t.Item2)),
        Gen.Elements(0.0, -0.0, 0.1, 1e-5, 1e-4, 1e16, 1e15, 123456789012345680.0, double.Epsilon, double.MaxValue,
            -double.MaxValue, 5e-324, 1.7976931348623157e308, 0.30000000000000004));

    private static Gen<J> Value(int size) => size <= 0
        ? Scalar
        : Gen.Frequency(
            (4, Scalar),
            (1, Few(Value(size / 3)).Select(items => (J)new JArray(items))),
            (1, Few(Gen.Zip(Text, Value(size / 3))).Select(members => (J)new JObject(Unique(members)))));

    /// <summary>Up to four items: nested lists stay small whatever the size.</summary>
    private static Gen<List<T>> Few<T>(Gen<T> item) =>
        Gen.Choose(0, 4).SelectMany(n => Gen.ListOf(item, n)).Select(items => items.ToList());

    private static readonly Gen<J> Scalar = Gen.Frequency(
        (1, Gen.Constant((J)new JNull())),
        (1, Gen.Elements(true, false).Select(b => (J)new JBool(b))),
        (3, Integer.Select(i => (J)new JInt(i))),
        (3, Float.Select(f => (J)new JFloat(f))),
        (3, Text.Select(s => (J)new JString(s))));

    private static List<(string, J)> Unique(IEnumerable<(string Key, J Value)> members) =>
        members.GroupBy(m => m.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();

    private static readonly Gen<J> AnyValue = Gen.Sized(size => Value(Math.Min(size, 40)));

    /// <summary>A value and the seed of the choices made writing it (spacing, escapes, number spelling).</summary>
    private static readonly Arbitrary<(J Value, int Seed)> Written =
        Gen.Zip(AnyValue, Gen.Choose(0, int.MaxValue)).ToArbitrary();

    // ── a JSON writer that makes every choice JSON allows ───────────────────

    private sealed class Writer(int seed)
    {
        private readonly Random _random = new(seed);
        private readonly StringBuilder _text = new();

        public static string Write(J value, int seed)
        {
            var writer = new Writer(seed);
            writer.Value(value);
            writer.Space();
            return writer._text.ToString();
        }

        private bool Chance(int percent) => _random.Next(100) < percent;

        public void Space()
        {
            while (Chance(25)) _text.Append(" \t\n\r"[_random.Next(4)]);
        }

        public void Value(J value)
        {
            Space();
            switch (value)
            {
                case JNull: _text.Append("null"); break;
                case JBool b: _text.Append(b.Value ? "true" : "false"); break;
                case JInt i: _text.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
                case JFloat f: _text.Append(FloatLexeme(f.Value)); break;
                case JString s: String(s.Value); break;
                case JArray a:
                    _text.Append('[');
                    for (var k = 0; k < a.Items.Count; k++)
                    {
                        if (k > 0) _text.Append(',');
                        Value(a.Items[k]);
                        Space();
                    }
                    Space();
                    _text.Append(']');
                    break;
                case JObject o:
                    _text.Append('{');
                    for (var k = 0; k < o.Members.Count; k++)
                    {
                        if (k > 0) _text.Append(',');
                        Member(o.Members[k].Key, o.Members[k].Value);
                    }
                    Space();
                    _text.Append('}');
                    break;
            }
        }

        public void Member(string key, J value)
        {
            Space();
            String(key);
            Space();
            _text.Append(':');
            Value(value);
            Space();
        }

        /// <summary>A float as JSON may spell it: the shortest digits, or with an exponent of either case and sign.</summary>
        private string FloatLexeme(double value)
        {
            var r = value.ToString("R", CultureInfo.InvariantCulture);
            if (!r.Contains('.', StringComparison.Ordinal) && !r.Contains('E', StringComparison.Ordinal)) r += ".0";
            if (Chance(30)) r = value.ToString("E16", CultureInfo.InvariantCulture);
            if (Chance(50)) r = r.Replace('E', 'e');
            return r.Replace("e+", Chance(50) ? "e" : "e+", StringComparison.Ordinal)
                .Replace("E+", Chance(50) ? "E" : "E+", StringComparison.Ordinal);
        }

        /// <summary>A string with each character raw where JSON allows it, or as a short or a \u escape.</summary>
        public void String(string value)
        {
            _text.Append('"');
            for (var k = 0; k < value.Length; k++)
            {
                var c = value[k];
                if (char.IsHighSurrogate(c) && k + 1 < value.Length && char.IsLowSurrogate(value[k + 1]))
                {
                    // A pair: both raw or both escaped.
                    if (Chance(50)) _text.Append(c).Append(value[k + 1]);
                    else _text.Append(Escape(c)).Append(Escape(value[k + 1]));
                    k++;
                    continue;
                }
                var shortEscape = c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '/' => "\\/",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => null,
                };
                var mustEscape = c is '"' or '\\' || c < 0x20 || char.IsSurrogate(c);
                if (!mustEscape && Chance(70)) _text.Append(c);
                else if (shortEscape is not null && Chance(50)) _text.Append(shortEscape);
                else _text.Append(Escape(c));
            }
            _text.Append('"');
        }

        private string Escape(char c) =>
            "\\u" + ((int)c).ToString(Chance(50) ? "x4" : "X4", CultureInfo.InvariantCulture);
    }

    // ── checks against the model ────────────────────────────────────────────

    private static void AssertSame(J expected, JsonElement actual, string path = "$")
    {
        switch (expected)
        {
            case JNull:
                Assert.Equal(JsonValueKind.Null, actual.ValueKind);
                break;
            case JBool b:
                Assert.Equal(b.Value ? JsonValueKind.True : JsonValueKind.False, actual.ValueKind);
                break;
            case JInt i:
                Assert.Equal(i.Value.ToString(CultureInfo.InvariantCulture), actual.GetRawText());
                break;
            case JFloat f:
                {
                    var raw = actual.GetRawText();
                    Assert.True(raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.Ordinal),
                        $"{path}: the float {raw} would read back as an integer");
                    Assert.Equal(BitConverter.DoubleToInt64Bits(f.Value),
                        BitConverter.DoubleToInt64Bits(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)));
                    break;
                }
            case JString s:
                Assert.Equal(s.Value, actual.GetString());
                break;
            case JArray a:
                Assert.Equal(a.Items.Count, actual.GetArrayLength());
                for (var k = 0; k < a.Items.Count; k++) AssertSame(a.Items[k], actual[k], $"{path}[{k}]");
                break;
            case JObject o:
                {
                    var members = actual.EnumerateObject().ToList();
                    Assert.Equal(o.Members.Count, members.Count);
                    var names = members.Select(m => m.Name).ToList();
                    Assert.Equal(names.Order(CodePoints.Instance), names);
                    foreach (var (key, value) in o.Members) AssertSame(value, actual.GetProperty(key), $"{path}.{key}");
                    break;
                }
        }
    }

    /// <summary>The order of Python's sorted(): by code point, compared as UTF-32.</summary>
    private sealed class CodePoints : IComparer<string>
    {
        public static readonly CodePoints Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = x!.EnumerateRunes().Select(r => r.Value).ToArray();
            var b = y!.EnumerateRunes().Select(r => r.Value).ToArray();
            for (var k = 0; k < Math.Min(a.Length, b.Length); k++)
                if (a[k] != b[k]) return a[k].CompareTo(b[k]);
            return a.Length.CompareTo(b.Length);
        }
    }

    private static string Refusal(string json)
    {
        var e = Assert.Throws<StrictJsonException>(() => Canonical.FromJson(json));
        return e.Reason;
    }

    // ── properties ──────────────────────────────────────────────────────────

    [Fact]
    public void AnyValueWrittenAnyWayReadsBackFromItsCanonicalForm() => Holds(Written, input =>
    {
        var text = Writer.Write(input.Value, input.Seed);
        var canonical = Canonical.FromJson(text);
        Assert.All(canonical, c => Assert.InRange(c, ' ', '~'));
        Assert.Equal(canonical, Canonical.FromJson(canonical));
        using var parsed = JsonDocument.Parse(canonical);
        AssertSame(input.Value, parsed.RootElement);
    });

    [Fact]
    public void TheCanonicalFormDoesNotDependOnHowTheValueWasWritten() => Holds(
        Zip(AnyValue, Gen.Choose(0, int.MaxValue), Gen.Choose(0, int.MaxValue)).ToArbitrary(), input =>
            Assert.Equal(Canonical.FromJson(Writer.Write(input.Item1, input.Item2)),
                Canonical.FromJson(Writer.Write(input.Item1, input.Item3))));

    /// <summary>
    /// An object whose first key appears again (each spelling of it chosen anew by the writer), nested in arrays
    /// and objects.
    /// </summary>
    private static readonly Arbitrary<(J Value, int Seed)> DuplicateKeys =
        Gen.Zip(Zip(Text, AnyValue, AnyValue), Zip(Text, Gen.Choose(0, 10), Gen.Choose(0, int.MaxValue)))
            .Select(t =>
            {
                var ((key, first, again), (outerKey, depth, seed)) = t;
                J value = new JObject([(key, first), ("other", first), (key, again)]);
                for (var d = 0; d < depth; d++)
                    value = d % 2 == 0 ? new JArray([value]) : new JObject([(outerKey, value)]);
                return (value, seed);
            })
            .ToArbitrary();

    [Fact]
    public void ADuplicateKeyIsRefusedWhereverItIs() => Holds(DuplicateKeys, input =>
        Assert.Equal("duplicate_key", Refusal(Writer.Write(input.Value, input.Seed))));

    /// <summary>A number lexeme by the JSON grammar: sign, integer part, fraction, exponent.</summary>
    private static readonly Gen<string> Digits = Gen.Choose(0, 9).ListOf().Select(ds => string.Concat(ds));

    private static readonly Arbitrary<string> NumberLexemes = Zip(
            Gen.Elements("", "-"),
            Gen.OneOf(Gen.Constant("0"), Gen.Zip(Gen.Choose(1, 9), Digits).Select(t => t.Item1 + t.Item2)),
            Gen.OneOf(Gen.Constant(""), Gen.Zip(Gen.Choose(0, 9), Digits).Select(t => "." + t.Item1 + t.Item2)),
            Gen.OneOf(Gen.Constant(""),
                Zip(Gen.Elements("e", "E"), Gen.Elements("", "+", "-"), Gen.Choose(0, 999))
                    .Select(t => t.Item1 + t.Item2 + t.Item3.ToString(CultureInfo.InvariantCulture))))
        .Select(t => t.Item1 + t.Item2 + t.Item3 + t.Item4)
        .ToArbitrary();

    [Fact]
    public void ANumberIsReadByTheReferenceRules() => Holds(NumberLexemes, lexeme =>
    {
        var integer = !lexeme.Contains('.', StringComparison.Ordinal) && !lexeme.Contains('e', StringComparison.OrdinalIgnoreCase);
        string? expected = null;
        if (integer)
        {
            var value = BigInteger.Parse(lexeme, CultureInfo.InvariantCulture);
            if (lexeme == "-0") expected = "negative_zero";
            else if (value < MinInteger || value > MaxInteger) expected = "integer_out_of_range";
        }
        else if (!double.IsFinite(double.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture)))
        {
            expected = "non_finite_number";
        }

        foreach (var text in new[] { lexeme, $"[{lexeme}]", $"{{\"n\": {lexeme} }}" })
        {
            if (expected is not null)
            {
                Assert.Equal(expected, Refusal(text));
                continue;
            }
            var canonical = Canonical.FromJson(text);
            var written = canonical.TrimStart('[', '{').TrimEnd(']', '}').Replace("\"n\":", "", StringComparison.Ordinal);
            if (integer)
            {
                Assert.Equal(lexeme, written);
            }
            else
            {
                Assert.True(written.Contains('.', StringComparison.Ordinal) || written.Contains('e', StringComparison.Ordinal), written);
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(double.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    BitConverter.DoubleToInt64Bits(double.Parse(written, NumberStyles.Float, CultureInfo.InvariantCulture)));
            }
        }
    });

    /// <summary>A valid lexeme spoilt the ways other parsers forgive: these are never numbers.</summary>
    private static readonly Arbitrary<string> MalformedNumbers = Gen.Zip(
            NumberLexemes.Generator,
            Gen.Elements<Func<string, string>>(
                n => "+" + n.TrimStart('-'),
                n => n.StartsWith('-') ? "-0" + n[1..] : "0" + n,
                n => n + ".",
                n => n.Contains('.', StringComparison.Ordinal) ? n.Replace(".", "..", StringComparison.Ordinal) : "." + n.TrimStart('-'),
                n => n + "e",
                n => n + "E+",
                n => "--" + n.TrimStart('-'),
                n => "0x" + n.TrimStart('-'),
                n => n.Replace('0', 'O'),
                _ => "NaN",
                _ => "Infinity",
                _ => "-Infinity",
                n => n + "f",
                n => n + "_0"))
        .Select(t => t.Item2(t.Item1))
        .Where(n => n.Length > 0 && !IsJsonNumber(n))
        .ToArbitrary();

    private static bool IsJsonNumber(string n) =>
        System.Text.RegularExpressions.Regex.IsMatch(n, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$");

    [Fact]
    public void AMalformedNumberIsRefused() => Holds(MalformedNumbers, lexeme =>
    {
        Assert.Throws<StrictJsonException>(() => Canonical.FromJson($"[{lexeme}]"));
        Assert.Throws<StrictJsonException>(() => Canonical.FromJson(lexeme));
    });

    /// <summary>Any UTF-16 code units, lone surrogates included, escaped as \u: refused as lone_surrogate or read exactly.</summary>
    [Fact]
    public void AStringOfAnyCodeUnitsIsReadExactlyOrRefusedAsALoneSurrogate() => Holds(
        Gen.Frequency(
                (3, Gen.Choose(0, 0xffff)),
                (2, Gen.Choose(0xd800, 0xdfff)),
                (2, Gen.Choose(0x20, 0x7e)))
            .ListOf().Select(units => new string(units.Select(u => (char)u).ToArray())).ToArbitrary(),
        value =>
        {
            var escaped = "\"" + string.Concat(value.Select(c => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture))) + "\"";
            if (!IsWellFormed(value))
            {
                Assert.Equal("lone_surrogate", Refusal(escaped));
                return;
            }
            var canonical = Canonical.FromJson(escaped);
            Assert.All(canonical, c => Assert.InRange(c, ' ', '~'));
            using var parsed = JsonDocument.Parse(canonical);
            Assert.Equal(value, parsed.RootElement.GetString());
        });

    private static bool IsWellFormed(string value)
    {
        for (var k = 0; k < value.Length; k++)
        {
            if (char.IsHighSurrogate(value[k]) && k + 1 < value.Length && char.IsLowSurrogate(value[k + 1])) k++;
            else if (char.IsSurrogate(value[k])) return false;
        }
        return true;
    }

    /// <summary>Bytes: decoded exactly as UTF-8, or refused as invalid_json; never repaired.</summary>
    [Fact]
    public void BytesAreDecodedExactlyOrRefused() => Holds(
        Gen.OneOf(
                Gen.Choose(0, 255).Select(b => (byte)b).ArrayOf(),
                Text.Select(Encoding.UTF8.GetBytes),
                Zip(Text, Gen.Choose(0, 255), Gen.Choose(0, 64)).Select(t =>
                {
                    var bytes = Encoding.UTF8.GetBytes(t.Item1);
                    if (bytes.Length > 0) bytes[t.Item3 % bytes.Length] = (byte)t.Item2;
                    return bytes;
                }))
            .ToArbitrary(),
        bytes =>
        {
            string text;
            try
            {
#pragma warning disable CS0618 // StrictJson is obsolete (internal at 2.0); the transport uses the same reader
                text = StrictJson.DecodeUtf8(bytes);
#pragma warning restore CS0618
            }
            catch (StrictJsonException e)
            {
                Assert.Equal("invalid_json", e.Reason);
                Assert.ThrowsAny<ArgumentException>(() => new UTF8Encoding(false, true).GetString(bytes));
                return;
            }
            Assert.Equal(bytes, Encoding.UTF8.GetBytes(text));
        });

    [Fact]
    public void NestingIsRefusedBeyondItsLimit() => Holds(
        Gen.Zip(Gen.Choose(1, 100), Gen.Elements(true, false)).ToArbitrary(), input =>
        {
            var (depth, arrays) = input;
            var text = arrays
                ? new string('[', depth) + new string(']', depth)
                : string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);
            if (depth <= 64) Assert.Equal(text, Canonical.FromJson(text));
            else Assert.Equal("invalid_json", Refusal(text));
        });

    [Fact]
    public void KeysSortByCodePoint() => Holds(Gen.Zip(Text, Text).ToArbitrary(), pair =>
        Assert.Equal(Math.Sign(CodePoints.Instance.Compare(pair.Item1, pair.Item2)),
            Math.Sign(Canonical.CodePointComparer.Instance.Compare(pair.Item1, pair.Item2))));

    [Fact]
    public void AFloatIsWrittenAsPythonReprWrites() => Holds(Float.ToArbitrary(), value =>
    {
        var repr = Canonical.FloatRepr(value);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture)));
        var magnitude = Math.Abs(value);
        // Python writes 1e-4 <= |x| < 1e16 positionally (and 0.0), everything else with an exponent of 2+ digits.
        if (magnitude == 0 || (magnitude >= 1e-4 && magnitude < 1e16))
        {
            Assert.DoesNotContain('e', repr);
            Assert.Contains('.', repr);
        }
        else
        {
            Assert.Matches(@"^-?[1-9](\.[0-9]*[1-9])?e[+-][0-9]{2,3}$", repr);
        }
    }, runs: 1000);
}
