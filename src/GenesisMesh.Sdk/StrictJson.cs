using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GenesisMesh;

/// <summary>
/// JSON refused as input to a signed record (v1.2.0). <see cref="Reason"/> is one of
/// <c>invalid_json</c> (not JSON, including <c>NaN</c> and <c>Infinity</c>), <c>duplicate_key</c>,
/// <c>non_finite_number</c> (<c>1e400</c>), <c>integer_out_of_range</c> (outside
/// <c>-2**63 .. 2**64 - 1</c>), <c>negative_zero</c> (the integer <c>-0</c>) or <c>lone_surrogate</c>.
/// </summary>
public sealed class StrictJsonException : FormatException
{
    /// <summary>Why the input was refused, as every implementation names it.</summary>
    public string Reason { get; }

    public StrictJsonException(string reason, string detail) : base($"JSON refused ({reason}): {detail}")
    {
        Reason = reason;
    }
}

/// <summary>
/// Strict JSON input (v1.2.0): refuse what parsers read differently.
/// <para>
/// The canonical form of a signed record is computed from parsed JSON, so every implementation must
/// parse a record to the same value. Some JSON does not parse alike: <see cref="JsonDocument"/> keeps
/// both of two duplicate keys where the other parsers keep the last, and Rust reads an integer beyond
/// 64 bits as a float. Such input is refused here, as in every implementation, by a named reason (the
/// conformance suite <c>canonical</c>).
/// </para>
/// </summary>
public static class StrictJson
{
    private static readonly BigInteger MinInteger = -BigInteger.Pow(2, 63);
    private static readonly BigInteger MaxInteger = BigInteger.Pow(2, 64) - 1;
    private const int MaxDepth = 10000;

    /// <summary>Throw <see cref="StrictJsonException"/> unless <paramref name="json"/> is JSON every implementation reads alike.</summary>
    public static void Check(string json)
    {
        var scanner = new Scanner(json);
        scanner.Value();
        scanner.Space();
        if (scanner.At != json.Length) throw new StrictJsonException("invalid_json", "text after the value");
    }

    /// <summary>Parse JSON read strictly (<see cref="Check"/>).</summary>
    public static JsonDocument Parse(string json)
    {
        Check(json);
        return JsonDocument.Parse(json);
    }

    private sealed class Scanner(string text)
    {
        public int At;
        private int _depth;

        private char Peek => At < text.Length ? text[At] : '\0';

        public void Space()
        {
            while (At < text.Length && text[At] is ' ' or '\t' or '\n' or '\r') At++;
        }

        private static StrictJsonException Refuse(string reason, string detail) => new(reason, detail);

        private string String(bool keep)
        {
            At++;
            var units = keep ? new System.Text.StringBuilder() : null;
            var pending = -1;
            while (true)
            {
                if (At >= text.Length) throw Refuse("invalid_json", "a string is not closed");
                var c = text[At];
                int unit;
                if (c == '"')
                {
                    At++;
                    break;
                }
                if (c < 0x20) throw Refuse("invalid_json", "a control character in a string");
                if (c == '\\')
                {
                    var escape = At + 1 < text.Length ? text[At + 1] : '\0';
                    At += 2;
                    switch (escape)
                    {
                        case 'u':
                            if (At + 4 > text.Length || !int.TryParse(text.AsSpan(At, 4), NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture, out unit))
                                throw Refuse("invalid_json", "a malformed \\u escape");
                            At += 4;
                            break;
                        case '"': unit = '"'; break;
                        case '\\': unit = '\\'; break;
                        case '/': unit = '/'; break;
                        case 'b': unit = 8; break;
                        case 'f': unit = 12; break;
                        case 'n': unit = 10; break;
                        case 'r': unit = 13; break;
                        case 't': unit = 9; break;
                        default: throw Refuse("invalid_json", "an unknown escape");
                    }
                }
                else
                {
                    unit = c;
                    At++;
                }
                if (pending >= 0)
                {
                    if (unit is < 0xDC00 or > 0xDFFF) throw Refuse("lone_surrogate", "a high surrogate without its low half");
                    units?.Append((char)pending).Append((char)unit);
                    pending = -1;
                }
                else if (unit is >= 0xD800 and <= 0xDBFF)
                {
                    pending = unit;
                }
                else if (unit is >= 0xDC00 and <= 0xDFFF)
                {
                    throw Refuse("lone_surrogate", "a low surrogate without its high half");
                }
                else
                {
                    units?.Append((char)unit);
                }
            }
            if (pending >= 0) throw Refuse("lone_surrogate", "a high surrogate without its low half");
            return units?.ToString() ?? "";
        }

        private void Number()
        {
            var start = At;
            bool Digits()
            {
                var from = At;
                while (At < text.Length && text[At] is >= '0' and <= '9') At++;
                return At > from;
            }
            if (Peek == '-') At++;
            if (Peek == '0') At++;
            else if (Peek is >= '1' and <= '9') Digits();
            else throw Refuse("invalid_json", "a malformed number");
            var integer = true;
            if (Peek == '.')
            {
                At++;
                integer = false;
                if (!Digits()) throw Refuse("invalid_json", "a malformed number");
            }
            if (Peek is 'e' or 'E')
            {
                At++;
                integer = false;
                if (Peek is '+' or '-') At++;
                if (!Digits()) throw Refuse("invalid_json", "a malformed number");
            }
            var literal = text[start..At];
            if (integer)
            {
                if (literal == "-0") throw Refuse("negative_zero", "the integer -0");
                var value = BigInteger.Parse(literal, CultureInfo.InvariantCulture);
                if (value < MinInteger || value > MaxInteger)
                    throw Refuse("integer_out_of_range", $"{literal} is outside the 64-bit range");
            }
            else if (!double.IsFinite(double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture)))
            {
                throw Refuse("non_finite_number", $"{literal} overflows a 64-bit float");
            }
        }

        public void Value()
        {
            Space();
            switch (Peek)
            {
                case '{':
                {
                    if (++_depth > MaxDepth) throw Refuse("invalid_json", "nested too deeply");
                    At++;
                    Space();
                    if (Peek == '}')
                    {
                        At++;
                        _depth--;
                        return;
                    }
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    while (true)
                    {
                        Space();
                        if (Peek != '"') throw Refuse("invalid_json", "expected a key");
                        var key = String(true);
                        if (!keys.Add(key)) throw Refuse("duplicate_key", $"key \"{key}\" appears twice");
                        Space();
                        if (Peek != ':') throw Refuse("invalid_json", "expected \":\"");
                        At++;
                        Value();
                        Space();
                        if (Peek == ',')
                        {
                            At++;
                            continue;
                        }
                        if (Peek == '}')
                        {
                            At++;
                            _depth--;
                            return;
                        }
                        throw Refuse("invalid_json", "expected \",\" or \"}\"");
                    }
                }
                case '[':
                {
                    if (++_depth > MaxDepth) throw Refuse("invalid_json", "nested too deeply");
                    At++;
                    Space();
                    if (Peek == ']')
                    {
                        At++;
                        _depth--;
                        return;
                    }
                    while (true)
                    {
                        Value();
                        Space();
                        if (Peek == ',')
                        {
                            At++;
                            continue;
                        }
                        if (Peek == ']')
                        {
                            At++;
                            _depth--;
                            return;
                        }
                        throw Refuse("invalid_json", "expected \",\" or \"]\"");
                    }
                }
                case '"':
                    String(false);
                    return;
                case '-' or (>= '0' and <= '9'):
                    Number();
                    return;
            }
            foreach (var literal in new[] { "true", "false", "null" })
            {
                if (string.CompareOrdinal(text, At, literal, 0, literal.Length) == 0)
                {
                    At += literal.Length;
                    return;
                }
            }
            throw Refuse("invalid_json", "an unexpected token");
        }
    }
}
